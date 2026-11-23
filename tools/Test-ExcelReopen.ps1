<#
================================================================================
 Graft Windows実機検証: Excel の保存・閉じる・開き直しの確認スクリプト
================================================================================

■ これは何をするものか
    Graft のリボン書き換え（仕様書_ブックの情報と依頼の観点 16.6「ブックを閉じて開き直す」。段階 F4）は、
    Excel で開いているブックを「保存 → 閉じる → ファイルを書き換える → 同じ Excel で開き直す」の順に扱います。
    その前提のうち、Linux の開発環境では確かめられなかったもの（付録 B の「実機で要確認」）を、Excel の COM を
    直接使って確かめ、結果を表で出力します。表をそのまま貼り付けて返してください。結果しだいで、Graft の
    実装（確認ダイアログの文面・待ち時間・開き直しの手順）を直します。

■ 確かめる項目（表の番号）
    (1) Saved の値: 開いた直後／セルを編集したとき／VBE でモジュールを編集しただけのとき／保存した直後
        （Graft が「未保存の変更あり」の文面を出すかどうかが、この値で決まります。仕様 16.6「Workbook.Saved の限界」）
    (2) イベント: Workbook.Save()・Close(SaveChanges:=False)・Workbooks.Open で、Workbook_Open・Workbook_BeforeClose・
        Workbook_BeforeSave・Workbook_AfterSave・Auto_Open・Auto_Close が動くか
        （確認ダイアログの「ブックのマクロが動きます」の文面。仕様 16.12）。BeforeClose が Cancel=True にしたときの
        Close の振る舞い（Graft の E820）も確かめます。
    (3) 閉じて開き直したときの見た目: アクティブなシート・ウィンドウの位置と大きさ・最大化・
        「ほかのブックがアクティブだったとき」の前面のブック
    (4) 開き直しで保護ビューや「マクロを有効にする」のバーが出るか（インターネットから取得した印〔Zone.Identifier〕を
        付けた写しでも試します）。画面を見て答えていただく項目です。
    (5) 閉じた直後に、ブックのファイルを排他で開けるまでの時間（仕様 16.5 段階 7 の待ち時間 10 秒が足りるか。実機検証 12）。
        あわせて、ROT（実行中オブジェクト表）からブックが消えるまでの時間
    (6) ブックが 1 つだけのときに閉じても Excel が終了しないか、閉じたあと同じ Excel で開き直せるか

■ 安全について（重要）
    ・あなたのブックは書き換えません。指定したブックを %TEMP% の作業フォルダへ写し、写しだけを使います。
      終わると作業フォルダごと削除します（-KeepCopy を付けると残します）。
    ・このスクリプトは、あなたが普段使っている Excel とは別の、新しい Excel を起動して使います
      （あなたが開いている Excel のブックは閉じません）。起動した Excel は最後に終了します。
    ・テスト用の Excel は DisplayAlerts を False にします（ダイアログで止まらないように）。Graft は DisplayAlerts を
      触りません。そのため、Graft では「保存の確認」などのダイアログが出て止まる場合でも、このスクリプトは止まらずに
      進みます。ダイアログが出たかどうかは、(4) の目視の確認で補います。

■ 事前の準備（(1) の VBE の項目と (2) のイベントの項目に必要）
    Excel の［ファイル］→［オプション］→［トラスト センター］→［トラスト センターの設定］→［マクロの設定］で
    「VBA プロジェクト オブジェクト モデルへのアクセスを信頼する」にチェックを入れてください（Graft の VBA 連携で
    すでに入れていれば、そのままで構いません）。入っていないと、(1) の VBE の項目と (2) は「未実施」と表示されます。
    確認が終わったら、元の設定へ戻して構いません。

