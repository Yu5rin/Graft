using Avalonia.Controls;
using Avalonia.Input;
using Graft.ViewModels;

namespace Graft.Views;

/// <summary>
/// コマンドパレット（Ctrl+Shift+P）オーバーレイの見た目。DataContextには
/// <see cref="CommandPaletteViewModel"/>をShellWindowから割り当てる。既存の
/// <see cref="QuickOpenOverlay"/>と同じ役割分担: 開閉・上下キー移動・Enter確定・Escapeは
/// ShellWindow.Keyboard.csがCommandPaletteViewModelを直接操作するため、このコード・ビハインドは
/// マウスクリックでの確定（<see cref="OnResultTapped"/>）と、ShellWindowから参照する
/// フォーカス対象（<see cref="QueryBoxElement"/>）のみを担う。
/// </summary>
public partial class CommandPaletteOverlay : UserControl
{
    public CommandPaletteOverlay()
    {
        InitializeComponent();
    }

    /// <summary>ShellWindowがオーバーレイを開いた直後にフォーカスする対象。</summary>
    public TextBox QueryBoxElement => QueryBox;

    /// <summary>マウスクリックで選択と同時に確定する（QuickOpenOverlay.OnResultTappedと同じ考え方）。</summary>
    private void OnResultTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: CommandPaletteItem item }) return;
        if (DataContext is not CommandPaletteViewModel viewModel) return;

        viewModel.SelectedResult = item;
        viewModel.ConfirmSelection();
    }
}
