<#
.SYNOPSIS
    Graftのリリース用配布物（ZIP）を1コマンドで作成する。

.DESCRIPTION
    1.0.0.0のリリースで手作業で行っていた次の手順を、そのまま自動化したもの。

      1. 前提の確認（gitの作業ツリーがクリーンか、現在のブランチ/コミット、バージョンの決定）
      2. 発行（dotnet publish、-r win-x64、自己完結型）
      3. 同梱物の配置（取扱説明書.md／はじめにお読みください.txt）
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
    配布物の表記（ZIP名・タグ・はじめにお読みください.txt・リリース説明.md等）に使うバージョン。
    省略時はsrc\Graft\Graft.csprojの<Version>の値をそのまま使う（例: 1.0.1。桁を補ったりしない）。
    引数で指定した場合も、渡した文字列をそのまま表記に使う（例: -Version 1.0.1.0 と指定すれば
    そのまま4点区切りで表記される。利用者が明示した表記を尊重する）。
    唯一の例外はGraft.exeのファイルバージョンとの照合で、そこだけは内部で4点区切りへ補って
    比較する（Get-PaddedVersion参照。表記そのものには影響しない）。
    なお1.0.0.0のみ、実際にタグ v1.0.0.0 として公開済みのため例外的に4点区切りで表記される
    （docs\リリース手順.mdの注記も参照）。1.0.1以降は3点区切り（<Version>の値そのまま）が既定。

.PARAMETER OutputDir
    dotnet publishの出力先。既定は publish\release\Graft。
    既存の内容は実行前に削除される（配布物以外のファイルを置かないこと）。

.PARAMETER ZipPath
    作成するWindows版ZIPのパス。既定はリポジトリ直下の Graft-<バージョン>-win-x64.zip。

.PARAMETER TarPath
    作成するLinux版tar.gzのパス。既定はリポジトリ直下の Graft-<バージョン>-linux-x64.tar.gz。
    Linux版をzipにしないのは、zipがUnixのファイル属性を持たず実行権限が失われるため
    （.github/workflows/release.yml に同じ理由のコメントがある）。

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
    [string]$TarPath,
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

# BOM無しUTF-8で書き出す（リリース説明.md用。他のdocs\*.mdと同じ流儀に揃える）。
function Write-Utf8NoBom([string]$Path, [string]$Text) {
    $dir = Split-Path -Path $Path -Parent
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $encoding = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText([System.IO.Path]::GetFullPath($Path), $Text, $encoding)
}

