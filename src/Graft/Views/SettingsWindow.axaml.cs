using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Graft.ViewModels;

namespace Graft.Views;

/// <summary>
/// 設定画面（仕様書14章）。DataContextには<see cref="SettingsViewModel"/>を受け取る。
/// v2.0のWPF版からの移植（19章 L3）。
///
/// 即時反映方式への移行に伴い、「閉じる」ボタン・Escapeキー・ウィンドウの×
/// （<see cref="Window.Closing"/>）の3経路が共有していた「未保存の変更を確認する」処理は
/// 撤去した。即時反映方式では変更のたびに（デバウンスを挟みつつ）保存されるため、
/// 「未保存の変更」という状態自体が存在しない。3経路とも、保留中の自動保存があれば
/// <see cref="SettingsViewModel.FlushPendingSaveAsync"/>で待たずに確定させてから、
/// 確認なしでそのままウィンドウを閉じる。<see cref="Window.Closing"/>は同期イベントで
/// 非同期の確定処理を待てないため、一旦<c>e.Cancel = true</c>で止め、確定が済んでから
/// <see cref="_closeApproved"/>を立てて改めて<see cref="Close()"/>を呼び直す構造は維持する
/// （3経路が同じ入口を通ることの検証価値は保存確認の有無に関わらず残るため）。
/// </summary>
public partial class SettingsWindow : Window
{
    // trueの間はClosingハンドラを素通りさせる（確定済み、または確定後の再Close呼び出し）。
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

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => _ = CloseAsync();

    private void OnTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) _ = CloseAsync();
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeApproved) return; // 確定済みの本物のClose呼び出しなのでそのまま閉じさせる

        // ×ボタンでのCloseは同期的に発火するため、ここでは一旦キャンセルし、
        // 非同期の確定処理（CloseAsync）を経てから改めてClose()する。
        e.Cancel = true;
        _ = CloseAsync();
    }

    /// <summary>
    /// 閉じるボタン・Escape・×の共通入口。保留中の自動保存（デバウンス待ち）があれば
    /// 待たずに確定させてから、実際にウィンドウを閉じる。
    /// </summary>
    private async Task CloseAsync()
    {
        if (_closeApproved) return; // 二重に呼ばれても再入しない

        if (DataContext is SettingsViewModel viewModel)
        {
            await SafeHandler.RunAsync(
                "設定を閉じる", () => viewModel.FlushPendingSaveAsync()).ConfigureAwait(true);
        }

        _closeApproved = true;
        Close();
    }
}
