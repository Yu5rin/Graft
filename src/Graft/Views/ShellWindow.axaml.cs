using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Graft.Infra;
using Graft.ViewModels;

namespace Graft.Views;

/// <summary>
/// 新シェルレイアウトのメインウィンドウ（仕様書9.2）。サイドバー・サイドビュー・エディタ領域・
/// 接ぎ木パネル・ステータスバーを1つのGridに束ね、ウィンドウ位置・ペイン幅・パネル高さの
/// 復元/保存（9.6・3.2）を担う。キーボード操作（9.5・附録A）は
/// ShellWindow.Keyboard.cs に分割する（1ファイル400行上限のため）。
/// v2.0のWPF版からの移植（19章 L3）。
/// </summary>
public partial class ShellWindow : Window
{
    /// <summary>接ぎ木パネルが折りたたまれているときのヘッダーのみの高さ（GraftPanel.axamlと一致させる）。</summary>
    internal const double GraftPanelHeaderHeight = 32;

    // 要望1（実機からの改善要望）: GridSplitterでドラッグして潰せる下限。
    // 0まで潰せると二度と戻せなくなって詰む（マウスで再び広げる手がかりが無くなる）ため、
    // サイドビュー・接ぎ木パネルそれぞれに「これ以上は狭められない」下限を設ける。
    // サイドビュー: 履歴・検索ペインのフィルタ用ドロップダウン等が実機でも窮屈にならない
    // 最小幅として180pxを採用（既定値260pxの約7割）。
    internal const double SideViewMinWidth = 180;
    // 接ぎ木パネル: ヘッダー32px＋パッチ行が最低2〜3件見える高さとして120pxを採用。
    internal const double GraftPanelMinHeight = 120;
    // 接ぎ木パネル（右配置）: ヘッダーのボタン群（開閉・配置切替・失敗を再依頼・プレビュー・適用）が
    // 横一列に並ぶため、下配置の120pxのような狭さは許容できない。仕様書の目安値は240pxだったが、
    // 実機検証（Xvfb）でボタンを1つずつ幅を変えながら実測したところ、240pxは疎か350pxでも
    // 「適用」ボタン（最重要の操作）自体が右パネルの外へはみ出して押せなくなることを確認した。
    // 420pxで初めて「適用」まで含めた全ボタンが収まったため、この実測値を最小幅として採用する
    // （目安の240pxをそのまま使うと、右配置で唯一の操作手段である「適用」が押せない状態に
    // なってしまうため、実機の結果を優先した）。
    internal const double GraftPanelMinWidth = 420;

    // 9.2/3.2: サイドビュー幅・接ぎ木パネルの高さ・幅は ProjectPaneLayout の
    // SideViewWidth/GraftPanelHeight/GraftPanelWidth に記憶する。
    // 既定の460pxはGraftPanelMinWidth（420px、実機検証済み）にヘッダーの余白分を足した値。
    private double _sideViewWidth = 260;
    private double _graftPanelHeight = 260;
    private double _graftPanelWidth = 460;

    // 課題2: 「閉じたときの動作」設定。既定は"exit"（終了する）。StartupCoordinatorが
    // 起動時・設定変更時に設定する。設定は即時反映のため、ここは可変プロパティにする。
    private string _closeBehavior = "exit";

    /// <summary>
    /// ×で閉じたときの動作。"exit"（終了する）/ "tray"（タスクトレイに常駐する）。
    /// 設定画面での変更を即時に反映できるよう、StartupCoordinatorから都度書き換える。
    /// </summary>
    public string CloseBehavior { get => _closeBehavior; set => _closeBehavior = value; }

    /// <summary>
    /// トレイが実際に機能する環境かどうか。falseの場合、CloseBehaviorが"tray"であっても
    /// 実際には終了する（仕様書2.3の縮退）。プロセス起動中に変わることは無いため、
    /// StartupCoordinatorが起動時に一度だけ設定する。
    /// </summary>
    public bool IsTraySupported { get; set; }

