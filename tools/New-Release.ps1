<#
.SYNOPSIS
    Graftのリリース用配布物（ZIP）を1コマンドで作成する。

.DESCRIPTION
    1.0.0.0のリリースで手作業で行っていた次の手順を、そのまま自動化したもの。

      1. 前提の確認（gitの作業ツリーがクリーンか、現在のブランチ/コミット、バージョンの決定）
      2. 発行（dotnet publish、-r win-x64、自己完結型）
      3. 同梱物の配置（取扱説明書.md／取扱説明書.pdf／はじめにお読みください.txt）
      4. ZIP作成（リポジトリ直下へ Graft-<バージョン>-win-x64.zip として作成）
      5. 検証（ZIPの中身一覧・Graft.exeのファイルバージョン・必須ファイルの有無）
      6. 次にやること（タグ作成/push・GitHubリリース作成URL・リリース本文下書きの案内）

    発行先は既定で publish\release\Graft。README「発行」節の手順で利用者が普段使う
    publish\Graft とは意図的に別の場所にしてある（このスクリプトは実行前に発行先を
    削除するため、publish\Graft を発行先にしてしまうと settings.json・履歴・バックアップが
    消えてしまう。過去に実際その手順を利用者へ案内してしまい迷惑をかけた経緯があるため、
    既定値はもちろん、-OutputDir に明示的にpublish\Graftを指定した場合も -Force の有無に
    かかわらず中断する）。

.PARAMETER Version
    配布物に使うバージョン。省略時はsrc\Graft\Graft.csprojの<Version>から算出する
    （3点区切りなら末尾に.0を補い4点区切りにする。例: 1.0.0 → 1.0.0.0）。

.PARAMETER OutputDir
    dotnet publishの出力先。既定は publish\release\Graft。
    既存の内容は実行前に削除される（配布物以外のファイルを置かないこと）。

.PARAMETER ZipPath
    作成するZIPのパス。既定はリポジトリ直下の Graft-<バージョン>-win-x64.zip。

.PARAMETER Force
    gitの作業ツリーが汚れていても続行する。
    ただし発行先がpublish\Graft（利用者が普段使う場所）の場合は、-Forceを付けても中断する。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\New-Release.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\New-Release.ps1 -Version 1.1.0.0 -Force
#>

