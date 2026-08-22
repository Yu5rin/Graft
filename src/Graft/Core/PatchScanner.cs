using System.Text.RegularExpressions;

namespace Graft.Core;

/// <summary>
/// 本文収集（CollectBody）の結果種別。
/// </summary>
internal enum BodyOutcome
{
    /// <summary>終了マーカーまで正常に収集できた。</summary>
    Completed,
    /// <summary>終了マーカーが見つからないまま入力が尽きた（切断・4.10）。</summary>
    Truncated,
    /// <summary>エスケープされていないマーカー行が現れ構文が壊れた（4.9・E006）。</summary>
    Broken,
}

/// <summary>
/// 本文収集の結果。
/// </summary>
/// <param name="Outcome">収集の結果種別。</param>
/// <param name="Lines">収集できた本文（Brokenの場合は打ち切られるまでの分）。</param>
/// <param name="BrokenLine">Brokenの場合、破損の原因になった行番号。それ以外は null。</param>
/// <param name="BrokenText">
/// Brokenの場合、破損の原因になった行の内容（前後の空白を除く）。呼び出し側が
/// 「新しいブロックの開始行として解釈できる形（"&lt;&lt;&lt;&lt;"始まり）かどうか」を
/// 判定してメッセージを出し分けるために使う（PatchParser参照）。それ以外は null。
/// </param>
/// <param name="TerminatorLine">
/// Completedの場合、終了マーカーが見つかった行番号。呼び出し側が「次のブロック（例:
/// REPLACE本文）の開始行」として引き継ぐために使う。それ以外は null。
/// </param>
internal sealed record BodyResult(
    BodyOutcome Outcome,
    IReadOnlyList<string> Lines,
    int? BrokenLine,
    string? BrokenText = null,
    int? TerminatorLine = null);

/// <summary>
/// パッチ全体を囲む「外側の」Markdownコードフェンスの除去（<see cref="PatchScanner.Create"/>）が、
/// 本文中の``` によって早まって閉じられた可能性があると判断した場合の内部シグナル。
/// <see cref="PatchParser"/> が捕捉して <see cref="ErrorCode.E009"/> の <see cref="GraftIssue"/> へ変換する。
/// 黙って本文の一部を欠落させたまま解析を続けるのではなく、ここで打ち切って利用者に
/// 気づける形で失敗させることが狙い（<see cref="PatchScanner"/> のクラスコメント参照）。
/// </summary>
internal sealed class FenceAmbiguousCloseException(GraftIssue issue) : Exception
{
    /// <summary>呼び出し側（PatchParser）へ伝える問題内容。</summary>
    public GraftIssue Issue { get; } = issue;
}

/// <summary>
/// パッチ本文を行単位で走査するためのカーソル。パッチ全体を囲む「外側の」Markdown
/// コードフェンスの除去と、エスケープ規則（4.9）を踏まえた本文収集をまとめて担当する。
///
/// 【剥がす対象を「外側の1個」に限定する理由（v1.0.8で修正）】
/// 従来は "```" で始まる行を無条件にすべて除去していた。これは
/// <see cref="Graft.Features.PromptTemplateStore"/> の CodeBlockWrapNote の指示により
/// AIがパッチ全体を1つのコードフェンスで囲んで出力するようになったための対処
/// （コピー機能で一括コピーできるようにするため。同クラス参照）だが、
/// 「パッチ本文自体に``` を含む変更」（Markdownファイルの編集など）では、本文中の
/// フェンス行まで無言で消えたまま適用されてしまう不具合があった（v1.0.5〜1.0.7で
/// 未修正のまま持ち越し）。
///
/// 代わりに、CommonMarkのフェンス規則（開始: バッククォート3個以上＋任意の情報文字列。
/// 終了: 開始と同数以上のバッククォートのみの行。開始が4個なら3個の行では閉じない）に
/// 従ってテキスト全体のフェンス開閉を対応付け、"&lt;&lt;&lt;&lt; PATCH" の行を含む
/// フェンスを1つだけ見つけて、その開始行・終了行の2行だけを取り除く。それ以外の
/// バッククォート行（本文中の``` や、パッチの前後にあるAIの説明文中の例示コード）は
/// 一切対象にしない。囲むフェンスが見つからない場合（生のパッチ、または本文中に
/// "&lt;&lt;&lt;&lt; PATCH" を含むフェンスが無い場合）は何も取り除かない。
///
/// CodeBlockWrapNoteは「本文に``` を含む場合は外側をバッククォート4個にする」ようAIへ
/// 指示済みだが、AIがこれに反して3個のまま出力し、かつ本文にも``` が含まれる場合、
/// 本文中の最初の``` が外側のフェンスを早まって閉じてしまう。この場合、閉じたはずの
/// 位置より後にまだGraft形式のマーカー行（"&lt;&lt;&lt;&lt;"や">>>>"始まり、"======="）が
/// 続いているはずなので、それを手がかりに検出し、黙って剥がさず
/// <see cref="FenceAmbiguousCloseException"/> を投げて利用者に気づける形で失敗させる
/// （PatchParser参照）。
/// </summary>
internal sealed class PatchScanner
{
    private readonly List<(int LineNumber, string Text)> _lines;
    private int _position;

