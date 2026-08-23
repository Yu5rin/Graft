using System.Reflection;
using Avalonia.Media.TextFormatting;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Graft.Infra;

namespace Graft.Editor;

/// <summary>
/// 課題#72「折り返し行のインデント継承」。折り返された行の2行目以降を、1行目のインデント位置
/// （＋<see cref="TextEditorOptions.WordWrapIndentation"/>）まで字下げして表示する
/// （いわゆる ぶら下げインデント／hanging indent）。
///
/// <para>
/// 【なぜ自前で実装するのか】 AvaloniaEditには<see cref="TextEditorOptions.
/// InheritWordWrapIndentation"/>（既定<c>true</c>）という、まさにこの機能のための設定が
/// 最初から用意されている。にもかかわらずGraftでは一切効いていなかった。原因は
/// <b>2つのライブラリ双方にあり、どちらもGraft側の設定値では回避できない</b>。
/// </para>
///
/// <para>
/// 【原因① AvaloniaEdit 11.1.0 のバグ】 <c>AvaloniaEdit/Rendering/TextView.cs</c> の
/// <c>CreateParagraphProperties()</c>（1034行、<c>private</c>）は
/// <c>VisualLineTextParagraphProperties</c> を生成する際、<c>defaultTextRunProperties</c>・
/// <c>textWrapping</c>・<c>tabSize</c> の3つしか設定せず、<c>firstLineInParagraph</c>
/// （<c>internal</c>フィールド）を設定し忘れている。<c>bool</c>の既定値<c>false</c>のままに
/// なるため、<c>BuildVisualLine()</c> の整形ループにあるインデント計算ブロック
/// （1105〜1123行の <c>if (paragraphProperties.firstLineInParagraph)</c>）が<b>一度も実行
/// されない</b>。すなわち <c>paragraphProperties.indent</c> は永久に0のままで、
/// <see cref="TextEditorOptions.InheritWordWrapIndentation"/> も
/// <see cref="TextEditorOptions.WordWrapIndentation"/> も無視される。
/// このバグは執筆時点の最新である 11.4.0 でも直っていないことをソースで確認した
/// （なおAvaloniaEditは18章の性能要件のため<b>11.1.0に固定</b>している。CLAUDE.md参照）。
/// </para>
///
/// <para>
/// 【原因② Avalonia 11.2.3 側】 仮に原因①が直って <c>paragraphProperties.indent</c> に値が
/// 入ったとしても、<b>Avaloniaの<c>TextFormatter</c>は
/// <see cref="TextParagraphProperties.Indent"/>を一切読まない</b>。
/// <c>Avalonia.Base/Media/TextFormatting/TextFormatterImpl.cs</c>・<c>TextLineImpl.cs</c>の
/// どちらにも<c>Indent</c>への参照が0件で、行の開始X座標
/// （<see cref="TextLine.Start"/>）は<c>TextLineImpl.GetParagraphOffsetX()</c>（1421行、
/// <c>private</c>）だけで決まる。その分岐は<see cref="Avalonia.Media.TextAlignment"/>のみを
/// 見ており、AvaloniaEditの<c>VisualLineTextParagraphProperties</c>は
/// <c>TextAlignment => TextAlignment.Left</c>・<c>FlowDirection => LeftToRight</c>を
/// 返す固定実装なので、必ず<c>default: return 0</c>へ落ちる。最新の 11.3.20 でも
/// <c>Indent</c>の参照は0件である。
/// </para>
///
/// <para>
/// 【なぜ公開APIの拡張点では実現できないのか】 Graftが普段使っている拡張点
/// （<see cref="IVisualLineTransformer"/>・<see cref="VisualLineElementGenerator"/>・
/// <see cref="IBackgroundRenderer"/>）は、いずれも「行を構成する要素の見た目」か
/// 「行の背景」にしか触れられず、<b>折り返し位置の決定と行の開始X座標</b>には関与できない。
/// 折り返し幅を決めているのは<c>TextFormatter.FormatLine</c>の<c>paragraphWidth</c>引数
/// そのものであり、この呼び出しは<c>TextView.BuildVisualLine()</c>の中に閉じている。
/// また<see cref="TextParagraphProperties"/>を差し替えても原因②のとおり読まれない。
/// 唯一の接合点が「<c>TextView</c>が握っている<c>TextFormatter</c>そのものを包む」ことで、
/// これには後述のリフレクションが要る。
/// </para>
///
/// <para>
/// 【なぜリフレクションが避けられないのか】 包んだ<see cref="TextFormatter"/>を
/// <c>TextView</c>へ渡す方法は2つしかなく、どちらもリフレクションを必要とする。
/// <list type="number">
///   <item><c>TextView._formatter</c>（<c>private</c>フィールド、<c>TextView.cs</c> 1019行）を
///   直接差し替える。本クラスが採る方法。</item>
///   <item><c>AvaloniaLocator</c>へ<see cref="TextFormatter"/>を登録して
///   <c>TextFormatter.Current</c>の戻り値ごと差し替える。しかし
///   <c>AvaloniaLocator.CurrentMutable</c>は<c>[PrivateApi]</c>属性が付いており、NuGetが配る
///   参照アセンブリ（<c>ref/</c>）からは除去されているため<b>コンパイルできない</b>
///   （実測: <c>error CS0117: 'AvaloniaLocator' に 'CurrentMutable' の定義が含まれていません</c>）。
///   結局リフレクションが要るうえ、アプリ全体のテキスト整形に影響する副作用まで背負う。</item>
/// </list>
/// 以上から①を選び、リフレクションが失敗したときは<b>例外を投げず素通し</b>して
/// 従来どおり（字下げなし）動くように縮退させる（<see cref="Install"/>参照）。
/// </para>
///
/// <para>
/// 【<c>Document</c>差し替えのたびに入れ直す理由】 <c>TextView.OnDocumentChanged</c>
/// （<c>TextView.cs</c> 141行）は新しい文書が設定されるたび157行目で
/// <c>_formatter = TextFormatter.Current;</c> と<b>素の整形器で上書きする</b>。つまり
/// タブを切り替えるたびに本機能が外れてしまう。EditorPaneの文書切替経路
/// （<c>ApplyDocumentTab</c>・<c>ApplyEmptyTab</c>）へ個別に呼び出しを足す方式だと将来
/// 経路が増えたときに漏れるため、ここでは<c>TextView.DocumentChanged</c>（<c>public</c>な
/// イベント、163行で上書きの<b>後に</b>発火する）を購読して自動的に入れ直す。
/// 課題#82対応の<c>FoldingSupport.PrepareForDocumentSwap()</c>は代入の<b>前</b>に呼ぶ処理で、
/// 本クラスは代入の<b>中で最後に</b>発火するイベントで動くため、順序が衝突することはない。
/// </para>
/// </summary>
public sealed class WrapIndentSupport
{
    /// <summary>
    /// 差し替え対象のフィールド名（<c>TextView.cs</c> 1019行の<c>private TextFormatter _formatter;</c>）。
    /// テストからは存在しない名前を渡して「将来AvaloniaEdit側の実装が変わって取得できなくなった」
    /// 状況を再現し、例外を投げずに縮退することを確かめる（WrapIndentTests参照）。
    /// </summary>
    private const string FormatterFieldName = "_formatter";