■ 使い方（PowerShell 5.1 で動きます。管理者権限は要りません）
    powershell -ExecutionPolicy Bypass -File .\tools\Test-ExcelReopen.ps1 -WorkbookPath 'C:\Work\計画.xlsm'

    主なオプション:
        -WorkbookPath <パス>   確認に使うブック（.xlsx／.xlsm／.xlsb）。必須。複数のシートがあると (3) がより確かです。
        -NoPrompt              画面を見ての質問（(4)）を省き、「未回答」と記録します。
        -KeepCopy              作業フォルダ（写し・イベントの記録）を削除せず残します。
        -OutFile <パス>        結果の表を保存するファイル（既定: %TEMP%\graft-excel-reopen-result.md）

■ 結果の返し方
    最後に出る「ここから」と「ここまで」のあいだの表（Markdown）を、そのまま貼り付けて返してください。
    同じ内容を、クリップボードにもコピーします（コピーできない環境では、ファイルを開いてください）。

■ 重要: このスクリプトは「動作確認済み」ではありません
    開発・レビューは Linux 環境で行っており、Windows の PowerShell で実行して検証したことはありません。
    実行前に中身を読み、何をするか理解してから使ってください。エラーが出たら、そのメッセージを報告してください。
================================================================================
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$WorkbookPath,

    [switch]$NoPrompt,

    [switch]$KeepCopy,

    [string]$OutFile = (Join-Path $env:TEMP 'graft-excel-reopen-result.md')
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

# ------------------------------------------------------------------
# 結果の記録
# ------------------------------------------------------------------
$script:Results = New-Object System.Collections.ArrayList

function Add-Result {
    param([string]$No, [string]$Item, [string]$Result, [string]$Note = '')
    [void]$script:Results.Add([pscustomobject]@{ No = $No; Item = $Item; Result = $Result; Note = $Note })
    Write-Host ("  [{0}] {1} => {2} {3}" -f $No, $Item, $Result, $Note)
}