    /// <summary>フェンス開始行のパターン（0〜3個の先頭空白＋バッククォート3個以上＋任意の情報文字列）。</summary>
    private static readonly Regex FenceOpenPattern = new(@"^ {0,3}(`{3,})(.*)$", RegexOptions.Compiled);

    /// <summary>フェンス終了行のパターン（0〜3個の先頭空白＋バッククォートのみ。情報文字列は持てない）。</summary>
    private static readonly Regex FenceClosePattern = new(@"^ {0,3}(`{3,})[ \t]*$", RegexOptions.Compiled);

    private PatchScanner(List<(int, string)> lines)
    {
        _lines = lines;
    }

    /// <summary>
    /// 元テキストからカーソルを作る。パッチ全体を囲む外側のMarkdownコードフェンス行だけを
    /// 除去する（クラスコメント参照）。本文中の``` によって外側のフェンスが早まって
    /// 閉じられた可能性がある場合は <see cref="FenceAmbiguousCloseException"/> を投げる。
    /// </summary>
    public static PatchScanner Create(string patchText)
    {
        var raw = PatchTextUtil.SplitRawLines(patchText);
        var toRemove = FindWrapperFenceLineIndexes(raw);

        var filtered = new List<(int, string)>();
        for (var i = 0; i < raw.Length; i++)
        {
            if (toRemove.Contains(i)) continue;
            filtered.Add((i + 1, raw[i]));
        }
        return new PatchScanner(filtered);
    }

    /// <summary>
    /// テキスト全体のフェンス開閉をCommonMark規則で対応付け、"&lt;&lt;&lt;&lt; PATCH" の行を
    /// 含むフェンスを1つ見つけて、その開始・終了行のインデックス（0始まり）を返す。
    /// 見つからなければ空集合を返す（何も取り除かない）。
    /// </summary>
    private static HashSet<int> FindWrapperFenceLineIndexes(string[] raw)
    {
        foreach (var span in FindFenceSpans(raw))
        {
            var contentEnd = span.CloseIndex ?? raw.Length; // 内容範囲は [open+1, contentEnd)
            if (!ContainsPatchMetaLine(raw, span.OpenIndex + 1, contentEnd)) continue;

            if (span.CloseIndex is null)
            {
                // 閉じないまま入力が尽きた（AI出力が途中で切断された等）。開始行だけ取り除けばよく、
                // 残りは後段の切断検出（4.10・E005）に委ねる。
                return new HashSet<int> { span.OpenIndex };
            }

            var closeIndex = span.CloseIndex.Value;
            ThrowIfAmbiguousClose(raw, closeIndex);
            return new HashSet<int> { span.OpenIndex, closeIndex };
        }

        return new HashSet<int>();
    }

    /// <summary>
    /// 見つけた終了フェンスより後に、まだGraft形式のマーカー行が続いていないかを確認する。
    /// 続いている場合、この終了フェンスは本当の外側フェンスの閉じ行ではなく、本文中の
    /// ``` を誤って閉じマーカーと解釈してしまった可能性が高い（クラスコメント参照）。
    /// </summary>
    private static void ThrowIfAmbiguousClose(string[] raw, int closeIndex)
    {
        for (var j = closeIndex + 1; j < raw.Length; j++)
        {
            if (!PatchTextUtil.LooksLikeMarker(raw[j])) continue;

            const string detail =
                "パッチ本文に ``` が含まれているため、囲みが途中で閉じられた可能性があります。" +
                "AIへの指示文のとおり、外側をバッククォート4個（````text 〜 ````）にして出力し直してください。";
            throw new FenceAmbiguousCloseException(GraftIssue.Of(ErrorCode.E009, detail, line: closeIndex + 1));
        }
    }

