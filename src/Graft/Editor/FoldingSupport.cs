using Avalonia.Input;
using Avalonia.Interactivity;
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
///
/// 【課題#82: 上の「対処」だけでは閉じ切れていなかった食い違いの窓】
/// 不具合1の再発（実機ログに<c>Invalid document</c>が3回、うち1回は
/// <c>A Task's exception(s) were not observed</c>としてファイナライザスレッドから記録）を受けて
/// 再調査した結果、上の「対処」（<see cref="TextEditor.DocumentChanged"/>を購読して同期的に
/// uninstallする）には見落としがあったと判明した。<see cref="TextEditor.DocumentChanged"/>は
/// 「一番最後に」発火するイベントであり、それより手前で<b>TextView.Documentは既に新しい文書へ
/// 切り替わっている</b>。AvaloniaEdit 11.1.0のソース（ILSpy逆コンパイルおよび
/// https://github.com/AvaloniaUI/AvaloniaEdit の11.1.0タグで実際に確認済み）を辿ると:
/// <code>
/// TextEditor.OnDocumentChanged(old, new)
///   → TextArea.Document = new;              // ← ①ここでTextArea.OnDocumentChangedへ
///        TextArea.OnDocumentChanged(old, new)
///          → TextView.Document = new;        // ← ②ここでTextView.Documentが既に新しい文書になる
///               （TextView.InvalidateMeasure()も呼ばれ、Renderジョブがこの時点でキューに積まれる）
///          → Caret.Location = new TextLocation(1, 1);  // ← ③キャレット位置のリセット
///               → Caret.RaisePositionChanged();          // TextViewPositionが変わっていれば同期発火
///                    → TextArea.CaretPositionChanged（TextArea購読）
///                         → ScrollToLine → BringIntoView（ルーテッドイベント。実機ではIME通知・
///                           フォーカス変更等に伴うWin32メッセージポンプの入れ子呼び出しが
///                           ここに割り込みうる）
///          → TextArea.DocumentChanged?.Invoke(...)  // TextAreaのDocumentChanged（本クラスは未購読）
///   → TextEditor.DocumentChanged?.Invoke(...);      // ← ④本クラスのOnEditorDocumentChangedはここで初めて発火
/// </code>
/// つまり①〜④の間（TextView.Documentは新しいが、本クラスがまだ気付いていない区間）が
/// 実在し、③のCaret位置リセット（すべての文書差し替えで必ず起きる。タブ切替時は元のタブでの
/// カーソル行が(1,1)でない限り必ずPositionChangedが同期発火する）の最中に、②で既にキューへ
/// 積まれた保留中のRenderジョブ（<c>MediaContext.BeginInvokeOnRender</c>経由）を処理してしまう
/// ような再入（Windows実機でのIME・フォーカス変更・アクセシビリティ通知等に伴うネイティブ
/// メッセージポンプの入れ子呼び出しが典型例）が起きると、「TextView.Documentは新しい文書、
/// FoldingManagerは古い文書のまま」という食い違った状態でレイアウトパスが走り、
/// <see cref="FoldingElementGenerator.StartGeneration"/>が<c>Invalid document</c>を投げる。
/// <para>
/// この機序はheadlessテストでも実際に再現できる（tests/Graft.UiTests/EditorTests.csの
/// 「文書代入中に再入したディスパッチャジョブがInvalid documentを引き起こす」参照）。
/// テストでは<see cref="AvaloniaEdit.Editing.Caret.PositionChanged"/>のハンドラの中で
/// <c>Dispatcher.UIThread.RunJobs()</c>を呼び、②で積まれたRenderジョブを即座に処理させることで
/// 実機のネイティブメッセージポンプ再入と同じ効果をプラットフォーム非依存に模擬している。
/// 修正前のコード（本クラスのコンストラクタで<see cref="TextEditor.DocumentChanged"/>のみを
/// 購読する版）では実際にこのテストが<c>Invalid document</c>で失敗することを確認済み。
/// </para>
/// <para>
/// 【対処2】 呼び出し側（<see cref="Views.EditorPane"/>）が<c>Editor.Document</c>へ代入する
/// "前"に、<see cref="PrepareForDocumentSwap"/>を呼んで古い<see cref="FoldingManager"/>を
/// 先回りしてuninstallする。これにより①より前の時点で本クラスの状態は「未取り付け」になり、
/// ②〜④のどの瞬間に再入が起きても、<see cref="FoldingElementGenerator"/>の
/// <c>FoldingManager</c>フィールドは既にnullで<see cref="FoldingElementGenerator.StartGeneration"/>
/// が即return（例外を投げるコードパス自体を通らない）。<see cref="TextEditor.DocumentChanged"/>
/// を購読するコンストラクタの対処はそのまま残す（<see cref="PrepareForDocumentSwap"/>の
/// 呼び出しを呼び出し側が万一忘れた場合でも、①〜④の外側では引き続き機能する二重の防御）。
/// </para>
///
/// 【実機での指摘（L字線を消す）】 折りたたみマージンの＋/－マーカーから下へ伸びる縦線と
/// 終端の横線（合わせてL字に見える線）が不要との指摘があった。<c>FoldingManager.Install</c>が
/// 作る標準の<see cref="FoldingMargin"/>はこの線とマーカー枠の両方を同じブラシで描くため
/// ブラシを透明にする方法は採れず（マーカーごと消えてしまう）、代わりに<see cref="Attach"/>の中で
/// <see cref="ReplaceFoldingMarginWithMarkerOnly"/>を呼び、線を描かない
/// <see cref="MarkerOnlyFoldingMargin"/>へその場で（同じ<c>LeftMargins</c>のインデックスのまま）
/// 差し替えている。差し替え後は<c>FoldingManager.Uninstall</c>がこのインスタンスを取り除いて
/// くれない（標準の<see cref="FoldingMargin"/>インスタンスをRemoveしようとするだけのため）ので、
/// <see cref="Uninstall"/>で必ず<see cref="RemoveCustomMargin"/>を呼び自分で取り除く。
/// 詳細な理由は<see cref="ReplaceFoldingMarginWithMarkerOnly"/>と<see cref="MarkerOnlyFoldingMargin"/>の
/// クラスコメントを参照。
///
/// 【課題#73: スクロールバーのドラッグがマウスに追い付かない — 折りたたみが最大の原因だった】
/// 10万行のファイルでつまみを1px動かすと、文書は2,244px（≒128行）進み、可視46行がすべて
/// 作り直される。このレイアウト1回分の実測は、素のAvaloniaEditが7.1msなのに対しGraftは20.2ms
/// （2.77倍）で、上乗せ+12.6msのうち<b>+6.90msが折りたたみ</b>（残りは構文強調+3.41ms。
/// <see cref="SyntaxHighlightBridge"/>参照）だった。HeightTree探索（0.004ms）・Arrange（0.07ms）・
/// Gitガター・括弧強調・カラープレビュー・折り返しインデント（課題#72、倍率1.02）はいずれも無罪。
/// <para>
/// 【真因】 <see cref="FoldingElementGenerator.GetFirstInterestedOffset"/>は可視行を1行作る
/// たびに<see cref="FoldingManager.GetNextFoldedFoldingStart"/>を呼ぶ。その中身
/// （AvaloniaEdit 11.1.0 Folding/FoldingManager.cs）は
/// <code>
/// var fs = _foldings.FindFirstSegmentWithStartAfter(startOffset);
/// while (fs != null &amp;&amp; !fs.IsFolded)   // 畳まれた範囲が1つも無ければ末尾まで走査しきる
///     fs = _foldings.GetNextSegment(fs);
/// </code>
/// であり、<b>「どこも畳んでいない」という一番普通の状態が最悪ケース</b>になっている
/// （10万行の.csでは折りたたみ範囲が20,000個でき、それを1行ごとに末尾まで舐める）。
/// ドラッグ位置で費用が変わる（＝残りの範囲数に比例する）ことが実測でも裏付けられた:
/// 文書の2%地点で21.2ms / 25% 17.2 / 50% 13.6 / 75% 11.4 / 98% 8.6ms（装飾＝折りたたみのみ）。
/// 素のAvaloniaEditは同じ位置で7.04 / 6.93 / 6.79 / 7.38 / 7.01と完全にフラットだった。
/// </para>
/// <para>
/// 【対処】 <b>1つも畳まれていない間は<see cref="FoldingElementGenerator"/>を
/// <c>TextView.ElementGenerators</c>から外し、1つでも畳まれたら戻す</b>
/// （<see cref="SyncFoldingGenerator"/>）。等価である根拠は、何も畳まれていないとき
/// <see cref="FoldingManager.GetNextFoldedFoldingStart"/>は必ず-1を返し、
/// <see cref="FoldingElementGenerator.ConstructElement"/>も呼ばれない＝生成器は何も生成しない、
/// という点にある。実際に2,000行のファイルを500行目まで送った状態で描画結果のPNGを比較し、
/// 付けた場合と外した場合で<b>1バイトも違わない</b>ことを確認済み（調査時の実測で
/// 188,870バイトどうしが完全一致。tests/Graft.UiTests/FoldingGeneratorDetachTests.csの
/// 「何も畳んでいない状態の描画結果は、折りたたみ生成器の有無で1バイトも変わらない」で
/// 自動テストとしても固定した）。
/// <b>効果の実測</b>（本対処の前後を同一手順・同一環境で交互25組計測。数値は中央値）:
/// <list type="bullet">
/// <item>折りたたみの上乗せ（ドラッグ1ステップ、文書50%地点）: <b>+5.06ms → −0.03ms</b></item>
/// <item>位置依存（同、折りたたみのみ）: 2%地点 +10.84ms → +0.09ms / 25% +8.45 → −0.09 /
///       50% +5.30 → +0.08 / 75% +1.27 → −0.46（<b>位置依存そのものが消滅</b>）</item>
/// <item>Graftの装飾すべてでのレイアウト費用: 16.99ms → <b>10.83ms</b>
///       （素のAvaloniaEdit 8.3ms比で 2.12倍 → <b>1.28倍</b>）</item>
/// <item>Skia描画まで含めたフレーム全体: 基準比 +13.41ms → <b>+2.68ms</b>（1.71倍 → 1.18倍）</item>
/// </list>
/// （調査時は別条件での計測のため絶対値が異なる: 素7.1ms / Graft 20.2ms、折りたたみの上乗せ
/// +6.90ms。どちらの計測でも「折りたたみの上乗せがほぼ消える」という結論は同じ。）
/// </para>
/// <para>
/// 【付け外しの同期をどこで行うか】 <see cref="Avalonia.Threading.Dispatcher"/>経由の遅延同期は
/// 採らない（tests/Graft.Tests/DispatcherUIThreadUsageGuardTests.csの許可リストを増やすことに
/// なるうえ、レイアウトとの前後関係が保証できない）。畳み状態が変わりうる経路をすべて洗い出し、
/// その場で同期的に付け外しする:
/// (1) 折りたたみコマンド3種（<see cref="FoldToLevel"/>・<see cref="FoldAllComments"/>・
///     <see cref="FoldRecursiveAt"/>。キーボードとコマンドパレットはどちらもこの3つを通る）、
/// (2) ＋/－マーカーのクリック（<see cref="HookFoldingMargin"/>でマージンのPointerPressedを
///     トンネル・バブルの両方で購読）、
/// (3) 本文側の展開（畳まれた"..."のクリック・キャレット移動に伴うAvaloniaEditの自動展開。
///     コンストラクタでTextViewのPointerReleasedを購読）、
/// (4) デバウンス後の再計算（<see cref="RecalculateNow"/>。UpdateFoldingsで畳まれた範囲が
///     消えることがある）。
/// (1)(2)は「畳む」方向を含むため取りこぼすと表示が崩れる（＝必ず同期的に拾う必要がある）。
/// (3)は「展開」方向のみで、取りこぼしても生成器が付いたまま＝修正前と同じ費用に戻るだけの
/// 安全側の失敗になる。キーボード操作でのキャレット移動による自動展開だけは購読していないが、
/// これも(3)と同じ安全側で、次の編集の300ms後（(4)）に必ず回収される。20,000件の走査を
/// キー入力のたびに行う方が害が大きいと判断した（1回あたり約0.15ms）。
/// </para>
/// <para>
/// 【副次的な安全性】 生成器が外れている間は、課題#82で問題になった
/// <see cref="FoldingElementGenerator.StartGeneration"/>（AvaloniaEdit全ソース中で
/// <c>ArgumentException("Invalid document")</c>を投げる唯一の箇所）自体が呼ばれない。
/// つまり「何も畳んでいない通常状態」では、課題#82の経路そのものが塞がる
/// （tests/Graft.UiTests/EditorTests.csの「何も畳んでいない通常状態では課題#82の経路自体を
/// 通らない」で検証）。ただしこれは<see cref="PrepareForDocumentSwap"/>の代わりにはならない
/// （1つでも畳んでいれば生成器は付いており、従来どおりの窓が開く）ため、対処2はそのまま残す。
/// </para>
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

    // 実機での指摘（L字線を消す）: FoldingManager.Installが作った標準のFoldingMarginと
    // 差し替えた自前のMarkerOnlyFoldingMargin。Uninstall時にLeftMarginsから自分で
    // 取り除く必要がある（ReplaceFoldingMarginWithMarkerOnlyのコメント参照）。
    private MarkerOnlyFoldingMargin? _customMargin;

    // 課題#73: FoldingManager.Installが作った折りたたみ生成器と、それが現在
    // TextView.ElementGeneratorsへ入っているかどうか（クラスコメントの【課題#73】節参照）。
    // 「1つも畳まれていない」間は外しておき、1つでも畳まれたら戻す。
    private FoldingElementGenerator? _generator;
    private bool _generatorAttached;

    public FoldingSupport(TextEditor editor)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(RecalculateDebounceMs) };
        _debounceTimer.Tick += OnDebounceTick;

        // 不具合1: Editor.Documentが差し替わった瞬間に古いFoldingManagerを同期的に
        // uninstallする（クラスコメントの「対処」参照）。
        _editor.DocumentChanged += OnEditorDocumentChanged;

        // 課題#73: 本文側で畳まれた範囲（"..."の箱）をクリックすると、AvaloniaEditの
        // FoldingElementGenerator.FoldingLineElement.OnPointerPressedがIsFolded=falseにする。
        // またクリックによるキャレット移動でも、畳まれた範囲へ入った場合はAvaloniaEdit側
        // （FoldingManagerInstallation.TextArea_Caret_PositionChanged）が自動で展開する。
        // どちらも「展開」方向のみのため取りこぼしても表示は壊れない（生成器が付いたまま＝
        // 修正前と同じ費用に戻るだけ）が、最も普通の操作なのでここで確実に拾って外す。
        // TextViewはTextEditorに対して不変のインスタンスなので、Install/Uninstallとは無関係に
        // コンストラクタで1回だけ購読する（マージン側はInstallのたびに作り直されるため
        // HookFoldingMargin/UnhookFoldingMarginで対にしている）。
        _editor.TextArea.TextView.AddHandler(
            InputElement.PointerReleasedEvent, OnTextViewPointerReleased,
            RoutingStrategies.Bubble, handledEventsToo: true);
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

    /// <summary>
    /// 課題#82: 呼び出し側（<see cref="Views.EditorPane"/>）が<c>Editor.Document</c>へ代入する
    /// "直前"に必ず呼ぶ。クラスコメントの【課題#82】節のとおり、<see cref="TextEditor.
    /// DocumentChanged"/>（本クラスがコンストラクタで購読している同期イベント）は
    /// <c>Editor.Document</c>代入の中で一番最後に発火するため、それより前段（<c>TextView.
    /// Document</c>の切り替え・Caretのリセット）で何らかの理由により描画パスが再入すると、
    /// 古い<see cref="FoldingManager"/>がまだ生きたままレイアウトパスが走ってしまう。
    /// 本メソッドを代入の前に呼んでおけば、その時点で本クラスは「未取り付け」（<see
    /// cref="Manager"/>がnull）になるため、代入中のどの瞬間に再入が起きても
    /// <see cref="FoldingElementGenerator.StartGeneration"/>は<c>_foldingManager == null</c>で
    /// 即returnし、<c>Invalid document</c>を投げる余地そのものが無くなる。
    /// 呼び忘れても<see cref="OnEditorDocumentChanged"/>が引き続き安全網として働くが、
    /// それは【課題#82】節の①〜④区間の外でのみ有効なため、必ず呼ぶこと。
    /// </summary>
    public void PrepareForDocumentSwap() => DetachDocument();

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

        // 課題#73: Installが作った折りたたみ生成器（TextView.ElementGeneratorsの先頭へ
        // 挿入される）をここで捕まえておく。以降、畳み状態に応じて付け外しする対象になる。
        // FoldingElementGeneratorはpublicな型で、Installが入れる場所も先頭（index 0）と
        // 決まっているが、将来AvaloniaEdit側で挿入位置が変わっても壊れないよう、型で探す。
        _generator = _editor.TextArea.TextView.ElementGenerators.OfType<FoldingElementGenerator>().FirstOrDefault();
        _generatorAttached = _generator is not null;

        ReplaceFoldingMarginWithMarkerOnly();
        HookFoldingMargin();
        document.Changed += OnDocumentChanged;
        RecalculateNow();

        // 課題#73: 取り付け直後は（Brace/IndentFoldingStrategyのどちらも DefaultClosed を
        // 立てないため）必ず「1つも畳まれていない」状態になる。つまりファイルを開いた直後の
        // 一番普通の状態で生成器が外れ、ドラッグの費用が素のAvaloniaEdit並みに戻る。
        SyncFoldingGenerator();
    }

    /// <summary>
    /// 実機での指摘（Windows）: 折りたたみマージンのL字線（マーカーから下へ伸びる縦線と
    /// 終端の横線）が不要とのことなので、<see cref="FoldingManager.Install"/>が
    /// <c>TextArea.LeftMargins</c>へ追加した標準の<see cref="FoldingMargin"/>を、同じ位置
    /// （インデックス）で<see cref="MarkerOnlyFoldingMargin"/>（L字線を描かない派生クラス。
    /// 詳細はそのクラスコメント参照）へ差し替える。末尾へ追加し直すのではなく同じ位置で
    /// 差し替えるのは、Graftは行番号マージンやGitガター（<see cref="GitGutterProvider"/>）も
    /// <c>LeftMargins</c>に持っており、並び順が変わってしまうと困るため。
    ///
    /// <c>ObservableCollection&lt;Control&gt;</c>のインデクサで置き換えるだけで、
    /// <c>TextArea.LeftMargins</c>の<c>CollectionChanged</c>ハンドラ（内部で
    /// <c>ITextViewConnect.AddToTextView</c>/<c>RemoveFromTextView</c>を呼ぶ）がReplaceとして
    /// 検知して自動的に配線し直すため、<c>TextView</c>への接続・切断をここで自前で行う必要は無い。
    ///
    /// 【解除漏れへの注意】 <see cref="FoldingManager.Install"/>が生成した標準の
    /// <see cref="FoldingMargin"/>インスタンスへの参照は、AvaloniaEdit内部の
    /// <c>FoldingManagerInstallation</c>のprivateフィールドにのみ保持されている。
    /// <see cref="FoldingManager.Uninstall"/>はそのインスタンスを<c>LeftMargins.Remove</c>
    /// しようとするだけなので、ここで差し替えた後は対象インスタンスが<c>LeftMargins</c>に
    /// 存在せず<c>Remove</c>は空振りする。つまり<see cref="FoldingManager.Uninstall"/>は
    /// 自前のマージンを取り除いてくれない。取り除いた自前のマージンは<c>_customMargin</c>
    /// に保持しておき、<see cref="Uninstall"/>側で必ず自分で<c>LeftMargins</c>から取り除く
    /// （そうしないと折りたたみの有効化・無効化やタブ切替を繰り返すたびに増殖する）。
    /// </summary>
    private void ReplaceFoldingMarginWithMarkerOnly()
    {
        var margins = _editor.TextArea.LeftMargins;
        var index = -1;
        for (var i = 0; i < margins.Count; i++)
        {
            // MarkerOnlyFoldingMarginもFoldingMarginを継承しているため、既に差し替え済みの
            // インスタンスを誤って対象にしないよう明示的に除外する。
            if (margins[i] is FoldingMargin and not MarkerOnlyFoldingMargin) { index = i; break; }
        }

        // 見つからない場合（AvaloniaEdit側の内部実装が変わった等）は静かに諦める。
        // L字線が残るだけで、折りたたみ自体の動作には影響させない。
        if (index < 0) return;

        var custom = new MarkerOnlyFoldingMargin { FoldingManager = _manager };
        margins[index] = custom;
        _customMargin = custom;
    }

    /// <summary>
    /// 検討書「マーカーのホバー強調」: <see cref="FoldingManager.Install"/>が
    /// <c>TextArea.LeftMargins</c>へ追加した<see cref="FoldingMargin"/>（Install呼び出しのたびに
    /// 新しいインスタンスが作られる）を見つけ、ポインタの出入りを購読する。見つからない場合
    /// （AvaloniaEdit側の内部実装が変わった等）は静かに諦める（ホバー強調が効かないだけで、
    /// 折りたたみ自体の動作には影響させない）。
    /// <see cref="MarkerOnlyFoldingMargin"/>は<see cref="FoldingMargin"/>の派生クラスであり、
    /// <see cref="ReplaceFoldingMarginWithMarkerOnly"/>を先に呼んでから本メソッドを呼ぶため、
    /// <c>OfType&lt;FoldingMargin&gt;()</c>は差し替え後の自前のマージンを正しく拾う
    /// （tests/Graft.UiTests/FoldingMarginTests.csで確認）。
    /// </summary>
    private void HookFoldingMargin()
    {
        var margin = _editor.TextArea.LeftMargins.OfType<FoldingMargin>().FirstOrDefault();
        if (margin is null) return;

        _hookedMargin = margin;
        margin.PointerMoved += OnFoldingMarginPointerMoved;
        margin.PointerExited += OnFoldingMarginPointerExited;

        // 課題#73: ＋/－マーカーのクリックで畳み状態が変わる経路。マーカーの実体
        // （AvaloniaEdit.Folding.FoldingMarginMarker、internal sealed）はこのマージンの
        // ビジュアル子で、IsFoldedの反転はそのOnPointerPressed（Avaloniaのクラスハンドラ）の
        // 中で行われる。イベントは子→親へバブルするため、親であるこのマージンで購読すれば
        // 「マーカーが反転させた直後」に必ず呼ばれる。handledEventsToo:trueが必要
        // （マーカーがe.Handled=trueにしてから親へ上がるため）。
        //
        // 【押した瞬間に畳まれていなくてよいのか】 押した時点では生成器が外れている
        // （＝TextViewがFoldingManager.TextViewsに未登録の）ままIsFolded=trueになるため、
        // FoldingSection.ValidateCollapsedLineSectionsは行を隠すためのCollapsedLineSectionを
        // 0個しか作らない（TextViews.Countが0）。それでも表示が崩れないのは、直後に本ハンドラが
        // 生成器を戻し、その際のFoldingManager.AddToTextViewが「CollapsedSectionsが非nullの
        // 範囲を作り直す」（Array.Resize + ValidateCollapsedLineSections）ためである
        // （AvaloniaEdit 11.1.0のFolding/FoldingManager.cs・FoldingSection.csで確認）。
        // ここまでがポインタイベント1回分の同期処理の中で完結し、その途中でレイアウトパスが
        // 割り込むことはないため、押している間のフレームも正しく畳まれた状態で描かれる
        // （tests/Graft.UiTests/FoldingGeneratorDetachTests.csの「マーカーを押した時点
        // （離す前）で、既に本文の折りたたみが表示へ反映されている」で実際に確認している。
        // トンネル段階で先回りして戻す版も試したが、同テストで違いが出ず、経路が増えるだけ
        // だったため採らなかった）。
        margin.AddHandler(InputElement.PointerPressedEvent, OnFoldingMarginPointerPressed,
            RoutingStrategies.Bubble, handledEventsToo: true);
        margin.AddHandler(InputElement.PointerReleasedEvent, OnFoldingMarginPointerReleased,
            RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void UnhookFoldingMargin()
    {
        if (_hookedMargin is null) return;
        _hookedMargin.PointerMoved -= OnFoldingMarginPointerMoved;
        _hookedMargin.PointerExited -= OnFoldingMarginPointerExited;
        _hookedMargin.RemoveHandler(InputElement.PointerPressedEvent, OnFoldingMarginPointerPressed);
        _hookedMargin.RemoveHandler(InputElement.PointerReleasedEvent, OnFoldingMarginPointerReleased);
        _hookedMargin = null;
        SetHoveredFolding(null);
    }

    /// <summary>
    /// 課題#73: マーカーが畳み状態を反転させた直後に同期する（<see cref="HookFoldingMargin"/>の
    /// コメント参照）。マーカー以外の場所を押した場合も呼ばれるが、同期は「畳まれた範囲が
    /// 1つでもあるか」を数えて必要なときだけ付け外しする冪等な処理なので、何も起きない。
    /// </summary>
    private void OnFoldingMarginPointerPressed(object? sender, PointerPressedEventArgs e)
        => SyncFoldingGenerator();

    /// <summary>
    /// 課題#73: 押した瞬間（<see cref="OnFoldingMarginPointerPressed"/>）で足りているが、
    /// マージン上でのドラッグ等、押下と離上の間に畳み状態が変わる操作が将来加わっても
    /// 取りこぼさないよう、離した時にも同期しておく（上と同じく冪等）。
    /// </summary>
    private void OnFoldingMarginPointerReleased(object? sender, PointerReleasedEventArgs e)
        => SyncFoldingGenerator();

    /// <summary>
    /// 課題#73: 本文側での展開（畳まれた"..."の箱のクリック・畳まれた範囲へのキャレット移動に
    /// 伴うAvaloniaEditの自動展開）を拾って生成器を外す（コンストラクタのコメント参照）。
    /// </summary>
    private void OnTextViewPointerReleased(object? sender, PointerReleasedEventArgs e)
        => SyncFoldingGenerator();

    /// <summary>
    /// 課題#73の中核（詳細はクラスコメントの【課題#73】節）。畳まれた範囲が1つでもあれば
    /// <see cref="FoldingElementGenerator"/>を<c>TextView.ElementGenerators</c>へ戻し、
    /// 1つも無ければ外す。畳み状態が変わりうる操作のたびに呼ぶ（冪等）。
    ///
    /// <c>AllFoldings</c>の走査は10万行の.csで20,000件あるが、呼ぶのは「畳み状態が変わりうる
    /// 操作」（マージンのクリック・折りたたみコマンド・デバウンス後の再計算）に限っているため
    /// 費用にならない。1回あたりは0.15ms程度（レイアウト1回で46行ぶん繰り返して+6.9msだった、
    /// という調査時の実測からの逆算）。これに対し外した効果は、可視行を構築するたびに起きて
    /// いた同じ走査を丸ごと消すこと（ドラッグ1ステップの上乗せ +5.06ms → −0.03ms）。
    /// キー入力のたびに呼ぶ（キャレット移動による自動展開を追う）ことをしていないのは、
    /// この0.15msを入力のたびに払う方が害が大きいと判断したため（クラスコメント参照）。
    /// </summary>
    private void SyncFoldingGenerator()
    {
        if (_manager is null || _generator is null) return;

        var anyFolded = false;
        foreach (var fs in _manager.AllFoldings)
        {
            if (!fs.IsFolded) continue;
            anyFolded = true;
            break;
        }

        if (anyFolded) EnsureFoldingGeneratorAttached();
        else DetachFoldingGenerator();
    }

    /// <summary>
    /// 課題#73: 生成器を<c>TextView.ElementGenerators</c>の先頭へ戻す。先頭であることは
    /// AvaloniaEdit側の要求（<c>FoldingManagerInstallation</c>のコメント "HACK: folding only
    /// works correctly when it has highest priority"）で、Graftはカラープレビュー
    /// （<see cref="ColorPreviewElementGenerator"/>）も同じリストへ入れるため、末尾へ足し直すと
    /// 優先順位が変わってしまう。
    /// </summary>
    private void EnsureFoldingGeneratorAttached()
    {
        if (_generator is null || _generatorAttached) return;

        try
        {
            _editor.TextArea.TextView.ElementGenerators.Insert(0, _generator);
            _generatorAttached = true;
        }
        catch (Exception ex)
        {
            // 万一失敗しても折りたたみの表示が欠けるだけに留め、アプリは継続させる
            // （附録A.4・設計目標5）。実際の並びから状態を取り直して食い違いを残さない。
            _generatorAttached = _editor.TextArea.TextView.ElementGenerators.Contains(_generator);
            SafeHandler.OnUnexpected?.Invoke("折りたたみ生成器の再取り付け", ex);
        }
    }

    private void DetachFoldingGenerator()
    {
        if (_generator is null || !_generatorAttached) return;

        try
        {
            _editor.TextArea.TextView.ElementGenerators.Remove(_generator);
            _generatorAttached = false;
        }
        catch (Exception ex)
        {
            _generatorAttached = _editor.TextArea.TextView.ElementGenerators.Contains(_generator);
            SafeHandler.OnUnexpected?.Invoke("折りたたみ生成器の取り外し", ex);
        }
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

    /// <summary>
    /// 【実機での指摘（Windows）: ホバー強調のちらつき、調査結果】
    /// 真因は本メソッドではなく<see cref="Editor.IndentGuideRenderer.OnHoveredFoldingChanged"/>
    /// 側にあった（<see cref="Editor.TextViewRedraw"/>のクラスコメント参照:
    /// <c>TextView.InvalidateLayer</c>が実質<c>InvalidateMeasure()</c>で、可視行の作り直しに
    /// 伴い<c>FoldingMargin</c>が＋/－マーカーを再生成し、それによって本メソッドが呼ばれる、
    /// という循環だった）。その修正後、実機相当のXvfb + xdotool（実際のX11入力イベント、
    /// 座標はマーカーの<c>Bounds</c>から算出）で「マーカーへカーソルを合わせて数秒静止・
    /// 微小なジッターを加えながら保持」を複数回試したが、<see cref="HoveredFoldingChanged"/>が
    /// 意図せずnullへ戻る（＝本メソッドが不要に呼ばれる）事象は一度も再現しなかった。
    /// 上の修正だけで実際に流れが止まる（可視行・マーカーが再生成されなくなる）ため、本メソッド
    /// 自身に「退出時に本当にマージンの矩形外にあるか確認してから消す」という防御を追加で
    /// 入れる必要は実測上確認できなかった。よって本メソッドはあえて変更していない
    /// （測れていない問題への対処を先回りで足すと、かえって挙動の見通しを悪くするため）。
    /// </summary>
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
        _editor.TextArea.TextView.RemoveHandler(InputElement.PointerReleasedEvent, OnTextViewPointerReleased);
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
        if (_manager is null) { RemoveCustomMargin(); ForgetFoldingGenerator(); return; }
        var manager = _manager;
        _manager = null;
        try
        {
            // 課題#73: ここで生成器を戻し直す必要は無い。FoldingManager.Uninstallは
            // (1) Clear()で全範囲のIsFoldedをfalseにし (2) ElementGenerators.Removeを呼ぶ、
            // という順序だが、外している間は「1つも畳まれていない」ことが本クラスの不変条件
            // （SyncFoldingGenerator）であり、(1)は何も変えず、(2)は既にリストに無いので
            // 空振りするだけで例外にならない（List.Removeはfalseを返すのみ）。
            // 逆に戻してから呼ぶと、TextView.Documentが既に新しい文書へ切り替わっている
            // 場面（課題#82の①〜④区間。OnEditorDocumentChanged経由でここへ来る）で
            // 一瞬だけ生成器が復活することになり、わざわざ塞いだ窓を開け直すことになる。
            FoldingManager.Uninstall(manager);
        }
        catch (Exception ex)
        {
            SafeHandler.OnUnexpected?.Invoke("折りたたみの解除", ex);
        }
        finally
        {
            // ReplaceFoldingMarginWithMarkerOnlyのコメントのとおり、FoldingManager.Uninstallは
            // 差し替え前の標準FoldingMarginインスタンスを取り除こうとするだけで、差し替え後の
            // 自前のマージンはLeftMarginsに残り続けてしまう。ここで確実に自分で取り除く。
            RemoveCustomMargin();
            ForgetFoldingGenerator();
        }
    }

    /// <summary>
    /// 課題#73: <see cref="FoldingManager.Uninstall"/>の後、生成器への参照を捨てる。
    /// 生成器は<c>Install</c>のたびに新しく作られるため、次の<see cref="Attach"/>で
    /// 改めて捕まえ直す（古いインスタンスを掴んだままにすると、外れているつもりのフラグと
    /// 実際のリストが食い違う恐れがある）。
    /// </summary>
    private void ForgetFoldingGenerator()
    {
        _generator = null;
        _generatorAttached = false;
    }

    /// <summary>
    /// <see cref="ReplaceFoldingMarginWithMarkerOnly"/>で差し替えた<see cref="MarkerOnlyFoldingMargin"/>を
    /// <c>TextArea.LeftMargins</c>から取り除く。<see cref="FoldingManager.Uninstall"/>はこれを
    /// 取り除いてくれないため（同メソッドのコメント参照）、<see cref="Uninstall"/>から必ず呼ぶ。
    /// </summary>
    private void RemoveCustomMargin()
    {
        if (_customMargin is null) return;
        _editor.TextArea.LeftMargins.Remove(_customMargin);
        _customMargin = null;
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

        // 課題#73: UpdateFoldingsは畳まれていた範囲が編集で消えれば取り除く（＝畳まれた範囲が
        // ゼロになりうる）ため、再計算のたびに同期する。編集のたびではなくデバウンス後に
        // 1回だけ走る経路なので、走査の費用は問題にならない（SyncFoldingGenerator参照）。
        // 逆に「畳んだままキャレット移動で自動展開された」等でここまで同期が遅れていた場合も、
        // 次の編集の300ms後には必ず外れるという意味での回収経路になっている。
        SyncFoldingGenerator();
    }

    // ========================================================================
    // 検討書「折りたたみの機能追加」(b) 折りたたみコマンドの追加。
    // AvaloniaEditのFoldingManagerには相当する組み込みコマンドが無いため、公開API
    // （AllFoldings・GetFoldingsContaining・FoldingSection.IsFolded）だけで自前実装する。
    //
    // 【課題#73】 3つとも「IsFoldedを立てる前にEnsureFoldingGeneratorAttached、処理後に
    // SyncFoldingGenerator（finally）」という同じ形にしてある。前者が必要なのは、生成器が
    // 外れている＝TextViewがFoldingManager.TextViewsに登録されていない状態でIsFolded=trueに
    // すると、行を隠すためのCollapsedLineSectionが1つも作られないため
    // （AvaloniaEdit 11.1.0 Folding/FoldingSection.ValidateCollapsedLineSectionsは
    // TextViews.Count個だけ作る）。先に戻しておけば、以降の処理は本対処を入れる前と
    // 1バイトも変わらない経路を通る。後者は「レベル指定で結果的に1つも畳まれなかった」
    // 「全部展開になった」場合に外し直すため（コマンドはキーボード・コマンドパレットからの
    // 単発操作なので、走査の費用は問題にならない）。
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

        EnsureFoldingGeneratorAttached(); // 課題#73（上の共通コメント参照）。
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
        finally
        {
            SyncFoldingGenerator();
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

        EnsureFoldingGeneratorAttached(); // 課題#73（上の共通コメント参照）。
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
        finally
        {
            SyncFoldingGenerator();
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

        EnsureFoldingGeneratorAttached(); // 課題#73（上の共通コメント参照）。
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
        finally
        {
            SyncFoldingGenerator();
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
