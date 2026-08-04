using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Graft.Core;
using Graft.Infra;

namespace Graft.Features;

/// <summary>ファイル横断検索（Ctrl+Shift+F）の検索条件。エディタ内オーバーレイとも
/// <see cref="SearchPatternBuilder"/>を共有する（4.4節）。</summary>
public sealed record CrossFileSearchOptions
{
    /// <summary>検索文字列。空の場合は何も検索しない。</summary>
    public required string Query { get; init; }
    /// <summary>正規表現として解釈するかどうか。</summary>
    public bool UseRegex { get; init; }
    /// <summary>大文字・小文字を区別するかどうか。</summary>
    public bool CaseSensitive { get; init; }
    /// <summary>単語単位（\b境界）で一致させるかどうか。</summary>
    public bool WholeWord { get; init; }
    /// <summary>1ファイルあたりのヒット上限（暴走防止、18章）。</summary>
    public int MaxHitsPerFile { get; init; } = 500;
    /// <summary>全体のヒット上限（暴走防止、18章）。</summary>
    public int MaxTotalHits { get; init; } = 5000;
}

/// <summary>検索・置換で共有する正規表現の組み立て。不正な正規表現は例外にせず失敗として返す
/// （4.4節「正規表現が不正な場合はエラーにせず、その旨を表示する」）。</summary>
public static class SearchPatternBuilder
{
    public static (Regex? Regex, string? Error) TryBuild(
        string query, bool useRegex, bool caseSensitive, bool wholeWord)
    {
        if (string.IsNullOrEmpty(query)) return (null, null);

        var body = useRegex ? query : Regex.Escape(query);
        var pattern = wholeWord ? $@"\b(?:{body})\b" : body;
        var options = RegexOptions.CultureInvariant | (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
        try
        {
            return (new Regex(pattern, options, TimeSpan.FromSeconds(2)), null);
        }
        catch (ArgumentException ex)
        {
            return (null, $"正規表現が不正です: {ex.Message}");
        }
    }
}

/// <summary>1件のヒット。</summary>
public sealed record SearchHit
{
    public required string FullPath { get; init; }
    /// <summary>プロジェクトルートからの相対パス（区切りは "/")。</summary>
    public required string RelativePath { get; init; }
    /// <summary>1始まりの行番号。</summary>
    public required int LineNumber { get; init; }
    /// <summary>行全体のテキスト（改行文字を含まない）。</summary>
    public required string LineText { get; init; }
    /// <summary>行内での一致開始位置（0始まり、文字単位）。</summary>
    public required int ColumnStart { get; init; }
    /// <summary>一致文字列の長さ。</summary>
    public required int MatchLength { get; init; }
}

/// <summary>検索実行1回分の進行状態。列挙中・列挙後の両方で参照できるよう可変で公開する。</summary>
public sealed class SearchRunState
{
    /// <summary>検索対象として走査したファイル数。</summary>
    public int FilesScanned { get; internal set; }
    /// <summary>これまでに返したヒット総数。</summary>
    public int TotalHits { get; internal set; }
    /// <summary>クエリ・正規表現が不正だった場合のメッセージ。</summary>
    public string? PatternError { get; internal set; }
    /// <summary>全体のヒット上限に達して打ち切ったかどうか。</summary>
    public bool TruncatedByTotalLimit { get; internal set; }
    /// <summary>1ファイルあたりの上限に達したファイルの相対パス一覧（打ち切りをUIへ明示するため）。</summary>
    public List<string> FilesTruncatedByPerFileLimit { get; } = new();
}

/// <summary>置換の実行結果。</summary>
public sealed record ReplaceOutcome
{
    /// <summary>置換した件数の合計。</summary>
    public int ReplacedCount { get; init; }
    /// <summary>内容が変化し書き込んだファイル数。</summary>
    public int FilesChanged { get; init; }
    /// <summary>読み込み・書き込みに失敗したファイル（相対パスと理由）。</summary>
    public IReadOnlyList<(string RelativePath, string Reason)> Failures { get; init; } = Array.Empty<(string, string)>();
}

/// <summary>
/// ファイル横断検索（4.4節）の実体。WPF非依存（テストプロジェクトが直接コンパイルするため）。
/// <see cref="GitignoreFilter"/>（10章の除外規則）を<c>Features/ContextCollector</c>と同じ
/// 考え方で再利用し、既定除外・.gitignore・プロジェクト設定の3層を合成する。
/// 18章の要件により、結果は<see cref="IAsyncEnumerable{T}"/>で逐次返し、<see cref="CancellationToken"/>で
/// 中断できる。ディレクトリ単位で除外を判定してから再帰するため、除外フォルダの配下は
/// 読み込みすら行わない。
/// </summary>
public sealed class CrossFileSearchEngine
{
    // 既定の除外パターンとバイナリ拡張子は ContextCollector を唯一の定義とし共有する（10.2章）。