# ファイルバージョン照合専用。表示・命名には使わない。
# .NET SDKは<Version>が3点以下の場合、末尾を0で補ってAssemblyVersion/FileVersionを生成する
# （例: 1.0.1 → 1.0.1.0）。ZIP名・タグ・はじめにお読みください.txt・リリース説明.md等の
# 利用者向けの表記は$resolvedVersion（<Version>や-Versionの値をそのまま使う。桁を補わない）を
# 使うが、Graft.exeの実際のFileVersionは常に4点区切りになるため、比較のときだけこの関数で
# 4点区切りへそろえる（下の「Graft.exeのファイルバージョンを確認する」参照）。
function Get-PaddedVersion([string]$RawVersion) {
    $parts = New-Object System.Collections.Generic.List[string]
    $parts.AddRange([string[]]$RawVersion.Split('.'))
    while ($parts.Count -lt 4) { $parts.Add('0') }
    return ($parts -join '.')
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
    # 渡された文字列をそのまま表記に使う（桁を補ったり削ったりしない。例えば4点区切りを
    # 渡せばそのまま4点区切りで表記される。利用者が明示した表記を尊重する）。
    $resolvedVersion = $Version
    Write-Host "バージョン: $resolvedVersion（引数で指定。Graft.csprojの<Version>は $csprojVersion）"
} else {
    # <Version>の値をそのまま使う（桁を補わない。既定では3点区切りのまま表記される）。
    $resolvedVersion = $csprojVersion
    Write-Host "バージョン: $resolvedVersion（Graft.csprojの<Version>から算出）"
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

# Linux版の発行先。Windows版の発行先と同じ階層に linux\Graft を作る。書庫のトップに来る
# フォルダ名は発行先の末端フォルダ名になるため、Windows版と同じ "Graft" に揃える。
$linuxParentDir = Join-Path (Split-Path $resolvedOutputDir -Parent) 'linux'
$linuxOutputDir = Join-Path $linuxParentDir 'Graft'
if ($TarPath) {
    $resolvedTarPath = [System.IO.Path]::GetFullPath($TarPath)
} else {
    $resolvedTarPath = Join-Path $repoRoot "Graft-$resolvedVersion-linux-x64.tar.gz"
}

Write-Host "発行先: $resolvedOutputDir"
Write-Host "ZIP出力先: $resolvedZipPath"
Write-Host "Linux発行先: $linuxOutputDir"
Write-Host "tar.gz出力先: $resolvedTarPath"

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
if ($LASTEXITCODE -ne 0) { throw "dotnet publish（win-x64）が失敗しました（終了コード $LASTEXITCODE）。" }
Write-Host "発行完了（win-x64）: $resolvedOutputDir"

# Linux版。.NETはクロスOSの発行に対応しているため、Windows上からでもlinux-x64の
# 発行物を作れる（.github/workflows/release.ymlも逆にLinuxランナー上でwin-x64を作っている）。
if (Test-Path $linuxParentDir) {
    Write-Host "既存のLinux発行先を削除します: $linuxParentDir"
    Remove-Item -Recurse -Force $linuxParentDir
}
New-Item -ItemType Directory -Path $linuxOutputDir -Force | Out-Null

& dotnet publish $csprojPath -c Release -r linux-x64 --self-contained true -o $linuxOutputDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish（linux-x64）が失敗しました（終了コード $LASTEXITCODE）。" }
Write-Host "発行完了（linux-x64）: $linuxOutputDir"

# ============================================================================
# 3. 同梱物の配置
# ============================================================================

Write-Host ''
Write-Host '=== 3. 同梱物の配置 ==='

$manualMdSrc = Join-Path $repoRoot 'docs/取扱説明書.md'
if (-not (Test-Path $manualMdSrc)) { throw "取扱説明書.mdが見つかりません: $manualMdSrc" }
Copy-Item -Path $manualMdSrc -Destination (Join-Path $resolvedOutputDir '取扱説明書.md') -Force
Write-Host '配置: 取扱説明書.md'

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
  同じ内容を 取扱説明書.md としても同梱しています。

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
Write-Host '配置: はじめにお読みください.txt（win-x64）'

# --- Linux版の同梱物 ---
Copy-Item -Path $manualMdSrc -Destination (Join-Path $linuxOutputDir '取扱説明書.md') -Force

# Windows版とは実行ファイル名（Graft.exe→Graft）も、注意すべき点（SmartScreenではなく
# 実行権限）も違うため、文面を作り分ける。
# 実行権限について: Windows上でtarを作るとNTFSにUnixのファイル属性が無く、展開しても
# 実行権限が付かないことがある（Linux上で作った場合は保たれる）。どちらで作られたかを
# 利用者は知りようがないので、常にchmodの案内を載せておく。
$linuxReadmeText = @"
$readmeTitle
$readmeUnderline

AIが提案したコードの変更を、手元のプロジェクトへ安全に取り込むためのツールです。

■ 使い方
  このフォルダの中で次を実行してください。

    chmod +x Graft
    ./Graft

  インストール作業は不要です。
  初回起動時に案内が開きます。3画面進むだけでひととおり体験できます。

■ 動作環境
  Linux（64bit、X11）
  .NET のインストールは不要です（同梱しています）。

■ 実行権限について
  書庫を展開した直後は Graft に実行権限が付いていないことがあります。
  「許可がありません」と出たら、上の chmod +x を実行してください。

■ 取扱説明書
  アプリ内で F1 キーを押すといつでも開けます。
  同じ内容を 取扱説明書.md としても同梱しています。

■ データの保存場所
  既定では、この Graft と同じフォルダに設定・履歴・バックアップを保存します。
  このフォルダごとコピーすれば、別のPCへそのまま持ち運べます。

  書き込めない場所に置いた場合は、
  設定 → 一般 →「ユーザーフォルダへ移動」で保存先を移せます。

■ アンインストール
  このフォルダを削除するだけです。
"@
Write-Utf8Bom -Path (Join-Path $linuxOutputDir 'はじめにお読みください.txt') -Text $linuxReadmeText
Write-Host '配置: 取扱説明書.md／はじめにお読みください.txt（linux-x64）'

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

# --- Linux版（tar.gz） ---
# zipではUnixのファイル属性を持てず実行権限が失われるためtar.gzにする
# （.github/workflows/release.yml に同じ理由のコメントがある）。
# tarはWindows 10 1803以降にも標準で入っている（bsdtar）。無い環境では
# Linux版の書庫だけを飛ばして続行する（Windows版のリリースは止めない）。
$tarCommand = Get-Command tar -ErrorAction SilentlyContinue
if (-not $tarCommand) {
    Write-Warning 'tarコマンドが見つからないため、Linux版のtar.gz作成を飛ばしました。発行物は ' + $linuxOutputDir + ' に残っています。'
    $linuxArchiveCreated = $false
} else {
    if (Test-Path $resolvedTarPath) { Remove-Item -Path $resolvedTarPath -Force }
    # -C で親フォルダへ移動してから "Graft" を指定することで、書庫のトップに
    # フォルダを1つ挟む（Windows版のZIPと同じ構造）。
    & tar -czf $resolvedTarPath -C $linuxParentDir 'Graft'
    if ($LASTEXITCODE -ne 0) { throw "tarの実行に失敗しました（終了コード $LASTEXITCODE）。" }
    Write-Host "作成: $resolvedTarPath"
    $linuxArchiveCreated = $true
}

# .gitignoreに配布用ZIPのパターンが無ければ追加する（publish/自体は既に無視されているが、
# ZIPはリポジトリ直下に作るため別途必要）。
$gitignorePath = Join-Path $repoRoot '.gitignore'
$gitignoreText = if (Test-Path $gitignorePath) { Get-Content -Path $gitignorePath -Raw -Encoding UTF8 } else { '' }
foreach ($pattern in @('Graft-*-win-x64.zip', 'Graft-*-linux-x64.tar.gz')) {
    if ($gitignoreText -notmatch [regex]::Escape($pattern)) {
        $addition = ("`n# tools\New-Release.ps1 が作るリリース用の配布物`n" + $pattern + "`n")
        Add-Content -Path $gitignorePath -Value $addition -Encoding UTF8
        $gitignoreText += $addition
        Write-Host ".gitignoreに `"$pattern`" を追加しました。"
    }
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

# 期待するファイルが全部そろっているか検査する（欠けていたらエラーにする）。
$requiredFiles = @(
    'Graft.exe',
    'av_libglesv2.dll',
    'libHarfBuzzSharp.dll',
    'libSkiaSharp.dll',
    '取扱説明書.md',
    'はじめにお読みください.txt'
)
$missing = @()
foreach ($f in $requiredFiles) {
    if (-not (Test-Path (Join-Path $resolvedOutputDir $f))) { $missing += $f }
}
if ($missing.Count -gt 0) {
    throw ('配布物に含まれるべきファイルが不足しています: ' + ($missing -join ', '))
}
Write-Host ('必須ファイルの確認（win-x64）: OK（' + ($requiredFiles -join ', ') + '）')

# --- Linux版の検証 ---
# 発行物はGraft（拡張子なしの実行ファイル）とネイティブライブラリ2つ。Windows版の
# av_libglesv2.dllに相当するものはLinuxには無い（実測で確認）。
$linuxRequiredFiles = @(
    'Graft',
    'libHarfBuzzSharp.so',
    'libSkiaSharp.so',
    '取扱説明書.md',
    'はじめにお読みください.txt'
)
$linuxMissing = @()
foreach ($f in $linuxRequiredFiles) {
    if (-not (Test-Path (Join-Path $linuxOutputDir $f))) { $linuxMissing += $f }
}
if ($linuxMissing.Count -gt 0) {
    throw ('Linux版の配布物に含まれるべきファイルが不足しています: ' + ($linuxMissing -join ', '))
}
Write-Host ('必須ファイルの確認（linux-x64）: OK（' + ($linuxRequiredFiles -join ', ') + '）')

if ($linuxArchiveCreated) {
    Write-Host 'tar.gzの中身:'
    & tar -tzvf $resolvedTarPath | ForEach-Object { Write-Host "  $_" }
    $tarSize = (Get-Item $resolvedTarPath).Length
    Write-Host ('tar.gzファイルサイズ: {0:N0} bytes（約{1:N1} MB）' -f $tarSize, ($tarSize / 1MB))
}

# ============================================================================
# リリース本文の下書きを書き出す
# ============================================================================

Write-Host ''
Write-Host '=== リリース本文の下書き ==='

# リリース本文は、GitHubのリリース作成画面の「Generate release notes」が出す
# 「What's Changed」「Full Changelog」の前に置く固定の前文だけを用意する。
# 変更点の一覧をこちらで組み立てないのは、GitHub側の自動生成と二重になり、
# 書き漏れや食い違いが起きるため（リリース本文の形式はdocs\リリース手順.md参照）。
# バージョン番号も本文に含めないため、差し替えは行わずそのまま書き出す。
$templatePath = Join-Path $repoRoot 'docs/リリース説明_テンプレート.md'
$releaseNotesPath = Join-Path $releaseRoot 'リリース説明.md'
if (Test-Path $templatePath) {
    $templateText = Get-Content -Path $templatePath -Raw -Encoding UTF8
    Write-Utf8NoBom -Path $releaseNotesPath -Text $templateText
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
Write-Host '3. リリース本文には、下記の内容を貼り付けてから'
Write-Host '   「Generate release notes」を押して変更点の一覧を足す:'
Write-Host "     $releaseNotesPath"
Write-Host ''
Write-Host '4. 添付する配布物:'
Write-Host "     $resolvedZipPath"
if ($linuxArchiveCreated) {
    Write-Host "     $resolvedTarPath"
} else {
    Write-Host '     （Linux版のtar.gzは作られませんでした。上の警告を参照してください）'
}
Write-Host ''
Write-Host '=== 完了 ==='
