<#
.SYNOPSIS
    履歴の「ここまで戻す」を検証するためのサンプル一式を生成する。

.DESCRIPTION
    リビジョンを4世代（r1〜r4）作るためのパッチと、その適用対象ファイルを生成する。

    「ここまで戻す」は、選んだリビジョン以降を新しい順に取り消す操作なので、
    次の2点を確かめられる構成にしてある。

      1. 同じファイルを毎回書き換える（note.txt）
         → 取り消す順序を誤ると内容が壊れるため、逆順処理の正しさを確かめられる。
      2. 一部のリビジョンでだけ書き換わるファイル（memo.txt）
         → r2 と r4 でのみ変わる。r2 まで戻したときに r2 時点の値へ戻るかを確かめられる。

    各リビジョン適用後の期待値は下表のとおり。

      | 適用後 | note.txt | memo.txt |
      |--------|----------|----------|
      | 初期   | 初期     | 初期     |
      | r1     | 1        | 初期     |
      | r2     | 2        | 2        |
      | r3     | 3        | 2        |
      | r4     | 4        | 4        |

    したがって「r2 まで戻す」を実行すると note.txt=2 / memo.txt=2 になるのが正解。

.PARAMETER OutputPath
    生成先。既定はデスクトップの「Graft履歴検証サンプル」。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\New-RevisionTestSample.ps1
#>

[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Graft履歴検証サンプル')
)

$ErrorActionPreference = 'Stop'

# 生成先を絶対パスへ正規化する（相対パス指定・末尾の \ 有無を吸収する）。
$root = [System.IO.Path]::GetFullPath($OutputPath)
$projectRoot = Join-Path $root 'project'
$patchesRoot = Join-Path $root 'patches'

# 既存の生成物があれば作り直す（前回の適用結果が残っていると
# 「適用済みパッチの再投入」ガード（E302）に阻まれ検証にならないため）。
if (Test-Path $root) { Remove-Item -Recurse -Force $root }
New-Item -ItemType Directory -Path $projectRoot -Force | Out-Null
New-Item -ItemType Directory -Path $patchesRoot -Force | Out-Null

Write-Host "生成先: $root"

# --------------------------------------------------------------------------
# 適用対象のファイル
# --------------------------------------------------------------------------
# UTF-8（BOM無し）・LF で書き出す。改行コードの違いは TextNormalizer が
# 吸収するため、パッチ側の改行コードには依存しない。
function Write-Utf8NoBom([string]$Path, [string]$Text) {
    $encoding = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $Text, $encoding)
}

Write-Utf8NoBom (Join-Path $projectRoot 'note.txt') @"
バージョン: 初期
この行は毎回のパッチで書き換わります。
"@
Write-Host '生成: project\note.txt'

Write-Utf8NoBom (Join-Path $projectRoot 'memo.txt') @"
メモ: 初期
この行は r2 と r4 でだけ書き換わります。
"@
Write-Host '生成: project\memo.txt'

# --------------------------------------------------------------------------
# パッチ（4世代）
# --------------------------------------------------------------------------
# summary はリビジョン一覧に表示されるので、どれがどれか一目で分かる文言にする。
# 各パッチは内容が異なるためハッシュも異なり、再投入ガード（E302）には掛からない。

$patch1 = @'
<<<< PATCH
summary: r1 バージョンを1にする
type: feat
>>>>

<<<< FILE: note.txt
<<<<<<< SEARCH
バージョン: 初期
=======
バージョン: 1
>>>>>>> REPLACE
'@

$patch2 = @'
<<<< PATCH
summary: r2 バージョンを2にし、メモも更新する
type: feat
>>>>

<<<< FILE: note.txt
<<<<<<< SEARCH
バージョン: 1
=======
バージョン: 2
>>>>>>> REPLACE

<<<< FILE: memo.txt
<<<<<<< SEARCH
メモ: 初期
=======
メモ: 2
>>>>>>> REPLACE
'@

$patch3 = @'
<<<< PATCH
summary: r3 バージョンを3にする
type: feat
>>>>

<<<< FILE: note.txt
<<<<<<< SEARCH
バージョン: 2
=======
バージョン: 3
>>>>>>> REPLACE
'@

$patch4 = @'
<<<< PATCH
summary: r4 バージョンを4にし、メモも更新する
type: feat
>>>>

<<<< FILE: note.txt
<<<<<<< SEARCH
バージョン: 3
=======
バージョン: 4
>>>>>>> REPLACE

<<<< FILE: memo.txt
<<<<<<< SEARCH
メモ: 2
=======
メモ: 4
>>>>>>> REPLACE
'@

$patches = @(
    @{ Name = '01_r1.txt'; Text = $patch1 },
    @{ Name = '02_r2.txt'; Text = $patch2 },
    @{ Name = '03_r3.txt'; Text = $patch3 },
    @{ Name = '04_r4.txt'; Text = $patch4 }
)
foreach ($p in $patches) {
    Write-Utf8NoBom (Join-Path $patchesRoot $p.Name) ($p.Text + "`n")
    Write-Host ('生成: patches\' + $p.Name)
}

# --------------------------------------------------------------------------
# 現在の内容を確認するための補助スクリプト
# --------------------------------------------------------------------------
# [System.IO.File] に相対パスを渡すと .NET プロセスのカレント（多くの場合
# C:\WINDOWS\system32）を基準に探してしまうため、絶対パスへ直してから渡す。
$checkScript = @'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
foreach ($name in 'note.txt','memo.txt') {
    $path = Join-Path (Join-Path $dir 'project') $name
    if (-not (Test-Path $path)) { "{0,-10} （ファイルがありません）" -f $name; continue }
    $first = ([System.IO.File]::ReadAllLines($path))[0]
    "{0,-10} {1}" -f $name, $first
}
'@
Write-Utf8NoBom (Join-Path $root '現在の内容を確認.ps1') $checkScript
Write-Host '生成: 現在の内容を確認.ps1'

Write-Host ''
Write-Host '--- 使い方 ---'
Write-Host "1. Graft で $projectRoot をプロジェクトとして登録する"
Write-Host '2. patches\01_r1.txt 〜 04_r4.txt の中身を順にコピーし、Graft へ Ctrl+V して「適用」する（4回）'
Write-Host '3. 履歴で r2 を選び「ここまで戻す」を実行する'
Write-Host '4. 次を実行して内容を確認する'
Write-Host "   powershell -ExecutionPolicy Bypass -File `"$(Join-Path $root '現在の内容を確認.ps1')`""
Write-Host ''
Write-Host '   期待される結果: note.txt が「バージョン: 2」、memo.txt が「メモ: 2」'