    private const int BinarySniffBytes = 8000;

    /// <summary>プロジェクト配下を検索し、ヒットを見つけ次第逐次返す。</summary>
    public async IAsyncEnumerable<SearchHit> SearchAsync(
        Project project, Settings settings, CrossFileSearchOptions options, SearchRunState state,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(state);

        var (regex, error) = SearchPatternBuilder.TryBuild(
            options.Query, options.UseRegex, options.CaseSensitive, options.WholeWord);
        state.PatternError = error;
        if (regex is null || !Directory.Exists(project.Root)) yield break;

        var filter = await BuildFilterAsync(project, settings, ct).ConfigureAwait(false);
        await foreach (var hit in WalkAsync(project.Root, project.Root, filter, regex, options, state, ct)
            .ConfigureAwait(false))
        {
            yield return hit;
        }
    }

    /// <summary>指定ファイル群に対して同じ条件で一括置換する（サイドビューの確認後に呼ぶ）。</summary>
    public async Task<ReplaceOutcome> ReplaceInFilesAsync(
        IReadOnlyCollection<string> fullPaths, string projectRoot, CrossFileSearchOptions options,
        string replacement, CancellationToken ct = default)
    {
        var (regex, _) = SearchPatternBuilder.TryBuild(
            options.Query, options.UseRegex, options.CaseSensitive, options.WholeWord);
        if (regex is null) return new ReplaceOutcome();

        var safeReplacement = options.UseRegex ? replacement : replacement.Replace("$", "$$", StringComparison.Ordinal);
        var replaced = 0;
        var changedFiles = 0;
        var failures = new List<(string, string)>();

        foreach (var fullPath in fullPaths)
        {
            ct.ThrowIfCancellationRequested();
            var relative = ToRelative(projectRoot, fullPath);
            var outcome = await ReplaceOneFileAsync(fullPath, regex, safeReplacement, ct).ConfigureAwait(false);
            if (outcome.Error is not null) { failures.Add((relative, outcome.Error)); continue; }
            replaced += outcome.Count;
            if (outcome.Count > 0) changedFiles++;
        }

        return new ReplaceOutcome { ReplacedCount = replaced, FilesChanged = changedFiles, Failures = failures };
    }

    private static async Task<(int Count, string? Error)> ReplaceOneFileAsync(
        string fullPath, Regex regex, string safeReplacement, CancellationToken ct)
    {
        var read = await FileTextIO.ReadAsync(fullPath, ct).ConfigureAwait(false);
        if (!read.IsSuccess) return (0, read.Issues.FirstOrDefault()?.ToDisplayText() ?? "読み込みに失敗しました");

        var (text, shape) = read.Value;
        var count = regex.Matches(text).Count;
        if (count == 0) return (0, null);

        var newText = regex.Replace(text, safeReplacement);
        var write = await FileTextIO.WriteAsync(fullPath, newText, shape, ct).ConfigureAwait(false);
        if (!write.IsSuccess) return (0, write.Issues.FirstOrDefault()?.ToDisplayText() ?? "書き込みに失敗しました");
        return (count, null);
    }

