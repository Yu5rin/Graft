<#
================================================================================
 Graft Windows実機検証: サンプル生成スクリプト
================================================================================

■ これは何をするものか
    docs/Windows実機検証手順.md に沿ってWindows実機でGraftを確認するための
    「検証用プロジェクトフォルダ」と「貼り付け用パッチ文面」を自動生成します。
    手作業でこれらのファイルを1つずつ作るのは手間で間違いも起きやすいため、
    このスクリプトでまとめて用意します。

■ どこに何を作るか（既定値）
    出力先（既定）: デスクトップ\Graft検証サンプル\
        ├ project\                          … Graftに登録する検証用プロジェクト
        │   ├ shiftjis-sample.cs             Shift_JIS(CP932)・CRLF・日本語コメント入り
        │   ├ utf8bom-sample.txt             UTF-8 BOM付き
        │   ├ crlf-sample.txt                UTF-8(BOM無し)・CRLF改行
        │   ├ lf-sample.txt                  UTF-8(BOM無し)・LF改行
        │   ├ no-trailing-newline-sample.txt UTF-8(BOM無し)・末尾に改行なし
        │   └ long-path\...\長いパスのファイル.txt   260文字を超える深いパス
        ├ patches\
        │   ├ 01_正常系_文字コード改行往復.txt   上記の各ファイルを書き換える正常パッチ
        │   └ 02_失敗ケース_存在しないコード.txt  意図的に一致しない（失敗する）パッチ
        └ このフォルダについて.txt           このフォルダの説明（手順書への導線）

    -OutputPath で生成先を変更できます。

■ 使い方
    生成:   powershell -File .\New-WindowsTestSample.ps1
            powershell -File .\New-WindowsTestSample.ps1 -OutputPath 'D:\GraftTest'
    後片付け（生成物を削除）:
            powershell -File .\New-WindowsTestSample.ps1 -Clean
            powershell -File .\New-WindowsTestSample.ps1 -Clean -Force   (確認なしで削除)

■ 重要: このスクリプトは「動作確認済み」ではありません
    開発・レビューはLinux環境で行っており、実際にWindows上のPowerShellで
    実行して検証したことは一度もありません。文字コード変換やパス処理の
    細部で意図どおりに動かない可能性があります。実行前に中身を読み、
    「何をするスクリプトか」を理解してから使ってください。
    もし途中でエラーが出た場合は、そのエラーメッセージを報告してもらえると
    修正の助けになります。

