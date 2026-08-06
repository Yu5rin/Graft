using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Graft.ViewModels;

namespace Graft.Views;

/// <summary>
/// <see cref="SideViewKind"/> をConverterParameter（文字列）と比較し、一致するときのみ
/// <see cref="Visibility.Visible"/> を返す。サイドビューの4候補（エクスプローラ／プロジェクト／
/// 履歴／検索）を同じセルに重ねて切り替えるために使う（仕様書9.2）。
/// </summary>
public sealed class SideViewVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is SideViewKind kind && parameter is string target
            && Enum.TryParse<SideViewKind>(target, out var wanted) && kind == wanted)
        {
            return Visibility.Visible;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 新シェルレイアウトのメインウィンドウ（仕様書9.2）。サイドバー・サイドビュー・エディタ領域・
/// 接ぎ木パネル・ステータスバーを1つのGridに束ね、ウィンドウ位置・ペイン幅・パネル高さの
/// 復元/保存（9.6・3.2）を担う。キーボード操作（9.5・附録A）は
/// <see cref="ShellWindow.Keyboard.cs"/> に分割する（1ファイル400行上限のため）。
/// </summary>
public partial class ShellWindow : Window
{
    /// <summary>接ぎ木パネルが折りたたまれているときのヘッダーのみの高さ（GraftPanel.xamlと一致させる）。</summary>
    internal const double GraftPanelHeaderHeight = 32;

    // 9.2/3.2: サイドビュー幅・接ぎ木パネル高さの専用保存項目がWindowLayoutStoreに無いため、
    // ProjectPaneLayout の専用フィールド（SideViewWidth/GraftPanelHeight）に記憶する
    // （WindowLayoutStore.csは担当外ファイルのため変更していない。完了報告で共有する）。
    private double _sideViewWidth = 260;
    private double _graftPanelHeight = 260;

    public ShellWindow(ShellViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

        Loaded += OnLoaded;
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
        viewModel.PropertyChanged += OnShellViewModelPropertyChanged;
        viewModel.Graft.RequestOpenQueue += OnRequestOpenQueue;
        viewModel.Graft.RequestOpenContextCollect += OnRequestOpenContextCollect;
        viewModel.Graft.RequestFocusHistory += OnRequestFocusHistory;
    }

    private ShellViewModel ViewModel => (ShellViewModel)DataContext;

    /// <summary>4.10: キュー管理ウィンドウを開く（コマンドバー「キュー」ボタン）。</summary>
    private void OnRequestOpenQueue(object? sender, EventArgs e)
    {
        var window = new QueueWindow(ViewModel.Graft.Queue) { Owner = this };
        window.ShowDialog();
    }

    /// <summary>10章: コンテキスト収集ウィンドウを開く（コマンドバー「ファイル」ボタン）。</summary>
    private void OnRequestOpenContextCollect(object? sender, EventArgs e)
    {
        if (ViewModel.Graft.ContextCollect is null)
        {
            return;
        }
        var window = new ContextCollectWindow(ViewModel.Graft.ContextCollect) { Owner = this };
        window.ShowDialog();
    }

    /// <summary>9.2: 「履歴」ボタン・Ctrl+Shift+H。サイドバーの履歴ビューを開いて一覧へフォーカスする。</summary>
    private void OnRequestFocusHistory(object? sender, EventArgs e)
    {
        ViewModel.SelectSideView(SideViewKind.History);
        HistoryPaneControl.ListBoxElement.Focus();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.Graft.InitializeAsync().ConfigureAwait(true);
        ApplyLayoutToWindow();
    }

    /// <summary>仕様書9.6: 保存済みレイアウトをウィンドウ・ペインへ反映する。</summary>
    private void ApplyLayoutToWindow()
    {
        var layout = ViewModel.Graft.Layout;
        var bounds = WindowLayoutStore.ResolveWindowBounds(layout, MinWidth, MinHeight, ViewModel.Ui.Screens);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = bounds.Left;
        Top = bounds.Top;
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
            SideViewSplitter.Visibility = Visibility.Collapsed;
        }
        else
        {
            SideViewColumn.Width = new GridLength(_sideViewWidth);
            SideViewSplitter.Visibility = Visibility.Visible;
        }
    }

    /// <summary>接ぎ木パネルの折りたたみ・展開をRowDefinitionへ反映する。</summary>
    private void ApplyGraftPanelState()
    {
        if (ViewModel.IsGraftPanelOpen)
        {
            GraftPanelRow.Height = new GridLength(_graftPanelHeight);
            GraftSplitterRow.Height = GridLength.Auto;
            GraftSplitter.Visibility = Visibility.Visible;
        }
        else
        {
            GraftPanelRow.Height = new GridLength(GraftPanelHeaderHeight);
            GraftSplitterRow.Height = new GridLength(0);
            GraftSplitter.Visibility = Visibility.Collapsed;
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
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        var layout = ViewModel.Graft.Layout;
        layout.IsMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            layout.Left = Left;
            layout.Top = Top;
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