    /// <summary>[start, endExclusive) の範囲に "&lt;&lt;&lt;&lt; PATCH" 行があるかどうか。</summary>
    private static bool ContainsPatchMetaLine(string[] raw, int start, int endExclusive)
    {
        for (var i = start; i < endExclusive; i++)
        {
            if (raw[i].TrimEnd() == "<<<< PATCH") return true;
        }
        return false;
    }

    /// <summary>フェンス1つ分の範囲。<see cref="CloseIndex"/> がnullの場合は入力の終わりまで閉じていない。</summary>
    private readonly record struct FenceSpan(int OpenIndex, int TickLength, int? CloseIndex);

    /// <summary>
    /// テキスト全体を先頭から走査し、CommonMark規則に従ってフェンスの開始・終了を対応付ける。
    /// フェンスは入れ子にならない（開いている間は終了パターンにマッチする行が来るまで、
    /// 別の開始パターンを探さない）ため、単純な逐次走査で足りる。
    /// </summary>
    private static List<FenceSpan> FindFenceSpans(string[] lines)
    {
        var spans = new List<FenceSpan>();
        var i = 0;
        while (i < lines.Length)
        {
            var openMatch = FenceOpenPattern.Match(lines[i]);
            if (!openMatch.Success)
            {
                i++;
                continue;
            }

            var tickLength = openMatch.Groups[1].Length;
            var infoString = openMatch.Groups[2].Value;
            if (infoString.Contains('`'))
            {
                // CommonMark: バッククォートフェンスの情報文字列にバッククォートは含められない。
                // この行はフェンス開始として扱わない。
                i++;
                continue;
            }

            var openIndex = i;
            int? closeIndex = null;
            for (var j = openIndex + 1; j < lines.Length; j++)
            {
                var closeMatch = FenceClosePattern.Match(lines[j]);
                if (closeMatch.Success && closeMatch.Groups[1].Length >= tickLength)
                {
                    closeIndex = j;
                    break;
                }
            }

            spans.Add(new FenceSpan(openIndex, tickLength, closeIndex));
            // 閉じたフェンスの次の行から走査を再開する。閉じなかった場合はここで走査終了
            // （入れ子にならないため、開いたまま残るフェンスの内部を別のフェンスとして
            // 再解釈することはない）。
            i = closeIndex.HasValue ? closeIndex.Value + 1 : lines.Length;
        }
        return spans;
    }

    /// <summary>次の行が存在するかどうか。</summary>
    public bool HasNext => _position < _lines.Count;

    /// <summary>次の行を消費せずに参照する。</summary>
    public (int LineNumber, string Text) Peek() => _lines[_position];

    /// <summary>次の行を消費して返す。</summary>
    public (int LineNumber, string Text) Next() => _lines[_position++];

    /// <summary>
    /// 終了判定 <paramref name="isTerminator"/> が真を返す行まで本文を収集する。
    /// エスケープ済み行は先頭の "\" を1つ取り除いて内容行として扱い、
    /// 未エスケープのマーカー様の行が現れた場合は破損（Broken）として打ち切る。
    /// </summary>
    public BodyResult CollectBody(Func<string, bool> isTerminator)
    {
        var buffer = new List<string>();
        while (HasNext)
        {
            var (lineNumber, text) = Peek();
            if (isTerminator(text))
            {
                Next();
                return new BodyResult(BodyOutcome.Completed, buffer, null, TerminatorLine: lineNumber);
            }

            var unescaped = PatchTextUtil.TryUnescapeMarkerLine(text);
            if (unescaped is not null)
            {
                buffer.Add(unescaped);
                Next();
                continue;
            }

            if (PatchTextUtil.LooksLikeMarker(text))
                return new BodyResult(BodyOutcome.Broken, buffer, lineNumber, BrokenText: text.Trim());

            buffer.Add(text);
            Next();
        }
        return new BodyResult(BodyOutcome.Truncated, buffer, null);
    }
}
