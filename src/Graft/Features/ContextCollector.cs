using System.IO;
using System.Text;
using Graft.Core;
using Graft.Infra;

namespace Graft.Features;

/// <summary>収集モード。仕様書10.1。</summary>
public enum ContextMode
{
    /// <summary>フォルダ構成のみ。</summary>
    TreeOnly,
    /// <summary>チェックしたファイルの全文のみ。</summary>
    SelectedFiles,
    /// <summary>ツリーと選択ファイルの併用（既定）。</summary>
    TreeAndSelected,
    /// <summary>指定リビジョン以降に変更されたファイルのみ。</summary>
    ChangedSince,
}

/// <summary>
/// ファイル単位の3状態選択（仕様書10.2追補）。「ライブラリのように存在は知らせたいが中身は
/// 不要」というケースに対応するため、単純な含める／含めないの2択ではなく3値にした。
/// </summary>
public enum ContextFileState
{
    /// <summary>内容も出す（既定）。ファイル構成に載り、中身もコードブロックとして出力される。</summary>
    Full,
    /// <summary>構成だけ。ファイル構成には載るが、中身は出力されない。</summary>
    StructureOnly,
    /// <summary>出さない。ファイル構成にも載らない。</summary>
    Hidden,
}

/// <summary>コンテキスト収集の要求。仕様書10章。</summary>
public sealed record ContextRequest
{
    /// <summary>対象プロジェクト。</summary>
    public required Project Project { get; init; }
    /// <summary>収集モード。既定はツリー＋選択。</summary>
    public ContextMode Mode { get; init; } = ContextMode.TreeAndSelected;
    /// <summary>選択ファイルのプロジェクト相対パス（SelectedFiles / TreeAndSelected で使用）。</summary>
    public IReadOnlyList<string> SelectedPaths { get; init; } = Array.Empty<string>();
    /// <summary>
    /// 3状態選択で「出さない」を選んだファイルの相対パス。ツリーからも除く。
    /// <see cref="ContextMode.TreeAndSelected"/> でのみ意味を持つ（他モードでは無視される。
    /// 詳細は<see cref="ContextCollector.CollectAsync"/>のコメント参照）。
    /// </summary>
    public IReadOnlyList<string> HiddenPaths { get; init; } = Array.Empty<string>();
    /// <summary>ChangedSince のとき、この番号より新しいリビジョンで変更されたファイルを集める。</summary>
    public int? SinceRevision { get; init; }
    /// <summary>適用する設定。</summary>
    public required Settings Settings { get; init; }
}

/// <summary>選択用ツリーの1エントリ（ファイルまたはディレクトリ）。</summary>
public sealed record ContextFileNode
{
    /// <summary>プロジェクトルートからの相対パス（区切りは "/"）。</summary>
    public required string RelativePath { get; init; }
    /// <summary>ディレクトリかどうか。</summary>
    public bool IsDirectory { get; init; }
    /// <summary>ファイルサイズ（バイト）。ディレクトリは0。</summary>
    public long SizeBytes { get; init; }
    /// <summary>除外規則により除外されているか。</summary>
    public bool IsExcluded { get; init; }
    /// <summary>除外理由（UI表示用）。除外されていない場合はnull。</summary>
    public string? ExcludeReason { get; init; }
    /// <summary>
    /// 除外ディレクトリ配下の総ファイル数（要件G）。除外ディレクトリ（node_modules等）は
    /// 配下を再帰的に走査していない（このリスト自体に個々のファイルのノードを持たない）ため、
    /// ツリー出力で「N ファイル、内容は非出力」と畳んで要約するために別途数えた値をここへ持つ。
    /// 除外ディレクトリ以外・数え取りに失敗した場合はnull。
    /// </summary>
    public int? ExcludedDescendantFileCount { get; init; }
}

/// <summary>コンテキスト収集の結果。仕様書10.3・10.4。</summary>
public sealed record ContextResult
{
    /// <summary>出力テキスト全体。</summary>
    public required string Text { get; init; }
    /// <summary>推定トークン数。</summary>
    public int EstimatedTokens { get; init; }
    /// <summary>走査済みの全ファイル・ディレクトリ一覧（選択UI用。除外分も含む）。</summary>
    public IReadOnlyList<ContextFileNode> Files { get; init; } = Array.Empty<ContextFileNode>();
    /// <summary>tokenWarnThreshold を超えたかどうか。</summary>
    public bool ExceedsWarnThreshold { get; init; }
}

