using Avalonia.Input;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;
using Graft.Core;
using Graft.ViewModels;

namespace Graft.Editor;

/// <summary>
/// コードの折りたたみ（4.4節）。インデントベースを既定とし、C系（<c>{}</c>を持つ言語）は
/// 括弧ベースで折りたたみ範囲を求める。<see cref="AvaloniaEdit.Folding.FoldingManager"/>を
/// <see cref="TextEditor"/>へ直接インストールして動作するため、エディタへの組み込み
/// （<see cref="Attach"/>の呼び出し・<see cref="Dispose"/>のタイミング管理）は統合担当が行う。
/// 18章の性能要件により、再計算は編集のたびではなくデバウンスして行う。
/// v2.0のWPF版（AvalonEdit）からの移植。FoldingManager/NewFoldingのAPIはAvaloniaEditでも
/// 同名同形のため、名前空間の差し替えのみで移植できる。
///
/// 課題3（再設計）: 以前は極端に長い行を含むファイルではこの機能自体をファイル全体で
/// 無効化していた。<see cref="RecalculateNow"/>（<see cref="BraceFoldingStrategy"/>/
/// <see cref="IndentFoldingStrategy"/>）は各行を1回ずつ読んで文字を走査するだけの
/// 線形処理（1文字ごとにレキサを呼び直すような二乗コストの経路が無い）のため、実測では
/// 1行10万文字のファイルで1ms未満、3万行＋1行10万文字が混在するファイルでも最大19ms程度
/// だった（デバウンス300msの予算に対して十分小さい）。このコストなら無効化する理由が
/// 無いため、極端に長い行の有無に関わらず常に利用者の設定（<c>editor.folding</c>）へ
/// そのまま従う（EditorPane.axaml.cs参照。無効化していたのはEditorPane側の判定であり、
/// 本クラス自体に長い行を特別扱いするコードは元々存在しない）。
///
/// 不具合1（実機で確認された未処理例外の修正）: Windows実機で
/// <c>System.ArgumentException: Invalid document at AvaloniaEdit.Folding.
/// FoldingElementGenerator.StartGeneration</c> が未処理のままアプリごと落ちる不具合が
/// 報告された。
///
/// 【真因】 <see cref="AvaloniaEdit.Folding.FoldingManager"/>は<c>Install</c>した瞬間の
/// <c>TextArea.Document</c>に紐づいて生成される。呼び出し側（<see cref="Views.EditorPane"/>）は
/// タブ切替のたび「<c>Editor.Document</c>を新しい文書へ差し替える → <see cref="Attach"/>を
/// 呼んで古い<see cref="FoldingManager"/>をuninstallし新しい文書で作り直す」という2つの
/// 文を続けて実行するが、この2文の間には一瞬「<c>Editor.Document</c>は新しい文書だが、
/// インストール済みの<see cref="FoldingManager"/>（＝<see cref="FoldingElementGenerator"/>）は
/// まだ古い文書に紐づいたまま」という食い違った状態が存在する。この間にAvaloniaEditの
/// レイアウト/描画パス（<c>DispatcherPriority.Render</c>で非同期に走るジョブ。デバウンスタイマー
/// の発火・折り返しの再計算・ウィンドウリサイズ等、UIスレッドの別ジョブとして割り込みうる）が
/// 差し込まれると、<see cref="FoldingElementGenerator.StartGeneration"/>が
/// 「レンダリング対象の文書とFoldingManagerが保持する文書が一致しない」と判定し
/// <see cref="ArgumentException"/>（"Invalid document"）を投げる。この例外は
/// <c>Avalonia.Threading.DispatcherOperation.InvokeCore</c>から素通りで
/// <c>AppDomain.UnhandledException</c>まで抜けるため、このアプリ側のどのtry/catchにも
/// 引っかからずプロセスごと終了する（tests/Graft.UiTests/EditorTests.csの再現テストで
/// 修正前のコードが実際にこの例外で失敗することを確認済み）。
///
/// 【対処】 <see cref="TextEditor.DocumentChanged"/>はAvaloniaのプロパティ変更通知の仕組みにより
/// <c>Editor.Document = 新しい文書</c>という代入そのものの中で同期的に発火する（代入が
/// 呼び出し元へ返ってきた時点で購読側の処理は完了している）。本クラスのコンストラクタで
/// この事件を直接購読し、その場で古い<see cref="FoldingManager"/>を同期的にuninstallする
/// ことで、「<c>Editor.Document</c>は新しいが<see cref="FoldingManager"/>は古いまま」という
/// 食い違った状態が1行たりとも存在しなくなる。呼び出し側（<see cref="Views.EditorPane"/>）の
/// 文の並び順（Document代入→<see cref="Attach"/>呼び出し）に依存しないため、統合担当側の
/// コードを変更する必要が無い（タブ切替・タブを閉じる・空タブ化のいずれの経路も
/// 内部的には<c>Editor.Document</c>の代入を経由するため、この1箇所の対処で全経路をカバーする）。
/// なお再読込（<see cref="Editor.DocumentSession.ReloadAsync"/>）は同一の<see cref="TextDocument"/>
/// インスタンスの<c>Text</c>を書き換えるだけで<c>Editor.Document</c>自体は差し替わらないため、
/// この食い違いは元々発生しない。
///
/// デバウンスタイマー（<see cref="OnDebounceTick"/>）の発火は<see cref="DispatcherTimer.Stop"/>で
/// 大半のケースを防げるが、念のため<see cref="RecalculateNow"/>側でも発火時点の
/// <c>Editor.Document</c>が取り付け対象と一致するかを二重に確認する。加えて、
/// <see cref="FoldingManager"/>への操作（Install/Uninstall/UpdateFoldings）は万一
/// AvaloniaEdit側の内部状態と食い違っても例外を外へ漏らさないよう<c>try/catch</c>で囲み、
/// <see cref="SafeHandler.OnUnexpected"/>へ記録したうえで折りたたみ1回分の更新を諦めるに
/// 留める（アプリ本体は継続させる。附録A.4・設計目標5）。
///
/// 【検討書「折りたたみの機能追加」・「インデントガイド（縦線）」（Pane移植第2波）】
/// 上記の食い違い対策（Install/Uninstallのタイミング管理）には一切手を入れず、その外側に
/// 3つを追加した。
/// (1) <see cref="Manager"/>/<see cref="Document"/>: 現在有効な<see cref="FoldingManager"/>と
///     対象文書を読み取り専用で公開する。<see cref="Editor.IndentGuideRenderer"/>が縦線の
///     元データ（折りたたみ範囲）を取得するために使う。Draw()のたびに
///     <c>textView.Document</c>との一致を呼び出し側で確認させる設計とし（読み取り専用の
///     プロパティを都度読むだけ）、本クラスのInstall/Uninstallのタイミングそのものには
///     一切関与させない。
/// (2) <see cref="HoveredFoldingChanged"/>: 折りたたみマーカーへのマウス乗り入れを、
///     <see cref="FoldingManager.Install"/>が生成する<see cref="FoldingMargin"/>の
///     <c>PointerMoved</c>/<c>PointerExited</c>を購読して検知する（<see cref="FoldingMargin"/>
///     自体はInstall/Uninstallのたびに作り直されるインスタンスのため、フック・アンフックを
///     Attach/DetachDocumentの対になる箇所へ追加した。Install/Uninstallの呼び出し順序・
///     タイミング自体は変更していない）。
/// (3) <see cref="FoldToLevel"/>/<see cref="FoldAllComments"/>/<see cref="FoldRecursiveAt"/>:
///     折りたたみコマンド3種。AvaloniaEditの<see cref="FoldingManager"/>にはレベル指定・
///     コメント一括・再帰的の折りたたみに相当する組み込みコマンドが無いため、公開API
///     （<see cref="FoldingManager.AllFoldings"/>・<see cref="FoldingManager.
///     GetFoldingsContaining"/>・<see cref="FoldingSection.IsFolded"/>）だけで自前実装した。
/// </summary>
public sealed class FoldingSupport : IDisposable
{
    private const int RecalculateDebounceMs = 300;