[CmdletBinding()]
param(
    [string]$Version,
    [string]$OutputDir,
    [string]$ZipPath,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

# ============================================================================
# 0. 共通ヘルパー
# ============================================================================

# BOM付きUTF-8で書き出す（はじめにお読みください.txtをメモ帳で開いても文字化けしないように）。
function Write-Utf8Bom([string]$Path, [string]$Text) {
    $dir = Split-Path -Path $Path -Parent
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $encoding = New-Object System.Text.UTF8Encoding($true)
    [System.IO.File]::WriteAllText([System.IO.Path]::GetFullPath($Path), $Text, $encoding)
}

# BOM無しUTF-8で書き出す（中間HTML・リリース説明.md用。他のdocs\*.mdと同じ流儀に揃える）。
function Write-Utf8NoBom([string]$Path, [string]$Text) {
    $dir = Split-Path -Path $Path -Parent
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $encoding = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText([System.IO.Path]::GetFullPath($Path), $Text, $encoding)
}

# <Version>のような3点区切り（例: 1.0.0）を、実際にビルドへ埋め込まれる
# AssemblyVersion/FileVersionと同じ4点区切り（例: 1.0.0.0）へそろえる。
# .NET SDKは<Version>が3点以下の場合、末尾を0で補ってFileVersion等を生成するため、
# これに合わせておかないと後段の「Graft.exeのFileVersionと一致するか」の比較が
# 常に食い違ってしまう。
function Get-PaddedVersion([string]$RawVersion) {
    $parts = New-Object System.Collections.Generic.List[string]
    $parts.AddRange([string[]]$RawVersion.Split('.'))
    while ($parts.Count -lt 4) { $parts.Add('0') }
    return ($parts -join '.')
}

# ============================================================================
# インラインMarkdown（強調・インラインコード・リンク）をHTMLへ変換する。
#
#   - `コード` → <code>コード</code>（先に退避させ、後段のリンク/強調処理の影響を受けないようにする。
#     そうしないと、例えば `**印刷されない強調**` のようにコード中の記号まで強調されてしまう）
#   - [表示文字](URL) → URLが#始まり（文書内アンカー）なら表示文字だけを残す。
#     印刷物ではアンカーが機能せず、リンク記法がそのまま出てしまうため。
#     それ以外のURLは<a href="URL">表示文字</a>にする。
#   - **強調** → <strong>強調</strong>
# ============================================================================
function Convert-InlineMarkdown([string]$Text) {
    if ([string]::IsNullOrEmpty($Text)) { return '' }

    # まずHTMLの特殊文字をエスケープする（& を最初に処理しないと、後で生成する
    # &lt; 等のエンティティ自体が二重エスケープされてしまう）。
    $s = $Text -replace '&', '&amp;' -replace '<', '&lt;' -replace '>', '&gt;'

    # インラインコードを一時的なプレースホルダへ退避する。
    $codeStore = New-Object System.Collections.Generic.List[string]
    $sb = New-Object System.Text.StringBuilder
    $lastEnd = 0
    foreach ($m in [regex]::Matches($s, '`([^`]+)`')) {
        [void]$sb.Append($s.Substring($lastEnd, $m.Index - $lastEnd))
        [void]$sb.Append([char]1 + 'CODE' + $codeStore.Count + [char]1)
        $codeStore.Add($m.Groups[1].Value)
        $lastEnd = $m.Index + $m.Length
    }
    [void]$sb.Append($s.Substring($lastEnd))
    $s = $sb.ToString()

    # リンク [表示文字](URL)
    $sb = New-Object System.Text.StringBuilder
    $lastEnd = 0
    foreach ($m in [regex]::Matches($s, '\[([^\]]+)\]\(([^)]+)\)')) {
        [void]$sb.Append($s.Substring($lastEnd, $m.Index - $lastEnd))
        $linkText = $m.Groups[1].Value
        $url = $m.Groups[2].Value
        if ($url.StartsWith('#')) {
            [void]$sb.Append($linkText)
        } else {
            [void]$sb.Append('<a href="' + $url + '">' + $linkText + '</a>')
        }
        $lastEnd = $m.Index + $m.Length
    }
    [void]$sb.Append($s.Substring($lastEnd))
    $s = $sb.ToString()

    # 強調 **太字**
    $s = [regex]::Replace($s, '\*\*(.+?)\*\*', '<strong>$1</strong>')

    # 退避させたインラインコードを戻す。
    for ($i = 0; $i -lt $codeStore.Count; $i++) {
        $token = [char]1 + 'CODE' + $i + [char]1
        $s = $s.Replace($token, '<code>' + $codeStore[$i] + '</code>')
    }

    return $s
}

# テーブル行（"| a | b |"形式の文字列のリスト。先頭が見出し行）をHTMLの<table>へ変換する。
function ConvertTo-TableHtml([System.Collections.Generic.List[string]]$Rows) {
    if ($Rows.Count -eq 0) { return '' }
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append("<table>`n")
    for ($i = 0; $i -lt $Rows.Count; $i++) {
        $trimmed = $Rows[$i].Trim()
        $trimmed = $trimmed -replace '^\|', '' -replace '\|$', ''
        $cells = $trimmed -split '\|'
        if ($i -eq 0) {
            [void]$sb.Append('<thead><tr>')
            foreach ($c in $cells) { [void]$sb.Append('<th>' + (Convert-InlineMarkdown $c.Trim()) + '</th>') }
            [void]$sb.Append("</tr></thead>`n<tbody>`n")
        } else {
            [void]$sb.Append('<tr>')
            foreach ($c in $cells) { [void]$sb.Append('<td>' + (Convert-InlineMarkdown $c.Trim()) + '</td>') }
            [void]$sb.Append("</tr>`n")
        }
    }
    [void]$sb.Append("</tbody></table>`n")
    return $sb.ToString()
}

# ============================================================================
# Markdown本文（ブロック単位）をHTMLへ変換する。
#
#   - ```コードブロック``` は他の処理より先に抜き出し、中身をエスケープしたうえで
#     プレースホルダへ差し替える（コード中の記号がリンクや強調として誤変換されないように）。
#   - 見出し（#〜######）
#   - 水平線（---のみの行）
#   - テーブル（|区切りの行。次行が|---|---|のような区切り行なら見出し行として扱う）
#   - 番号付きリスト（1.で始まる行）は<ol>へ、箇条書き（-/*で始まる行）は<ul>へ
#     グループ化する（1行ずつ<li>を出すだけだと<ol>にならず、番号が振られない）。
#   - リスト項目の直後に続くインデント行（2文字以上の字下げ）は、リストを閉じずに
#     項目の続きの説明文として扱う（docs\取扱説明書.md の「1. **見出し**\n   説明文」という
#     書き方に対応するため。ここでリストを閉じてしまうと、次の番号がまた1から始まってしまう）。
#   - それ以外の行は1行=1段落として<p>にする
#     （docs\取扱説明書.md 自体が「1つの段落は1つの物理行」という書き方で統一されているため）。
# ============================================================================
function Convert-ManualMarkdownToHtml([string]$Markdown) {
    $md = $Markdown -replace "`r`n", "`n" -replace "`r", "`n"

    # コードブロックを抜き出してプレースホルダへ差し替える。
    $codeBlocks = New-Object System.Collections.Generic.List[string]
    $fencePattern = [regex]::new('```[^\n]*\n(.*?)\n```[ \t]*(\n|$)', [System.Text.RegularExpressions.RegexOptions]::Singleline)
    $sb0 = New-Object System.Text.StringBuilder
    $lastEnd = 0
    foreach ($m in $fencePattern.Matches($md)) {
        [void]$sb0.Append($md.Substring($lastEnd, $m.Index - $lastEnd))
        $raw = $m.Groups[1].Value
        $escaped = ($raw -replace '&', '&amp;' -replace '<', '&lt;' -replace '>', '&gt;')
        $codeBlocks.Add('<pre><code>' + $escaped + '</code></pre>')
        [void]$sb0.Append([char]2 + 'BLOCK' + ($codeBlocks.Count - 1) + [char]2 + "`n")
        $lastEnd = $m.Index + $m.Length
    }
    [void]$sb0.Append($md.Substring($lastEnd))
    $md = $sb0.ToString()

    $lines = $md -split "`n"
    $html = New-Object System.Text.StringBuilder
    $listType = $null
    $tableRows = New-Object System.Collections.Generic.List[string]
    $inTable = $false

    foreach ($rawLine in $lines) {
        $line = $rawLine.TrimEnd()

        # コードブロックのプレースホルダ行
        $blockMatch = [regex]::Match($line, '^' + [char]2 + 'BLOCK(\d+)' + [char]2 + '$')
        if ($blockMatch.Success) {
            if ($listType) { [void]$html.Append("</$listType>`n"); $listType = $null }
            if ($inTable) { [void]$html.Append((ConvertTo-TableHtml $tableRows)); $tableRows.Clear(); $inTable = $false }
            [void]$html.Append($codeBlocks[[int]$blockMatch.Groups[1].Value])
            [void]$html.Append("`n")
            continue
        }

        # 空行
        if ($line -eq '') {
            if ($listType) { [void]$html.Append("</$listType>`n"); $listType = $null }
            if ($inTable) { [void]$html.Append((ConvertTo-TableHtml $tableRows)); $tableRows.Clear(); $inTable = $false }
            continue
        }

        # 見出し
        if ($line -match '^(#{1,6})\s+(.*)$') {
            if ($listType) { [void]$html.Append("</$listType>`n"); $listType = $null }
            if ($inTable) { [void]$html.Append((ConvertTo-TableHtml $tableRows)); $tableRows.Clear(); $inTable = $false }
            $level = $Matches[1].Length
            [void]$html.Append("<h$level>" + (Convert-InlineMarkdown $Matches[2]) + "</h$level>`n")
            continue
        }

        # 水平線
        if ($line -match '^-{3,}$') {
            if ($listType) { [void]$html.Append("</$listType>`n"); $listType = $null }
            if ($inTable) { [void]$html.Append((ConvertTo-TableHtml $tableRows)); $tableRows.Clear(); $inTable = $false }
            [void]$html.Append("<hr>`n")
            continue
        }

        # テーブルの区切り行（|---|---|）は出力せず読み飛ばす。
        if ($inTable -and $line -match '^\|[\s:|-]+\|$') {
            continue
        }

        # テーブル行
        if ($line -match '^\|.*\|$') {
            if ($listType) { [void]$html.Append("</$listType>`n"); $listType = $null }
            $inTable = $true
            $tableRows.Add($line)
            continue
        } elseif ($inTable) {
            [void]$html.Append((ConvertTo-TableHtml $tableRows))
            $tableRows.Clear()
            $inTable = $false
        }

        # リスト項目の続き（字下げされた行）。リストは閉じずに説明文として追加する。
        if ($listType -and $line -match '^\s{2,}\S') {
            [void]$html.Append('<p>' + (Convert-InlineMarkdown $line.Trim()) + "</p>`n")
            continue
        }

        # 番号付きリスト
        if ($line -match '^\d+\.\s+(.*)$') {
            if ($listType -and $listType -ne 'ol') { [void]$html.Append("</$listType>`n"); $listType = $null }
            if (-not $listType) { [void]$html.Append("<ol>`n"); $listType = 'ol' }
            [void]$html.Append('<li>' + (Convert-InlineMarkdown $Matches[1]) + "</li>`n")
            continue
        }

        # 箇条書き
        if ($line -match '^[-*]\s+(.*)$') {
            if ($listType -and $listType -ne 'ul') { [void]$html.Append("</$listType>`n"); $listType = $null }
            if (-not $listType) { [void]$html.Append("<ul>`n"); $listType = 'ul' }
            [void]$html.Append('<li>' + (Convert-InlineMarkdown $Matches[1]) + "</li>`n")
            continue
        }

        # 通常の段落
        if ($listType) { [void]$html.Append("</$listType>`n"); $listType = $null }
        [void]$html.Append('<p>' + (Convert-InlineMarkdown $line) + "</p>`n")
    }

    if ($listType) { [void]$html.Append("</$listType>`n") }
    if ($inTable) { [void]$html.Append((ConvertTo-TableHtml $tableRows)) }

    return $html.ToString()
}

# 取扱説明書.md全体を、印刷（PDF化）向けのCSSを添えた1枚のHTML文書へ変換する。
function ConvertTo-ManualHtmlDocument([string]$MarkdownPath, [string]$Title) {
    $markdown = Get-Content -Path $MarkdownPath -Raw -Encoding UTF8
    $bodyHtml = Convert-ManualMarkdownToHtml $markdown

    $css = @'
@page { size: A4; margin: 18mm; }
* { box-sizing: border-box; }
body {
    font-family: "Noto Sans CJK JP", "Noto Sans JP", "Yu Gothic UI", "Meiryo", sans-serif;
    font-size: 10.5pt;
    line-height: 1.7;
    color: #1a1a1a;
    margin: 0;
}
h1, h2, h3, h4, h5, h6 {
    page-break-after: avoid;
    line-height: 1.4;
    margin-top: 1.3em;
    margin-bottom: 0.5em;
}
h1 { font-size: 19pt; border-bottom: 2px solid #333; padding-bottom: 0.2em; }
h2 { font-size: 14.5pt; border-bottom: 1px solid #999; padding-bottom: 0.15em; }
h3 { font-size: 12pt; }
h4, h5, h6 { font-size: 11pt; }
p { margin: 0.55em 0; }
ol, ul { margin: 0.4em 0 0.8em 0; padding-left: 1.6em; }
li { margin: 0.2em 0; }
table { border-collapse: collapse; width: 100%; margin: 0.8em 0; page-break-inside: avoid; }
th, td { border: 1px solid #999; padding: 0.35em 0.6em; text-align: left; font-size: 9.5pt; vertical-align: top; }
th { background: #eee; }
pre {
    background: #f5f5f5;
    border: 1px solid #ddd;
    padding: 0.6em 0.8em;
    page-break-inside: avoid;
    white-space: pre-wrap;
    word-break: break-all;
    font-size: 9pt;
}
code { font-family: "Consolas", "Courier New", monospace; background: #f0f0f0; padding: 0.05em 0.3em; border-radius: 2px; font-size: 0.95em; }
pre code { background: none; padding: 0; }
hr { border: none; border-top: 1px solid #ccc; margin: 1.2em 0; }
a { color: #1a1a1a; text-decoration: underline; }
'@

    return @"
<!doctype html>
<html lang="ja">
<head>
<meta charset="utf-8">
<title>$Title</title>
<style>
$css
</style>
</head>
<body>
$bodyHtml
</body>
</html>
"@
}

# msedge.exeを既知の場所から順に探す。見つからなければ$nullを返す
# （呼び出し側でPDF生成だけをスキップし、他の処理は続行する）。
function Find-MsEdge {
    $candidates = @()
    $pf86 = ${env:ProgramFiles(x86)}
    if ($pf86) { $candidates += (Join-Path $pf86 'Microsoft\Edge\Application\msedge.exe') }
    if ($env:ProgramFiles) { $candidates += (Join-Path $env:ProgramFiles 'Microsoft\Edge\Application\msedge.exe') }
    foreach ($c in $candidates) {
        if (Test-Path $c) { return $c }
    }
    $cmd = Get-Command msedge -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

# ============================================================================
# 1. 前提の確認
# ============================================================================

$repoRoot = Split-Path -Parent $PSScriptRoot

Write-Host '=== 1. 前提の確認 ==='

$gitStatusOutput = & git -C $repoRoot status --porcelain 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "gitコマンドの実行に失敗しました（$repoRoot がgitの管理下か確認してください）。出力: $gitStatusOutput"
}

$branch = (& git -C $repoRoot rev-parse --abbrev-ref HEAD).Trim()
$commit = (& git -C $repoRoot rev-parse --short HEAD).Trim()
Write-Host "現在のブランチ: $branch"
Write-Host "現在のコミット: $commit"

if ($gitStatusOutput) {
    Write-Warning '作業ツリーに未コミットの変更があります:'
    foreach ($line in $gitStatusOutput) { Write-Warning "  $line" }
    if (-not $Force) {
        throw '作業ツリーが汚れているため中断しました。問題なければ -Force を付けて再実行してください。'
    }
    Write-Warning '-Force が指定されているため続行します。'
} else {
    Write-Host '作業ツリー: クリーン'
}

# src\Graft\Graft.csprojの<Version>を読み取る。
$csprojPath = Join-Path $repoRoot 'src/Graft/Graft.csproj'
if (-not (Test-Path $csprojPath)) { throw "Graft.csprojが見つかりません: $csprojPath" }
$csprojText = Get-Content -Path $csprojPath -Raw -Encoding UTF8
$versionMatch = [regex]::Match($csprojText, '<Version>([^<]+)</Version>')
if (-not $versionMatch.Success) { throw "Graft.csprojから<Version>を読み取れませんでした: $csprojPath" }
$csprojVersion = $versionMatch.Groups[1].Value.Trim()

if ($Version) {
    $resolvedVersion = $Version
    Write-Host "バージョン: $resolvedVersion（引数で指定。Graft.csprojの<Version>は $csprojVersion）"
} else {
    $resolvedVersion = Get-PaddedVersion $csprojVersion
    Write-Host "バージョン: $resolvedVersion（Graft.csprojの<Version>=$csprojVersion から算出）"
}

# ============================================================================
# 発行先・ZIP出力先の決定
# ============================================================================

$publishRoot = Join-Path $repoRoot 'publish'
$releaseRoot = Join-Path $publishRoot 'release'
$defaultOutputDir = Join-Path $releaseRoot 'Graft'
# 利用者が普段使う場所（README「発行」節の手順で作られる）。誤ってここへ出力すると
# settings.json・projects.json・back（バックアップ）・logsが消えてしまうため、
# 発行先として指定することを禁止する（-Forceでも回避不可）。
$userOutputDir = Join-Path $publishRoot 'Graft'

if ($OutputDir) {
    $resolvedOutputDir = [System.IO.Path]::GetFullPath($OutputDir)
} else {
    $resolvedOutputDir = [System.IO.Path]::GetFullPath($defaultOutputDir)
}

$normalize = {
    param([string]$Path)
    return $Path.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
}
$normalizedOutputDir = & $normalize $resolvedOutputDir
$normalizedUserOutputDir = & $normalize ([System.IO.Path]::GetFullPath($userOutputDir))

if ($normalizedOutputDir -ieq $normalizedUserOutputDir) {
    throw "出力先に $resolvedOutputDir は指定できません。ここは利用者が普段使う publish\Graft と同じ場所で、settings.json・projects.json・back（バックアップ）・logsが入っている可能性があります。別の -OutputDir を指定してください（既定値: $defaultOutputDir）。"
}

if ($ZipPath) {
    $resolvedZipPath = [System.IO.Path]::GetFullPath($ZipPath)
} else {
    $resolvedZipPath = Join-Path $repoRoot "Graft-$resolvedVersion-win-x64.zip"
}

Write-Host "発行先: $resolvedOutputDir"
Write-Host "ZIP出力先: $resolvedZipPath"

# ============================================================================
# 2. 発行
# ============================================================================

Write-Host ''
Write-Host '=== 2. 発行 ==='

if (Test-Path $resolvedOutputDir) {
    Write-Host "既存の発行先を削除します: $resolvedOutputDir"
    Remove-Item -Recurse -Force $resolvedOutputDir
}
New-Item -ItemType Directory -Path $resolvedOutputDir -Force | Out-Null

& dotnet publish $csprojPath -c Release -r win-x64 --self-contained true -o $resolvedOutputDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publishが失敗しました（終了コード $LASTEXITCODE）。" }
Write-Host "発行完了: $resolvedOutputDir"

# ============================================================================
# 3. 同梱物の配置
# ============================================================================

Write-Host ''
Write-Host '=== 3. 同梱物の配置 ==='

$manualMdSrc = Join-Path $repoRoot 'docs/取扱説明書.md'
if (-not (Test-Path $manualMdSrc)) { throw "取扱説明書.mdが見つかりません: $manualMdSrc" }
Copy-Item -Path $manualMdSrc -Destination (Join-Path $resolvedOutputDir '取扱説明書.md') -Force
Write-Host '配置: 取扱説明書.md'

# --- 取扱説明書.pdf（Microsoft Edgeのheadless印刷で生成。無ければスキップして続行） ---
$pdfGenerated = $false
$msedge = Find-MsEdge
if ($msedge) {
    Write-Host "msedge.exeを検出: $msedge"
    $tempHtmlPath = Join-Path ([System.IO.Path]::GetTempPath()) ('Graft取扱説明書_' + $resolvedVersion + '_' + [System.Guid]::NewGuid().ToString('N') + '.html')
    try {
        $manualHtml = ConvertTo-ManualHtmlDocument -MarkdownPath $manualMdSrc -Title 'Graft 取扱説明書'
        Write-Utf8NoBom -Path $tempHtmlPath -Text $manualHtml

        $pdfDest = Join-Path $resolvedOutputDir '取扱説明書.pdf'
        Write-Host "取扱説明書.pdfを生成します..."
        & $msedge --headless --disable-gpu --no-pdf-header-footer "--print-to-pdf=$pdfDest" $tempHtmlPath
        if ((Test-Path $pdfDest) -and ((Get-Item $pdfDest).Length -gt 0)) {
            $pdfGenerated = $true
            Write-Host "配置: 取扱説明書.pdf"
        } else {
            Write-Warning 'msedgeの実行後も取扱説明書.pdfが作成されませんでした。取扱説明書.pdfの同梱をスキップし、他の処理は続行します。'
        }
    } finally {
        # 中間HTMLはZIPに含めない。
        if (Test-Path $tempHtmlPath) { Remove-Item -Path $tempHtmlPath -Force -ErrorAction SilentlyContinue }
    }
} else {
    Write-Warning 'msedge.exeが見つからなかったため、取扱説明書.pdfの生成をスキップします（他の処理は続行します）。手作業でPDF化する場合はdocs\取扱説明書.mdを変換してください。'
}

# --- はじめにお読みください.txt（BOM付きUTF-8） ---
$readmeTitle = "Graft $resolvedVersion"
$readmeUnderline = '=' * $readmeTitle.Length
$readmeText = @"
$readmeTitle
$readmeUnderline

AIが提案したコードの変更を、手元のプロジェクトへ安全に取り込むためのツールです。

■ 使い方
  Graft.exe を実行してください。インストール作業は不要です。
  初回起動時に案内が開きます。3画面進むだけでひととおり体験できます。

■ 動作環境
  Windows 10 / 11（64bit）
  .NET のインストールは不要です（同梱しています）。

■ 取扱説明書
  アプリ内で F1 キーを押すといつでも開けます。
  同じ内容を 取扱説明書.pdf / 取扱説明書.md としても同梱しています。

■ 「WindowsによってPCが保護されました」と出たら
  コード署名証明書を付けていないための警告です。
  内容をご確認のうえ「詳細情報」→「実行」で起動できます。

■ データの保存場所
  既定では、この Graft.exe と同じフォルダに設定・履歴・バックアップを保存します。
  このフォルダごとコピーすれば、別のPCへそのまま持ち運べます。

  Program Files など書き込めない場所に置いた場合は、
  設定 → 一般 →「ユーザーフォルダへ移動」で保存先を移せます。

■ アンインストール
  このフォルダを削除するだけです。
"@
Write-Utf8Bom -Path (Join-Path $resolvedOutputDir 'はじめにお読みください.txt') -Text $readmeText
Write-Host '配置: はじめにお読みください.txt'

# ============================================================================
# 4. ZIP作成
# ============================================================================

Write-Host ''
Write-Host '=== 4. ZIP作成 ==='

if (Test-Path $resolvedZipPath) { Remove-Item -Path $resolvedZipPath -Force }
# フォルダそのもの（$resolvedOutputDirの末端フォルダ名、既定では"Graft"）をZIPの
# トップレベルに含める。利用者が展開したときにファイルが散らばらないようにするため。
Compress-Archive -Path $resolvedOutputDir -DestinationPath $resolvedZipPath -Force
Write-Host "作成: $resolvedZipPath"

# .gitignoreに配布用ZIPのパターンが無ければ追加する（publish/自体は既に無視されているが、
# ZIPはリポジトリ直下に作るため別途必要）。
$gitignorePath = Join-Path $repoRoot '.gitignore'
$zipIgnorePattern = 'Graft-*-win-x64.zip'
$gitignoreText = if (Test-Path $gitignorePath) { Get-Content -Path $gitignorePath -Raw -Encoding UTF8 } else { '' }
if ($gitignoreText -notmatch [regex]::Escape($zipIgnorePattern)) {
    $addition = ("`n# tools\New-Release.ps1 が作るリリース用ZIP`n" + $zipIgnorePattern + "`n")
    Add-Content -Path $gitignorePath -Value $addition -Encoding UTF8
    Write-Host ".gitignoreに `"$zipIgnorePattern`" を追加しました。"
}

# ============================================================================
# 5. 検証と表示
# ============================================================================

Write-Host ''
Write-Host '=== 5. 検証 ==='

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zipArchive = [System.IO.Compression.ZipFile]::OpenRead($resolvedZipPath)
try {
    Write-Host 'ZIPの中身:'
    foreach ($entry in ($zipArchive.Entries | Sort-Object FullName)) {
        if ($entry.Length -eq 0 -and $entry.FullName.EndsWith('/')) { continue }
        Write-Host ('  {0,12:N0} bytes  {1}' -f $entry.Length, $entry.FullName)
    }
} finally {
    $zipArchive.Dispose()
}
$zipSize = (Get-Item $resolvedZipPath).Length
Write-Host ('ZIPファイルサイズ: {0:N0} bytes（約{1:N1} MB）' -f $zipSize, ($zipSize / 1MB))

# Graft.exeのファイルバージョンを確認する。
$exePath = Join-Path $resolvedOutputDir 'Graft.exe'
if (-not (Test-Path $exePath)) {
    throw "Graft.exeが発行先に見つかりません: $exePath"
}
$fileVersion = (Get-Item $exePath).VersionInfo.FileVersion
$expectedFileVersion = Get-PaddedVersion $resolvedVersion
Write-Host "Graft.exe のファイルバージョン: $fileVersion"
if ([string]::IsNullOrEmpty($fileVersion)) {
    Write-Warning 'Graft.exeからファイルバージョンを読み取れませんでした。'
} elseif ($fileVersion -ne $expectedFileVersion) {
    Write-Warning "Graft.exeのファイルバージョン（$fileVersion）が、想定するバージョン（$expectedFileVersion）と一致しません。Graft.csprojの<Version>を確認してください。"
} else {
    Write-Host '一致を確認しました。'
}

# 期待するファイルが全部そろっているか検査する。
# 取扱説明書.pdfだけは、msedgeが無い環境ではあらかじめ警告のうえスキップしているため、
# その場合は無くてもエラーにしない（他は必ず入っていなければならない）。
$hardRequiredFiles = @(
    'Graft.exe',
    'av_libglesv2.dll',
    'libHarfBuzzSharp.dll',
    'libSkiaSharp.dll',
    '取扱説明書.md',
    'はじめにお読みください.txt'
)
$missing = @()
foreach ($f in $hardRequiredFiles) {
    if (-not (Test-Path (Join-Path $resolvedOutputDir $f))) { $missing += $f }
}
if ($pdfGenerated -and -not (Test-Path (Join-Path $resolvedOutputDir '取扱説明書.pdf'))) {
    $missing += '取扱説明書.pdf'
}
if ($missing.Count -gt 0) {
    throw ('配布物に含まれるべきファイルが不足しています: ' + ($missing -join ', '))
}
Write-Host ('必須ファイルの確認: OK（' + ($hardRequiredFiles -join ', ') + $(if ($pdfGenerated) { ', 取扱説明書.pdf' } else { '' }) + '）')
if (-not $pdfGenerated) {
    Write-Warning '取扱説明書.pdfは同梱されていません（msedgeが見つからなかったため）。配布前に手作業でPDFを用意することを検討してください。'
}

# ============================================================================
# リリース本文の下書きを書き出す
# ============================================================================

Write-Host ''
Write-Host '=== リリース本文の下書き ==='

$templatePath = Join-Path $repoRoot 'docs/リリース説明_テンプレート.md'
$releaseNotesPath = Join-Path $releaseRoot 'リリース説明.md'
if (Test-Path $templatePath) {
    $templateText = Get-Content -Path $templatePath -Raw -Encoding UTF8
    $releaseNotesText = $templateText.Replace('{VERSION}', $resolvedVersion)
    Write-Utf8NoBom -Path $releaseNotesPath -Text $releaseNotesText
    Write-Host "書き出し: $releaseNotesPath"
} else {
    Write-Warning "リリース本文の雛形が見つかりません: $templatePath（リリース説明.mdの生成をスキップしました）"
}

# ============================================================================
# 6. 次にやること
# ============================================================================

Write-Host ''
Write-Host '=== 6. 次にやること ==='
$tag = "v$resolvedVersion"
Write-Host '1. タグを作成してpushする:'
Write-Host "     git tag -a $tag -m `"Graft $resolvedVersion`""
Write-Host "     git push origin $tag"
Write-Host ''
Write-Host '2. GitHubでリリースを作成する（Targetは main を選ぶこと）:'
Write-Host "     https://github.com/Yu5rin/Graft/releases/new?tag=$tag"
Write-Host ''
Write-Host '3. リリース本文には、下記の下書きの内容を貼り付ける:'
Write-Host "     $releaseNotesPath"
Write-Host ''
Write-Host '4. 添付するZIP:'
Write-Host "     $resolvedZipPath"
Write-Host ''
Write-Host '=== 完了 ==='
