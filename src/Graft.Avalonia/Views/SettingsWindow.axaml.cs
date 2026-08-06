using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Graft.ViewModels;

namespace Graft.Views;

/// <summary>
/// 設定画面（仕様書14章）。DataContextには<see cref="SettingsViewModel"/>を受け取る。
/// WPF版からの移植（19章 L3）。
/// </summary>
public partial class SettingsWindow : Window
{
    /// <summary>headlessテスト・デザイナ用の引数なしコンストラクタ。</summary>
    public SettingsWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnTunnelKeyDown, RoutingStrategies.Tunnel);
    }

    public SettingsWindow(SettingsViewModel viewModel) : this()
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        Loaded += async (_, _) =>
            await SafeHandler.RunAsync("設定画面の初期化", () => viewModel.InitializeAsync()).ConfigureAwait(true);
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();

    private void OnTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }
}