    // C#/JavaScript・TypeScript等、{}でブロックを表す言語は括弧ベースで折りたたむ。
    // それ以外（Python・HTML・Markdown等）はインデントベースにする（4.4節）。
    private static readonly HashSet<string> BraceBasedLanguageNames = new(StringComparer.Ordinal)
    {
        "C#", "JavaScript/TypeScript", "CSS", "JSON",
    };

    private readonly TextEditor _editor;
    private readonly DispatcherTimer _debounceTimer;
    private FoldingManager? _manager;
    private bool _enabled = true;
    private bool _useBraceStrategy;
    private TextDocument? _document;
    private string? _extension;
    private bool _disposed;

    // 検討書「マーカーのホバー強調」: 現在フックしているFoldingMargin（Install/Uninstallの
    // たびに作り直される）と、現在ホバー中の折りたたみ範囲。
    private FoldingMargin? _hookedMargin;
    private FoldingSection? _hoveredFolding;

    public FoldingSupport(TextEditor editor)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(RecalculateDebounceMs) };
        _debounceTimer.Tick += OnDebounceTick;

        // 不具合1: Editor.Documentが差し替わった瞬間に古いFoldingManagerを同期的に
        // uninstallする（クラスコメントの「対処」参照）。
        _editor.DocumentChanged += OnEditorDocumentChanged;
    }

    /// <summary>現在有効な<see cref="FoldingManager"/>（未取り付け・無効化中はnull）。
    /// <see cref="Editor.IndentGuideRenderer"/>が読み取り専用で参照する。</summary>
    public FoldingManager? Manager => _manager;

    /// <summary>現在取り付け対象の文書（取り付け前はnull）。</summary>
    public TextDocument? Document => _document;

    /// <summary>
    /// マウスが乗っている折りたたみマーカーに対応する範囲が変わるたびに発火する
    /// （検討書「マーカーのホバー強調」）。マーカーの外へ出た・畳まれている範囲の場合はnull。
    /// </summary>
    public event EventHandler<FoldingSection?>? HoveredFoldingChanged;

    /// <summary>
    /// 不具合1: <see cref="TextEditor.Document"/>が変わった瞬間に同期的に発火する。
    /// 呼び出し側が<see cref="Attach"/>を呼び直すより前に、古い文書に紐づいた
    /// <see cref="FoldingManager"/>をここで確実にuninstallしておく。
    /// </summary>
    private void OnEditorDocumentChanged(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(_document, _editor.Document)) DetachDocument();
    }

    /// <summary>15章 <c>editor.folding</c> 設定の反映。無効化するとインストール済みの
    /// <see cref="FoldingManager"/>を解除する。</summary>
    public void SetEnabled(bool enabled)
    {
        if (_enabled == enabled) return;
        _enabled = enabled;
        if (!enabled) Uninstall();
        else if (_document is not null) Attach(_document, _useBraceStrategy);
    }

    /// <summary>対象ドキュメントと言語（拡張子）を切り替える（タブ切替のたび呼ぶ）。</summary>
    public void Attach(TextDocument document, string extension)
    {
        // 「すべてのコメントブロックを折りたたむ」（FoldAllComments）が対象言語の判定に使う。
        _extension = extension;
        var rule = SyntaxLexer.RuleForExtension(extension);
        Attach(document, rule is not null && BraceBasedLanguageNames.Contains(rule.Name));
    }

    private void Attach(TextDocument document, bool useBraceStrategy)
    {
        ArgumentNullException.ThrowIfNull(document);
        DetachDocument();

        _document = document;
        _useBraceStrategy = useBraceStrategy;
        if (!_enabled) return;

        // 不具合1: FoldingManager.Installはこの瞬間のEditor.Documentに紐づいて作られる。
        // 呼び出し側は本来Editor.Document==documentの状態でAttachを呼ぶ契約だが、万一
        // 食い違っていた場合にInvalid documentの温床となる不整合なFoldingManagerを
        // 作らないよう、ここで確認してから取り付ける。
        if (!ReferenceEquals(_editor.Document, document))
        {
            SafeHandler.OnUnexpected?.Invoke(
                "折りたたみの取り付け",
                new InvalidOperationException(
                    "FoldingSupport.Attachに渡された文書がEditor.Documentと一致しません。"));
            return;
        }

        try
        {
            _manager = FoldingManager.Install(_editor.TextArea);
        }
        catch (Exception ex)
        {
            // 万一AvaloniaEdit側の内部状態と食い違っても、折りたたみを諦めるだけに留め
            // アプリは継続させる（附録A.4・設計目標5）。
            _manager = null;
            SafeHandler.OnUnexpected?.Invoke("折りたたみの取り付け", ex);
            return;
        }

        HookFoldingMargin();
        document.Changed += OnDocumentChanged;
        RecalculateNow();
    }

    /// <summary>
    /// 検討書「マーカーのホバー強調」: <see cref="FoldingManager.Install"/>が
    /// <c>TextArea.LeftMargins</c>へ追加した<see cref="FoldingMargin"/>（Install呼び出しのたびに
    /// 新しいインスタンスが作られる）を見つけ、ポインタの出入りを購読する。見つからない場合
    /// （AvaloniaEdit側の内部実装が変わった等）は静かに諦める（ホバー強調が効かないだけで、
    /// 折りたたみ自体の動作には影響させない）。
    /// </summary>
    private void HookFoldingMargin()
    {
        var margin = _editor.TextArea.LeftMargins.OfType<FoldingMargin>().FirstOrDefault();
        if (margin is null) return;

        _hookedMargin = margin;
        margin.PointerMoved += OnFoldingMarginPointerMoved;
        margin.PointerExited += OnFoldingMarginPointerExited;
    }

    private void UnhookFoldingMargin()
    {
        if (_hookedMargin is null) return;
        _hookedMargin.PointerMoved -= OnFoldingMarginPointerMoved;
        _hookedMargin.PointerExited -= OnFoldingMarginPointerExited;
        _hookedMargin = null;
        SetHoveredFolding(null);
    }

    /// <summary>
    /// マージン上のポインタ位置から、その行のマーカーが開く折りたたみ範囲を求める。
    /// <see cref="AvaloniaEdit.Folding.FoldingMargin.OnTextViewVisualLinesChanged"/>が
    /// マーカーの表示要否を判定するのと同じ条件（<c>GetNextFolding</c> + 行内に収まっているか）
    /// を使うため、実際にマーカーが描かれている行でだけ強調が発生する。
    /// </summary>
    private void OnFoldingMarginPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_manager is null || _hookedMargin is null) { SetHoveredFolding(null); return; }

        var textView = _editor.TextArea.TextView;
        var position = e.GetPosition(_hookedMargin);
        var docLine = textView.GetDocumentLineByVisualTop(position.Y + textView.VerticalOffset);
        if (docLine is null) { SetHoveredFolding(null); return; }

        var fs = _manager.GetNextFolding(docLine.Offset);
        var onThisLine = fs is not null && fs.StartOffset <= docLine.Offset + docLine.Length && !fs.IsFolded;
        SetHoveredFolding(onThisLine ? fs : null);
    }

    private void OnFoldingMarginPointerExited(object? sender, PointerEventArgs e) => SetHoveredFolding(null);

    private void SetHoveredFolding(FoldingSection? folding)
    {
        if (ReferenceEquals(_hoveredFolding, folding)) return;
        _hoveredFolding = folding;
        HoveredFoldingChanged?.Invoke(this, folding);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _editor.DocumentChanged -= OnEditorDocumentChanged;
        _debounceTimer.Stop();
        DetachDocument();
    }

    private void DetachDocument()
    {
        if (_document is not null) _document.Changed -= OnDocumentChanged;
        _debounceTimer.Stop();
        Uninstall();
        _document = null;
    }

    private void Uninstall()
    {
        UnhookFoldingMargin(); // Uninstallでこのマージン自体がLeftMarginsから取り除かれるため先に外す。
        if (_manager is null) return;
        var manager = _manager;
        _manager = null;
        try
        {
            FoldingManager.Uninstall(manager);
        }
        catch (Exception ex)
        {
            SafeHandler.OnUnexpected?.Invoke("折りたたみの解除", ex);
        }
    }

    private void OnDocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private void OnDebounceTick(object? sender, EventArgs e)
    {
        _debounceTimer.Stop();
        RecalculateNow();
    }

    private void RecalculateNow()
    {
        if (_manager is null || _document is null) return;

        // 不具合1: デバウンスタイマーの発火は最大300ms遅延するため、発火時点で
        // Editor.Documentが取り付け対象からすでに差し替わっていないかを必ず確認する
        // （Stop()で大半は防げるが、クラスコメントのとおり念のための二重の防御）。
        if (!ReferenceEquals(_document, _editor.Document)) return;

        try
        {
            var foldings = _useBraceStrategy
                ? BraceFoldingStrategy.ComputeFoldings(_document)
                : IndentFoldingStrategy.ComputeFoldings(_document);
            _manager.UpdateFoldings(foldings, -1);
        }
        catch (Exception ex)
        {
            SafeHandler.OnUnexpected?.Invoke("折りたたみの再計算", ex);
        }
    }

    // ========================================================================
    // 検討書「折りたたみの機能追加」(b) 折りたたみコマンドの追加。
    // AvaloniaEditのFoldingManagerには相当する組み込みコマンドが無いため、公開API
    // （AllFoldings・GetFoldingsContaining・FoldingSection.IsFolded）だけで自前実装する。
    // ========================================================================

    /// <summary>
    /// レベル<paramref name="level"/>（1〜5、最も外側が1）の範囲だけを折りたたみ、
    /// それ以外はすべて展開する（VS Codeの「フォールドレベルN」と同じ挙動）。
    /// 深さの算出は<see cref="FoldingLevelCalculator"/>（純粋ロジック、tests/Graft.Tests参照）に
    /// 委譲する。
    /// </summary>
    public void FoldToLevel(int level)
    {
        if (_manager is null) return;

        try
        {
            // AllFoldingsはStartOffset昇順で返る（FoldingManagerのドキュメントコメントどおり）
            // ため、そのままFoldingLevelCalculatorの前提（開始オフセット昇順）を満たす。
            var all = _manager.AllFoldings.ToList();
            var ranges = all.Select(fs => (fs.StartOffset, fs.EndOffset)).ToList();
            var levels = FoldingLevelCalculator.ComputeLevels(ranges);
            for (var i = 0; i < all.Count; i++)
            {
                all[i].IsFolded = levels[i] == level;
            }
        }
        catch (Exception ex)
        {
            SafeHandler.OnUnexpected?.Invoke("折りたたみレベルの変更", ex);
        }
    }

    /// <summary>
    /// カーソル位置<paramref name="offset"/>を含む折りたたみ範囲のうち最も内側（カーソルに
    /// 最も近い）のものを起点に、それ自身とその内側にあるすべての範囲を折りたたむ
    /// （VS Codeの「折りたたみ（再帰的）」相当）。該当する範囲が無ければ何もしない。
    /// </summary>
    public void FoldRecursiveAt(int offset)
    {
        if (_manager is null) return;

        try
        {
            var containing = _manager.GetFoldingsContaining(offset);
            if (containing.Count == 0) return;

            var target = containing[0];
            foreach (var fs in containing)
            {
                if (fs.StartOffset > target.StartOffset) target = fs; // より内側（開始が後ろ）を採用。
            }

            foreach (var fs in _manager.AllFoldings)
            {
                if (fs.StartOffset >= target.StartOffset && fs.EndOffset <= target.EndOffset)
                {
                    fs.IsFolded = true;
                }
            }
        }
        catch (Exception ex)
        {
            SafeHandler.OnUnexpected?.Invoke("再帰的な折りたたみ", ex);
        }
    }

    /// <summary>
    /// ドキュメント内の「コメント専用行」が2行以上連続する区間（複数行コメント・連続する
    /// 単一行コメントのどちらも該当）をすべて折りたたむ。区間の探索は
    /// <see cref="CommentBlockCalculator"/>（純粋ロジック）に委譲し、ここでは対象言語の判定
    /// （<see cref="SyntaxLexer"/>でのトークン化）と実際のFoldingSection作成のみを行う。
    ///
    /// BraceFoldingStrategy/IndentFoldingStrategyが生成する通常の折りたたみ範囲とは独立した
    /// 一時的な範囲として作成するため、次の編集後の再計算（<see cref="RecalculateNow"/>→
    /// <c>UpdateFoldings</c>）でBrace/IndentFoldingStrategyの出力に無ければ消える
    /// （「今すぐ全部畳む」という1回限りのコマンドとして割り切り、常時追跡はしない）。
    /// </summary>
    public void FoldAllComments()
    {
        if (_manager is null || _document is null || _extension is null) return;

        var rule = SyntaxLexer.RuleForExtension(_extension);
        if (rule is null) return; // 対応言語が無ければコメントかどうかの判定自体ができない。

        try
        {
            var lines = TextNormalizer.SplitLines(_document.Text);
            var lexer = new SyntaxLexer(rule);
            if (!lexer.Scan(lines)) return; // 性能上限超過等でスキャンできなければ諦める。

            var isCommentOnly = new bool[lines.Count];
            for (var i = 0; i < lines.Count; i++)
            {
                isCommentOnly[i] = IsCommentOnlyLine(lines[i], lexer.TokenizeLine(i, lines[i]));
            }

            foreach (var (startLine, endLine) in CommentBlockCalculator.FindCommentBlocks(isCommentOnly))
            {
                var start = _document.GetLineByNumber(startLine);
                var end = _document.GetLineByNumber(endLine);
                var startOffset = start.Offset;
                var endOffset = end.Offset + end.Length;
                if (startOffset >= endOffset) continue;

                // 同じコマンドを繰り返し実行しても、同一範囲を重複して作らない。
                if (_manager.GetFoldingsAt(startOffset).Any(fs => fs.EndOffset == endOffset)) continue;

                _manager.CreateFolding(startOffset, endOffset).IsFolded = true;
            }
        }
        catch (Exception ex)
        {
            SafeHandler.OnUnexpected?.Invoke("コメントブロックの折りたたみ", ex);
        }
    }

    /// <summary>
    /// 行のトークン列が「コメントだけ（空白を除く）」かどうかを判定する。コメント以外の
    /// 実トークン（キーワード・文字列・識別子等）が1つでもあれば対象外。Plainトークンは
    /// 空白のみであれば許容する（例: "    // foo"の行頭空白）。
    /// </summary>
    private static bool IsCommentOnlyLine(string lineText, IReadOnlyList<SyntaxToken> tokens)
    {
        var hasComment = false;
        foreach (var token in tokens)
        {
            if (token.Kind == TokenKind.Comment) { hasComment = true; continue; }
            if (token.Kind != TokenKind.Plain) return false;

            var end = Math.Min(token.Start + token.Length, lineText.Length);
            if (end > token.Start && !lineText.AsSpan(token.Start, end - token.Start).IsWhiteSpace()) return false;
        }
        return hasComment;
    }
}