    private static async IAsyncEnumerable<SearchHit> WalkAsync(
        string root, string dir, GitignoreFilter filter, Regex regex, CrossFileSearchOptions options,
        SearchRunState state, [EnumeratorCancellation] CancellationToken ct)
    {
        if (state.TruncatedByTotalLimit) yield break;

        List<string> dirEntries;
        List<string> fileEntries;
        try
        {
            dirEntries = Directory.EnumerateDirectories(dir).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToList();
            fileEntries = Directory.EnumerateFiles(dir).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var subDir in dirEntries)
        {
            ct.ThrowIfCancellationRequested();
            if (state.TruncatedByTotalLimit) yield break;
            var rel = ToRelative(root, subDir);
            if (filter.IsIgnored(rel, isDirectory: true)) continue;

            await foreach (var hit in WalkAsync(root, subDir, filter, regex, options, state, ct).ConfigureAwait(false))
            {
                yield return hit;
            }
        }

        foreach (var file in fileEntries)
        {
            ct.ThrowIfCancellationRequested();
            if (state.TruncatedByTotalLimit) yield break;

            var rel = ToRelative(root, file);
            if (filter.IsIgnored(rel, isDirectory: false)) continue;
            if (ContextCollector.BinaryFileExtensions.Contains(Path.GetExtension(file))) continue;
            if (await LooksBinaryAsync(file, ct).ConfigureAwait(false)) continue;

            await foreach (var hit in ScanFileAsync(file, rel, regex, options, state, ct).ConfigureAwait(false))
            {
                yield return hit;
            }
        }
    }

    private static async IAsyncEnumerable<SearchHit> ScanFileAsync(
        string fullPath, string relativePath, Regex regex, CrossFileSearchOptions options, SearchRunState state,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var read = await FileTextIO.ReadAsync(fullPath, ct).ConfigureAwait(false);
        state.FilesScanned++;
        if (!read.IsSuccess) yield break;

        var lines = TextNormalizer.SplitLines(read.Value.Text);
        var hitsInFile = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            foreach (Match m in regex.Matches(lines[i]))
            {
                if (hitsInFile >= options.MaxHitsPerFile)
                {
                    state.FilesTruncatedByPerFileLimit.Add(relativePath);
                    yield break;
                }
                if (state.TotalHits >= options.MaxTotalHits) { state.TruncatedByTotalLimit = true; yield break; }

                hitsInFile++;
                state.TotalHits++;
                yield return new SearchHit
                {
                    FullPath = fullPath,
                    RelativePath = relativePath,
                    LineNumber = i + 1,
                    LineText = lines[i],
                    ColumnStart = m.Index,
                    MatchLength = m.Length,
                };
            }
        }
    }

    private static async Task<bool> LooksBinaryAsync(string fullPath, CancellationToken ct)
    {
        try
        {
            var ioPath = LongPath.Extended(fullPath);
            await using var stream = new FileStream(ioPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, useAsync: true);
            var length = (int)Math.Min(BinarySniffBytes, stream.Length);
            if (length == 0) return false;

            var buffer = new byte[length];
            var read = await stream.ReadAsync(buffer.AsMemory(0, length), ct).ConfigureAwait(false);
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] == 0) return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true; // 読めないファイルは安全側で除外する
        }
    }

    private static async Task<GitignoreFilter> BuildFilterAsync(Project project, Settings settings, CancellationToken ct)
    {
        var defaultFilter = GitignoreFilter.FromPatterns(ContextCollector.DefaultExcludePatterns, "既定除外");
        var gitignoreFilter = settings.Context.RespectGitignore
            ? await GitignoreFilter.LoadAsync(project.Root, ct).ConfigureAwait(false)
            : GitignoreFilter.Empty;
        var overrideFilter = GitignoreFilter.FromPatterns(project.Overrides.Excludes, "プロジェクト設定");
        return defaultFilter.Merge(gitignoreFilter).Merge(overrideFilter);
    }

    private static string ToRelative(string root, string fullPath) => Path.GetRelativePath(root, fullPath).Replace('\\', '/');
}