    /// <summary>
    /// トレイメニューの「終了」等、CloseBehaviorの設定に関わらず必ず終了させたい経路から
    /// Close()を呼ぶ前にtrueにする。OnClosingがトレイへ隠す分岐を迂回するための目印。
    /// </summary>
    public bool IsForceClosing { get; set; }

    /// <summary>
    /// 課題1: 終了処理の経路（ウィンドウを閉じた／トレイメニューの終了）・レイアウト保存の
    /// 成否を記録するためのロガー。StartupCoordinator.StartAsyncが生成後に設定する
    /// （コンストラクタの時点ではまだLoggerが存在しないため）。未設定（null）でも
    /// 終了処理自体は通常どおり行う。
    /// 負荷下フレーク調査（案件A）: IndentGuideRenderer.Draw の防御的catchもこのロガーへ
    /// 記録できるよう、設定と同時にEditorHost内の<see cref="EditorPane"/>（XAMLで静的に
    /// 1つだけ配置。クラスコメント参照）へも橋渡しする。
    /// </summary>
    public Logger? Logger
    {
        get => _logger;
        set
        {
            _logger = value;
            if (EditorHost.Child is EditorPane pane) pane.Logger = value;
        }
    }

    private Logger? _logger;

    /// <summary>
    /// 終了処理（レイアウト保存を含む）を開始した時刻。トレイへ隠しただけ（実際には
    /// 終了しない）の場合は設定しない。起動側の「操作可能まで N ms」（StartupCoordinator.
    /// StartAsync）に対応する形で、終了処理全体の所要時間をログへ残すために
    /// StartupCoordinator.DisposeAsyncから参照する。
    /// </summary>
    public DateTime? ShutdownStartedAt { get; private set; }

    // AvaloniaのXAMLコンパイラは Row/ColumnDefinition の x:Name に対してフィールドを
    // 生成しない（コントロールではないため）。そのため寸法を書き換える定義は、
    // 名前を付けたGridから位置で取得する。
    private ColumnDefinition SideViewColumn => BodyGrid.ColumnDefinitions[1];
    private RowDefinition GraftSplitterRow => EditorAreaGrid.RowDefinitions[1];
    private RowDefinition GraftPanelRow => EditorAreaGrid.RowDefinitions[2];
    private ColumnDefinition GraftSplitterColumn => EditorAreaGrid.ColumnDefinitions[1];
    private ColumnDefinition GraftPanelColumn => EditorAreaGrid.ColumnDefinitions[2];

