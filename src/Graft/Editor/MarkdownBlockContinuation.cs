using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Graft.Editor;

/// <summary>行頭の入れ子構造1階層ぶん（引用またはリスト）。</summary>
public enum MarkupLevelKind
{
    Quote,
    List,
}

/// <summary><see cref="MarkdownBlockContinuation"/>が扱う階層1段ぶん。<paramref name="Width"/>は
/// その階層がこの行で専有する列数、<paramref name="Marker"/>はこの行から実測したマーカー文字列
/// （引用は常に、リストは「そのノード自身が最初に現れた行」でだけ実際の文字が見える）。</summary>
public readonly record struct MarkupLevel(MarkupLevelKind Kind, int Width, string Marker);

/// <summary>行頭からマーカー列を消費した結果。</summary>
public readonly record struct MarkupExitContext(IReadOnlyList<MarkupLevel> Levels, int ConsumedWidth);

/// <summary>行テキストへの読み取り専用アクセス（<see cref="MarkdownBlockContinuation"/>をAvaloniaEdit
/// から独立させ、<c>tests/Graft.Tests</c>で純粋ロジックとして検証できるようにするための最小の抽象）。
/// 10万行級の文書でも、実際に触れた行だけを<see cref="GetLine"/>で取得する（文書全体を
/// 一度に文字列化しない）。</summary>
public interface IMarkdownLineSource
{
    int LineCount { get; }

    /// <summary>0始まりの行番号でその行のテキスト（改行文字を含まない）を返す。</summary>
    string GetLine(int index);
}

/// <summary>テスト用の単純な実装（<c>string[]</c>ラップ）。</summary>
public sealed class ArrayMarkdownLineSource(IReadOnlyList<string> lines) : IMarkdownLineSource
{
    public int LineCount => lines.Count;
    public string GetLine(int index) => lines[index];
}

/// <summary>
/// Markdown編集支援（検討書「Markdownの編集支援」）: Enterキーでのリスト・引用の継続／脱出判定を、
/// AvaloniaEditに依存しない純粋ロジックとして切り出したもの。呼び出し側（<c>EditorPane</c>）は
/// このクラスの結果をもとに<c>TextDocument.Replace</c>を1回呼ぶだけでよい。
///
/// 【Paneには構文木（@lezer/markdown）があるが、Graftには無い】
/// 移植元Pane（CodeMirror 6）は構文木からリスト/引用の祖先ノードを辿って各階層の幅を求めていたが、
/// AvaloniaEditにMarkdown構文木は無い。そこで本クラスは以下の性質を利用し、テキストだけから
/// 同等の判定を行う:
///   ・引用（<c>&gt;</c>）は入れ子のどの深さでも、その階層ぶんの文字が**その行に文字どおり
///     繰り返される**（CommonMarkの規則）。そのため引用の階層は常にカーソル行1行だけから
///     100%判定できる。
///   ・リストは浅い階層のマーカー文字を深い行では再掲せず、幅ぶんの空白としてしか現れない
///     （Pane <c>editor.js</c>のコメントと同じ観察）。そのため多段リストの祖先階層の「幅」を
///     知るには、直近の祖先行を遡って実測するしかない。本クラスは<see cref="AncestorScanCap"/>
///     行までの範囲でこれを行う（<see cref="IndentGuideRenderer"/>の<c>BlankLineScanCap</c>と
///     同じ「上限付きの後方探索」という作法に倣った）。整形が崩れている文書やスキャン上限を
///     超える文書では祖先を見つけられず脱出が1段浅くならないことがあり得るが、その場合でも
///     本文を壊す方向には倒れない（安全側）。
///
/// 【Pane実機で見つかった不具合の教訓（必ず守る）】
/// CommonMarkの遅延継続（lazy continuation）により、引用の直後の行は行頭に"&gt;"が無くても
/// 引用パラグラフの続きとみなされる。この行で「中身が空か」を幅の合計だけで判定すると、
/// 本文の先頭2文字（ちょうどマーカー幅と同じ文字数）を引用マーカーだと誤認して消してしまう
/// （実際にPaneでこの不具合が起き、利用者のデータが壊れた）。
/// 対策: 幅を足し合わせて位置を決めるのではなく、<see cref="ComputeExitContext"/>は
/// カーソル行の先頭から各階層を**実際に「消費」**していく（<see cref="ConsumeQuoteMarkers"/>・
/// <see cref="ConsumeListMarker"/>）。消費できなければ（＝その階層の実際のマーカーがその行に
/// 存在しなければ）<c>null</c>を返し、対象外として通常のEnter処理に委ねる。
/// 引用直後の遅延継続行は、そもそも行頭に"&gt;"が無い＝<see cref="ConsumeQuoteMarkers"/>が
/// 何も消費しないため、この経路で自然に弾かれる。
///
/// 【引用から完全に抜けるときは空行を挟む】
/// <see cref="ExitsQuoteCompletely"/>が真の場合、呼び出し側は書き換え文字列の先頭に改行を1つ
/// 足すこと。CommonMarkでは空行が無いと遅延継続が働き、マーカー文字を消して見た目上抜けたつもりでも
/// パーサ上はずっと引用の中のまま扱われる。保存した.mdを他のビューアで開くと、続けて書いた段落が
/// 引用の中に表示されてしまう（文書の意味が変わる不具合）。
/// </summary>
public static class MarkdownBlockContinuation
{
    /// <summary>多段リストの祖先階層（幅）を後方探索するときの上限行数。</summary>
    private const int AncestorScanCap = 200;

