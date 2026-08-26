using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Graft.Core;
using Graft.Themes;

namespace Graft.Editor;

/// <summary>
/// 自前レキサ（<see cref="SyntaxLexer"/>、11言語）をAvaloniaEditの描画パイプラインへ接続する
/// カラライザ（4.1節）。AvaloniaEdit内蔵の.xshd定義は一切使用しない。可視行のみが
/// <see cref="ColorizeLine"/>経由で<see cref="SyntaxLexer.TokenizeLine"/>へ渡されるため、
/// ファイル全体のUI要素を一括生成することはない（18章: 仮想化の維持）。
/// 色は<c>Themes/Dark.axaml</c>・<c>Themes/Light.axaml</c>の<c>SyntaxXxxColor</c>を
/// <c>TryFindResource</c>で解決し、テーマ切替に追従する
/// （<see cref="ThemeManager.ThemeChanged"/>購読による破棄＋再描画で反映する。
/// <see cref="ResolveBrush"/>・<see cref="OnThemeChanged"/>参照）。
/// v2.0のWPF版（AvalonEdit）からの移植。DocumentColorizingTransformerのAPIはAvaloniaEditでも
/// 同名同形のため、System.Windows.*をAvalonia.*へ、DispatcherTimerをAvalonia.Threading側へ
/// 差し替えるのみで移植できる。
///
/// 【課題#73（ドラッグ追従）に伴う変更】 移植時は「Brush/PenのFreeze()はAvaloniaに対応物が
/// 無いため呼び出さない。都度生成してもスレッド共有できないため凍結による共有最適化自体が
/// 不要」と判断してトークンごとにブラシを作り直していたが、これは誤りだった。Avaloniaでの
/// 対応物は<see cref="Avalonia.Media.Immutable.ImmutableSolidColorBrush"/>（不変ブラシ）であり、
/// 使い回しは安全にできる。10万行のファイルのドラッグでは1フレームあたり数百個のブラシ・書体を
/// 作っては捨てていたため、種別ごとにキャッシュする形へ改めた（実測での効果と限界は
/// <see cref="ResolveBrush"/>のコメント参照）。
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

    // 課題#73: トークン種別ごとのブラシのキャッシュ（OnThemeChangedで破棄する）。解決に
    // 成功した種別だけを入れる（Application.Currentがまだ無い等の"解決できなかった"状態を
    // 焼き付けないよう、失敗はキャッシュしない。ResolveBrush参照）。
    private readonly Dictionary<TokenKind, IBrush> _brushCache = new();

    // 課題#73: コメント用イタリック書体のキャッシュ。元になる書体（フォント設定・Ctrl+ホイールの
    // 拡大縮小で変わりうる）が同じ間だけ使い回す1件キャッシュ。1つのエディタの可視行は
    // GlobalTextRunPropertiesから同じ書体を引くため、実質的に常に当たる。
    private Typeface? _italicSource;
    private Typeface _italicTypeface;

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

        // 課題3（再設計）: ファイル全体の無効化ではなく、しきい値を超えるその行だけ強調を
        // 打ち切る（VS Codeの既定のトークナイズ上限と同じ考え方）。ColorizeLineは元々
        // AvaloniaEditが可視行のみを対象に呼ぶため、ここで打ち切っても他の行（ファイルの
        // 残り99%）の強調には影響しない（DocumentSession.LongLineThresholdのコメント参照）。
        if (line.Length > DocumentSession.LongLineThreshold) return;

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

    private void ApplyColor(VisualLineElement element, TokenKind kind)
    {
        if (ResolveBrush(kind) is { } brush)
        {
            element.TextRunProperties.SetForegroundBrush(brush);
        }

        // 8.6: コメントトークンのみイタリック表示にする（他種別のウェイト/スタイルは変更しない）。
        if (kind == TokenKind.Comment)
        {
            element.TextRunProperties.SetTypeface(ResolveItalicTypeface(element.TextRunProperties.Typeface));
        }
    }

    /// <summary>
    /// 課題#73（スクロールバーのドラッグがマウスに追い付かない）: 以前はトークン1つごとに
    /// <c>Application.TryFindResource</c>で色を引き直し、<c>new SolidColorBrush(...)</c>を
    /// 作り直していた。構文強調はドラッグ1ステップ（10万行でつまみ1px＝可視46行の作り直し）
    /// あたり<b>+2.0ms</b>を占めており、折りたたみ（+5.1ms、<see cref="FoldingSupport"/>の
    /// クラスコメント【課題#73】節）に次ぐ2番目の要因だったため、色はテーマが変わらない限り
    /// 不変であることを使って種別ごとに1度だけ解決してキャッシュし、
    /// <see cref="OnThemeChanged"/>で捨てるようにした。
    ///
    /// 【正直な実測結果】 このキャッシュ<b>単体でのレイアウト時間の短縮は測定誤差の範囲だった</b>
    /// （交互25組・中央値で 上乗せ +2.03ms → +2.00ms）。構文強調の費用の大半はブラシ生成では
    /// なくトークン化と<c>ChangeLinePart</c>以降の処理側にある、というのが実測から言えること
    /// である。一方で<b>割り当て量ははっきり減っている</b>: ドラッグ60ステップ分の割り当てが
    /// 108.96MB → 104.39MB（Graftの装飾すべてでは116.62MB → 110.92MB、1フレームあたり約95KB減）。
    /// フレームあたり数百個のブラシ・書体を捨て続けるのをやめる分、GCの圧力が下がる
    /// （中央値には出ないが、長いドラッグ中のスパイクに効く種類の改善）。効果が測定誤差でも
    /// 残す判断をしたのは、実装が単純（辞書1つ）で、テーマ追従の回帰も
    /// tests/Graft.UiTests/SyntaxHighlightThemeCacheTests.csで固定してあるため。
    ///
    /// <see cref="ImmutableSolidColorBrush"/>を使うのは、クラスコメントにあるとおりAvaloniaには
    /// WPFの<c>Freeze()</c>が無い代わりに不変ブラシ型が用意されており、描画スレッドとの
    /// 共有時に変更通知の購読が要らない（＝使い回しても安全な）ためである。AvaloniaEdit自身も
    /// 折りたたみ要素の枠線描画で<c>ToImmutable()</c>して同じことをしている。
    /// </summary>
    private IBrush? ResolveBrush(TokenKind kind)
    {
        if (_brushCache.TryGetValue(kind, out var cached)) return cached;

        if (Application.Current is not { } app
            || !app.TryFindResource(ColorKeyFor(kind), null, out var value)
            || value is not Color color)
        {
            // 解決できなかった（まだApplicationが無い等）ケースはキャッシュしない。
            return null;
        }

        var brush = new ImmutableSolidColorBrush(color);
        _brushCache[kind] = brush;
        return brush;
    }

    /// <summary>
    /// 課題#73: コメント行の<c>new Typeface(...)</c>もトークンごとに作り直していたため、
    /// 元の書体が変わっていない限り使い回す（<see cref="_italicSource"/>参照）。
    /// フォント設定の変更・Ctrl+ホイールでの拡大縮小で元の書体が変われば、比較が外れて
    /// 自動的に作り直される（<see cref="Typeface"/>は値の等価比較を持つ構造体）。
    /// </summary>
    private Typeface ResolveItalicTypeface(Typeface source)
    {
        if (_italicSource is { } cachedSource && cachedSource.Equals(source)) return _italicTypeface;

        _italicSource = source;
        _italicTypeface = new Typeface(source.FontFamily, FontStyle.Italic, source.Weight, source.Stretch);
        return _italicTypeface;
    }

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

    /// <summary>
    /// テーマが変わったら、キャッシュしたブラシを捨ててから再描画する（課題#73）。
    /// <see cref="ThemeManager"/>はテーマ辞書を差し替えたあとにこのイベントを発火し、
    /// Avalonia側の再レイアウト・再描画はそれより後のディスパッチャジョブとして走るため、
    /// ここで捨てておけば古い色で1フレーム描かれることはない。イタリック書体は色を含まない
    /// （テーマで変わらない）ため捨てる必要は無い。
    /// </summary>
    private void OnThemeChanged(object? sender, EventArgs e)
    {
        _brushCache.Clear();
        _editor.TextArea.TextView.Redraw();
    }

    private void DetachDocument()
    {
        if (_document is not null) _document.Changed -= OnDocumentChanged;
        _rescanTimer.Stop();
        _document = null;
        _lexer = null;
    }
}