■ PowerShell のバージョンについて
    Windows PowerShell 5.1（.NET Framework）と PowerShell 7 系（.NET / pwsh）の
    どちらでも動くことを狙って書いています。具体的には次の点に配慮しています。
      - 文字コード変換は .NET の CodePagesEncodingProvider に頼らず、
        Shift_JISのバイト列をあらかじめ計算済みの値として埋め込んでいます
        （5.1と7とでコードページ対応状況が異なるため、実行時変換に頼らない
        方が確実だと判断しました）。
      - 長いパスの作成・削除は \\?\ プレフィックス付きの絶対パスを
        [System.IO.Directory] / [System.IO.File] へ直接渡す方式にしています
        （Graft本体の Core/LongPath.cs と同じ考え方です）。PowerShellの
        Provider経由のコマンドレット（New-Item 等）は260文字超のパスで
        バージョンによって挙動が割れるため使っていません。
      - 改行コードは `n` / `r`n` のエスケープシーケンスで明示的に組み立てて
        おり、このスクリプトファイル自体の改行コード（gitの改行変換設定に
        よってはLFがCRLFに化けることがあります）に影響されないようにして
        あります。
    それでも実機でしか確認できない差異が残っている可能性があります。

■ Windowsの「長いパスを有効にする」設定について
    Windows 10/11では既定でMAX_PATH（260文字）超のパスがOSレベルで
    ブロックされることがあります（レジストリ
    HKLM\SYSTEM\CurrentControlSet\Control\FileSystem の LongPathsEnabled が
    0の場合）。このスクリプトは \\?\ プレフィックスを使うため、この設定の
    値に関わらずフォルダ・ファイル自体は作成できるはずです（\\?\ 付きの
    パスはこの設定と関係なく昔から長いパスに対応しています）。ただし
    Graft側やエクスプローラ等、他のアプリがこのファイルを開けるかどうかは
    この設定の影響を受ける場合があります。うまくいかない場合は手順書の
    説明を参照してください。
================================================================================
#>

[CmdletBinding()]
param(
    # 生成先フォルダ（既定: デスクトップ\Graft検証サンプル）。
    [string]$OutputPath = (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Graft検証サンプル'),

    # 指定すると生成はせず、$OutputPath を丸ごと削除して終了する（後片付け）。
    [switch]$Clean,

    # -Clean と併用時、削除前の確認プロンプトを省略する。
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

# --------------------------------------------------------------------------
# 事前チェック: Windows以外での実行を止める
# --------------------------------------------------------------------------
$isWindowsHost = $true
if ($PSVersionTable.PSVersion.Major -ge 6) {
    $isWindowsHost = $IsWindows
}
if (-not $isWindowsHost) {
    Write-Warning 'このスクリプトはWindows専用です（\\?\ プレフィックスやShift_JISなど、Windows固有の前提で作られています）。Windows上のPowerShellで実行してください。'
    exit 1
}

Write-Host "PowerShell: $($PSVersionTable.PSVersion) / $($PSVersionTable.PSEdition)"

# 生成先を絶対パスへ正規化する（相対パス指定・末尾の \ 有無を吸収する）。
$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)

# --------------------------------------------------------------------------
# 長いパス対応ヘルパー: \\?\ プレフィックスを付けて .NET の I/O API を直接叩く。
# Graft本体の Core/LongPath.cs と同じ考え方（PowerShellのProvider経由だと
# バージョンによって260文字超のパスの扱いが割れるため、あえて素通しする）。
# --------------------------------------------------------------------------
function Get-ExtendedPath {
    param([Parameter(Mandatory)][string]$Path)
    if ($Path.StartsWith('\\?\')) { return $Path }
    if ($Path.StartsWith('\\')) { return '\\?\UNC\' + $Path.Substring(2) }
    return '\\?\' + $Path
}

function New-DirectoryExtended {
    param([Parameter(Mandatory)][string]$Path)
    [System.IO.Directory]::CreateDirectory((Get-ExtendedPath $Path)) | Out-Null
}

function Write-BytesExtended {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][byte[]]$Bytes
    )
    $dir = Split-Path -Path $Path -Parent
    if ($dir) { New-DirectoryExtended $dir }
    [System.IO.File]::WriteAllBytes((Get-ExtendedPath $Path), $Bytes)
}

function Write-TextExtended {
    # BOM無しUTF-8で書き出す（[System.Text.Encoding]::UTF8.GetBytesはBOMを付与しない）。
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Text
    )
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Text)
    Write-BytesExtended -Path $Path -Bytes $bytes
}

function Remove-TreeExtended {
    param([Parameter(Mandatory)][string]$Path)
    $ext = Get-ExtendedPath $Path
    if ([System.IO.Directory]::Exists($ext)) {
        [System.IO.Directory]::Delete($ext, $true)
        return $true
    }
    if ([System.IO.File]::Exists($ext)) {
        [System.IO.File]::Delete($ext)
        return $true
    }
    return $false
}

# --------------------------------------------------------------------------
# 後片付けモード
# --------------------------------------------------------------------------
if ($Clean) {
    if (-not $Force) {
        $answer = Read-Host "次のフォルダを削除します。よろしいですか？`n  $OutputPath`n[y/N]"
        if ($answer -notmatch '^[yY]') {
            Write-Host '中止しました。何も削除していません。'
            exit 0
        }
    }

    $removed = Remove-TreeExtended -Path $OutputPath
    if ($removed) {
        Write-Host "削除しました: $OutputPath"
    } else {
        Write-Host "対象が見つかりませんでした（既に削除済みの可能性があります）: $OutputPath"
    }
    exit 0
}

# --------------------------------------------------------------------------
# 生成モード
# --------------------------------------------------------------------------
Write-Host "生成先: $OutputPath"
$projectRoot = Join-Path $OutputPath 'project'
$patchesRoot = Join-Path $OutputPath 'patches'
New-DirectoryExtended $projectRoot
New-DirectoryExtended $patchesRoot

# --------------------------------------------------------------------------
# 1. Shift_JIS (CP932) ファイル。日本語コメント入り。
#
# 実行時にエンコーディング変換を行うと、PowerShell 5.1 と 7 とで
# コードページ932の扱いが異なる（7はSystem.Text.Encoding.CodePages
# プロバイダの登録が必要で、既定では使えないことがある）ため、
# あらかじめ確定させたバイト列をBase64で埋め込み、そのまま書き出す方式に
# している。内容は以下のC#風テキスト（実行はしない、文字コード確認専用）。
#
#   // このファイルは Shift_JIS (CP932) で保存されています。
#   // 日本語コメントが文字化けしないか確認するためのサンプルです。
#   namespace GraftTest
#   {
#       public class ShiftJisSample
#       {
#           // 検証用の文字列（このパッチ検証手順で書き換えます）
#           public string Message => "こんにちは、これは検証前のメッセージです。";
#       }
#   }
# --------------------------------------------------------------------------
$shiftJisBase64 = 'Ly8ggrGCzIN0g0CDQ4OLgs0gU2hpZnRfSklTIChDUDkzMikggsWV25G2grOC6oLEgqKC3IK3gUINCi8vIJP6lnuM6oNSg4GDk4NngqqVto6aibuCr4K1gsiCooKpim2URoK3gumCvYLfgsyDVIOTg3aDi4LFgreBQg0KbmFtZXNwYWNlIEdyYWZ0VGVzdA0Kew0KICAgIHB1YmxpYyBjbGFzcyBTaGlmdEppc1NhbXBsZQ0KICAgIHsNCiAgICAgICAgLy8gjJ+P2JdwgsyVto6al/GBaYKxgsyDcINig2CMn4/YjuiPh4LFj5GCq4q3gqaC3IK3gWoNCiAgICAgICAgcHVibGljIHN0cmluZyBNZXNzYWdlID0+ICKCsYLxgsmCv4LNgUGCsYLqgs2Mn4/YkU+CzIOBg2KDWoFbg1eCxYK3gUIiOw0KICAgIH0NCn0NCg=='
$shiftJisPath = Join-Path $projectRoot 'shiftjis-sample.cs'
Write-BytesExtended -Path $shiftJisPath -Bytes ([System.Convert]::FromBase64String($shiftJisBase64))
Write-Host '生成: project\shiftjis-sample.cs (Shift_JIS/CP932, CRLF)'

# --------------------------------------------------------------------------
# 2. UTF-8 BOM付きファイル
# --------------------------------------------------------------------------
$utf8BomText = "UTF-8 BOM 付きファイルの検証用サンプルです。`n" +
               "この行を書き換えるパッチが正しく当たるか確認してください。`n"
