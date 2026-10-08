namespace Graft.Features;

/// <summary>
/// 「gitの変更ファイルだけ」を押したときの、選択状態の組み立て結果。
/// </summary>
/// <param name="States">
/// 選べる（除外されていない）全ファイルの新しい状態。変更ファイルは
/// <see cref="ContextFileState.Full"/>（内容も出す）、それ以外は
/// <see cref="ContextFileState.StructureOnly"/>（構成だけ）。キーはプロジェクト相対パス（区切りは <c>/</c>）。
/// </param>
/// <param name="FullPaths">「内容も出す」にするファイルのプロジェクト相対パス（重複なし）。</param>
/// <param name="Excluded">変更されているが、除外されていて選べないもの（除外理由つき）。</param>
/// <param name="OtherCount">
/// 走査結果に載っていない、またはファイルではなかったために扱えなかった変更の数
/// （フォルダ・サブモジュール・走査後に消えたものなど）。
/// </param>
public sealed record ChangedFileSelection(
    IReadOnlyDictionary<string, ContextFileState> States,
    IReadOnlyList<string> FullPaths,
    IReadOnlyList<RequestedFileMatch> Excluded,
    int OtherCount)
{
    /// <summary>「構成だけ」にするファイルの数。</summary>
    public int StructureOnlyCount => States.Count - FullPaths.Count;

    /// <summary>
    /// git の変更ファイルを、コンテキスト収集の走査結果と突き合わせて選択状態を組み立てる。
    ///
    /// 【設計判断】
    /// <list type="bullet">
    /// <item>照合は AI の「このファイルも見せて」と同じ <see cref="RequestedFileMatcher"/> に任せる。
    /// 除外されたフォルダの配下（走査していないため個別のノードが無い）にある変更も、
    /// 「除外されていて選べない」として拾える。独自の照合を別に持つと、除外の判定が
    /// 2 系統に分かれて食い違う。</item>
    /// <item>変更の無いファイルを「出さない」ではなく「構成だけ」にするのは、この機能の使い方が
    /// 「作業中のファイルだけ中身を渡し、プロジェクト全体の形は AI にも分かるようにする」ため。
    /// 「出さない」にすると構成からも消え、AI が周囲のファイルの存在を知れない。</item>
    /// <item>除外されたファイルは <see cref="States"/> に含めない（選べないため状態を持たない。
    /// 除外ノードの状態は常に null で、書き換える対象ではない）。</item>
    /// </list>
    /// </summary>
    /// <param name="scan">コンテキスト収集の走査結果（除外分・ディレクトリを含む）。</param>
    /// <param name="changedPaths">プロジェクト相対の変更ファイル（<see cref="GitChangedFiles.Paths"/>）。</param>
    public static ChangedFileSelection Build(IReadOnlyList<ContextFileNode> scan, IEnumerable<string> changedPaths)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(changedPaths);

        var matches = RequestedFileMatcher.Match(scan, changedPaths);

        var full = new List<string>();
        var fullSet = new HashSet<string>(StringComparer.Ordinal);
        var excluded = new List<RequestedFileMatch>();
        var excludedSeen = new HashSet<string>(StringComparer.Ordinal);
        var other = 0;
        foreach (var match in matches)
        {
            switch (match.Kind)
            {
                case RequestedFileMatchKind.Matched when match.RelativePath is { } path:
                    if (fullSet.Add(path)) full.Add(path);
                    break;
                case RequestedFileMatchKind.Excluded:
                    // 除外フォルダの配下の変更は、同じ祖先に対して何件も出る。同じ変更ファイルが
                    // 2 回数えられないよう、AI が書いたまま（ここでは git が返した）パスで重複を除く。
                    if (excludedSeen.Add(match.Requested)) excluded.Add(match);
                    break;
                default:
                    other++;
                    break;
            }
        }

        var states = new Dictionary<string, ContextFileState>(StringComparer.Ordinal);
        foreach (var node in scan)
        {
            if (node.IsDirectory || node.IsExcluded) continue;
            states[node.RelativePath] = fullSet.Contains(node.RelativePath)
                ? ContextFileState.Full
                : ContextFileState.StructureOnly;
        }

        return new ChangedFileSelection(states, full, excluded, other);
    }
}
