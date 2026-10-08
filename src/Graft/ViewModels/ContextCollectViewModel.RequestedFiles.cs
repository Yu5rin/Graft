using Graft.Features;

namespace Graft.ViewModels;

/// <summary>
/// <see cref="ContextCollectViewModel.ApplyRequestedFilesAsync"/> の結果。AIが求めたファイルのうち、
/// どれが「内容も出す」になり、どれがならなかったか（なぜか）を利用者へ示すために持つ。
/// </summary>
/// <param name="Matched">「内容も出す」にしたファイルのプロジェクト相対パス（重複なし）。</param>
/// <param name="Excluded">プロジェクト内にあるが、除外されていて選べなかったもの。</param>
/// <param name="Directories">ファイルではなくフォルダを指していたもの。</param>
/// <param name="NotFound">プロジェクト内に見つからなかったパス（AIが書いたまま）。</param>
/// <param name="ModeChanged">選んだファイルが出力に載るよう、収集モードを切り替えたか。</param>
public sealed record RequestedFilesResult(
    IReadOnlyList<string> Matched,
    IReadOnlyList<RequestedFileMatch> Excluded,
    IReadOnlyList<RequestedFileMatch> Directories,
    IReadOnlyList<string> NotFound,
    bool ModeChanged)
{
    /// <summary>何も要求されなかった場合の結果。</summary>
    public static RequestedFilesResult Empty { get; } = new(
        Array.Empty<string>(), Array.Empty<RequestedFileMatch>(), Array.Empty<RequestedFileMatch>(),
        Array.Empty<string>(), ModeChanged: false);

    /// <summary>要求されたパスの総数（重複を除いた、結果の4区分の合計）。</summary>
    public int TotalCount => Matched.Count + Excluded.Count + Directories.Count + NotFound.Count;

    /// <summary>要求されたファイルがすべて「内容も出す」になったか。</summary>
    public bool IsFullyApplied => TotalCount > 0 && Matched.Count == TotalCount;
}

/// <summary>
/// コンテキスト収集ViewModelのうち、AIの「このファイルも見せて」（E710）を選択状態へ反映する部分。
/// 本体が大きいため分割ファイルにしている。
/// </summary>
public sealed partial class ContextCollectViewModel
{
    /// <summary>ステータス表示でパスを並べる最大件数。超えた分は件数にまとめる。</summary>
    private const int MaxListedPaths = 8;

    /// <summary>
    /// AIが求めたファイルを「内容も出す」に設定する。**求められたファイル以外の状態は変えない**。
    ///
    /// 【設計判断】
    /// <list type="bullet">
    /// <item>照合は走査結果（<c>_lastScan</c>）に対して<see cref="RequestedFileMatcher"/>で行い、区切り文字や
    /// 大文字小文字の違いを吸収する。見つからなかったもの・除外されていて選べないものは、黙って
    /// 落とさず、どれがなぜ該当しなかったかをステータス欄へ書く（AIに渡したつもりで欠けている、を防ぐ）。</item>
    /// <item>状態の保存は、手でチェックを切り替えたときと同じ
    /// <see cref="PersistFileStatesAsync"/>（<c>ProjectOverrides.ContextFileStates</c>）に乗せる。
    /// 別の保存経路を作ると、画面を開き直したときに片方が反映されない食い違いを生む。
    /// 手動操作のデバウンス（300ms）は待たずその場で保存する。ウィンドウを開いてすぐ閉じても
    /// 反映が失われないようにするため。</item>
    /// <item>収集モードが「ツリーのみ」「差分のみ」のときは、選んだファイルがそもそも出力に載らない
    /// （ファイル一覧も操作できない）。それでは「追加したのに出ない」状態になるので、通常の既定である
    /// 「ツリー＋選択」へ切り替え、その旨をステータスに書く。「選択ファイル」「ツリー＋選択」は
    /// 利用者の選択を尊重してそのまま。</item>
    /// </list>
    /// まだ一度も走査していない場合は先に走査する。
    /// </summary>
    /// <param name="requestedPaths">AIが要求したパス（書かれたまま。区切り文字などは照合時に整える）。</param>
    public async Task<RequestedFilesResult> ApplyRequestedFilesAsync(
        IReadOnlyList<string> requestedPaths, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requestedPaths);
        if (requestedPaths.Count == 0) return RequestedFilesResult.Empty;