$utf8BomBytes = [byte[]](0xEF, 0xBB, 0xBF) + [System.Text.Encoding]::UTF8.GetBytes($utf8BomText)
$utf8BomPath = Join-Path $projectRoot 'utf8bom-sample.txt'
Write-BytesExtended -Path $utf8BomPath -Bytes $utf8BomBytes
Write-Host '生成: project\utf8bom-sample.txt (UTF-8 BOM付き)'

# --------------------------------------------------------------------------
# 3. CRLF改行のファイル
# --------------------------------------------------------------------------
$crlfText = "CRLF改行のファイルです。`r`n" +
            "この行を書き換えるパッチが正しく当たるか確認してください。`r`n"
$crlfPath = Join-Path $projectRoot 'crlf-sample.txt'
Write-TextExtended -Path $crlfPath -Text $crlfText
Write-Host '生成: project\crlf-sample.txt (UTF-8 BOM無し, CRLF)'

# --------------------------------------------------------------------------
# 4. LF改行のファイル
# --------------------------------------------------------------------------
$lfText = "LF改行のファイルです。`n" +
          "この行を書き換えるパッチが正しく当たるか確認してください。`n"
$lfPath = Join-Path $projectRoot 'lf-sample.txt'
Write-TextExtended -Path $lfPath -Text $lfText
Write-Host '生成: project\lf-sample.txt (UTF-8 BOM無し, LF)'