/// <summary>
/// 仕様書10章のコンテキスト収集を担う。ScanAsyncで除外規則を反映したツリーを、CollectAsyncで
/// 10.3の出力形式のテキストを生成する。BuildTreeTextAsync・BuildFilesTextAsyncは
/// <see cref="PromptTemplateRenderer"/> の {{tree}}・{{files}} 展開と処理を共有し、
/// 4.8.4「コンテキスト収集とは同一の出力パイプラインを共有する」を満たす。
///
/// 出力はAIが最も正確に解釈できるようMarkdown形式に整えている。冒頭に概要（プロジェクト名・
/// 生成日時・収集モード・ファイル数）、続けて「# 前提」「# プロジェクト構成」（フェンス付き
/// コードブロックのツリー）、各ファイルは「# 相対パス  (ハッシュ)」の見出し＋言語名付き
/// コードブロックで出力する。3状態選択で「構成だけ」としたファイルは、ツリー上に
/// 「(構成のみ・内容は省略)」と注記して、内容を意図的に省いたことがAIにも人にも分かるようにする。
/// </summary>
public sealed class ContextCollector
{
    /// <summary>
    /// 既定の除外パターン（仕様書10.2）。エクスプローラ（4.2）・横断検索（4.4）と
    /// 同一の規則を使うため、唯一の定義としてここに置き公開する。
    /// </summary>
    public static IReadOnlyList<string> DefaultExcludePatterns { get; } = new[]
    {
        "node_modules/", "bin/", "obj/", ".venv/", "dist/", ".git/", "*.min.js",
    };

    /// <summary>
    /// 要件F: コンテキスト収集の「初期チェック状態」だけをオフにするロックファイル名。
    ///
    /// これは<see cref="DefaultExcludePatterns"/>とは別物であり、意図的に分けている。
    /// 既定除外パターンに加えると、FileTreeService（エクスプローラ）・横断検索・
    /// クイックオープンなど同じ定義を共有する他画面からもロックファイルが見えなくなって
    /// しまう（検索したい場面があるため、それらの画面からは見えたままにする必要がある）。
    /// そのためロックファイルは除外（<see cref="ContextFileNode.IsExcluded"/>）扱いにはせず、
    /// あくまで<see cref="ContextCollectViewModel"/>がファイルツリーを初期表示する際の
    /// 既定選択状態（3状態選択の既定値）だけをここで判定できるようにする。チェックボックス
    /// （3状態切替アイコン）自体は有効なままなので、ユーザーは自分の判断でオンに戻せる。
    ///
    /// package-lock.json等のロックファイルは、コミットされるのが通例のため.gitignoreにも
    /// 載らず、テキストファイルなのでバイナリ判定にもかからない。数万行になることもあり、
    /// AIへ渡す文脈としては存在（構成）だけ分かれば十分でトークンの無駄になりやすい。
    /// 一方、go.mod・package.json・Cargo.toml等の依存関係マニフェスト自体は依存関係の
    /// 把握に有用なため、ここには含めない（既定どおり内容も出す）。
    /// </summary>
    public static IReadOnlyList<string> LockFileNamesForInitialUncheck { get; } = new[]
    {
        "package-lock.json", "yarn.lock", "pnpm-lock.yaml", "npm-shrinkwrap.json",
        "Cargo.lock", "composer.lock", "Gemfile.lock", "poetry.lock", "Pipfile.lock",
        "go.sum", "packages.lock.json", "pubspec.lock", "Podfile.lock",
    };

    private static readonly HashSet<string> LockFileNameSet =
        new(LockFileNamesForInitialUncheck, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 指定の相対パスが、コンテキスト収集の初期チェック状態を外すロックファイルかどうか。
    /// ファイル名（拡張子込み・大文字小文字区別なし）の完全一致で判定する。
    /// </summary>
    public static bool IsLockFileForInitialUncheck(string relativePath)
        => LockFileNameSet.Contains(Path.GetFileName(relativePath));

    /// <summary>
    /// バイナリとみなす拡張子（仕様書10.2）。エクスプローラ・横断検索と共有する。
    /// </summary>
    public static IReadOnlySet<string> BinaryFileExtensions => BinaryExtensions;

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".pdb", ".so", ".dylib", ".bin", ".dat", ".db", ".sqlite", ".sqlite3",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".tiff",
        ".pdf", ".zip", ".7z", ".rar", ".tar", ".gz", ".xz",
        ".mp3", ".mp4", ".avi", ".mov", ".wav", ".flac", ".ogg",
        ".ttf", ".otf", ".woff", ".woff2", ".class", ".pyc", ".jar", ".war",
        ".o", ".a", ".lib", ".msi", ".iso", ".node",
    };