        if (_lastScan.Count == 0 && !IsScanning)
        {
            await RefreshAsync(ct).ConfigureAwait(true);
        }

        var matches = RequestedFileMatcher.Match(_lastScan, requestedPaths, _project.Root);
        var nodesByPath = Files.Where(f => !f.IsDirectory)
            .GroupBy(f => f.RelativePath, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var matched = new List<string>();
        var excluded = new List<RequestedFileMatch>();
        var directories = new List<RequestedFileMatch>();
        var notFound = new List<string>();
        foreach (var match in matches)
        {
            switch (match.Kind)
            {
                case RequestedFileMatchKind.Matched
                    when match.RelativePath is { } path && nodesByPath.TryGetValue(path, out var node) && !node.IsExcluded:
                    node.State = ContextFileState.Full;
                    if (!matched.Contains(path, StringComparer.Ordinal)) matched.Add(path);
                    break;
                case RequestedFileMatchKind.Excluded:
                    if (!excluded.Any(e => e.RelativePath == match.RelativePath)) excluded.Add(match);
                    break;
                case RequestedFileMatchKind.Directory:
                    if (!directories.Any(e => e.RelativePath == match.RelativePath)) directories.Add(match);
                    break;
                default:
                    if (!notFound.Contains(match.Requested, StringComparer.Ordinal)) notFound.Add(match.Requested);
                    break;
            }
        }

        var previousMode = _selectedMode;
        var modeChanged = matched.Count > 0 && previousMode is ContextMode.TreeOnly or ContextMode.ChangedSince;
        if (modeChanged) SelectedMode = ContextMode.TreeAndSelected;

        var result = new RequestedFilesResult(matched, excluded, directories, notFound, modeChanged);

        if (matched.Count > 0)
        {
            RecomputeDirectoryStates();
            UpdateApproxTokenEstimate();
            _persistTimer.Stop();
            await PersistFileStatesAsync().ConfigureAwait(true);
        }

        StatusMessage = BuildRequestedFilesStatus(result, previousMode);
        return result;
    }

    /// <summary>結果を利用者向けの文面にする。該当しなかったものは、どれがなぜかを必ず書く。</summary>
    private string BuildRequestedFilesStatus(RequestedFilesResult result, ContextMode previousMode)
    {
        var parts = new List<string>();

        parts.Add(result.Matched.Count == 0
            ? $"AIが求めた{result.TotalCount}件は、どれも「内容も出す」にできませんでした。"
            : $"AIが求めた{result.TotalCount}件のうち{result.Matched.Count}件を「内容も出す」にしました。");

        if (result.ModeChanged)
        {
            var label = Modes.FirstOrDefault(m => m.Mode == previousMode)?.Label ?? previousMode.ToString();
            parts.Add($"収集モードが「{label}」だとファイルの内容が出力されないため、「ツリー＋選択」に切り替えました。");
        }

        if (result.Excluded.Count > 0)
        {
            var items = result.Excluded.Select(e => string.IsNullOrEmpty(e.ExcludeReason)
                ? e.Requested
                : $"{e.Requested}（{e.ExcludeReason}）");
            parts.Add($"除外されていて選べないもの: {JoinLimited(items)}。");
        }

        if (result.Directories.Count > 0)
        {
            parts.Add($"フォルダのため指定できないもの: {JoinLimited(result.Directories.Select(d => d.Requested))}。");
        }

        if (result.NotFound.Count > 0)
        {
            parts.Add($"プロジェクト内に見つからないもの: {JoinLimited(result.NotFound)}。");
        }

        return string.Join(" ", parts);
    }

    private static string JoinLimited(IEnumerable<string> items)
    {
        var all = items.ToList();
        var listed = string.Join("、", all.Take(MaxListedPaths));
        return all.Count > MaxListedPaths ? $"{listed} ほか{all.Count - MaxListedPaths}件" : listed;
    }
}
