namespace Graft.Core;

/// <summary>行頭の状態。複数行文字列・ブロックコメントの継続を表す。</summary>
public enum LineState
{
    Normal,
    InBlockComment,
    InMultilineString,
}

/// <summary>行頭から次行へ引き継ぐ字句状態。</summary>
internal struct LineScanState
{
    public LineState State;
    public string? Closer;
    public char? EscapeChar;
    public bool DoubledClosingEscapes;
    public bool CloserIsLineAnchor;
}

/// <summary>
/// 行頭状態（<see cref="LineState"/>）の継続判定を行う。ブロックコメント・複数行文字列・
/// ヒアドキュメントが前行から続いている場合はその終了位置を現在行から探し、Normal状態に
/// 戻った残りの内容は <see cref="SyntaxTokenizer"/> に委譲する。<see cref="SyntaxLexer.Scan"/>
/// によるファイル全体の事前走査と、<see cref="SyntaxLexer.TokenizeLine"/> による表示行単位の
/// トークン化の両方から呼ばれる共通の状態遷移ロジック。
/// </summary>
internal static class LineStateScanner
{
    internal static void ProcessLine(
        string text, ref LineScanState state, List<SyntaxToken>? tokens, SyntaxTokenizer tokenizer)
    {
        var pos = 0;
        var length = text.Length;

        switch (state.State)
        {
            case LineState.InBlockComment:
                pos = ContinueSpan(text, length, ref state, tokens, TokenKind.Comment);
                break;
            case LineState.InMultilineString when state.CloserIsLineAnchor:
                // ヒアドキュメント: 行全体（先頭空白を除く）が終端語と一致するかどうかで判定する。
                ProcessHeredocBody(text, ref state, tokens);
                return;
            case LineState.InMultilineString:
                pos = ContinueSpan(text, length, ref state, tokens, TokenKind.String);
                break;
        }

        if (state.State != LineState.Normal)
        {
            return; // 継続中の状態が今行でも閉じなかった場合はここで終了。
        }

        tokenizer.ScanNormal(text, pos, length, ref state, tokens);
    }

    // 継続中のブロックコメント／複数行文字列の終了トークンを現在行から探す。開始行に適用した
    // エスケープ規則（EscapeChar・DoubledClosingEscapes）を継続行でも同じく適用する必要があるため、
    // 単純な IndexOf ではなく SyntaxTokenizer.FindClose を使う。見つからなければ行全体を1トークン
    // とし状態を維持、見つかればそこまでを1トークンとして Normal に戻す。
    private static int ContinueSpan(
        string text, int length, ref LineScanState state, List<SyntaxToken>? tokens, TokenKind kind)
    {
        var closer = state.Closer!;
        var closeIndex = SyntaxTokenizer.FindClose(text, 0, closer, state.EscapeChar, state.DoubledClosingEscapes);
        if (closeIndex < 0)
        {
            SyntaxTokenizer.AddToken(tokens, 0, length, kind);
            return length;
        }

        var end = closeIndex + closer.Length;
        SyntaxTokenizer.AddToken(tokens, 0, end, kind);
        state.State = LineState.Normal;
        state.Closer = null;
        state.EscapeChar = null;
        state.DoubledClosingEscapes = false;
        return end;
    }

    private static void ProcessHeredocBody(string text, ref LineScanState state, List<SyntaxToken>? tokens)
    {
        var trimmed = text.TrimStart();
        SyntaxTokenizer.AddToken(tokens, 0, text.Length, TokenKind.String);
        if (trimmed == state.Closer)
        {
            state.State = LineState.Normal;
            state.Closer = null;
            state.CloserIsLineAnchor = false;
        }
    }
}
