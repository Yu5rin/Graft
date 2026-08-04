namespace Graft.Core;

/// <summary>
/// トークンの種別。配色は担当しない（Themes/Syntax.xaml の責務）。
/// </summary>
public enum TokenKind
{
    Plain,
    Keyword,
    String,
    Number,
    Comment,
    Function,
    Type,
    Operator,
}

/// <summary>
/// 1個のトークンの位置と種別。<paramref name="Start"/> と <paramref name="Length"/> は
/// 対象行の文字インデックス（0始まり）を表す。
/// </summary>
public readonly record struct SyntaxToken(int Start, int Length, TokenKind Kind);

/// <summary>
/// Normal状態にある行内容のトークン化を担当する。言語ルールに基づき、識別子・数値・演算子・
/// 文字列・ブロックコメント・ヒアドキュメントの開始を判定する。文字列やブロックコメントが
/// 行末で閉じなかった場合は <see cref="LineScanState"/> を更新し、複数行への継続を
/// 呼び出し元（<see cref="LineStateScanner"/>）へ伝える。
/// </summary>
internal sealed class SyntaxTokenizer
{
    private readonly LanguageRule _rule;

    internal SyntaxTokenizer(LanguageRule rule)
    {
        _rule = rule;
    }

    internal void ScanNormal(string text, int start, int length, ref LineScanState state, List<SyntaxToken>? tokens)
    {
        var i = start;
        while (i < length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (TryMatchAny(text, i, _rule.LineCommentPrefixes, out _))
            {
                AddToken(tokens, i, length - i, TokenKind.Comment);
                return;
            }

            if (TryStartSpan(text, i, length, _rule.BlockComments, LineState.InBlockComment, ref state, tokens, ref i))
            {
                if (state.State != LineState.Normal) return;
                continue;
            }

            if (_rule.Heredoc is { } heredoc && TryStartHeredoc(text, i, heredoc, ref state, tokens, ref i))
            {
                return;
            }

            if (TryStartSpan(text, i, length, _rule.Strings, LineState.InMultilineString, ref state, tokens, ref i))
            {
                if (state.State != LineState.Normal) return;
                continue;
            }

            i = ScanWordOrSymbol(text, i, c, tokens);
        }
    }

    // 識別子・数値・演算子のいずれにも当てはまらない文字は1つ読み飛ばして次へ進む。
    private int ScanWordOrSymbol(string text, int i, char c, List<SyntaxToken>? tokens)
    {
        if (IsIdentifierStart(c) && TryScanIdentifier(text, i, tokens, ref i))
        {
            return i;
        }

        if (char.IsDigit(c))
        {
            var numberMatch = _rule.NumberPattern.Match(text, i);
            if (numberMatch.Success)
            {
                AddToken(tokens, i, numberMatch.Length, TokenKind.Number);
                return i + numberMatch.Length;
            }
        }

        if (_rule.OperatorChars.Contains(c))
        {
            AddToken(tokens, i, 1, TokenKind.Operator);
            return i + 1;
        }

        return i + 1; // 未分類文字（記号以外の非ASCII等）は着色せず読み飛ばす。
    }

    private bool TryStartHeredoc(
        string text, int i, HeredocSpec heredoc, ref LineScanState state, List<SyntaxToken>? tokens, ref int cursor)
    {
        var match = heredoc.StartPattern.Match(text, i);
        if (!match.Success) return false;

        AddToken(tokens, i, match.Length, TokenKind.Operator);
        state.State = LineState.InMultilineString;
        state.Closer = match.Groups["term"].Value;
        state.CloserIsLineAnchor = true;
        cursor = text.Length;
        return true;
    }

    // ブロックコメント／文字列リテラルの開始を判定する共通処理。見つからないまま行末に達し、
    // spec.Multiline が true の場合のみ continuationState へ遷移して次行へ継続する。
    private static bool TryStartSpan(
        string text, int i, int length, IReadOnlyList<StringSpec> specs, LineState continuationState,
        ref LineScanState state, List<SyntaxToken>? tokens, ref int cursor)
    {
        foreach (var spec in specs)
        {
            if (!MatchesAt(text, i, spec.Open)) continue;

            var closeIndex = FindClose(text, i + spec.Open.Length, spec.Close, spec.EscapeChar, spec.DoubledClosingEscapes);
            if (closeIndex < 0)
            {
                AddToken(tokens, i, length - i, spec.Kind);
                if (spec.Multiline)
                {
                    state.State = continuationState;
                    state.Closer = spec.Close;
                    state.EscapeChar = spec.EscapeChar;
                    state.DoubledClosingEscapes = spec.DoubledClosingEscapes;
                    state.CloserIsLineAnchor = false;
                }

                cursor = length;
                return true;
            }

            var end = closeIndex + spec.Close.Length;
            AddToken(tokens, i, end - i, spec.Kind);
            cursor = end;
            return true;
        }

        return false;
    }

