using System.Diagnostics;

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

/// <summary>行頭の状態。複数行文字列・ブロックコメントの継続を表す。</summary>
public enum LineState
{
    Normal,
    InBlockComment,
    InMultilineString,
}

/// <summary>
/// 正規表現ベースの軽量シンタックスレキサ。外部ライブラリに依存しない。
/// ファイル全体を一度スキャンして各行の開始状態をキャッシュし（<see cref="Scan"/>）、
/// 表示範囲の行のみをトークン化する（<see cref="TokenizeLine"/>）。
/// </summary>
public sealed class SyntaxLexer
{
    // 10000行のスキャンを100ms以内に収める、という仕様書8.6の基準を行数に応じて按分する。
    private const double ScanBudgetMsPer10000Lines = 100.0;
    private const int ScanBudgetCheckInterval = 512;

    private readonly LanguageRule _rule;

    private LineState[] _lineStartStates = Array.Empty<LineState>();
    private string?[] _lineStartClosers = Array.Empty<string?>();
    private char?[] _lineStartEscapeChars = Array.Empty<char?>();
    private bool[] _lineStartDoubledClosingEscapes = Array.Empty<bool>();
    private bool[] _lineStartCloserIsLineAnchor = Array.Empty<bool>();

    public SyntaxLexer(LanguageRule rule)
    {
        _rule = rule ?? throw new ArgumentNullException(nameof(rule));
    }

    /// <summary>スキャンが性能基準を満たせず無効化されたかどうか。</summary>
    public bool IsDisabled { get; private set; }

    /// <summary>各行の開始状態。<see cref="Scan"/> 実行後のみ有効な要素数を持つ。</summary>
    public IReadOnlyList<LineState> LineStartStates => _lineStartStates;

    /// <summary>拡張子から対応する言語ルールを取得する。未対応の拡張子は null（プレーン表示）。</summary>
    public static LanguageRule? RuleForExtension(string extension) => LanguageRule.ForExtension(extension);

    /// <summary>
    /// ファイル全体を一度スキャンし、各行の開始状態を配列として保持する。
    /// 10000行換算で100msを超えた場合は打ち切り、<see cref="IsDisabled"/> を true にして false を返す。
    /// </summary>
    public bool Scan(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var count = lines.Count;
        var states = new LineState[count];
        var closers = new string?[count];
        var escapeChars = new char?[count];
        var doubledClosingEscapes = new bool[count];
        var anchors = new bool[count];

        var current = new LineScanState { State = LineState.Normal };
        var budgetMs = Math.Max(ScanBudgetMsPer10000Lines, count / 10000.0 * ScanBudgetMsPer10000Lines);
        var stopwatch = Stopwatch.StartNew();

        for (var i = 0; i < count; i++)
        {
            states[i] = current.State;
            closers[i] = current.Closer;
            escapeChars[i] = current.EscapeChar;
            doubledClosingEscapes[i] = current.DoubledClosingEscapes;
            anchors[i] = current.CloserIsLineAnchor;

            ProcessLine(lines[i], ref current, tokens: null);

            if (i % ScanBudgetCheckInterval == 0 && stopwatch.Elapsed.TotalMilliseconds > budgetMs)
            {
                Disable();
                return false;
            }
        }

        if (stopwatch.Elapsed.TotalMilliseconds > budgetMs)
        {
            Disable();
            return false;
        }

        _lineStartStates = states;
        _lineStartClosers = closers;
        _lineStartEscapeChars = escapeChars;
        _lineStartDoubledClosingEscapes = doubledClosingEscapes;
        _lineStartCloserIsLineAnchor = anchors;
        IsDisabled = false;
        return true;
    }

    /// <summary>
    /// 指定行のみをトークン化する。<see cref="Scan"/> 未実行、またはハイライトが無効化されている場合は空を返す。
    /// </summary>
    public IReadOnlyList<SyntaxToken> TokenizeLine(int lineIndex, string lineText)
    {
        ArgumentNullException.ThrowIfNull(lineText);

        if (IsDisabled || lineIndex < 0 || lineIndex >= _lineStartStates.Length)
        {
            return Array.Empty<SyntaxToken>();
        }

        var state = new LineScanState
        {
            State = _lineStartStates[lineIndex],
            Closer = _lineStartClosers[lineIndex],
            EscapeChar = _lineStartEscapeChars[lineIndex],
            DoubledClosingEscapes = _lineStartDoubledClosingEscapes[lineIndex],
            CloserIsLineAnchor = _lineStartCloserIsLineAnchor[lineIndex],
        };

        var tokens = new List<SyntaxToken>();
        ProcessLine(lineText, ref state, tokens);
        return tokens;
    }

    private void Disable()
    {
        IsDisabled = true;
        _lineStartStates = Array.Empty<LineState>();
        _lineStartClosers = Array.Empty<string?>();
        _lineStartEscapeChars = Array.Empty<char?>();
        _lineStartDoubledClosingEscapes = Array.Empty<bool>();
        _lineStartCloserIsLineAnchor = Array.Empty<bool>();
    }

    // 行頭の継続状態を考慮しつつ1行をスキャンする。tokens が null の場合はトークンを生成せず
    // 状態遷移のみ計算する（Scan での高速な事前走査に使う）。
    private void ProcessLine(string text, ref LineScanState state, List<SyntaxToken>? tokens)
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

        ScanNormal(text, pos, length, ref state, tokens);
    }

    // 継続中のブロックコメント／複数行文字列の終了トークンを現在行から探す。開始行に適用した
    // エスケープ規則（EscapeChar・DoubledClosingEscapes）を継続行でも同じく適用する必要があるため、
    // 単純な IndexOf ではなく FindClose を使う。見つからなければ行全体を1トークンとし状態を維持、
    // 見つかればそこまでを1トークンとして Normal に戻す。
    private static int ContinueSpan(
        string text, int length, ref LineScanState state, List<SyntaxToken>? tokens, TokenKind kind)
    {
        var closer = state.Closer!;
        var closeIndex = FindClose(text, 0, closer, state.EscapeChar, state.DoubledClosingEscapes);
        if (closeIndex < 0)
        {
            AddToken(tokens, 0, length, kind);
            return length;
        }

        var end = closeIndex + closer.Length;
        AddToken(tokens, 0, end, kind);
        state.State = LineState.Normal;
        state.Closer = null;
        state.EscapeChar = null;
        state.DoubledClosingEscapes = false;
        return end;
    }

    private static void ProcessHeredocBody(string text, ref LineScanState state, List<SyntaxToken>? tokens)
    {
        var trimmed = text.TrimStart();
        AddToken(tokens, 0, text.Length, TokenKind.String);
        if (trimmed == state.Closer)
        {
            state.State = LineState.Normal;
            state.Closer = null;
            state.CloserIsLineAnchor = false;
        }
    }

    private void ScanNormal(string text, int start, int length, ref LineScanState state, List<SyntaxToken>? tokens)
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
    private static int FindClose(string text, int start, string close, char? escapeChar, bool doubledClosingEscapes)
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

    private static void AddToken(List<SyntaxToken>? tokens, int start, int length, TokenKind kind)
    {
        if (tokens is null || length <= 0) return;
        tokens.Add(new SyntaxToken(start, length, kind));
    }

    /// <summary>行頭から次行へ引き継ぐ字句状態。</summary>
    private struct LineScanState
    {
        public LineState State;
        public string? Closer;
        public char? EscapeChar;
        public bool DoubledClosingEscapes;
        public bool CloserIsLineAnchor;
    }
}
