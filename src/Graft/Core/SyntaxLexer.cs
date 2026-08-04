using System.Diagnostics;

namespace Graft.Core;

/// <summary>
/// 正規表現ベースの軽量シンタックスレキサ。外部ライブラリに依存しない。
/// ファイル全体を一度スキャンして各行の開始状態をキャッシュし（<see cref="Scan"/>）、
/// 表示範囲の行のみをトークン化する（<see cref="TokenizeLine"/>）。
/// 行頭状態の継続判定は <see cref="LineStateScanner"/>、行内容の実際のトークン化は
/// <see cref="SyntaxTokenizer"/> が担当し、本クラスは公開APIとキャッシュ・性能フォールバックを担う。
/// </summary>
public sealed class SyntaxLexer
{
    // 10000行のスキャンを100ms以内に収める、という仕様書8.6の基準を行数に応じて按分する。
    private const double ScanBudgetMsPer10000Lines = 100.0;
    private const int ScanBudgetCheckInterval = 512;

    private readonly SyntaxTokenizer _tokenizer;

    private LineState[] _lineStartStates = Array.Empty<LineState>();
    private string?[] _lineStartClosers = Array.Empty<string?>();
    private char?[] _lineStartEscapeChars = Array.Empty<char?>();
    private bool[] _lineStartDoubledClosingEscapes = Array.Empty<bool>();
    private bool[] _lineStartCloserIsLineAnchor = Array.Empty<bool>();

    public SyntaxLexer(LanguageRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        _tokenizer = new SyntaxTokenizer(rule);
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

            LineStateScanner.ProcessLine(lines[i], ref current, tokens: null, _tokenizer);

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
        LineStateScanner.ProcessLine(lineText, ref state, tokens, _tokenizer);
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
}
