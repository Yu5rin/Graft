using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Graft.ViewModels;

namespace Graft.Views;

/// <summary>
/// 設定画面（仕様書14章）。DataContextには<see cref="SettingsViewModel"/>を受け取る。
/// v2.0のWPF版からの移植（19章 L3）。
///
/// バグ2の対応: 「閉じる」ボタン・Escapeキー・ウィンドウの×（<see cref="Window.Closing"/>）の
/// 3経路はすべて<see cref="RequestCloseAsync"/>へ集約し、未保存の変更確認とテーマプレビューの
/// 取り消し（<see cref="SettingsViewModel.RequestCloseAsync"/>）を必ず通す。
/// <see cref="Window.Closing"/>は同期イベントで非同期の確認を待てないため、確認前は一旦
/// <c>e.Cancel = true</c>で止め、確認が済んでから<see cref="_closeApproved"/>を立てて
/// 改めて<see cref="Close()"/>を呼び直す。
/// </summary>
public partial class SettingsWindow : Window
{
    // trueの間はClosingハンドラを素通りさせる（確認済み、または確認後の再Close呼び出し）。
    private bool _closeApproved;

    /// <summary>headlessテスト・デザイナ用の引数なしコンストラクタ。</summary>
    public SettingsWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnTunnelKeyDown, RoutingStrategies.Tunnel);
        Closing += OnClosing;
    }

    public SettingsWindow(SettingsViewModel viewModel) : this()
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        Loaded += async (_, _) =>
            await SafeHandler.RunAsync("設定画面の初期化", () => viewModel.InitializeAsync()).ConfigureAwait(true);
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => _ = RequestCloseAsync();

    private void OnTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) _ = RequestCloseAsync();
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeApproved) return; // 確認済みの本物のClose呼び出しなのでそのまま閉じさせる

        // ×ボタンでのCloseは同期的に発火するため、ここでは一旦キャンセルし、
        // 非同期の確認（RequestCloseAsync）を経てから改めてClose()する。
        e.Cancel = true;
        _ = RequestCloseAsync();
    }

    /// <summary>
    /// 閉じるボタン・Escape・×の共通入口。未保存の変更があれば
    /// <see cref="SettingsViewModel.RequestCloseAsync"/>で確認・保存/破棄を行い、
    /// 閉じてよい場合のみ実際にウィンドウを閉じる。
    /// </summary>
    private async Task RequestCloseAsync()
    {
        if (_closeApproved) return; // 二重に呼ばれても再入しない

        // 既定はtrue（想定外の例外時は閉じる方向へフェイルセーフする。設計目標5）。
        var shouldClose = true;
        if (DataContext is SettingsViewModel viewModel)
        {
            await SafeHandler.RunAsync("設定画面を閉じる確認", async () =>
            {
                shouldClose = await viewModel.RequestCloseAsync().ConfigureAwait(true);
            }).ConfigureAwait(true);
        }

        if (!shouldClose) return;

        _closeApproved = true;
        Close();
    }
}