    private readonly TextView _textView;
    private readonly WrapIndentVisualLineTracker _tracker = new();

    // 取得できなければnull＝本機能は無効（素のAvaloniaEditと同じ挙動）。
    private FieldInfo? _formatterField;

    // 取得・差し替えに失敗した理由。Loggerが後から設定される（EditorPaneの流儀。
    // Logger プロパティのコメント参照）ため、記録できるようになるまで保持しておく。
    private string? _pendingFailureLog;
    private bool _loggedFailure;

    private Logger? _logger;

    public WrapIndentSupport(TextEditor editor) : this(editor, FormatterFieldName)
    {
    }

    internal WrapIndentSupport(TextEditor editor, string formatterFieldName)
    {
        _textView = editor.TextArea.TextView;

        // 整形直前のVisualLineを捕まえるための番人。LineTransformersは
        // TextView.BuildVisualLine が整形ループへ入る直前に必ず走る（VisualLine.RunTransformers）。
        // 何も書き換えない読み取り専用の実装のため、既存のLineTransformers
        // （SyntaxHighlightBridge・MarkdownInlineColorizer）との順序は結果に影響しない。
        // 先頭に置くのは「他の変換器が増えても必ず記録が走る」ことを読み手に示すため。
        _textView.LineTransformers.Insert(0, _tracker);

        // Graftはこの設定を利用者へ出さず常時オンとして扱う（判断の理由は本クラスの
        // クラスコメントではなく docs/変更履歴.md と仕様書4章に記す）。AvaloniaEditの既定も
        // trueだが、既定値に依存せず意図として明示しておく。
        editor.Options.InheritWordWrapIndentation = true;

        _formatterField = FindFormatterField(formatterFieldName);
        if (_formatterField is null)
        {
            // 【縮退】 フィールドが見つからない＝将来AvaloniaEditの内部実装が変わった、という
            // ことなので、例外を投げずに本機能だけを諦める（アプリは字下げなしで通常どおり動く）。
            RecordFailure(
                $"AvaloniaEditのTextView.{formatterFieldName}（privateフィールド）を見つけられなかったため、"
                + "折り返し行のインデント継承（課題#72）を無効にしました。字下げ以外の動作に影響はありません",
                new MissingFieldException(nameof(TextView), formatterFieldName));
            return;
        }

        // 文書が差し替わるたびに素の整形器で上書きされるため、そのたびに入れ直す
        // （クラスコメント【Document差し替えのたびに入れ直す理由】参照）。
        _textView.DocumentChanged += OnTextViewDocumentChanged;
        Install();
    }