    private const long MaxFileSizeBytes = 1024 * 1024;

    private static readonly IReadOnlySet<string> EmptyPathSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 拡張子からMarkdownコードフェンスの言語識別子を判定するための対応表。前半は
    /// <see cref="LanguageRule.ForExtension"/>（構文強調）が対応する拡張子と同じ言語対応にして
    /// あり（4.8.4: 出力パイプライン共有の一環。ただし LanguageRule.Name は
    /// "JavaScript/TypeScript" 等フェンス識別子に使えない表記のため、対応表自体はここに
    /// 独自に持つ）、後半は構文強調こそ未対応だがAIが言語を認識できたほうが解釈精度が
    /// 上がる拡張子を補助的に追加している（配色は持たないため Themes/Syntax.xaml 側の
    /// 対応表を増やす必要は無い）。
    /// </summary>
    private static readonly Dictionary<string, string> FenceLanguagesByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        ["py"] = "python", ["cs"] = "csharp",
        ["js"] = "javascript", ["jsx"] = "jsx", ["ts"] = "typescript", ["tsx"] = "tsx",
        ["html"] = "html", ["htm"] = "html", ["xml"] = "xml", ["md"] = "markdown",
        ["css"] = "css", ["json"] = "json", ["yaml"] = "yaml", ["yml"] = "yaml",
        ["sql"] = "sql", ["sh"] = "bash", ["bash"] = "bash",
        ["go"] = "go", ["rs"] = "rust", ["java"] = "java", ["kt"] = "kotlin", ["kts"] = "kotlin",
        ["rb"] = "ruby", ["php"] = "php", ["c"] = "c", ["h"] = "c", ["cpp"] = "cpp", ["cc"] = "cpp",
        ["hpp"] = "cpp", ["swift"] = "swift", ["toml"] = "toml", ["ini"] = "ini", ["cfg"] = "ini",
        ["ps1"] = "powershell", ["axaml"] = "xml", ["xaml"] = "xml", ["csproj"] = "xml",
        ["vue"] = "vue", ["lua"] = "lua", ["r"] = "r", ["scala"] = "scala", ["dart"] = "dart",
        ["graphql"] = "graphql", ["proto"] = "protobuf", ["txt"] = "text", ["editorconfig"] = "ini",
    };

    /// <summary>拡張子を持たない慣例的なファイル名からの言語判定（Dockerfile等）。</summary>
    private static readonly Dictionary<string, string> FenceLanguagesByFileName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Dockerfile"] = "dockerfile",
        ["Makefile"] = "makefile",
    };

    private readonly AppPaths _paths;
    private readonly JsonFileStore _jsonStore = new();

    public ContextCollector(AppPaths paths)
    {
        _paths = paths;
    }

    /// <summary>対象フォルダを走査して選択用のツリーを返す（除外規則を反映済み）。</summary>
    public async Task<GraftResult<IReadOnlyList<ContextFileNode>>> ScanAsync(Project project, Settings settings, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(project.Root) || !Directory.Exists(project.Root))
        {
            return GraftResult<IReadOnlyList<ContextFileNode>>.Fail(ErrorCode.E201, "プロジェクトルートが存在しません", path: project.Root);
        }
        var filter = await BuildFilterAsync(project, settings, ct).ConfigureAwait(false);
        var nodes = new List<ContextFileNode>();
        WalkDirectory(project.Root, project.Root, filter, nodes, ct);
        return GraftResult<IReadOnlyList<ContextFileNode>>.Ok(nodes);
    }

    /// <summary>10.3の出力形式でテキストを生成する。</summary>
    public async Task<GraftResult<ContextResult>> CollectAsync(ContextRequest request, CancellationToken ct = default)
    {
        var scan = await ScanAsync(request.Project, request.Settings, ct).ConfigureAwait(false);
        if (!scan.IsSuccess) return GraftResult<ContextResult>.Fail(scan.Issues);
        var files = scan.Value;

        var targetsResult = await ResolveTargetsAsync(request, files, ct).ConfigureAwait(false);
        if (!targetsResult.IsSuccess) return GraftResult<ContextResult>.Fail(targetsResult.Issues);

        // 3状態のツリー注記（構成のみの注記）・非表示（出さない）は TreeAndSelected モードでのみ
        // 意味を持つ。TreeOnly はファイル選択UI自体が無効（ContextCollectViewModel.ShowFileTree＝
        // false）で、常にフル構成を無注記で出す既存の意味を変えないため、モードで明示的に
        // 制御する（他モードでは request.HiddenPaths を渡されても無視する。これにより
        // ビュー側の実装ミスで意図せず他モードに影響することも防げる）。
        var hiddenPaths = request.Mode == ContextMode.TreeAndSelected
            ? new HashSet<string>(request.HiddenPaths, StringComparer.OrdinalIgnoreCase)
            : EmptyPathSet;

        // 「出さない」（HiddenPaths）は本文からも除く。SelectedPaths・HiddenPathsの両方に
        // 同じパスが渡された場合（呼び出し側の不整合）でも「出さない」を優先する。
        var contentTargets = hiddenPaths.Count == 0
            ? targetsResult.Value
            : targetsResult.Value.Where(n => !hiddenPaths.Contains(n.RelativePath)).ToArray();
        var contentPaths = new HashSet<string>(contentTargets.Select(n => n.RelativePath), StringComparer.OrdinalIgnoreCase);
        var annotateContent = request.Mode == ContextMode.TreeAndSelected ? contentPaths : null;

        var treeVisibleCount = IncludesTree(request.Mode) ? CountVisibleTreeFiles(files, hiddenPaths) : 0;

        var sb = new StringBuilder();
        AppendOverview(sb, request, treeVisibleCount, contentTargets.Count);
        AppendStandingContext(sb, request.Project.StandingContext);
        if (IncludesTree(request.Mode)) AppendTree(sb, files, hiddenPaths, annotateContent);

        var issues = new List<GraftIssue>(targetsResult.Issues);
        if (IncludesFiles(request.Mode))
        {
            var filesText = await BuildFileSectionsAsync(request.Project.Root, contentTargets, ct).ConfigureAwait(false);
            sb.Append(filesText.Value);
            issues.AddRange(filesText.Issues);
        }

        var text = sb.ToString();
        var tokens = TokenEstimator.Estimate(text, request.Settings.Context.TokenRatio);
        var exceeds = tokens > request.Settings.Context.TokenWarnThreshold;
        var result = new ContextResult { Text = text, EstimatedTokens = tokens, Files = files, ExceedsWarnThreshold = exceeds };
        return GraftResult<ContextResult>.Ok(result, issues);
    }

    /// <summary>
    /// {{tree}} 展開用に、見出しなしのツリー本文のみを返す。テンプレート変数は収集モード・
    /// 3状態選択と切り離された独立の変数のため、注記・非表示は行わずフル構成を返す
    /// （<see cref="CollectAsync"/>のコメント参照）。
    /// </summary>
    public async Task<GraftResult<string>> BuildTreeTextAsync(Project project, Settings settings, CancellationToken ct = default)
    {
        var scan = await ScanAsync(project, settings, ct).ConfigureAwait(false);
        if (!scan.IsSuccess) return GraftResult<string>.Fail(scan.Issues);
        return GraftResult<string>.Ok(BuildTreeText(scan.Value, EmptyPathSet, null));
    }

    /// <summary>{{files}} 展開用に、request のモードに従って選択されたファイルの全文のみを返す。</summary>
    public async Task<GraftResult<string>> BuildFilesTextAsync(ContextRequest request, CancellationToken ct = default)
    {
        var scan = await ScanAsync(request.Project, request.Settings, ct).ConfigureAwait(false);
        if (!scan.IsSuccess) return GraftResult<string>.Fail(scan.Issues);
        var targets = await ResolveTargetsAsync(request, scan.Value, ct).ConfigureAwait(false);
        if (!targets.IsSuccess) return GraftResult<string>.Fail(targets.Issues);
        return await BuildFileSectionsAsync(request.Project.Root, targets.Value, ct).ConfigureAwait(false);
    }

    private static bool IncludesTree(ContextMode mode) => mode is ContextMode.TreeOnly or ContextMode.TreeAndSelected;

    private static bool IncludesFiles(ContextMode mode)
        => mode is ContextMode.SelectedFiles or ContextMode.TreeAndSelected or ContextMode.ChangedSince;

    /// <summary>収集モードの日本語表示名。<c>ContextCollectViewModel.Modes</c>のラベルと対応させる。</summary>
    private static string ModeLabel(ContextMode mode) => mode switch
    {
        ContextMode.TreeOnly => "ツリーのみ",
        ContextMode.SelectedFiles => "選択ファイル",
        ContextMode.TreeAndSelected => "ツリー＋選択",
        ContextMode.ChangedSince => "差分のみ",
        _ => mode.ToString(),
    };

    /// <summary>
    /// 冒頭の概要セクション。AIが文脈を掴みやすいよう、プロジェクト名・生成日時・収集モード・
    /// ファイル数を最初に提示する。
    /// </summary>
    private static void AppendOverview(StringBuilder sb, ContextRequest request, int treeVisibleCount, int contentCount)
    {
        sb.AppendLine($"# {request.Project.DisplayName} — Graftコンテキスト");
        sb.AppendLine();
        sb.AppendLine($"- 生成日時: {DateTimeOffset.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"- 収集モード: {ModeLabel(request.Mode)}");
        if (IncludesTree(request.Mode)) sb.AppendLine($"- ファイル構成: {treeVisibleCount} 件");
        if (IncludesFiles(request.Mode)) sb.AppendLine($"- 内容を含むファイル: {contentCount} 件");
        sb.AppendLine();
    }

    private static void AppendStandingContext(StringBuilder sb, string? standingContext)
    {
        if (string.IsNullOrWhiteSpace(standingContext)) return;
        sb.AppendLine("# 前提");
        sb.AppendLine(standingContext.Trim());
        sb.AppendLine();
    }

    private static void AppendTree(StringBuilder sb, IReadOnlyList<ContextFileNode> files, IReadOnlySet<string> hiddenPaths, IReadOnlySet<string>? contentPaths)
    {
        sb.AppendLine("# プロジェクト構成");
        sb.Append(BuildTreeText(files, hiddenPaths, contentPaths));
        sb.AppendLine();
    }

    /// <summary>
    /// ツリー本文を組み立てる。フェンス付きコードブロック（```text）として出力し、AIが
    /// 「これはファイル構成であり、指示文の一部ではない」と誤読しないようにする。
    ///
    /// 要件G「ライブラリ等のファイルはファイル構成だけわかればいい」を満たすため、除外された
    /// ファイル・ディレクトリ（既定除外パターン・.gitignore・プロジェクト除外・バイナリ・
    /// サイズ超過）も名前だけはツリーに残す。ただしnode_modules等の除外ディレクトリは配下を
    /// 1ファイルずつ列挙せず、<see cref="ContextFileNode.ExcludedDescendantFileCount"/>を使って
    /// 「node_modules/  （1,234 ファイル、内容は非出力）」のように1行へ畳んで要約する
    /// （配下は<see cref="WalkDirectory"/>の時点で再帰走査していないため、そもそも個々の
    /// ファイルのノードが存在しない）。
    ///
    /// <paramref name="hiddenPaths"/>に含まれるファイル（3状態選択で「出さない」を選んだ非除外
    /// ファイル）、および配下の全ファイルが<paramref name="hiddenPaths"/>に含まれるディレクトリは
    /// ツリーから完全に除く。<paramref name="contentPaths"/>がnullでない場合、除外されておらず
    /// かつそこに含まれない非除外ファイルへ「(構成のみ・内容は省略)」と注記する
    /// （「構成だけ」の可視化）。nullの場合は注記しない（TreeOnlyモード等、3状態が意味を
    /// 持たない場面向け）。
    /// </summary>
    private static string BuildTreeText(IReadOnlyList<ContextFileNode> files, IReadOnlySet<string> hiddenPaths, IReadOnlySet<string>? contentPaths)
    {
        var hiddenDirs = ComputeHiddenDirectories(files, hiddenPaths);
        var sb = new StringBuilder();
        sb.AppendLine("```text");
        foreach (var node in files)
        {
            // 「出さない」（ユーザー操作による非表示）は除外ノードには適用しない
            // （除外ノードはそもそもユーザーが3状態を選べない対象のため）。
            if (!node.IsExcluded)
            {
                if (node.IsDirectory)
                {
                    if (hiddenDirs.Contains(node.RelativePath)) continue;
                }
                else if (hiddenPaths.Contains(node.RelativePath))
                {
                    continue;
                }
            }

            var depth = node.RelativePath.Count(c => c == '/');
            var nameStart = node.RelativePath.LastIndexOf('/') + 1;
            var name = node.RelativePath[nameStart..];
            sb.Append(' ', depth * 2).Append(name);

            if (node.IsDirectory)
            {
                sb.Append('/');
                if (node.IsExcluded) sb.Append(FormatExcludedDirectorySuffix(node));
            }
            else if (node.IsExcluded)
            {
                sb.Append("  (").Append(node.ExcludeReason).Append("・内容は非出力)");
            }
            else if (contentPaths is not null && !contentPaths.Contains(node.RelativePath))
            {
                sb.Append("  (構成のみ・内容は省略)");
            }
            sb.AppendLine();
        }
        sb.AppendLine("```");
        return sb.ToString();
    }

    /// <summary>除外ディレクトリの行に添える要約サフィックス。件数が分かれば件数付きで、分からなければ件数無しで示す。</summary>
    private static string FormatExcludedDirectorySuffix(ContextFileNode node)
        => node.ExcludedDescendantFileCount is int count && count > 0
            ? $"  （{count:N0} ファイル、内容は非出力）"
            : "  （内容は非出力）";

    /// <summary>ツリーに表示されるファイル数（除外・非表示指定を問わず、ツリーに行として現れる全ファイル）。</summary>
    private static int CountVisibleTreeFiles(IReadOnlyList<ContextFileNode> files, IReadOnlySet<string> hiddenPaths)
        => files.Count(f => !f.IsDirectory && !hiddenPaths.Contains(f.RelativePath));

    /// <summary>
    /// 「配下の非除外ファイルが1件以上あり、かつその全部が<paramref name="hiddenPaths"/>に
    /// 含まれる」ディレクトリの集合を求める。既存の「実際に空のディレクトリはそのまま表示する」
    /// 挙動（配下ファイルが0件のケース）を変えないよう、配下ファイルが1件も無い場合は
    /// 対象に含めない。
    /// </summary>
    private static HashSet<string> ComputeHiddenDirectories(IReadOnlyList<ContextFileNode> files, IReadOnlySet<string> hiddenPaths)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (hiddenPaths.Count == 0) return result;

        var totals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var hiddenCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            if (f.IsDirectory || f.IsExcluded) continue;
            var isHidden = hiddenPaths.Contains(f.RelativePath);
            foreach (var ancestor in AncestorsOf(f.RelativePath))
            {
                totals[ancestor] = totals.GetValueOrDefault(ancestor) + 1;
                if (isHidden) hiddenCounts[ancestor] = hiddenCounts.GetValueOrDefault(ancestor) + 1;
            }
        }

        foreach (var (dir, total) in totals)
        {
            if (total > 0 && hiddenCounts.GetValueOrDefault(dir) == total) result.Add(dir);
        }
        return result;
    }

    /// <summary>相対パスの祖先ディレクトリのパスを、直近の親から順に返す（ルート直下は返さない）。</summary>
    private static IEnumerable<string> AncestorsOf(string relativePath)
    {
        var path = relativePath;
        int idx;
        while ((idx = path.LastIndexOf('/')) >= 0)
        {
            path = path[..idx];
            yield return path;
        }
    }

    /// <summary>選択対象ファイルの本文セクションを連結して返す（CollectAsync・BuildFilesTextAsyncで共有）。</summary>
    private async Task<GraftResult<string>> BuildFileSectionsAsync(string root, IReadOnlyList<ContextFileNode> targets, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var issues = new List<GraftIssue>();
        foreach (var node in targets)
        {
            var issue = await AppendFileSectionAsync(sb, root, node, ct).ConfigureAwait(false);
            if (issue is not null) issues.Add(issue);
        }
        return GraftResult<string>.Ok(sb.ToString(), issues);
    }

    private async Task<GraftIssue?> AppendFileSectionAsync(StringBuilder sb, string root, ContextFileNode node, CancellationToken ct)
    {
        var fullPath = Path.Combine(root, node.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        var read = await FileTextIO.ReadAsync(fullPath, ct).ConfigureAwait(false);
        if (!read.IsSuccess)
        {
            return GraftIssue.Of(ErrorCode.E204, "コンテキスト収集時にファイルを読み込めませんでした", path: node.RelativePath, severity: Severity.Warning);
        }
        var (text, _) = read.Value;
        var hash = FileTextIO.ShortHash(FileTextIO.ComputeHash(text));
        sb.AppendLine($"# {node.RelativePath}  ({hash})");
        sb.AppendLine();
        AppendCodeBlock(sb, text, DetectLanguage(node.RelativePath));
        sb.AppendLine();
        return null;
    }

    /// <summary>
    /// 言語名付きのフェンス付きコードブロックを追加する。本文が3連バッククォートを含む場合は
    /// 4連バッククォートへ切り替え、コードブロックの入れ子で出力構造が壊れるのを防ぐ。
    /// </summary>
    private static void AppendCodeBlock(StringBuilder sb, string content, string language)
    {
        var fence = content.Contains("```", StringComparison.Ordinal) ? "````" : "```";
        sb.Append(fence).AppendLine(language);
        sb.Append(content);
        if (content.Length == 0 || content[^1] != '\n') sb.AppendLine();
        sb.AppendLine(fence);
    }

    /// <summary>拡張子・慣例的なファイル名からMarkdownコードフェンスの言語識別子を判定する。未対応なら空文字。</summary>
    private static string DetectLanguage(string relativePath)
    {
        var extension = Path.GetExtension(relativePath);
        if (extension.Length > 1 && FenceLanguagesByExtension.TryGetValue(extension[1..], out var byExtension))
        {
            return byExtension;
        }

        var fileName = Path.GetFileName(relativePath);
        return FenceLanguagesByFileName.TryGetValue(fileName, out var byFileName) ? byFileName : string.Empty;
    }

    private async Task<GraftResult<IReadOnlyList<ContextFileNode>>> ResolveTargetsAsync(
        ContextRequest request, IReadOnlyList<ContextFileNode> files, CancellationToken ct)
    {
        if (request.Mode == ContextMode.TreeOnly)
        {
            return GraftResult<IReadOnlyList<ContextFileNode>>.Ok(Array.Empty<ContextFileNode>());
        }

        if (request.Mode == ContextMode.ChangedSince)
        {
            if (request.SinceRevision is null) return GraftResult<IReadOnlyList<ContextFileNode>>.Ok(Array.Empty<ContextFileNode>());
            var changed = await LoadChangedPathsAsync(request.Project, request.SinceRevision.Value, ct).ConfigureAwait(false);
            if (!changed.IsSuccess) return GraftResult<IReadOnlyList<ContextFileNode>>.Fail(changed.Issues);
            return GraftResult<IReadOnlyList<ContextFileNode>>.Ok(ResolvePaths(files, changed.Value));
        }

        return GraftResult<IReadOnlyList<ContextFileNode>>.Ok(ResolvePaths(files, request.SelectedPaths));
    }

    private static IReadOnlyList<ContextFileNode> ResolvePaths(IReadOnlyList<ContextFileNode> files, IReadOnlyList<string> paths)
    {
        var byPath = files.Where(f => !f.IsDirectory && !f.IsExcluded)
            .ToDictionary(f => Normalize(f.RelativePath), StringComparer.OrdinalIgnoreCase);
        var result = new List<ContextFileNode>();
        foreach (var raw in paths)
        {
            if (byPath.TryGetValue(Normalize(raw), out var node)) result.Add(node);
        }
        return result;
    }

    /// <summary>
    /// 指定リビジョン番号より新しいリビジョンのmanifest.jsonをAppPathsから直接読み、変更された
    /// ファイルのパス一覧を集める。RevisionStoreへの依存を避けるための実装（10.1差分モード）。
    /// </summary>
    private async Task<GraftResult<IReadOnlyList<string>>> LoadChangedPathsAsync(Project project, int sinceRevision, CancellationToken ct)
    {
        var backupDir = _paths.GetProjectBackupDirectory(project.Id);
        if (!Directory.Exists(backupDir)) return GraftResult<IReadOnlyList<string>>.Ok(Array.Empty<string>());

        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in Directory.EnumerateDirectories(backupDir))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(dir);
            if (!TryParseRevisionNumber(name, out var revision) || revision <= sinceRevision) continue;

            var manifestPath = Path.Combine(dir, "manifest.json");
            var result = await _jsonStore.ValidateJsonAsync<RevisionManifest>(manifestPath, ct: ct).ConfigureAwait(false);
            if (!result.IsSuccess) continue;
            foreach (var entry in result.Value.Entries) changed.Add(entry.Path);
        }
        return GraftResult<IReadOnlyList<string>>.Ok(changed.ToArray());
    }

    private static bool TryParseRevisionNumber(string folderName, out int revision)
    {
        revision = 0;
        if (!folderName.StartsWith('r')) return false;
        var underscoreIndex = folderName.IndexOf('_');
        var numPart = underscoreIndex > 0 ? folderName[1..underscoreIndex] : folderName[1..];
        return int.TryParse(numPart, out revision);
    }

    private static async Task<GitignoreFilter> BuildFilterAsync(Project project, Settings settings, CancellationToken ct)
    {
        var defaultFilter = GitignoreFilter.FromPatterns(DefaultExcludePatterns, "既定除外");
        var gitignoreFilter = settings.Context.RespectGitignore
            ? await GitignoreFilter.LoadAsync(project.Root, ct).ConfigureAwait(false)
            : GitignoreFilter.Empty;
        var overrideFilter = GitignoreFilter.FromPatterns(project.Overrides.Excludes, "プロジェクト設定");
        return defaultFilter.Merge(gitignoreFilter).Merge(overrideFilter);
    }

    private static void WalkDirectory(string root, string currentDir, GitignoreFilter filter, List<ContextFileNode> nodes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        List<string> dirEntries;
        List<string> fileEntries;
        try
        {
            dirEntries = Directory.EnumerateDirectories(currentDir).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToList();
            fileEntries = Directory.EnumerateFiles(currentDir).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var dir in dirEntries)
        {
            var rel = ToRelative(root, dir);
            var (ignored, label) = filter.Evaluate(rel, true);
            nodes.Add(new ContextFileNode
            {
                RelativePath = rel,
                IsDirectory = true,
                SizeBytes = 0,
                IsExcluded = ignored,
                ExcludeReason = ignored ? ReasonOf(label) : null,
                // 要件G: 除外ディレクトリは配下を再帰的に走査しない代わりに、ツリー表示で
                // 「N ファイル、内容は非出力」と畳んで要約できるよう総ファイル数だけ数える。
                ExcludedDescendantFileCount = ignored ? CountDescendantFiles(dir) : null,
            });
            if (!ignored) WalkDirectory(root, dir, filter, nodes, ct);
        }

        foreach (var file in fileEntries) nodes.Add(BuildFileNode(root, file, filter));
    }

    /// <summary>
    /// 除外ディレクトリ配下の総ファイル数を数える（要件G）。個々のファイルを
    /// <see cref="ContextFileNode"/>として保持せず件数だけを求めるため、node_modules等の
    /// 巨大フォルダでもツリー生成のコストを抑えられる。読み取り権限が無い等で数えられない
    /// 場合はnull（ツリー上は件数無しの「内容は非出力」表示になる）。
    /// </summary>
    private static int? CountDescendantFiles(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Count();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static ContextFileNode BuildFileNode(string root, string fullPath, GitignoreFilter filter)
    {
        var rel = ToRelative(root, fullPath);
        var (ignored, label) = filter.Evaluate(rel, false);
        long size = 0;
        try
        {
            size = new FileInfo(fullPath).Length;
        }
        catch (IOException)
        {
            // サイズ取得に失敗しても走査自体は続行する
        }

        if (ignored) return new ContextFileNode { RelativePath = rel, SizeBytes = size, IsExcluded = true, ExcludeReason = ReasonOf(label) };
        if (BinaryExtensions.Contains(Path.GetExtension(fullPath)))
        {
            return new ContextFileNode { RelativePath = rel, SizeBytes = size, IsExcluded = true, ExcludeReason = "バイナリファイルのため除外" };
        }
        if (size > MaxFileSizeBytes)
        {
            return new ContextFileNode { RelativePath = rel, SizeBytes = size, IsExcluded = true, ExcludeReason = "サイズが1MBを超過" };
        }
        return new ContextFileNode { RelativePath = rel, SizeBytes = size, IsExcluded = false, ExcludeReason = null };
    }

    private static string? ReasonOf(string? label) => label switch
    {
        "既定除外" => "既定の除外パターンに一致",
        ".gitignore" => ".gitignoreに一致",
        "プロジェクト設定" => "プロジェクト設定の除外パターンに一致",
        _ => "除外パターンに一致",
    };

    private static string ToRelative(string root, string fullPath) => Path.GetRelativePath(root, fullPath).Replace('\\', '/');

    private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');
}