# --------------------------------------------------------------------------
# 5. 末尾に改行が無いファイル
# --------------------------------------------------------------------------
$noTrailingText = "末尾に改行が無いファイルです。`n" +
                  "この行を書き換えるパッチが正しく当たるか確認してください。"
$noTrailingPath = Join-Path $projectRoot 'no-trailing-newline-sample.txt'
Write-TextExtended -Path $noTrailingPath -Text $noTrailingText
Write-Host '生成: project\no-trailing-newline-sample.txt (末尾改行なし)'

# --------------------------------------------------------------------------
# 6. 260文字を超える深いパスのファイル
#
# 説明的な日本語名のフォルダを何重にも作り、素のパス長（\\?\ プレフィックス
# を含めない長さ）が260文字を超えるまで掘り下げる。何階層必要かは
# $OutputPath の長さ（ユーザー名等で変わる）に依存するため、固定回数では
# なく実測しながらループする。
# --------------------------------------------------------------------------
$longPathSegment = '検証用サブフォルダ_260文字制限確認用'
$longPathCurrent = Join-Path $projectRoot 'long-path'
New-DirectoryExtended $longPathCurrent

$longPathDepth = 0
while ($longPathCurrent.Length -le 260) {
    $longPathDepth++
    $longPathCurrent = Join-Path $longPathCurrent ('{0:00}_{1}' -f $longPathDepth, $longPathSegment)
    New-DirectoryExtended $longPathCurrent

    if ($longPathDepth -gt 40) {
        # 想定外の無限ループを防ぐ安全弁（通常はここに到達しない）。
        Write-Warning '長いパスの生成が想定より深くなったため打ち切りました。'
        break
    }
}

$longPathFilePath = Join-Path $longPathCurrent '長いパスのファイル.txt'
$longPathText = "このファイルはWindowsのMAX_PATH（260文字）を超える深さに置かれています。`n" +
                "このファイル自体のパス長: $($longPathFilePath.Length) 文字`n" +
                "Graftでこのファイルを開く・編集する・パッチを適用して保存する、の一連が`n" +
                "できるかを確認してください。`n"
Write-TextExtended -Path $longPathFilePath -Text $longPathText
Write-Host "生成: project\long-path\...\長いパスのファイル.txt (パス長 $($longPathFilePath.Length) 文字)"

# --------------------------------------------------------------------------
# 7. 貼り付け用パッチ文面（正常系）
#
# 上の5ファイル（Shift_JIS / UTF-8 BOM / CRLF / LF / 末尾改行なし）を
# それぞれ書き換えるGraft形式のパッチ。SEARCH/REPLACEの内容は
# TextNormalizer.SplitLines が改行コードの違いを吸収して比較するため、
# このテキストファイル自体の改行コードには依存しない。
# --------------------------------------------------------------------------
$successPatch = @'
<<<< PATCH
summary: 文字コード・改行往復の検証（Shift_JIS / UTF-8 BOM / CRLF / LF / 末尾改行なし）
type: test
>>>>

<<<< FILE: shiftjis-sample.cs
<<<<<<< SEARCH
        public string Message => "こんにちは、これは検証前のメッセージです。";
=======
        public string Message => "こんにちは、これはパッチ適用後のメッセージです。";
>>>>>>> REPLACE

<<<< FILE: utf8bom-sample.txt
<<<<<<< SEARCH
この行を書き換えるパッチが正しく当たるか確認してください。
=======
パッチ適用後の行です。書き換えが正しく反映されました。
>>>>>>> REPLACE

<<<< FILE: crlf-sample.txt
<<<<<<< SEARCH
この行を書き換えるパッチが正しく当たるか確認してください。
=======
パッチ適用後の行です。書き換えが正しく反映されました。
>>>>>>> REPLACE

