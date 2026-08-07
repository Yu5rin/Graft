using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
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

    /// <summary>仕様書9.6/3.2: 終了時にウィンドウ位置・サイズ・最大化・ペイン寸法を保存する。</summary>
    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        var layout = ViewModel.Graft.Layout;
        layout.IsMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            layout.Left = Position.X;
            layout.Top = Position.Y;
            layout.Width = Width;
            layout.Height = Height;
        }

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

        ViewModel.Graft.SaveLayoutAsync().GetAwaiter().GetResult();
    }

    /// <summary>layout.json が壊れていた場合等にGridLengthが受け付けない値（NaN・0以下）を弾く。</summary>
    private static double SafeLength(double value, double fallback)
        => double.IsFinite(value) && value > 0 ? value : fallback;
}