    // \G（直前のMatch終了位置、またはMatch(input, start)のstart位置に固定するアンカー）を使う。
    // \Aだと常に文字列の絶対先頭(index 0)にしか一致せず、Match(line, pos)のpos>0では
    // 2階層目以降のマーカーを検出できなくなる（複数レベルの引用をConsumeQuoteMarkersで
    // 1文字ずつ位置をずらしながら呼ぶため、offset付きの固定に対応できるアンカーが必要）。
    private static readonly Regex QuotePattern = new(@"\G {0,3}>( ?)", RegexOptions.Compiled);
    private static readonly Regex CheckboxListPattern = new(@"\A( *)([-*+])( \[[ xX]\])( +)", RegexOptions.Compiled);
    private static readonly Regex BulletListPattern = new(@"\A( *)([-*+])( +)", RegexOptions.Compiled);
    private static readonly Regex OrderedListPattern = new(@"\A( *)(\d+[.)])( +)", RegexOptions.Compiled);

    // Enter継続（handleEnter相当）: マーカー全体（先頭の空白込み）を丸ごとキャプチャする版。
    private static readonly Regex ContinuationMarkerPattern =
        new(@"\A(\s*)([-*+]\s\[[ xX]\]\s|[-*+]\s|\d+[.)]\s)", RegexOptions.Compiled);
    private static readonly Regex OrderedMarkerSuffixPattern = new(@"\A(\d+)([.)]\s)\z", RegexOptions.Compiled);
    private static readonly Regex CheckboxMarkerSuffixPattern = new(@"\A([-*+])\s\[[ xX]\]\s\z", RegexOptions.Compiled);