<<<< FILE: lf-sample.txt
<<<<<<< SEARCH
この行を書き換えるパッチが正しく当たるか確認してください。
=======
パッチ適用後の行です。書き換えが正しく反映されました。
>>>>>>> REPLACE

<<<< FILE: no-trailing-newline-sample.txt
<<<<<<< SEARCH
この行を書き換えるパッチが正しく当たるか確認してください。
=======
パッチ適用後の行です。書き換えが正しく反映されました。
>>>>>>> REPLACE
'@
$successPatchPath = Join-Path $patchesRoot '01_正常系_文字コード改行往復.txt'
Write-TextExtended -Path $successPatchPath -Text ($successPatch + "`n")
Write-Host '生成: patches\01_正常系_文字コード改行往復.txt'

# --------------------------------------------------------------------------
# 8. 貼り付け用パッチ文面（意図的な失敗ケース）
#
# crlf-sample.txt に対して、ファイル中のどこにも存在しないテキストを
# SEARCHに指定する。「一致しない」旨のエラーが分かりやすく表示され、
# アプリが落ちたりファイルが壊れたりしないことを確認するためのもの。
# --------------------------------------------------------------------------
$failurePatch = @'
<<<< PATCH
summary: 失敗ケースの検証（存在しないコードを指すSEARCH）
type: test
>>>>

<<<< FILE: crlf-sample.txt
<<<<<<< SEARCH
この行はサンプルファイルのどこにも存在しません_XYZ123
=======
ここには置き換わらないはずです。SEARCHが一致しないため適用は失敗します。
>>>>>>> REPLACE
'@
$failurePatchPath = Join-Path $patchesRoot '02_失敗ケース_存在しないコード.txt'
Write-TextExtended -Path $failurePatchPath -Text ($failurePatch + "`n")
Write-Host '生成: patches\02_失敗ケース_存在しないコード.txt'

# --------------------------------------------------------------------------
# 9. フォルダ全体の説明ファイル
# --------------------------------------------------------------------------
$readmeText = @"
このフォルダについて
====================

これは docs\Windows実機検証手順.md に沿ってGraftをWindows実機で確認するための、
自動生成された検証用データです（tools\New-WindowsTestSample.ps1 が生成しました）。

- project\  … Graftに「プロジェクトを追加」で登録してください。
- patches\  … 中のテキストファイルの中身を全選択してコピーし、Graftの
              ウィンドウにフォーカスした状態でCtrl+Vで貼り付けてください。

生成日時: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
生成元:   $PSCommandPath

手順の詳細は docs\Windows実機検証手順.md を参照してください。

後片付け（このフォルダごと削除する）:
    powershell -File "$PSCommandPath" -Clean -OutputPath "$OutputPath"
"@
$readmePath = Join-Path $OutputPath 'このフォルダについて.txt'
Write-TextExtended -Path $readmePath -Text $readmeText
Write-Host '生成: このフォルダについて.txt'

# --------------------------------------------------------------------------
# 10. 正常系パッチをクリップボードへコピー（できれば）
#
# Set-Clipboard は実行環境（リモートセッション等）によっては使えないことが
# あるため、失敗しても生成処理自体は成功として扱う（テキストファイルは
# 既に出力済みなので、その場合は手動でコピーしてもらえばよい）。
# --------------------------------------------------------------------------
try {
    Set-Clipboard -Value $successPatch
    Write-Host ''
    Write-Host '正常系パッチ（01_正常系_文字コード改行往復.txt の内容）をクリップボードへコピーしました。'
    Write-Host 'Graftのウィンドウをアクティブにして Ctrl+V を押せば、そのまま貼り付けできます。'
} catch {
    Write-Host ''
    Write-Warning "クリップボードへのコピーに失敗しました（$($_.Exception.Message)）。patches フォルダ内のファイルを開き、内容を手動でコピーしてください。"
}

Write-Host ''
Write-Host '=== 生成完了 ==='
Write-Host "出力先: $OutputPath"
Write-Host '手順の詳細は docs\Windows実機検証手順.md を参照してください。'
Write-Host "後片付け: powershell -File `"$PSCommandPath`" -Clean -OutputPath `"$OutputPath`""