    /// <summary>
    /// 課題1の<see cref="Views.ShellWindow"/>.Logger・<see cref="IndentGuideRenderer.Logger"/>と
    /// 同じ流儀（生成後にStartupCoordinator経由で設定するnullableプロパティ）。
    /// 本クラスはコンストラクタの時点で差し替えに失敗しうるが、その時点ではまだLoggerが
    /// 設定されていないため、失敗の記録は保留しておき、Loggerが入った瞬間に書き出す。
    /// </summary>
    public Logger? Logger
    {
        get => _logger;
        set
        {
            _logger = value;
            FlushPendingFailureLog();
        }
    }

    /// <summary>
    /// 現在このTextViewの整形器が本機能のもの（<see cref="WrapIndentTextFormatter"/>）に
    /// なっているか。falseなら縮退中＝素のAvaloniaEditと同じ挙動になっている。
    /// </summary>
    public bool IsInstalled
    {
        get
        {
            if (_formatterField is null) return false;
            try
            {
                return _formatterField.GetValue(_textView) is WrapIndentTextFormatter;
            }
            catch (Exception ex) when (IsReflectionFailure(ex))
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 整形器の差し替えを（必要なら）行う。既に差し替え済みなら何もしない。
    /// 通常は文書の差し替えを検知して自動的に呼ばれるため、外部から呼ぶ必要は無い
    /// （テストと、将来AvaloniaEdit側が別経路で整形器を作り直した場合の保険として公開する）。
    /// </summary>
    public void Install()
    {
        if (_formatterField is null) return;

        try
        {
            var current = _formatterField.GetValue(_textView);
            if (current is WrapIndentTextFormatter) return;

            // TextView._formatterは「Documentがnullでない」ときにしか代入されない
            // （TextView.cs 155〜157行）。まだnullのときは公開APIの
            // TextFormatter.Currentを内側に据えておけば、AvaloniaEditが後から
            // 代入する値と同じものを包むことになる（DocumentChangedで入れ直すため、
            // 実際にこの分岐が効くのは文書を持たない特殊な状態のときだけ）。
            var inner = current as TextFormatter ?? TextFormatter.Current;
            _formatterField.SetValue(_textView, new WrapIndentTextFormatter(inner, _tracker, _textView));
        }
        catch (Exception ex) when (IsReflectionFailure(ex))
        {
            // 【縮退】 ここで例外を投げると、タブを切り替えるたびにアプリが落ちることになる。
            // 字下げが効かないだけの機能縮退の方が、利用者にとって明らかに害が小さい。
            // 以後は再試行しない（同じ理由で必ず失敗するため。ログも肥大化する）。
            _formatterField = null;
            _textView.DocumentChanged -= OnTextViewDocumentChanged;
            RecordFailure(
                "AvaloniaEditのTextView._formatter（privateフィールド）を差し替えられなかったため、"
                + $"折り返し行のインデント継承（課題#72）を無効にしました。字下げ以外の動作に影響はありません: {ex}",
                ex);
        }
    }

    private void OnTextViewDocumentChanged(object? sender, DocumentChangedEventArgs e) => Install();

    /// <summary>
    /// <c>TextView._formatter</c>を指す<see cref="FieldInfo"/>を探す。名前だけでなく型
    /// （<see cref="TextFormatter"/>）も確認するのは、将来同名の別用途フィールドへ
    /// 変わったときに黙って壊れた値を書き込まないため。
    /// </summary>
    private static FieldInfo? FindFormatterField(string fieldName)
    {
        var field = typeof(TextView).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        return field is not null && field.FieldType == typeof(TextFormatter) ? field : null;
    }

    /// <summary>
    /// リフレクション由来の失敗として握りつぶしてよい例外かどうか。
    /// <see cref="FieldInfo.GetValue"/>/<see cref="FieldInfo.SetValue"/>が投げうるものに限定し、
    /// それ以外（Graft自身の論理エラー）は握りつぶさずそのまま外へ出す。
    /// </summary>
    private static bool IsReflectionFailure(Exception ex)
        => ex is FieldAccessException or TargetException or ArgumentException
            or InvalidOperationException or NotSupportedException or MemberAccessException;

    /// <summary>
    /// 失敗を「回数の集計」（<see cref="SuppressedExceptionTracker"/>）と「1回だけの詳細ログ」の
    /// 両方へ記録する。<see cref="IndentGuideRenderer"/>と同じ考え方で、集計は毎回・詳細は初回のみ。
    /// 実機で「いつのまにか字下げが効かなくなっていた」ことに終了時のshutdownログから気付ける
    /// ようにするのが目的。
    /// </summary>
    private void RecordFailure(string message, Exception exception)
    {
        SuppressedExceptionTracker.Shared.Record("wrap-indent-install", exception);
        _pendingFailureLog ??= message;
        FlushPendingFailureLog();
    }

    private void FlushPendingFailureLog()
    {
        if (_loggedFailure || _pendingFailureLog is null || _logger is null) return;
        _loggedFailure = true;
        _logger.Warn("wrap-indent-install", _pendingFailureLog);
    }
}