function Release-Com {
    param($Obj)
    if ($null -ne $Obj) {
        try { [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($Obj) } catch { }
    }
}

# 例外のときも、表に「例外」と型を残して先へ進む（1 項目の失敗で全体を止めない）。
function Invoke-Step {
    param([string]$No, [string]$Item, [scriptblock]$Body)
    try {
        & $Body
    }
    catch {
        $e = $_.Exception
        $hr = ''
        if ($e -is [System.Runtime.InteropServices.COMException]) { $hr = (' HRESULT=0x{0:X8}' -f $e.ErrorCode) }
        Add-Result $No $Item '例外' ($e.GetType().Name + $hr + ': ' + ($e.Message -replace '\s+', ' '))
    }
}

# ------------------------------------------------------------------
# 準備: 写しを作る
# ------------------------------------------------------------------
if (-not (Test-Path -LiteralPath $WorkbookPath -PathType Leaf)) {
    throw "ブックが見つかりません: $WorkbookPath"
}

$source = (Resolve-Path -LiteralPath $WorkbookPath).Path
$ext = [System.IO.Path]::GetExtension($source).ToLowerInvariant()
if ($ext -notin @('.xlsx', '.xlsm', '.xlsb')) {
    throw "対象は .xlsx／.xlsm／.xlsb です（.xls・.xlam は対象外）: $source"
}

$work = Join-Path $env:TEMP ('graft-excel-reopen-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $work | Out-Null
$copy = Join-Path $work ('copy' + $ext)
Copy-Item -LiteralPath $source -Destination $copy
$eventLog = Join-Path $work 'events.log'
$cancelFlag = Join-Path $work 'cancel-close.flag'

Write-Host ''
Write-Host '== Graft: Excel の保存・閉じる・開き直しの確認 =='
Write-Host ("  写し: {0}" -f $copy)
Write-Host '  （あなたのブックは書き換えません）'

$excel = $null
$wbs = $null
$header = @()
$prepared = $false
try {
    $excel = New-Object -ComObject Excel.Application
    $excel.Visible = $true
    $excel.DisplayAlerts = $false
    $wbs = $excel.Workbooks

    $header = @(
        ('Excel のバージョン: {0}（ビルド {1}）' -f $excel.Version, $excel.Build),
        ('Excel のビット数: {0}' -f $(if ($excel.OperatingSystem -match '64') { '64bit（OS 表記: ' + $excel.OperatingSystem + '）' } else { '32bit の可能性（OS 表記: ' + $excel.OperatingSystem + '）' })),
        ('AutomationSecurity: {0}（1=マクロを有効／2=無効／3=セキュリティ設定に従う）' -f $excel.AutomationSecurity),
        ('PowerShell: {0}' -f $PSVersionTable.PSVersion),
        ('OS: {0}' -f [System.Environment]::OSVersion.VersionString),
        ('ブック: 拡張子 {0}・{1:N0} バイト' -f $ext, (Get-Item -LiteralPath $copy).Length),
        ('実施日時: {0}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
    )

    # 共通の補助
    function Test-InRot {
        param([string]$Path)
        try {
            $o = [System.Runtime.InteropServices.Marshal]::BindToMoniker($Path)
            Release-Com $o
            return $true
        }
        catch { return $false }
    }

    function Measure-ExclusiveOpen {
        param([string]$Path, [int]$LimitMs = 30000)
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        while ($true) {
            try {
                $fs = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::None)
                $fs.Close()
                return [int]$sw.ElapsedMilliseconds
            }
            catch [System.IO.IOException] {
                if ($sw.ElapsedMilliseconds -gt $LimitMs) { return -1 }
                Start-Sleep -Milliseconds 20
            }
        }
    }

    function Measure-RotGone {
        param([string]$Path, [int]$LimitMs = 10000)
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        while (Test-InRot $Path) {
            if ($sw.ElapsedMilliseconds -gt $LimitMs) { return -1 }
            Start-Sleep -Milliseconds 20
        }
        return [int]$sw.ElapsedMilliseconds
    }

    # ==============================================================
    # (1) Saved の値
    # ==============================================================
    Write-Host ''
    Write-Host '(1) Workbook.Saved の値を調べています...'
    Invoke-Step '1' 'Saved の値' {
        $wb = $wbs.Open($copy)
        $ws = $null
        $vbp = $null
        try {
            Add-Result '1-a' '開いた直後の Saved' ([string]$wb.Saved) 'True なら未保存なし。False なら NOW()・RAND() などの再計算の可能性（Graft は「再計算だけでも」と文面に添える）'
            $wb.Save()
            Add-Result '1-b' 'Save() 直後の Saved' ([string]$wb.Saved) 'True のはず'

            $ws = $wb.Worksheets.Item(1)
            $ws.Range('XFD1048576').Value2 = 'graft-probe'
            Add-Result '1-c' 'セルを編集した直後の Saved' ([string]$wb.Saved) 'False のはず'
            $ws.Range('XFD1048576').ClearContents() | Out-Null
            $wb.Save()

            try {
                $vbp = $wb.VBProject
                [void]$vbp.VBComponents.Count
                $canVbe = $true
            }
            catch {
                $canVbe = $false
                Add-Result '1-d' 'VBE でモジュールを編集しただけのときの Saved' '未実施' 'VBA プロジェクトへのアクセスが許可されていません（上の「事前の準備」を参照）'
            }

            if ($canVbe) {
                if ($ext -eq '.xlsx') {
                    Add-Result '1-d' 'VBE でモジュールを編集しただけのときの Saved' '未実施' '.xlsx は VBA を持てません（.xlsm／.xlsb のブックで試してください）'
                }
                else {
                    $before = [string]$wb.Saved
                    $mod = $vbp.VBComponents.Add(1)
                    $mod.Name = 'GraftProbeSaved'
                    $afterAdd = [string]$wb.Saved
                    $wb.Save()
                    $mod.CodeModule.AddFromString("Public Sub GraftProbeSaved()`r`nEnd Sub")
                    $afterEdit = [string]$wb.Saved
                    Add-Result '1-d' 'VBE でモジュールを編集しただけのときの Saved' $afterEdit ("新しいモジュールを足した直後: {0}／保存後にコードを書き足した直後: {1}（保存前: {2}）。False なら、VBE の編集が Saved を False にする" -f $afterAdd, $afterEdit, $before)
                    $wb.Save()
                    Release-Com $mod
                }
            }
        }
        finally {
            Release-Com $vbp
            Release-Com $ws
            $wb.Close($false)
            Release-Com $wb
        }
    }

    # ==============================================================
    # (2) イベント
    # ==============================================================
    Write-Host ''
    Write-Host '(2) ブックのイベントが動くかを調べています...'

    $eventBook = Join-Path $work 'events.xlsm'
    Invoke-Step '2' 'イベント用の写しの準備' {
        $wb = $wbs.Open($copy)
        $vbp = $null
        try {
            try {
                $vbp = $wb.VBProject
                [void]$vbp.VBComponents.Count
            }
            catch {
                Add-Result '2-0' 'イベント用の VBA の追加' '未実施' 'VBA プロジェクトへのアクセスが許可されていません（上の「事前の準備」を参照）'
                return
            }

            # 既存のブックにイベントがあると重複で壊れるので、ThisWorkbook に既にコードがあるときは、空のブックで確かめる。
            $thisComp = $vbp.VBComponents.Item($wb.CodeName)
            $hasCode = $thisComp.CodeModule.CountOfLines -gt 0 -and (($thisComp.CodeModule.Lines(1, $thisComp.CodeModule.CountOfLines)) -match 'Sub\s+Workbook_')
            Release-Com $thisComp
            $vbp = $null
            $wb.Close($false)
            Release-Com $wb
            $wb = $null

            if ($hasCode) {
                $note = '写しの ThisWorkbook に既にイベントがあるため、空のブックで確かめました'
                $wb = $wbs.Add()
            }
            else {
                $note = '写しで確かめました'
                $wb = $wbs.Open($copy)
            }

            # 保存形式を .xlsm にして保存（.xlsx でも VBA を持てるように）。
            $wb.SaveAs($eventBook, 52)
            $vbp = $wb.VBProject

            $logLiteral = $eventLog.Replace('"', '""')
            $flagLiteral = $cancelFlag.Replace('"', '""')
            $moduleCode = @"
Option Explicit
Public Sub LogEv(ByVal s As String)
    Dim f As Integer
    f = FreeFile
    Open "$logLiteral" For Append As #f
    Print #f, s
    Close #f
End Sub
Public Sub Auto_Open()
    LogEv "Auto_Open"
End Sub
Public Sub Auto_Close()
    LogEv "Auto_Close"
End Sub
"@
            $thisCode = @"
Option Explicit
Private Sub Workbook_Open()
    GraftProbe.LogEv "Workbook_Open"
End Sub
Private Sub Workbook_BeforeSave(ByVal SaveAsUI As Boolean, Cancel As Boolean)
    GraftProbe.LogEv "Workbook_BeforeSave"
End Sub
Private Sub Workbook_AfterSave(ByVal Success As Boolean)
    GraftProbe.LogEv "Workbook_AfterSave"
End Sub
Private Sub Workbook_BeforeClose(Cancel As Boolean)
    GraftProbe.LogEv "Workbook_BeforeClose"
    If Dir("$flagLiteral") <> "" Then
        Cancel = True
        GraftProbe.LogEv "Workbook_BeforeClose:Cancel=True"
    End If
End Sub
"@
            $mod = $vbp.VBComponents.Add(1)
            $mod.Name = 'GraftProbe'
            $mod.CodeModule.AddFromString($moduleCode)
            $thisComp = $vbp.VBComponents.Item($wb.CodeName)
            $existing = $thisComp.CodeModule.CountOfLines
            if ($existing -gt 0) { $thisComp.CodeModule.DeleteLines(1, $existing) }
            $thisComp.CodeModule.AddFromString($thisCode)
            Release-Com $mod
            Release-Com $thisComp
            Release-Com $vbp
            $vbp = $null
            $wb.Save()
            $wb.Close($false)
            Release-Com $wb
            $wb = $null
            if (Test-Path -LiteralPath $eventLog) { Remove-Item -LiteralPath $eventLog -Force }
            $script:prepared = $true
            Add-Result '2-0' 'イベント用の VBA の追加' '完了' $note
        }
        finally {
            if ($null -ne $vbp) { Release-Com $vbp }
            if ($null -ne $wb) { try { $wb.Close($false) } catch { }; Release-Com $wb }
        }
    }

    function Read-EventLog {
        if (Test-Path -LiteralPath $eventLog) {
            $lines = @(Get-Content -LiteralPath $eventLog)
            Remove-Item -LiteralPath $eventLog -Force
            return $lines
        }
        return @()
    }

    function Add-EventResult {
        param([string]$No, [string]$Operation, [string[]]$Expected)
        $lines = @(Read-EventLog)
        $ran = if ($lines.Count -eq 0) { '（なし）' } else { ($lines -join ' → ') }
        $summary = @()
        foreach ($name in $Expected) {
            $summary += ('{0}: {1}' -f $name, $(if ($lines -contains $name) { '動いた' } else { '動かなかった' }))
        }
        Add-Result $No $Operation ($summary -join ' ／ ') ('記録された順: ' + $ran)
    }

    if ($prepared) {
        Invoke-Step '2' 'イベントの確認' {
            # Workbooks.Open（Graft の「開き直す」）
            $wb = $wbs.Open($eventBook)
            Add-EventResult '2-a' 'Workbooks.Open（Graft の開き直し）' @('Workbook_Open', 'Auto_Open')

            # Workbook.Save()（Graft の「保存する」）
            $wb.Save()
            Add-EventResult '2-b' 'Workbook.Save()（Graft の保存）' @('Workbook_BeforeSave', 'Workbook_AfterSave')

            # Close(SaveChanges:=False)（Graft の「閉じる」）
            $wb.Close($false)
            Release-Com $wb
            Add-EventResult '2-c' 'Close(SaveChanges:=False)（Graft の閉じる）' @('Workbook_BeforeClose', 'Auto_Close')

            # BeforeClose が Cancel = True にしたとき（Graft は E820 で止まる）
            $wb = $wbs.Open($eventBook)
            [void](Read-EventLog)
            Set-Content -LiteralPath $cancelFlag -Value 'x'
            $closeError = ''
            try { $wb.Close($false) } catch { $closeError = $_.Exception.GetType().Name + ': ' + ($_.Exception.Message -replace '\s+', ' ') }
            $stillOpen = $false
            foreach ($b in @($excel.Workbooks)) { if ($b.FullName -eq $eventBook) { $stillOpen = $true } }
            Remove-Item -LiteralPath $cancelFlag -Force
            $lines = @(Read-EventLog)
            Add-Result '2-d' 'BeforeClose が Cancel = True にしたときの Close' ('例外: ' + $(if ($closeError) { $closeError } else { 'なし' }) + ' ／ 閉じたあとも開いている: ' + $stillOpen) ('記録: ' + ($lines -join ' → ') + '。「開いている」なら Graft の「閉じたことの確認」で E820 になる')
            Release-Com $wb
            if ($stillOpen) {
                $wb2 = $wbs.Open($eventBook)
                $wb2.Close($false)
                Release-Com $wb2
            }
            [void](Read-EventLog)
        }
    }
    else {
        Add-Result '2-a' 'イベントの確認' '未実施' 'VBA の追加ができなかったため'
    }

    # ==============================================================
    # (3) 見た目（アクティブなシート・ウィンドウ・前面のブック）
    # ==============================================================
    Write-Host ''
    Write-Host '(3) 閉じて開き直したときの見た目を調べています...'
    Invoke-Step '3' '見た目' {
        $viewBook = Join-Path $work ('view' + $ext)
        Copy-Item -LiteralPath $copy -Destination $viewBook
        $wb = $wbs.Open($viewBook)
        $other = $null
        try {
            if ($wb.Worksheets.Count -lt 2) {
                [void]$wb.Worksheets.Add([System.Reflection.Missing]::Value, $wb.Worksheets.Item($wb.Worksheets.Count))
            }

            $sheet = $wb.Worksheets.Item(2)
            $sheet.Activate()
            $sheetName = [string]$sheet.Name
            $win = $wb.Windows.Item(1)
            $win.WindowState = -4143
            $win.Left = 60; $win.Top = 60; $win.Width = 700; $win.Height = 450
            $state = $win.WindowState
            $pos = @($win.Left, $win.Top, $win.Width, $win.Height)
            Release-Com $win
            Release-Com $sheet
            $wb.Save()
            $wb.Close($false)
            Release-Com $wb

            $wb = $wbs.Open($viewBook)
            $win = $wb.Windows.Item(1)
            $after = @($win.Left, $win.Top, $win.Width, $win.Height)
            $activeAfter = [string]$wb.ActiveSheet.Name
            Add-Result '3-a' 'アクティブなシート（閉じる前 → 開き直し直後）' ($sheetName + ' → ' + $activeAfter) $(if ($sheetName -eq $activeAfter) { 'Excel が保存時の状態を覚えているので、そのまま戻る' } else { '戻らない。Graft が控えたシート名で Activate する（仕様 16.6）' })
            Add-Result '3-b' 'ウィンドウ「通常」の位置と大きさ（閉じる前 → 開き直し直後）' (('{0} → {1}' -f ($pos -join ','), ($after -join ','))) $(if (($pos -join ',') -eq ($after -join ',')) { 'そのまま戻る' } else { '戻らない。Graft が Left/Top/Width/Height を書き戻す' })

            # 最大化
            $win.WindowState = -4137
            $wb.Save()
            $wb.Close($false)
            Release-Com $win
            Release-Com $wb
            $wb = $wbs.Open($viewBook)
            $win = $wb.Windows.Item(1)
            Add-Result '3-c' '最大化で保存したあとの WindowState（開き直し直後）' ([string]$win.WindowState) '-4137=最大化／-4143=通常。最大化のときは位置を書かず WindowState だけ戻す'
            $win.WindowState = -4143
            Release-Com $win

            # ほかのブックがアクティブだったとき
            $other = $wbs.Add()
            $otherName = [string]$other.Name
            $other.Activate()
            $wb.Close($false)
            Release-Com $wb
            $wb = $wbs.Open($viewBook)
            $activeBook = [string]$excel.ActiveWorkbook.Name
            Add-Result '3-d' '別のブックがアクティブのとき閉じて開き直したあとの、アクティブなブック' ('「' + $activeBook + '」（別のブック: 「' + $otherName + '」）') '開き直したブックが前面になる。Graft は、元々アクティブでなかったときだけ前のブックを前面へ戻す'
            try { $other.Close($false) } catch { }
        }
        finally {
            if ($null -ne $other) { Release-Com $other }
            if ($null -ne $wb) { try { $wb.Close($false) } catch { }; Release-Com $wb }
        }
    }

    # ==============================================================
    # (5) 排他で開けるまで・ROT から消えるまで / (6) Excel が終了しないか
    # ==============================================================
    Write-Host ''
    Write-Host '(5)(6) 閉じた直後のファイルの状態を調べています（5 回）...'
    Invoke-Step '5' '排他で開けるまで' {
        $exclusiveTimes = @()
        $rotTimes = @()
        $stayedAlive = $true
        for ($i = 1; $i -le 5; $i++) {
            $wb = $wbs.Open($copy)
            $wb.Save()
            $wb.Close($false)
            Release-Com $wb
            $exclusiveTimes += (Measure-ExclusiveOpen $copy)
            $rotTimes += (Measure-RotGone $copy)
            if ($i -eq 1) {
                # ほかにブックが無い（最後の 1 つを閉じた）あとも Excel が生きているか。
                try { $stayedAlive = ($excel.Workbooks.Count -eq 0) -and ([string]$excel.Name -ne '') } catch { $stayedAlive = $false }
            }
        }

        $ex = ($exclusiveTimes | ForEach-Object { if ($_ -lt 0) { '30 秒でも開けず' } else { '{0} ms' -f $_ } }) -join ', '
        $rt = ($rotTimes | ForEach-Object { if ($_ -lt 0) { '10 秒でも残った' } else { '{0} ms' -f $_ } }) -join ', '
        Add-Result '5-a' '閉じてから排他（FileShare.None）で開けるまで（5 回）' $ex 'Graft の待ち時間は 10 秒（200ms 間隔）。ここが 10 秒を超えるなら VbaRibbonRewriteTimings を延ばす'
        Add-Result '5-b' '閉じてから ROT（実行中オブジェクト表）に載らなくなるまで（5 回）' $rt '0 ms に近ければ、閉じたことの確認に待ちは要らない'
        Add-Result '6-a' 'ブックが 1 つだけのとき、閉じても Excel は終了しないか' $(if ($stayedAlive) { '終了しない（Workbooks.Count=0 のまま応答）' } else { '終了した／応答しない' }) 'Graft は同じ Application でそのまま開き直す'

        # 同じ Application で開き直せるか
        $wb = $wbs.Open($copy)
        Add-Result '6-b' '全部閉じたあと、同じ Application で Workbooks.Open できるか' $(if ($null -ne $wb) { '開けた' } else { '開けなかった' }) ''
        $wb.Close($false)
        Release-Com $wb
    }

    # ==============================================================
    # (4) 保護ビュー・「マクロを有効にする」のバー
    # ==============================================================
    Write-Host ''
    Write-Host '(4) 保護ビューとマクロのバーを調べています...'
    Invoke-Step '4' '保護ビュー・マクロのバー' {
        # 4-a: 通常の写し（インターネットの印なし）を開き直す。
        $wb = $wbs.Open($copy)
        $pv = $excel.ProtectedViewWindows.Count
        Add-Result '4-a' 'ふつうの写しを Workbooks.Open したときの保護ビューのウィンドウ数' ([string]$pv) ('0 なら保護ビューで開かない。Workbooks に載る: ' + $(if ($excel.Workbooks.Count -ge 1) { 'はい' } else { 'いいえ' }))
        if (-not $NoPrompt) {
            Write-Host ''
            Write-Host '  >>> Excel の画面を見てください（いま開いているのは写しのブックです）。'
            $answer = Read-Host '  黄色い「保護ビュー」のバー、または「セキュリティの警告 マクロが無効にされました」のバーが出ていますか？ 出ていない／保護ビュー／マクロ無効のバー のどれかを入力'
            Add-Result '4-b' '【目視】ふつうの写しを開き直したときのバー' ($(if ($answer) { $answer } else { '未回答' })) ''
        }
        else {
            Add-Result '4-b' '【目視】ふつうの写しを開き直したときのバー' '未回答' '-NoPrompt のため'
        }

        $wb.Close($false)
        Release-Com $wb

        # 4-c: インターネットから取得した印（Zone.Identifier）を付けた写し。
        $motw = Join-Path $work ('motw' + $ext)
        Copy-Item -LiteralPath $copy -Destination $motw
        $motwOk = $true
        try {
            Set-Content -LiteralPath $motw -Stream 'Zone.Identifier' -Value "[ZoneTransfer]`r`nZoneId=3"
        }
        catch {
            $motwOk = $false
            Add-Result '4-c' 'インターネットの印つきの写しを開く' '未実施' 'Zone.Identifier を付けられませんでした（NTFS 以外の可能性）'
        }

        if ($motwOk) {
            $eventAvail = $prepared
            if ($eventAvail) {
                Copy-Item -LiteralPath $eventBook -Destination (Join-Path $work 'motw-events.xlsm')
                $motw = Join-Path $work 'motw-events.xlsm'
                Set-Content -LiteralPath $motw -Stream 'Zone.Identifier' -Value "[ZoneTransfer]`r`nZoneId=3"
                if (Test-Path -LiteralPath $eventLog) { Remove-Item -LiteralPath $eventLog -Force }
            }

            $wb = $null
            $openError = ''
            try { $wb = $wbs.Open($motw) } catch { $openError = $_.Exception.GetType().Name + ': ' + ($_.Exception.Message -replace '\s+', ' ') }
            $pv = $excel.ProtectedViewWindows.Count
            $macroRan = ''
            if ($eventAvail) { $macroRan = if (@(Read-EventLog) -contains 'Workbook_Open') { '／Workbook_Open は動いた' } else { '／Workbook_Open は動かなかった（マクロが無効の可能性）' } }
            Add-Result '4-c' 'インターネットの印つきの写しを Workbooks.Open' ('例外: ' + $(if ($openError) { $openError } else { 'なし' }) + ' ／ Workbook を受け取れたか: ' + ($null -ne $wb) + ' ／ 保護ビューのウィンドウ数: ' + $pv + $macroRan) '保護ビューで開くなら、Graft の開き直しは E822（時間切れ）か、Workbook を受け取れない失敗になる'
            if (-not $NoPrompt) {
                Write-Host ''
                Write-Host '  >>> Excel の画面を見てください（いま開いているのは「インターネットの印つき」の写しです）。'
                $answer = Read-Host '  出ているバーは？ 出ていない／保護ビュー／マクロ無効のバー／そのほか（内容） のどれかを入力'
                Add-Result '4-d' '【目視】インターネットの印つきの写しを開いたときのバー' ($(if ($answer) { $answer } else { '未回答' })) ''
            }
            else {
                Add-Result '4-d' '【目視】インターネットの印つきの写しを開いたときのバー' '未回答' '-NoPrompt のため'
            }

            if ($null -ne $wb) { try { $wb.Close($false) } catch { }; Release-Com $wb }
            foreach ($w in @($excel.ProtectedViewWindows)) { try { $w.Close() } catch { } }
        }
    }
}
finally {
    # 後始末: テスト用の Excel を終了し、COM の参照を放し、作業フォルダを消す。
    if ($null -ne $excel) {
        try { foreach ($b in @($excel.Workbooks)) { try { $b.Close($false) } catch { } } } catch { }
        try { $excel.Quit() } catch { }
    }

    Release-Com $wbs
    Release-Com $excel
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()

    if (-not $KeepCopy) {
        try { Remove-Item -LiteralPath $work -Recurse -Force } catch { Write-Host ('作業フォルダを消せませんでした（手で消してください）: ' + $work) }
    }
    else {
        Write-Host ('作業フォルダを残しました: ' + $work)
    }
}

# ------------------------------------------------------------------
# 結果の表（Markdown）
# ------------------------------------------------------------------
$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('### Excel の保存・閉じる・開き直しの確認の結果')
[void]$sb.AppendLine('')
foreach ($line in $header) { [void]$sb.AppendLine('- ' + $line) }
[void]$sb.AppendLine('')
[void]$sb.AppendLine('| 番号 | 項目 | 結果 | 補足 |')
[void]$sb.AppendLine('|---|---|---|---|')
foreach ($r in $script:Results) {
    $cells = @($r.No, $r.Item, $r.Result, $r.Note) | ForEach-Object { ([string]$_).Replace('|', '／').Replace("`r", ' ').Replace("`n", ' ') }
    [void]$sb.AppendLine('| ' + ($cells -join ' | ') + ' |')
}

$table = $sb.ToString()
try { [System.IO.File]::WriteAllText($OutFile, $table, (New-Object System.Text.UTF8Encoding($true))) } catch { Write-Host ('結果をファイルへ保存できませんでした: ' + $_.Exception.Message) }
$copied = $false
try { Set-Clipboard -Value $table; $copied = $true } catch { }

Write-Host ''
Write-Host '================ ここから（この表をそのまま貼り付けて返してください）================'
Write-Host $table
Write-Host '================ ここまで ================'
Write-Host ('結果のファイル: ' + $OutFile)
if ($copied) { Write-Host '同じ内容をクリップボードへコピーしました。' }