    // close の出現位置を探す。escapeChar 指定時は直後の1文字を読み飛ばし、
    // doubledClosingEscapes 指定時は close が2つ連続する箇所をエスケープとみなして読み飛ばす。
    // 行をまたいだ継続（LineStateScanner.ContinueSpan）からも同じ規則で呼ばれる。
    internal static int FindClose(string text, int start, string close, char? escapeChar, bool doubledClosingEscapes)
    {
        var i = start;
        var length = text.Length;
        while (i < length)
        {
            if (escapeChar is { } escape && text[i] == escape && i + 1 < length)
            {
                i += 2;
                continue;
            }

            if (MatchesAt(text, i, close))
            {
                if (doubledClosingEscapes && MatchesAt(text, i + close.Length, close))
                {
                    i += close.Length * 2;
                    continue;
                }

                return i;
            }

            i++;
        }

        return -1;
    }

    private bool TryScanIdentifier(string text, int i, List<SyntaxToken>? tokens, ref int cursor)
    {
        var match = _rule.IdentifierPattern.Match(text, i);
        if (!match.Success) return false;

        var word = match.Value;
        var kind = ClassifyWord(text, i, word);
        if (kind != TokenKind.Plain)
        {
            AddToken(tokens, i, word.Length, kind);
        }

        cursor = i + word.Length;
        return true;
    }

    // キーワード → 関数呼び出し（直後が '('） → 型名ヒント（PascalCase／型導入キーワードの直後）の順で判定する。
    private TokenKind ClassifyWord(string text, int wordStart, string word)
    {
        if (_rule.Keywords.Contains(word)) return TokenKind.Keyword;

        var j = wordStart + word.Length;
        while (j < text.Length && char.IsWhiteSpace(text[j])) j++;
        if (j < text.Length && text[j] == '(') return TokenKind.Function;

        if (_rule.PascalCaseIsType && IsPascalCase(word)) return TokenKind.Type;

        if (_rule.TypeIntroducerKeywords.Count > 0)
        {
            var previous = PreviousWord(text, wordStart);
            if (previous is not null && _rule.TypeIntroducerKeywords.Contains(previous))
            {
                return TokenKind.Type;
            }
        }

        return TokenKind.Plain;
    }

    // 先頭が大文字かつ後続に小文字を含む場合に PascalCase とみなす（全大文字の定数は除外）。
    private static bool IsPascalCase(string word)
    {
        if (word.Length == 0 || !char.IsUpper(word[0])) return false;
        for (var i = 1; i < word.Length; i++)
        {
            if (char.IsLower(word[i])) return true;
        }

        return false;
    }

    private static string? PreviousWord(string text, int beforeIndex)
    {
        var end = beforeIndex;
        while (end > 0 && char.IsWhiteSpace(text[end - 1])) end--;

        var start = end;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_')) start--;

        return start < end ? text[start..end] : null;
    }

    private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';

    private static bool MatchesAt(string text, int i, string token)
    {
        if (i < 0 || i + token.Length > text.Length) return false;
        return string.CompareOrdinal(text, i, token, 0, token.Length) == 0;
    }

    private static bool TryMatchAny(string text, int i, IReadOnlyList<string> tokens, out string matched)
    {
        foreach (var token in tokens)
        {
            if (MatchesAt(text, i, token))
            {
                matched = token;
                return true;
            }
        }

        matched = string.Empty;
        return false;
    }

    // LineStateScanner（行頭状態の継続処理）からも共通で使うため internal にする。
    internal static void AddToken(List<SyntaxToken>? tokens, int start, int length, TokenKind kind)
    {
        if (tokens is null || length <= 0) return;
        tokens.Add(new SyntaxToken(start, length, kind));
    }
}