    /// <summary>
    /// Enterキーで既存のリスト項目・引用を継続する場合の、新しい行頭に置くべきマーカー文字列を返す。
    /// 継続対象でなければ<c>null</c>を返す（呼び出し側は脱出判定・通常のEnterへフォールバックする）。
    /// <paramref name="textBeforeCaret"/>はカーソル行の**カーソルより前**の部分だけを渡す
    /// （カーソルより後ろは行分割で自動的に新しい行へ付いてくるため、判定に含めない。Pane
    /// <c>handleEnter</c>と同じ）。
    ///
    /// 先頭の引用マーカー（<c>&gt;</c>、複数段可）を先に読み飛ばしてから、その内側でリストマーカーの
    /// 有無を見る。これにより「引用だけ（リストなし）で中身のある行」（例: <c>"&gt; 本文"</c>）も
    /// 「引用の中のリスト」（例: <c>"&gt; - 項目"</c>）も同じ経路で扱える。引用マーカーが無い場合は
    /// 従来どおりリストのみの判定になる（このメソッド名がリスト限定に見えるが、実装は引用も含む。
    /// 利用者向けAPIとしては<see cref="MarkdownEditingSupport.HandleEnter"/>から1本で呼べることを
    /// 優先し、既存呼び出し元・テストとの互換のためメソッド名は変えていない）。
    /// </summary>
    public static string? ComputeListContinuationMarker(string textBeforeCaret)
    {
        var quoteLevels = ConsumeQuoteMarkers(textBeforeCaret, out var afterQuotes);
        var quotePrefix = string.Concat(quoteLevels.Select(l => l.Marker));
        var rest = textBeforeCaret[afterQuotes..];

        var m = ContinuationMarkerPattern.Match(rest);
        if (m.Success)
        {
            var afterMarker = rest[m.Length..];
            if (afterMarker.Trim().Length == 0) return null; // 空項目 → 継続ではなく脱出/通常処理の対象。

            var indent = m.Groups[1].Value;
            var marker = m.Groups[2].Value;

            var ordered = OrderedMarkerSuffixPattern.Match(marker);
            var checkbox = CheckboxMarkerSuffixPattern.Match(marker);
            if (ordered.Success)
            {
                var next = int.Parse(ordered.Groups[1].Value) + 1;
                marker = next + ordered.Groups[2].Value;
            }
            else if (checkbox.Success)
            {
                marker = checkbox.Groups[1].Value + " [ ] "; // 継続した新項目は未チェックにする。
            }
            return quotePrefix + indent + marker;
        }

        // リストマーカーは無いが、引用マーカーだけはあり、かつ中身がある行（例:"> 本文"）。
        // 遅延継続に頼らず、同じ引用マーカーを次の行にも明示的に置く（CommonMark的には無くても
        // 引用として解釈されるが、利用者からは「引用が続いている」ことが行頭記号で見える方が
        // Paneでの体験に近く、また次の行を単独で見ても引用だと分かる）。
        if (quoteLevels.Count > 0 && rest.Trim().Length != 0) return quotePrefix;

        return null;
    }

    /// <summary>
    /// カーソル行が「マーカーだけで中身が空の行」であるとき、その階層スタックを外側→内側の順で返す。
    /// 対象外（通常の継続行・遅延継続の本文行・コードフェンス内・マーカー自体が無い行）ならnull。
    /// </summary>
    public static MarkupExitContext? ComputeExitContext(IMarkdownLineSource source, int lineIndex)
    {
        if (IsInsideFencedCodeBlock(source, lineIndex)) return null;

        var line = source.GetLine(lineIndex);
        var quoteLevels = ConsumeQuoteMarkers(line, out var afterQuotes);

        var ownList = ConsumeListMarker(line, afterQuotes, out var afterOwnList);
        var consumedWidth = afterOwnList;
        var remainder = line[consumedWidth..];
        if (remainder.Trim().Length != 0) return null; // 本文が残っている＝通常の継続行。対象外。
        if (quoteLevels.Count == 0 && ownList is null) return null; // 引用・リストどちらでもないただの空行。

        var levels = new List<MarkupLevel>(quoteLevels);
        if (ownList is { } own)
        {
            // 自分の行にリストマーカーがあるときだけ、その手前の列にある祖先リスト階層を探す
            // （マーカーが無い＝この行はどのリストにも属していない引用だけの空行、という扱い）。
            var ownColumn = LeadingWidth(line, afterQuotes);
            levels.AddRange(FindAncestorListLevels(source, lineIndex, line[..afterQuotes], ownColumn));
            levels.Add(own);
        }

        return levels.Count == 0 ? null : new MarkupExitContext(levels, consumedWidth);
    }

    /// <summary>1段浅くした後の行頭文字列を組み立てる（levelsは<see cref="ComputeExitContext"/>の
    /// 戻り値そのもの）。最も内側の階層を1つ取り除く。それより浅い階層のうち、結果として
    /// 一番深くなる階層だけは実測マーカー文字列を、それより浅い階層は幅ぶんの空白にする
    /// （リストの浅い階層は常に空白でしか表現されないため）。</summary>
    public static string RenderShallowerPrefix(IReadOnlyList<MarkupLevel> levels)
    {
        if (levels.Count <= 1) return string.Empty;
        var kept = levels.Count - 1;
        var sb = new StringBuilder();
        for (var i = 0; i < kept; i++)
        {
            var isDeepest = i == kept - 1;
            sb.Append(levels[i].Kind == MarkupLevelKind.Quote || isDeepest
                ? levels[i].Marker
                : new string(' ', levels[i].Width));
        }
        return sb.ToString();
    }

