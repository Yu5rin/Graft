using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
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

    // 9.2/3.2: サイドビュー幅・接ぎ木パネル高さは ProjectPaneLayout の
    // SideViewWidth/GraftPanelHeight に記憶する。
    private double _sideViewWidth = 260;
    private double _graftPanelHeight = 260;

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
    /// </summary>
    public Logger? Logger { get; set; }

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

    /// <summary>headlessテスト・デザイナ用の引数なしコンストラクタ。</summary>
    public ShellWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnTunnelKeyDown, RoutingStrategies.Tunnel);
    }

    public ShellWindow(ShellViewModel viewModel) : this()
    {
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

        Loaded += OnLoaded;
        Closing += OnClosing;
        viewModel.PropertyChanged += OnShellViewModelPropertyChanged;
        viewModel.Graft.RequestOpenQueue += OnRequestOpenQueue;
        viewModel.Graft.RequestOpenContextCollect += OnRequestOpenContextCollect;
        viewModel.Graft.RequestFocusHistory += OnRequestFocusHistory;
        viewModel.RequestFocusSearchView += OnRequestFocusSearchView;
        viewModel.RequestOpenShortcuts += OnRequestOpenShortcuts;
        viewModel.QuickOpen.Opened += OnQuickOpenOpened;
    }

    private ShellViewModel ViewModel => (ShellViewModel)DataContext!;

    /// <summary>4.10: キュー管理ウィンドウを開く（コマンドバー「キュー」ボタン）。</summary>
    private void OnRequestOpenQueue(object? sender, EventArgs e)
    {
        var window = new QueueWindow(ViewModel.Graft.Queue);
        _ = window.ShowDialog(this);
    }

    /// <summary>10章: コンテキスト収集ウィンドウを開く（コマンドバー「ファイル」ボタン）。</summary>
    private void OnRequestOpenContextCollect(object? sender, EventArgs e)
    {
        if (ViewModel.Graft.ContextCollect is null) return;

        var window = new ContextCollectWindow(ViewModel.Graft.ContextCollect);
        _ = window.ShowDialog(this);
    }

    /// <summary>Ctrl+/・ツールバーの「?」ボタン。キーボードショートカット一覧を開く（静的な内容のため専用ViewModelは持たない）。</summary>
    private void OnRequestOpenShortcuts(object? sender, EventArgs e)
    {
        var window = new ShortcutsWindow();
        _ = window.ShowDialog(this);
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

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        await SafeHandler.RunAsync("シェルの初期化", async () =>
        {
            await ViewModel.Graft.InitializeAsync().ConfigureAwait(true);
            ApplyLayoutToWindow();
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
        _graftPanelHeight = SafeLength(paneLayout.GraftPanelHeight, 260);
        ApplySideViewState();
        ApplyGraftPanelState();
    }

    /// <summary>サイドビューの折りたたみ・展開をColumnDefinitionへ反映する。</summary>
    private void ApplySideViewState()
    {
        if (ViewModel.IsSideViewCollapsed)
        {
            SideViewColumn.Width = new GridLength(0);
            SideViewSplitter.IsVisible = false;
        }
        else
        {
            SideViewColumn.Width = new GridLength(_sideViewWidth);
            SideViewSplitter.IsVisible = true;
        }
    }

    /// <summary>接ぎ木パネルの折りたたみ・展開をRowDefinitionへ反映する。</summary>
    private void ApplyGraftPanelState()
    {
        if (ViewModel.IsGraftPanelOpen)
        {
            GraftPanelRow.Height = new GridLength(_graftPanelHeight);
            GraftSplitterRow.Height = GridLength.Auto;
            GraftSplitter.IsVisible = true;
        }
        else
        {
            GraftPanelRow.Height = new GridLength(GraftPanelHeaderHeight);
            GraftSplitterRow.Height = new GridLength(0);
            GraftSplitter.IsVisible = false;
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
                    _graftPanelHeight = SafeLength(GraftPanelRow.ActualHeight, _graftPanelHeight);
                }
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
        if (ViewModel.IsGraftPanelOpen)
        {
            _graftPanelHeight = SafeLength(GraftPanelRow.ActualHeight, _graftPanelHeight);
        }

        var paneLayout = ViewModel.Graft.GetCurrentPaneLayout();
        paneLayout.SideViewWidth = _sideViewWidth;
        paneLayout.GraftPanelHeight = _graftPanelHeight;

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
