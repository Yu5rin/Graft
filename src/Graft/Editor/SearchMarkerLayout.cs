namespace Graft.Editor;

/// <summary>
/// 検索ヒットの行番号1つぶんの目印の描画位置（縦スクロールバー上）。
/// <see cref="Y"/>はトラック上端からのオフセット（px）、<see cref="Height"/>は目印自体の高さ（px）、
/// <see cref="IsCurrent"/>は現在ヒットかどうか（現在ヒットは他と区別できる大きさ・色にする。
/// 実際の色選定は<see cref="SearchMarkerBar"/>側が担当し、このstructは位置計算のみを持つ）。
/// </summary>
public readonly record struct SearchMarkerRect(double Y, double Height, bool IsCurrent);

/// <summary>
/// 検索ヒットの行番号を、縦スクロールバーのトラック上の目印位置へ変換する純粋関数
/// （AvaloniaEdit・Avaloniaのいずれにも依存しないため、tests/Graft.Testsから直接検証できる。
/// 実際の描画は<see cref="SearchMarkerBar"/>がこの結果をDrawingContextへ渡す）。
///
/// 【位置の算出式】 依頼どおり「行番号 ÷ 総行数でトラック全体を按分」する単純な比例配置。
/// AvaloniaEditの行番号は1起点（<c>TextDocument.GetLineByOffset(...).LineNumber</c>）だが、
/// このクラスは呼び出し側から渡された行番号をそのまま分子として扱うだけで、1起点か0起点かは
/// 関知しない（呼び出し側であるSearchOverlay.axaml.csが1起点の行番号を渡す前提）。
/// 実際のスクロール位置（ScrollBar.Value/Maximum、折り返し・折りたたみによる可視行数のずれ等）
/// とは連動させない。目印はあくまで「文書全体のどのあたりにヒットが散らばっているか」を
/// おおまかに示す用途であり、折りたたみ等で実際のスクロール位置と行番号の対応がずれても、
/// 目印同士の相対的な位置関係（どちらが上でどちらが下か）は保たれるため実用上問題にならない。
///
/// 【重複の畳み込み】 MaxMatches（SearchOverlayViewModel）が20000件のため、素直に全件を
/// 別々の矩形として描くと、狭いトラック上に大量の矩形が重なって描画コストが無駄になる
/// （多くは同じ1〜2pxの範囲に収まってしまう）。<see cref="MergeThresholdPixels"/>刻みの
/// バケットへ丸め、同じバケットに落ちた通常ヒットは1本へまとめる。
///
/// 【現在ヒットは必ず残す】 通常ヒットの畳み込みバケットから現在ヒットのインデックスだけは
/// 除外し、別枠で必ず1本を追加する。現在ヒットが他の大量のヒットと同じバケットに収まって
/// しまっても、色・大きさで区別できる目印が消えずに残る（呼び出し側のテストで保証する）。
/// </summary>
public static class SearchMarkerLayout
{
    /// <summary>通常ヒットの目印の高さ（px）。</summary>
    private const double NormalMarkerHeight = 2.0;

    /// <summary>現在ヒットの目印の高さ（px）。通常より大きくして「色が判別できなくても
    /// 現在位置が分かる」ようにする（9.4「色だけに依存しない」方針、SearchHighlightRenderer
    /// クラスコメントと同じ考え方をスクロールバー側にも適用する）。</summary>
    private const double CurrentMarkerHeight = 4.0;

    /// <summary>この幅（px）に収まる通常ヒットは1本へ畳み込む。</summary>
    private const double MergeThresholdPixels = 2.0;

    /// <summary>
    /// 検索ヒットの行番号一覧から、縦スクロールバー上に描く目印の一覧を求める。
    /// </summary>
    /// <param name="matchLineNumbers">各ヒットの行番号（1起点。<paramref name="currentMatchIndex"/>
    /// と同じ添字で対応する）。</param>
    /// <param name="totalLines">文書の総行数（<c>TextDocument.LineCount</c>）。0以下なら
    /// 位置の按分ができないため、空の一覧を返す。</param>
    /// <param name="trackHeight">スクロールバーのトラックの高さ（px）。0以下なら空の一覧を返す。</param>
    /// <param name="currentMatchIndex">現在ヒットの<paramref name="matchLineNumbers"/>内での
    /// 添字。範囲外（-1等、ヒットが無い場合を含む）なら「現在ヒットなし」として扱う。</param>
    public static IReadOnlyList<SearchMarkerRect> Compute(
        IReadOnlyList<int> matchLineNumbers,
        int totalLines,
        double trackHeight,
        int currentMatchIndex)
    {
        ArgumentNullException.ThrowIfNull(matchLineNumbers);

        if (matchLineNumbers.Count == 0 || totalLines <= 0 || trackHeight <= 0)
        {
            return Array.Empty<SearchMarkerRect>();
        }

        // 目印がトラックの下端からはみ出さないよう、実際に描画に使えるY座標の上限を
        // 「トラック高さ - 目印自身の高さ」までに制限する。通常ヒットと現在ヒットで
        // 目印の高さが違う（後者の方が大きい）ため、Yを求める関数は目印の高さを引数に取る。
        double YFor(int lineNumber, double markerHeight)
        {
            var clampedLine = Math.Clamp(lineNumber, 0, totalLines);
            var ratio = (double)clampedLine / totalLines;
            var y = ratio * trackHeight;
            var maxY = Math.Max(0.0, trackHeight - markerHeight);
            return Math.Clamp(y, 0.0, maxY);
        }

        var hasCurrent = currentMatchIndex >= 0 && currentMatchIndex < matchLineNumbers.Count;

        // 通常ヒット: MergeThresholdPixels刻みのバケットへ丸め、重複を畳み込む。
        // SortedSetにすることで描画順（上から下）が安定し、テストでも順序に依存した
        // アサーションを書きやすくなる。
        var buckets = new SortedSet<long>();
        for (var i = 0; i < matchLineNumbers.Count; i++)
        {
            if (hasCurrent && i == currentMatchIndex) continue; // 現在ヒットは別枠で必ず残す
            var y = YFor(matchLineNumbers[i], NormalMarkerHeight);
            buckets.Add((long)Math.Round(y / MergeThresholdPixels));
        }

        var result = new List<SearchMarkerRect>(buckets.Count + 1);
        foreach (var bucket in buckets)
        {
            result.Add(new SearchMarkerRect(bucket * MergeThresholdPixels, NormalMarkerHeight, false));
        }

        if (hasCurrent)
        {
            var y = YFor(matchLineNumbers[currentMatchIndex], CurrentMarkerHeight);
            result.Add(new SearchMarkerRect(y, CurrentMarkerHeight, true));
        }

        return result;
    }
}
