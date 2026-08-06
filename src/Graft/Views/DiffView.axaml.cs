using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Graft.ViewModels;

namespace Graft.Views;

/// <summary>
/// 仕様書8.7 diffプレビュー本体。DataContext に <see cref="DiffViewModel"/> を受け取る。
/// v2.0のWPF版からの移植（19章 L3）。
/// </summary>
public partial class DiffView : UserControl
{
    public DiffView()
    {
        InitializeComponent();

        // 8.4: コード表示のフォントサイズはCtrl+マウスホイールで変更する（プロジェクトごとの
        // 記憶自体はDiffViewModel.CodeFontSizeの変更を受けてシェル側が行う）。
        // AvaloniaにPreviewMouseWheelは無いため、トンネリング段階でPointerWheelChangedを拾う。
        AddHandler(PointerWheelChangedEvent, OnPointerWheelChanged, RoutingStrategies.Tunnel);
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.Control) return;
        if (DataContext is not DiffViewModel vm) return;

        vm.CodeFontSize += e.Delta.Y > 0 ? 1 : -1;
        e.Handled = true;
    }
}
