using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Graft.Core;

namespace Graft.Editor;

/// <summary>
/// 括弧の自動対応（4.4節）。<see cref="TextEditor"/>を直接受け取って動作し、エディタへの
/// 組み込み（イベント購読の生存管理・タブ切替時の<see cref="Attach"/>呼び出し）は統合担当が行う。
/// 入力時の自動閉じは<see cref="Core.SyntaxLexer"/>のトークン種別を使い、文字列・コメント内では
/// 行わない（<see cref="SyntaxHighlightBridge"/>とは別に独自のレキサインスタンスを持つ。
/// 色付け用インスタンスは<c>EditorPane</c>内部に閉じているため参照できないための実装）。
/// 対応括弧の強調は<see cref="IBackgroundRenderer"/>として描画する。
/// v2.0のWPF版（AvalonEdit）からの移植。TextEntering/TextEnteredのイベント引数は
/// <see cref="TextCompositionEventArgs"/>から<see cref="TextInputEventArgs"/>へ、
/// マウス関連は<see cref="MouseEventArgs"/>ではなく<see cref="PointerEventArgs"/>へ、
/// それぞれAvalonia側の対応物へ差し替える。Pen/BrushのFreeze()はAvaloniaに対応物が無いため
/// 呼び出さない。
/// </summary>
public sealed class BracketSupport : IBackgroundRenderer, IDisposable
{
    // 括弧入力のたびに全文再スキャンすると10万行級のファイルで性能要件（18章）を満たせないため、
    // SyntaxHighlightBridgeと同じ方針でデバウンスする。
    private const int RescanDebounceMs = 200;

    private static readonly Dictionary<char, char> Pairs = new() { ['('] = ')', ['['] = ']', ['{'] = '}' };
    private static readonly HashSet<char> OpenChars = new(Pairs.Keys);
    private static readonly HashSet<char> CloseChars = new(Pairs.Values);

    private readonly TextEditor _editor;
    private readonly DispatcherTimer _rescanTimer;
    private SyntaxLexer? _lexer;
    private TextDocument? _document;
    private bool _autoCloseEnabled = true;
    private bool _disposed;

