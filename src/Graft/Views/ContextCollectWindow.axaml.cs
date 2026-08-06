using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Graft.ViewModels;

namespace Graft.Views;

/// <summary>
/// コンテキスト収集ウィンドウ（仕様書10章）。DataContextには
/// <see cref="ContextCollectViewModel"/>を受け取る。v2.0のWPF版からの移植（19章 L3）。
/// </summary>
public partial class ContextCollectWindow : Window
{
    /// <summary>headlessテスト・デザイナ用の引数なしコンストラクタ。</summary>
    public ContextCollectWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnTunnelKeyDown, RoutingStrategies.Tunnel);
    }

    public ContextCollectWindow(ContextCollectViewModel viewModel) : this()
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        Loaded += async (_, _) =>
            await SafeHandler.RunAsync("コンテキスト収集の初期化", () => viewModel.InitializeAsync())
                .ConfigureAwait(true);
    }

    private void OnTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }
}
