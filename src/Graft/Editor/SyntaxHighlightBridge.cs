using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using Graft.Core;
using Graft.Themes;

namespace Graft.Editor;

/// <summary>
/// 自前レキサ（<see cref="SyntaxLexer"/>、11言語）をAvalonEditの描画パイプラインへ接続する
/// カラライザ（4.1節）。AvalonEdit内蔵の.xshd定義は一切使用しない。可視行のみが
/// <see cref="ColorizeLine"/>経由で<see cref="SyntaxLexer.TokenizeLine"/>へ渡されるため、
/// ファイル全体のUI要素を一括生成することはない（18章: 仮想化の維持）。
/// 色は<c>Themes/Dark.xaml</c>・<c>Themes/Light.xaml</c>の<c>SyntaxXxxColor</c>を
/// <c>TryFindResource</c>で都度解決するため、テーマ切替に自動追従する
/// （<see cref="ThemeManager.ThemeChanged"/>購読による再描画と合わせて反映する）。
/// </summary>
public sealed class SyntaxHighlightBridge : DocumentColorizingTransformer, IDisposable
{
    // 編集のたびに全行を再スキャンすると10万行級のファイルで性能要件（18章）を満たせないため、
    // 入力が止まってからまとめて1回スキャンする（デバウンス）。
    private const int RescanDebounceMs = 200;

    private readonly TextEditor _editor;
    private readonly DispatcherTimer _rescanTimer;
    private SyntaxLexer? _lexer;
    private TextDocument? _document;
    private bool _syntaxEnabled = true;
    private bool _disposed;

    public SyntaxHighlightBridge(TextEditor editor)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _rescanTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(RescanDebounceMs) };
        _rescanTimer.Tick += OnRescanTick;
        ThemeManager.ThemeChanged += OnThemeChanged;
    }

    /// <summary>性能上限超過によりハイライトが無効化されているかどうか（8.6/18章の通知用）。</summary>
    public bool IsDisabled => _lexer?.IsDisabled ?? false;

    /// <summary>
    /// 対象ドキュメントと拡張子を切り替える。<paramref name="syntaxEnabled"/>が false の場合
    /// （設定でのハイライト無効化）はプレーン表示にする。
    /// </summary>
    public void Attach(TextDocument document, string extension, bool syntaxEnabled)
    {
        ArgumentNullException.ThrowIfNull(document);
        DetachDocument();

        _document = document;
        _syntaxEnabled = syntaxEnabled;
        var rule = syntaxEnabled ? SyntaxLexer.RuleForExtension(extension) : null;
        _lexer = rule is null ? null : new SyntaxLexer(rule);
        RescanNow();

        document.Changed += OnDocumentChanged;
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        if (!_syntaxEnabled || _lexer is null || _lexer.IsDisabled) return;

        var text = CurrentContext.Document.GetText(line);
        foreach (var token in _lexer.TokenizeLine(line.LineNumber - 1, text))
        {
            if (token.Kind == TokenKind.Plain) continue;

            var start = line.Offset + token.Start;
            var end = Math.Min(start + token.Length, line.EndOffset);
            if (end <= start) continue;

            ChangeLinePart(start, end, element => ApplyColor(element, token.Kind));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DetachDocument();
        ThemeManager.ThemeChanged -= OnThemeChanged;
    }

    private static void ApplyColor(VisualLineElement element, TokenKind kind)
    {
        if (ResolveColor(kind) is { } color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            element.TextRunProperties.SetForegroundBrush(brush);
        }

        // 8.6: コメントトークンのみイタリック表示にする（他種別のウェイト/スタイルは変更しない）。
        if (kind == TokenKind.Comment)
        {
            var current = element.TextRunProperties.Typeface;
            element.TextRunProperties.SetTypeface(
                new Typeface(current.FontFamily, FontStyles.Italic, current.Weight, current.Stretch));
        }
    }

    private static Color? ResolveColor(TokenKind kind)
        => Application.Current?.TryFindResource(ColorKeyFor(kind)) is Color c ? c : null;

    private static string ColorKeyFor(TokenKind kind) => kind switch
    {
        TokenKind.Keyword => "SyntaxKeywordColor",
        TokenKind.String => "SyntaxStringColor",
        TokenKind.Number => "SyntaxNumberColor",
        TokenKind.Comment => "SyntaxCommentColor",
        TokenKind.Function => "SyntaxFunctionColor",
        TokenKind.Type => "SyntaxTypeColor",
        TokenKind.Operator => "SyntaxOperatorColor",
        _ => "TextPrimaryColor",
    };

    private void OnDocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        _rescanTimer.Stop();
        _rescanTimer.Start();
    }

    private void OnRescanTick(object? sender, EventArgs e)
    {
        _rescanTimer.Stop();
        RescanNow();
        _editor.TextArea.TextView.Redraw();
    }

    private void RescanNow()
    {
        if (_lexer is null || _document is null) return;
        _lexer.Scan(TextNormalizer.SplitLines(_document.Text));
    }

    private void OnThemeChanged(object? sender, EventArgs e) => _editor.TextArea.TextView.Redraw();

    private void DetachDocument()
    {
        if (_document is not null) _document.Changed -= OnDocumentChanged;
        _rescanTimer.Stop();
        _document = null;
        _lexer = null;
    }
}