    public BracketSupport(TextEditor editor)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _rescanTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(RescanDebounceMs) };
        _rescanTimer.Tick += OnRescanTick;

        _editor.TextArea.TextEntering += OnTextEntering;
        _editor.TextArea.TextEntered += OnTextEntered;
        _editor.TextArea.Caret.PositionChanged += OnCaretPositionChanged;
        _editor.TextArea.TextView.BackgroundRenderers.Add(this);
    }

    public KnownLayer Layer => KnownLayer.Selection;

    /// <summary>対象ドキュメントと言語ルールを切り替える（タブ切替のたび呼ぶ）。拡張子未対応の
    /// 言語は文字列・コメント判定を行わず、常に自動閉じを許可する。</summary>
    public void Attach(TextDocument document, string extension)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (_document is not null) _document.Changed -= OnDocumentChanged;

        _document = document;
        var rule = SyntaxLexer.RuleForExtension(extension);
        _lexer = rule is null ? null : new SyntaxLexer(rule);
        RescanNow();
        _document.Changed += OnDocumentChanged;
    }

    /// <summary>15章 <c>editor.autoClosingBrackets</c> 設定の反映。</summary>
    public void SetAutoCloseEnabled(bool enabled) => _autoCloseEnabled = enabled;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_editor.Document is null) return;
        textView.EnsureVisualLines();

        var pair = FindMatchingPairAtCaret();
        if (pair is null) return;

        var pen = ResolvePen();
        if (pen is null) return;

        DrawBracketBox(textView, drawingContext, pen, pair.Value.Open);
        DrawBracketBox(textView, drawingContext, pen, pair.Value.Close);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _rescanTimer.Stop();
        _editor.TextArea.TextEntering -= OnTextEntering;
        _editor.TextArea.TextEntered -= OnTextEntered;
        _editor.TextArea.Caret.PositionChanged -= OnCaretPositionChanged;
        _editor.TextArea.TextView.BackgroundRenderers.Remove(this);
        if (_document is not null) _document.Changed -= OnDocumentChanged;
    }

    private void OnTextEntering(object? sender, TextInputEventArgs e)
    {
        if (!_autoCloseEnabled || string.IsNullOrEmpty(e.Text)) return;
        var ch = e.Text[0];

        if (_editor.SelectionLength > 0 && Pairs.TryGetValue(ch, out var closeForSelection))
        {
            WrapSelection(ch, closeForSelection);
            e.Handled = true;
            return;
        }

        if (CloseChars.Contains(ch)) TryTypeOverClose(ch, e);
    }

    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (!_autoCloseEnabled || string.IsNullOrEmpty(e.Text)) return;
        var ch = e.Text[0];
        if (!Pairs.TryGetValue(ch, out var close)) return;

        var caret = _editor.CaretOffset;
        if (IsInsideStringOrComment(caret - 1)) return;

        _editor.Document.Insert(caret, close.ToString());
        _editor.CaretOffset = caret;
    }

    private void WrapSelection(char open, char close)
    {
        var start = _editor.SelectionStart;
        var length = _editor.SelectionLength;
        var doc = _editor.Document;

        doc.UndoStack.StartUndoGroup();
        try
        {
            doc.Insert(start + length, close.ToString());
            doc.Insert(start, open.ToString());
        }
        finally
        {
            doc.UndoStack.EndUndoGroup();
        }
        _editor.Select(start + 1, length);
    }

    private void TryTypeOverClose(char ch, TextInputEventArgs e)
    {
        var caret = _editor.CaretOffset;
        if (caret >= _editor.Document.TextLength || _editor.Document.GetCharAt(caret) != ch) return;
        _editor.CaretOffset = caret + 1;
        e.Handled = true;
    }

    private (int Open, int Close)? FindMatchingPairAtCaret()
    {
        var doc = _editor.Document;
        var caret = _editor.TextArea.Caret.Offset;

        if (caret < doc.TextLength && IsBracketChar(doc.GetCharAt(caret)))
        {
            return MatchBracket(doc, caret, doc.GetCharAt(caret));
        }
        if (caret > 0 && IsBracketChar(doc.GetCharAt(caret - 1)))
        {
            return MatchBracket(doc, caret - 1, doc.GetCharAt(caret - 1));
        }
        return null;
    }

    private static bool IsBracketChar(char c) => OpenChars.Contains(c) || CloseChars.Contains(c);

    /// <summary>同種の括弧のみを深さで数える簡易実装（異なる種類の括弧の対応関係までは追跡しない）。</summary>
    private (int Open, int Close)? MatchBracket(TextDocument doc, int offset, char ch)
    {
        var isOpen = OpenChars.Contains(ch);
        var openChar = isOpen ? ch : Pairs.First(p => p.Value == ch).Key;
        var closeChar = isOpen ? Pairs[ch] : ch;
        var step = isOpen ? 1 : -1;
        var depth = 0;

        for (var i = offset; i >= 0 && i < doc.TextLength; i += step)
        {
            if (!IsInsideStringOrComment(i))
            {
                var c = doc.GetCharAt(i);
                if (c == openChar) depth++;
                else if (c == closeChar) depth--;
                if (depth == 0) return isOpen ? (offset, i) : (i, offset);
            }
        }
        return null;
    }

    private bool IsInsideStringOrComment(int offset)
    {
        if (_lexer is null || _lexer.IsDisabled) return false;
        var doc = _editor.Document;
        if (offset < 0 || offset >= doc.TextLength) return false;

        var line = doc.GetLineByOffset(offset);
        var column = offset - line.Offset;
        var lineText = doc.GetText(line.Offset, line.Length);
        foreach (var token in _lexer.TokenizeLine(line.LineNumber - 1, lineText))
        {
            if (column < token.Start || column >= token.Start + token.Length) continue;
            return token.Kind is TokenKind.String or TokenKind.Comment;
        }
        return false;
    }

    private static void DrawBracketBox(TextView textView, DrawingContext drawingContext, IPen pen, int offset)
    {
        var builder = new BackgroundGeometryBuilder { AlignToWholePixels = true, CornerRadius = 2 };
        builder.AddSegment(textView, new TextSegment { StartOffset = offset, EndOffset = offset + 1 });
        var geometry = builder.CreateGeometry();
        if (geometry is not null) drawingContext.DrawGeometry(Brushes.Transparent, pen, geometry);
    }

    private static IPen? ResolvePen()
    {
        if (Application.Current is not { } app || !app.TryFindResource("AccentColor", null, out var value) || value is not Color color)
        {
            return null;
        }

        var brush = new SolidColorBrush(color);
        return new Pen(brush, 1.3);
    }

    private void OnCaretPositionChanged(object? sender, EventArgs e)
        => _editor.TextArea.TextView.InvalidateLayer(Layer);

    private void OnDocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        _rescanTimer.Stop();
        _rescanTimer.Start();
    }

    private void OnRescanTick(object? sender, EventArgs e)
    {
        _rescanTimer.Stop();
        RescanNow();
        _editor.TextArea.TextView.InvalidateLayer(Layer);
    }

    private void RescanNow()
    {
        if (_lexer is null || _document is null) return;
        _lexer.Scan(TextNormalizer.SplitLines(_document.Text));
    }
}