    /// <summary>headlessテスト・デザイナ用の引数なしコンストラクタ。</summary>
    public ShellWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnTunnelKeyDown, RoutingStrategies.Tunnel);
        InitializeProjectComboBoxWheel(); // ShellWindow.ProjectComboBox.cs参照。
        // 実機で確認された指摘3: プロンプトテンプレートの選択に必ず3クリックかかっていた
        // （開く→項目を選ぶ→「コピー」を押す）。ダブルタップとEnterで「選んで即コピーして
        // 閉じる」を追加する（ProjectPane.axaml.csのOnDoubleTappedと同じ作法）。
        PromptTemplateListBox.DoubleTapped += OnPromptTemplateDoubleTapped;
        PromptTemplateListBox.KeyDown += OnPromptTemplateKeyDown;
    }

    public ShellWindow(ShellViewModel viewModel) : this()
    {
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

        Loaded += OnLoaded;
        Closing += OnClosing;
        viewModel.PropertyChanged += OnShellViewModelPropertyChanged;
        viewModel.Graft.RequestOpenQueue += OnRequestOpenQueue;
        viewModel.Graft.ApplyPreviewRequested += OnApplyPreviewRequested;
        viewModel.Graft.RequestOpenContextCollect += OnRequestOpenContextCollect;
        viewModel.Graft.RequestFocusHistory += OnRequestFocusHistory;
        viewModel.RequestFocusSearchView += OnRequestFocusSearchView;
        viewModel.RequestOpenShortcuts += OnRequestOpenShortcuts;
        viewModel.RequestOpenManual += OnRequestOpenManual;
        // 画面上のチュートリアル（コーチマーク）。実体（ShellWindow.Tutorial.cs）は
        // Controlの座標計算等Avalonia固有の知識を要するためView側の責務とし、ここでは
        // ShellViewModel側からの要求（ツールバー「?」メニュー・コマンドパレット）を
        // StartTutorial()へ橋渡しするだけに留める。
        viewModel.RequestStartTutorial += (_, _) => StartTutorial();
        viewModel.QuickOpen.Opened += OnQuickOpenOpened;
        viewModel.CommandPalette.Opened += OnCommandPaletteOpened;
    }

    private ShellViewModel ViewModel => (ShellViewModel)DataContext!;

    /// <summary>4.10: キュー管理ウィンドウを開く（コマンドバー「キュー」ボタン）。</summary>
    private void OnRequestOpenQueue(object? sender, EventArgs e)
    {
        var window = new QueueWindow(ViewModel.Graft.Queue);
        _ = window.ShowDialog(this);
    }

    /// <summary>
    /// 課題1（設定「適用前にプレビューを表示する」）: ApplyAsyncからの適用前プレビュー要求を
    /// 受けて<see cref="ApplyPreviewWindow"/>を開き、結果（「適用」が押されたかどうか）を
    /// イベント引数のCompletionへ書き戻す。MainViewModelはWindowの型を知らないため、
    /// この橋渡しはBeforeApplyAsync/AfterApplyAsync（<see cref="ShellViewModel"/>）と同じく
    /// View側の責務とする。
    /// </summary>
    private async void OnApplyPreviewRequested(object? sender, ApplyPreviewRequestedEventArgs e)
    {
        var previewViewModel = new ApplyPreviewViewModel(e.PlansToApply, e.Settings, e.Ui);
        var window = new ApplyPreviewWindow(previewViewModel);
        var confirmed = await window.ShowAndConfirmAsync(this).ConfigureAwait(true);
        // 指摘4: 「今後は表示しない」がチェックされていれば、適用・キャンセルいずれで閉じても
        // 設定側（Settings.ShowPreview）をオフにする（ViewModel.ApplyPreviewDisableRequestedの
        // コメント参照）。
        if (window.DontShowAgainRequested) ViewModel.NotifyApplyPreviewDisableRequested();
        e.Completion.TrySetResult(confirmed);
    }

    /// <summary>10章: コンテキスト収集ウィンドウを開く（コマンドバー「ファイル」ボタン）。</summary>
    private void OnRequestOpenContextCollect(object? sender, EventArgs e)
    {
        if (ViewModel.Graft.ContextCollect is null) return;

        var window = new ContextCollectWindow(ViewModel.Graft.ContextCollect);
        _ = window.ShowDialog(this);
    }

    // 実機で確認された指摘6: F1で説明書を開くと、説明されている画面をモーダルに阻まれて
    // 触れず、「手順を読みながら操作する」という一番自然な読み方ができなかった
    // （ショートカット一覧も同様、開いている間はエディタ側を一切操作できない）。
    // 両方とも「見ながら他の操作をする」ことが前提の閲覧用ウィンドウで、本体の状態を
    // 変更する入力欄も持たないため非モーダル（Show）化の副作用が無く、この2つに限って
    // ShowDialogからShowへ切り替える。設定・コンテキスト収集は本体の状態（設定・
    // プロジェクト選択）と絡むため対象外とした（判断理由はタスク報告に記載）。
    // 同じものを二重に開けないよう、既に開いていればActivate()するだけに留め、
    // Closedで参照を外す。親（このShellWindow）を閉じると、Avaloniaの既定の複数ウィンドウ
    // シャットダウン動作（App.axaml.cs参照）によりOwnerを介して連動して閉じることを
    // 実機（Xvfb）で確認済み。
    private ShortcutsWindow? _shortcutsWindow;
    private ManualWindow? _manualWindow;

    /// <summary>Ctrl+/・ツールバーの「?」メニュー「キーボードショートカット一覧」。静的な内容のため専用ViewModelは持たない。</summary>
    private void OnRequestOpenShortcuts(object? sender, EventArgs e)
    {
        if (_shortcutsWindow is { } existing)
        {
            existing.Activate();
            return;
        }

        var window = new ShortcutsWindow();
        _shortcutsWindow = window;
        window.Closed += (_, _) => _shortcutsWindow = null;
        window.Show(this);
    }

    /// <summary>
    /// 取扱説明書機能: F1・ツールバーの「?」メニュー「取扱説明書」。埋め込みリソースから
    /// 読み込んだ本文を静的表示するだけのため、ShortcutsWindowと同じく専用ViewModelは持たない。
    /// </summary>
    private void OnRequestOpenManual(object? sender, EventArgs e)
    {
        if (_manualWindow is { } existing)
        {
            existing.Activate();
            return;
        }

        var window = new ManualWindow();
        _manualWindow = window;
        window.Closed += (_, _) => _manualWindow = null;
        window.Show(this);
    }

    /// <summary>9.2: 「履歴」ボタン・Ctrl+Shift+H。サイドバーの履歴ビューを開いて一覧へフォーカスする。</summary>
    private void OnRequestFocusHistory(object? sender, EventArgs e)
    {
        ViewModel.SelectSideView(SideViewKind.History);
        HistoryPaneControl.ListBoxElement.Focus();
    }

    /// <summary>
    /// 4.4: 検索ビュー（サイドバーの虫眼鏡アイコン・Ctrl+Shift+F）表示時に検索欄へフォーカスする。
    /// 折りたたみ/非表示から表示へ切り替わった直後はレイアウトが未確定のため、Focus()を
    /// 即座に呼んでも取れない（EditorPane.axaml.csのスクロール位置復元と同じ事情）。
    /// レイアウト確定後まで遅延させる。
    /// </summary>
    private void OnRequestFocusSearchView(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() => SearchViewControl.QueryBoxElement.Focus(), DispatcherPriority.Background);
    }

    /// <summary>
    /// クイックオープン（Ctrl+P）を開いた瞬間に検索欄へフォーカスする。
    /// オーバーレイが直前まで非表示だったため、検索ビュー表示時と同様レイアウト確定後まで遅延させる。
    /// </summary>
    private void OnQuickOpenOpened(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() => QuickOpenOverlayControl.QueryBoxElement.Focus(), DispatcherPriority.Background);
    }

    /// <summary>
    /// コマンドパレット（Ctrl+Shift+P）を開いた瞬間に検索欄へフォーカスする
    /// （<see cref="OnQuickOpenOpened"/>と同じ理由・同じ作法）。
    /// </summary>
    private void OnCommandPaletteOpened(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() => CommandPaletteOverlayControl.QueryBoxElement.Focus(), DispatcherPriority.Background);
    }

    /// <summary>
    /// 指摘3: プロンプトテンプレート一覧のダブルタップ。単クリック相当の選択
    /// （PromptTemplateListBox.SelectedItemバインド）はダブルタップの前段で既に届いているはずだが、
    /// ProjectPane.axaml.csのOnDoubleTappedと同じ理由で念のため明示的に選択を揃えてからコピーする。
    /// </summary>
    private void OnPromptTemplateDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not ShellViewModel || FindAncestor<ListBoxItem>(e.Source as Visual) is not
            { DataContext: PromptTemplateOptionViewModel item }) return;

        PromptTemplateListBox.SelectedItem = item;
        ExecutePromptCopy();
    }

    /// <summary>指摘3: プロンプトテンプレート一覧でのEnter確定。矢印キーでの選択移動はListBox既定の挙動のまま変えない。</summary>
    private void OnPromptTemplateKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        ExecutePromptCopy();
    }

    private void ExecutePromptCopy()
    {
        if (DataContext is not ShellViewModel viewModel) return;
        var copyCommand = viewModel.Graft.PromptCopy?.CopyCommand;
        if (copyCommand is not null && copyCommand.CanExecute(null)) copyCommand.Execute(null);
    }

    private static T? FindAncestor<T>(Visual? node) where T : Visual
    {
        while (node is not null and not T)
        {
            node = node.GetVisualParent();
        }
        return node as T;
    }

    /// <summary>
    /// ApplyLayoutToWindow（保存済みレイアウトの反映）が完了したかどうか。既定はfalse。
    /// OnLoadedはGraft.InitializeAsync()（実ファイルI/Oを含む非同期処理）の完了を待ってから
    /// ApplyLayoutToWindowを呼ぶ非同期の経路のため、headlessテストがShow()直後に
    /// Dispatcher.UIThread.RunJobs()を1回呼ぶだけでは、まだこの反映が終わっていないことがある
    /// （実測で確認済み）。テストが「保存済みレイアウトの反映が確実に終わった状態」を
    /// 待ち合わせるための目印として公開する（GraftPanelPlacementTests.WaitForWindowLoaded参照）。
    /// publicなのは、CloseBehavior/IsTraySupported等ここまでのテスト向け公開プロパティと
    /// 同じ理由（Graft.UiTestsプロジェクトにInternalsVisibleToが無いため）。
    /// </summary>
    public bool IsLayoutApplied { get; private set; }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        await SafeHandler.RunAsync("シェルの初期化", async () =>
        {
            await ViewModel.Graft.InitializeAsync().ConfigureAwait(true);
            ApplyLayoutToWindow();
            IsLayoutApplied = true;
        }).ConfigureAwait(true);
    }

    /// <summary>仕様書9.6: 保存済みレイアウトをウィンドウ・ペインへ反映する。</summary>
    private void ApplyLayoutToWindow()
    {
        var layout = ViewModel.Graft.Layout;
        var bounds = WindowLayoutStore.ResolveWindowBounds(layout, MinWidth, MinHeight, ViewModel.Ui.Screens);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint((int)bounds.Left, (int)bounds.Top);
        Width = bounds.Width;
        Height = bounds.Height;
        WindowState = layout.IsMaximized ? WindowState.Maximized : WindowState.Normal;

        var paneLayout = ViewModel.Graft.GetCurrentPaneLayout();
        _sideViewWidth = SafeLength(paneLayout.SideViewWidth, 260);
        // 配置（下／右）は先に反映する。IsGraftPanelOpenが既に真（パッチ解析済み等）だと
        // このSetterがOnShellViewModelPropertyChanged経由で一時的にApplyGraftPanelStateを
        // 呼ぶことがあるが、そのときの捕捉値（CaptureGraftPanelActualSize）は初期化前の
        // 仮の寸法でしかないため、直後の_graftPanelHeight/_graftPanelWidthへの代入で
        // 必ず上書きされる（保存値が最終的に勝つ）。
        ViewModel.GraftPanelPlacement = ShellViewModel.ParseGraftPanelPlacement(paneLayout.GraftPanelPlacement);
        _graftPanelHeight = SafeLength(paneLayout.GraftPanelHeight, 260);
        _graftPanelWidth = SafeLength(paneLayout.GraftPanelWidth, 460);
        ApplySideViewState();
        ApplyGraftPanelState();
    }

    /// <summary>
    /// サイドビューの折りたたみ・展開をColumnDefinitionへ反映する。
    /// 要望1: MinWidthも展開中(SideViewMinWidth)/折りたたみ中(0)で切り替える。
    /// 折りたたみ時にMinWidthを正の値のままにすると、直後のWidth=0への代入が
    /// MinWidthまで切り上げられてしまい「折りたたむと透明な帯が残る」不具合になる
    /// （GridSplitterのドラッグ下限と既存の折りたたみ機能が両立するようにするための対応）。
    /// </summary>
    private void ApplySideViewState()
    {
        if (ViewModel.IsSideViewCollapsed)
        {
            SideViewColumn.MinWidth = 0;
            SideViewColumn.Width = new GridLength(0);
            SideViewSplitter.IsVisible = false;
        }
        else
        {
            SideViewColumn.MinWidth = SideViewMinWidth;
            SideViewColumn.Width = new GridLength(_sideViewWidth);
            SideViewSplitter.IsVisible = true;
        }
    }

    /// <summary>
    /// 接ぎ木パネルの折りたたみ・展開・配置（下／右）をRow/ColumnDefinitionへ反映し、
    /// パネル自体（GraftPanelControl、1インスタンスのみ）のGrid.Row/Grid.Columnを
    /// 現在の配置に合わせて付け替える。配置切替時は<see cref="OnShellViewModelPropertyChanged"/>
    /// が事前に<see cref="CaptureGraftPanelActualSize"/>で移動元の実寸を記憶してから呼ぶ。
    /// </summary>
    private void ApplyGraftPanelState()
    {
        if (ViewModel.GraftPanelPlacement == GraftPanelPlacementKind.Right)
        {
            ApplyGraftPanelStateRight();
        }
        else
        {
            ApplyGraftPanelStateBottom();
        }
    }

    /// <summary>
    /// 下配置（既定）。要望1: MinHeightもサイドビューと同じ理由で開閉に合わせて切り替える。
    /// 列側（右配置用）は常に幅0へたたみ、右配置用スプリッタも隠す。
    /// </summary>
    private void ApplyGraftPanelStateBottom()
    {
        GraftSplitterColumn.Width = new GridLength(0);
        GraftPanelColumn.MinWidth = 0;
        GraftPanelColumn.Width = new GridLength(0);
        GraftSplitterRight.IsVisible = false;

        if (ViewModel.IsGraftPanelOpen)
        {
            GraftPanelRow.MinHeight = GraftPanelMinHeight;
            GraftPanelRow.Height = new GridLength(_graftPanelHeight);
            GraftSplitterRow.Height = GridLength.Auto;
            GraftSplitter.IsVisible = true;
        }
        else
        {
            GraftPanelRow.MinHeight = 0;
            GraftPanelRow.Height = new GridLength(GraftPanelHeaderHeight);
            GraftSplitterRow.Height = new GridLength(0);
            GraftSplitter.IsVisible = false;
        }

        Grid.SetRow(GraftPanelControl, 2);
        Grid.SetColumn(GraftPanelControl, 0);
    }

    /// <summary>
    /// 右配置（3列: サイドバー｜エディタ｜接ぎ木パネル）。展開時は列側を使い、行側（下配置用）は
    /// 常に高さ0へたたんで、エディタ行[0]がGrid全体の高さを占めるようにする。
    ///
    /// 折りたたみ時（利用者からの指摘2対応）: 以前は幅0まで完全にたたんでいたが、それだと
    /// 掴む対象が画面から消えてしまい、Ctrl+Jか配置切替ボタンの存在を知らないと二度と
    /// 展開できず「復帰しづらい」という利用者からの指摘があった。下配置の折りたたみと同じ
    /// 32pxのヘッダー帯を画面下部に表示する（列側は幅0のまま・行側[2]をヘッダー高さへ）ことで、
    /// 右配置のままでも下配置と同じ手がかりで再展開できるようにする。
    /// 配置の設定値（GraftPanelPlacement）自体はRightのまま変えない。展開すると
    /// ApplyGraftPanelStateRightのIsGraftPanelOpen=true側の分岐へ戻り、列（右）配置が復活する。
    /// </summary>
    private void ApplyGraftPanelStateRight()
    {
        if (ViewModel.IsGraftPanelOpen)
        {
            GraftSplitterRow.Height = new GridLength(0);
            GraftPanelRow.MinHeight = 0;
            GraftPanelRow.Height = new GridLength(0);
            GraftSplitter.IsVisible = false;

            GraftPanelColumn.MinWidth = GraftPanelMinWidth;
            GraftPanelColumn.Width = new GridLength(_graftPanelWidth);
            GraftSplitterColumn.Width = GridLength.Auto;
            GraftSplitterRight.IsVisible = true;

            Grid.SetRow(GraftPanelControl, 0);
            Grid.SetColumn(GraftPanelControl, 2);
        }
        else
        {
            // 折りたたみ中は下配置の折りたたみ（ApplyGraftPanelStateBottomのelse側）と
            // 見た目を完全に揃える: 列側は幅0、行側[2]はヘッダー高さ(32px)のみ、
            // パネル自体もGrid.Row=2/Grid.Column=0へ一時的に移す。
            GraftPanelColumn.MinWidth = 0;
            GraftPanelColumn.Width = new GridLength(0);
            GraftSplitterColumn.Width = new GridLength(0);
            GraftSplitterRight.IsVisible = false;

            GraftPanelRow.MinHeight = 0;
            GraftPanelRow.Height = new GridLength(GraftPanelHeaderHeight);
            GraftSplitterRow.Height = new GridLength(0);
            GraftSplitter.IsVisible = false;

            Grid.SetRow(GraftPanelControl, 2);
            Grid.SetColumn(GraftPanelControl, 0);
        }
    }

    /// <summary>
    /// 接ぎ木パネルが展開中に限り、現在の配置（移動元）での実寸を記憶する。
    /// 折りたたみ直前・配置切替直前のどちらからも呼ばれる共通処理
    /// （<see cref="OnShellViewModelPropertyChanged"/>参照）。
    /// </summary>
    private void CaptureGraftPanelActualSize()
    {
        if (!ViewModel.IsGraftPanelOpen) return;

        if (Grid.GetColumn(GraftPanelControl) == 2)
        {
            _graftPanelWidth = SafeLength(GraftPanelColumn.ActualWidth, _graftPanelWidth);
        }
        else
        {
            _graftPanelHeight = SafeLength(GraftPanelRow.ActualHeight, _graftPanelHeight);
        }
    }

    /// <summary>折りたたみ直前の実寸を記憶してから、View側の状態を再適用する。</summary>
    private void OnShellViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ShellViewModel.IsSideViewCollapsed):
                if (ViewModel.IsSideViewCollapsed)
                {
                    _sideViewWidth = SafeLength(SideViewColumn.ActualWidth, _sideViewWidth);
                }
                ApplySideViewState();
                break;
            case nameof(ShellViewModel.IsGraftPanelOpen):
                if (!ViewModel.IsGraftPanelOpen)
                {
                    CaptureGraftPanelActualSize();
                }
                ApplyGraftPanelState();
                break;
            case nameof(ShellViewModel.GraftPanelPlacement):
                // 移動元（切替前の配置）の実寸を、行き先を切り替える前に記憶しておく。
                CaptureGraftPanelActualSize();
                ApplyGraftPanelState();
                break;
        }
    }

    /// <summary>
    /// 仕様書9.6/3.2: 終了時にウィンドウ位置・サイズ・最大化・ペイン寸法を保存する。
    /// 課題2: CloseBehaviorが"tray"、トレイが実際に機能する環境、かつ強制終了経路
    /// （IsForceClosing）でない場合は、閉じる代わりに隠すだけにしてプロセスを残す。
    /// トレイが使えない環境では"tray"が設定されていても無視して通常どおり終了する
    /// （仕様書2.3の縮退。設定画面側でも選べないようにするが、手動でsettings.jsonを
    /// 書き換えた場合や環境が変わった場合に備え、ここでも二重に守る）。
    /// </summary>
    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeBehavior == "tray" && IsTraySupported && !IsForceClosing)
        {
            e.Cancel = true;
            Hide();
            // 課題1: 「終了できない」「勝手に落ちた」という報告の中には、実際にはこの
            // 常駐設定によって意図どおり隠れているだけのものが混ざりうる。終了ログが
            // 一切残らないという診断上の欠陥を防ぐため、隠しただけの場合も記録しておく。
            Logger?.Info("shutdown", "×で閉じましたが、タスクトレイに常駐する設定のため終了せず非表示にしました。");
            return;
        }

        // 指摘6: 非モーダル化したショートカット一覧・取扱説明書（Owner=thisで開いている）は、
        // Avaloniaの既定動作でもOwnerが閉じれば連動して閉じるが、上のトレイ分岐で「隠すだけ」を
        // 選んだ場合はここへ到達しない（＝子ウィンドウは開いたまま残る想定どおり）。実際に
        // ウィンドウを閉じる経路に限り、開いていれば確実に片付ける（Closedイベントで
        // _shortcutsWindow/_manualWindowの参照自体はnullへ戻る）。
        _shortcutsWindow?.Close();
        _manualWindow?.Close();

        // 課題1: 終了処理の開始と、どの経路から来たかを記録する。トレイメニューの「終了」は
        // StartupCoordinator.ForceExitがIsForceClosingを立ててからClose()を呼ぶため、
        // ×で閉じた場合と区別できる（多重起動検出による自動終了はウィンドウを一切作らない
        // 経路のため、ここではなくApp.OnFrameworkInitializationCompletedが別途記録する）。
        ShutdownStartedAt = DateTime.Now;
        var source = IsForceClosing ? "トレイメニューの終了" : "ウィンドウを閉じた";
        Logger?.Info("shutdown", $"終了処理を開始しました（経路: {source}）");

        var layout = ViewModel.Graft.Layout;
        layout.IsMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            layout.Left = Position.X;
            layout.Top = Position.Y;
            layout.Width = Width;
            layout.Height = Height;
        }
        // WindowState.Normalへ一度もならずに終了した場合（初回起動→最大化のまま終了等）は
        // Left/Topを更新しない。既定値はnull（未保存）のままなので、次回起動時は
        // ResolveWindowBoundsがプライマリモニタ中央へ補正する（バグ1: NaNのままSaveAsyncして
        // 例外になっていた不具合の修正）。

        if (!ViewModel.IsSideViewCollapsed)
        {
            _sideViewWidth = SafeLength(SideViewColumn.ActualWidth, _sideViewWidth);
        }
        // 終了時点の配置での実寸を記憶する（下配置なら高さ、右配置なら幅）。
        CaptureGraftPanelActualSize();

        var paneLayout = ViewModel.Graft.GetCurrentPaneLayout();
        paneLayout.SideViewWidth = _sideViewWidth;
        paneLayout.GraftPanelHeight = _graftPanelHeight;
        paneLayout.GraftPanelWidth = _graftPanelWidth;
        paneLayout.GraftPanelPlacement = ShellViewModel.ToGraftPanelPlacementValue(ViewModel.GraftPanelPlacement);

        // 3章: 終了時点で選択中のプロジェクトのタブ構成・展開状態を取り込んでから保存する
        // （プロジェクト切替時にしか取り込まれず、最後に使っていたプロジェクトのタブが
        // 復元されない不具合の修正）。
        ViewModel.CaptureCurrentProjectState();

        // 課題1/課題2: 保存先が読み取り専用等で書き込みに失敗しても、Closingを完了させて
        // プロセスが確実に終了できるようにする（以前は例外を捕捉しておらず、ここで例外が
        // 飛ぶとOnClosingが完了せずウィンドウが閉じない・終了できない恐れがあった）。
        // 成功・失敗のいずれもログへ残し、異常終了時は原因調査の手がかりにする。
        try
        {
            ViewModel.Graft.SaveLayoutAsync().GetAwaiter().GetResult();
            Logger?.Info("shutdown", "レイアウト・タブ構成の保存に成功しました");
        }
        catch (Exception ex)
        {
            Logger?.Error("shutdown", $"レイアウト・タブ構成の保存に失敗しました: {ex}");
        }
    }

    /// <summary>layout.json が壊れていた場合等にGridLengthが受け付けない値（NaN・0以下）を弾く。</summary>
    private static double SafeLength(double value, double fallback)
        => double.IsFinite(value) && value > 0 ? value : fallback;
}
