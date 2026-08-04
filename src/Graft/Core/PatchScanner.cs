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

/// <summary>本文収集の結果。</summary>
internal sealed record BodyResult(BodyOutcome Outcome, IReadOnlyList<string> Lines, int? BrokenLine);

/// <summary>
/// パッチ本文を行単位で走査するためのカーソル。Markdownコードフェンスの除去と、
/// エスケープ規則（4.9）を踏まえた本文収集をまとめて担当する。
/// </summary>
internal sealed class PatchScanner
{
    private readonly List<(int LineNumber, string Text)> _lines;
    private int _position;

    private PatchScanner(List<(int, string)> lines)
    {
        _lines = lines;
    }

    /// <summary>元テキストからカーソルを作る。Markdownコードフェンス行は除去する（4.1）。</summary>
    public static PatchScanner Create(string patchText)
    {
        var raw = PatchTextUtil.SplitRawLines(patchText);
        var filtered = new List<(int, string)>();
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i].StartsWith("```", StringComparison.Ordinal)) continue;
            filtered.Add((i + 1, raw[i]));
        }
        return new PatchScanner(filtered);
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
                return new BodyResult(BodyOutcome.Completed, buffer, null);
            }

            var unescaped = PatchTextUtil.TryUnescapeMarkerLine(text);
            if (unescaped is not null)
            {
                buffer.Add(unescaped);
                Next();
                continue;
            }

            if (PatchTextUtil.LooksLikeMarker(text))
                return new BodyResult(BodyOutcome.Broken, buffer, lineNumber);

            buffer.Add(text);
            Next();
        }
        return new BodyResult(BodyOutcome.Truncated, buffer, null);
    }
}