/// <summary>C系言語向けの括弧ベース折りたたみ。<c>{</c> <c>}</c> の対応のみを深さで数える
/// 簡易実装で、文字列・コメント内の括弧は区別しない（性能を優先した簡略化）。</summary>
internal static class BraceFoldingStrategy
{
    public static IEnumerable<NewFolding> ComputeFoldings(TextDocument document)
    {
        var foldings = new List<NewFolding>();
        var starts = new Stack<int>();

        foreach (var line in document.Lines)
        {
            var text = document.GetText(line.Offset, line.Length);
            for (var col = 0; col < text.Length; col++)
            {
                if (text[col] == '{')
                {
                    starts.Push(line.Offset + col);
                }
                else if (text[col] == '}' && starts.Count > 0)
                {
                    var startOffset = starts.Pop();
                    var endOffset = line.Offset + col + 1;
                    if (document.GetLineByOffset(startOffset).LineNumber != line.LineNumber)
                    {
                        foldings.Add(new NewFolding(startOffset, endOffset));
                    }
                }
            }
        }

        foldings.Sort((a, b) => a.StartOffset.CompareTo(b.StartOffset));
        return foldings;
    }
}

/// <summary>
/// インデントベースの折りたたみ（既定）。行頭の空白幅が自分より深い行が連続する間を
/// 1つの折りたたみ範囲とする（Pythonのブロック構造等を想定）。空行は開始・終了の判定に使わない。
/// </summary>
internal static class IndentFoldingStrategy
{
    public static IEnumerable<NewFolding> ComputeFoldings(TextDocument document)
    {
        var foldings = new List<NewFolding>();
        var stack = new Stack<(int Indent, DocumentLine Line)>();
        DocumentLine? lastNonBlank = null;

        foreach (var line in document.Lines)
        {
            var text = document.GetText(line.Offset, line.Length);
            if (string.IsNullOrWhiteSpace(text)) continue;

            var indent = LeadingWhitespaceLength(text);
            while (stack.Count > 0 && stack.Peek().Indent >= indent)
            {
                var entry = stack.Pop();
                if (lastNonBlank is { } last) ClosePendingFold(foldings, entry, last);
            }
            stack.Push((indent, line));
            lastNonBlank = line;
        }

        while (stack.Count > 0)
        {
            var entry = stack.Pop();
            if (lastNonBlank is { } last) ClosePendingFold(foldings, entry, last);
        }

        foldings.Sort((a, b) => a.StartOffset.CompareTo(b.StartOffset));
        return foldings;
    }

    private static void ClosePendingFold(
        List<NewFolding> foldings, (int Indent, DocumentLine StartLine) entry, DocumentLine lastChildLine)
    {
        if (lastChildLine.LineNumber <= entry.StartLine.LineNumber) return; // 子を持たない行は折りたためない
        var endOffset = lastChildLine.Offset + lastChildLine.Length;
        foldings.Add(new NewFolding(entry.StartLine.Offset + entry.StartLine.Length, endOffset));
    }

    private static int LeadingWhitespaceLength(string text)
    {
        var i = 0;
        while (i < text.Length && (text[i] == ' ' || text[i] == '\t')) i++;
        return i;
    }
}