    /// <summary>浅くした結果、階層が0（プレーンな行）になり、かつ最も内側が引用だった場合はtrue。
    /// 呼び出し側はこの場合、書き換え文字列の先頭に改行を1つ足す（クラスコメント参照）。</summary>
    public static bool ExitsQuoteCompletely(IReadOnlyList<MarkupLevel> levels)
        => levels.Count == 1 && levels[0].Kind == MarkupLevelKind.Quote;

    private static List<MarkupLevel> ConsumeQuoteMarkers(string line, out int consumedTo)
    {
        var levels = new List<MarkupLevel>();
        var pos = 0;
        while (pos <= line.Length)
        {
            var m = QuotePattern.Match(line, pos);
            if (!m.Success || m.Index != pos) break;
            levels.Add(new MarkupLevel(MarkupLevelKind.Quote, m.Length, m.Value));
            pos += m.Length;
        }
        consumedTo = pos;
        return levels;
    }

    private static MarkupLevel? ConsumeListMarker(string line, int start, out int consumedTo)
    {
        var slice = line[start..];
        var m = CheckboxListPattern.Match(slice);
        if (!m.Success) m = BulletListPattern.Match(slice);
        if (!m.Success) m = OrderedListPattern.Match(slice);
        if (!m.Success || m.Index != 0)
        {
            consumedTo = start;
            return null;
        }
        consumedTo = start + m.Length;
        return new MarkupLevel(MarkupLevelKind.List, m.Length, m.Value);
    }

    /// <summary>行内の、指定オフセットより前にある空白の連続長（＝マーカー開始までの列）。</summary>
    private static int LeadingWidth(string line, int start)
    {
        var i = start;
        while (i < line.Length && line[i] == ' ') i++;
        return i - start;
    }

    /// <summary>
    /// カーソル行より前を遡り、同じ引用プレフィックス配下で、カーソル行のリストマーカーより
    /// 浅い列に位置する祖先リスト階層を集める（クラスコメント参照）。列が浅い順（外側→内側）で返す。
    /// </summary>
    private static List<MarkupLevel> FindAncestorListLevels(
        IMarkdownLineSource source, int lineIndex, string quotePrefix, int ownColumn)
    {
        var seenByColumn = new SortedDictionary<int, MarkupLevel>();
        var scanned = 0;
        for (var i = lineIndex - 1; i >= 0 && scanned < AncestorScanCap; i--, scanned++)
        {
            var candidate = source.GetLine(i);
            if (!candidate.StartsWith(quotePrefix, StringComparison.Ordinal))
            {
                if (candidate.Trim().Length == 0) continue; // 空行はまたいで遡る。
                break; // 引用の文脈が変わる行に当たったら打ち切り。
            }
            var rest = candidate[quotePrefix.Length..];
            if (rest.Trim().Length == 0) continue;

            var marker = ConsumeListMarker(rest, 0, out _);
            if (marker is null) break; // リストマーカーで始まらない本文行＝この文脈の先頭に到達。
            var column = LeadingWidth(rest, 0);
            if (column >= ownColumn) continue; // 自分と同じか、自分より深い列は祖先ではない。
            seenByColumn.TryAdd(column, marker.Value);
        }
        return seenByColumn.Values.ToList(); // SortedDictionaryなので列の昇順（外側→内側）。
    }

    // コードフェンス（```）の内側かどうかは開始行からの出現回数の偶奇で判定する（構文木が無いための
    // 簡易な代替）。Enterキー押下時に一度だけ呼ばれる処理であり、可視範囲限定が必要な描画パスとは
    // 性能特性が異なる（キー入力1回ぶんのコストとして許容する）。
    private static bool IsInsideFencedCodeBlock(IMarkdownLineSource source, int lineIndex)
    {
        var fenceCount = 0;
        for (var i = 0; i < lineIndex; i++)
        {
            if (source.GetLine(i).TrimStart().StartsWith("```", StringComparison.Ordinal)) fenceCount++;
        }
        return fenceCount % 2 == 1;
    }
}
