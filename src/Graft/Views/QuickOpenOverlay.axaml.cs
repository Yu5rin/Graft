using Avalonia.Controls;
using Avalonia.Input;
using Graft.ViewModels;

namespace Graft.Views;

/// <summary>
/// クイックオープン（Ctrl+P）オーバーレイの見た目。DataContextには
/// <see cref="QuickOpenViewModel"/>をShellWindowから割り当てる。開閉・上下キー移動・
/// Enter確定・EscapeはShellWindow.Keyboard.csがQuickOpenViewModelを直接操作するため、
/// このコード・ビハインドはマウスクリックでの確定（<see cref="OnResultTapped"/>）と、
/// ShellWindowから参照するフォーカス対象（<see cref="QueryBoxElement"/>）のみを担う。
/// </summary>
public partial class QuickOpenOverlay : UserControl
{
    public QuickOpenOverlay()
    {
        InitializeComponent();
    }

    /// <summary>ShellWindowがオーバーレイを開いた直後にフォーカスする対象。</summary>
    public TextBox QueryBoxElement => QueryBox;

    /// <summary>仕様書「マウスクリックでも確定」。候補行のクリックで選択と同時に確定する。</summary>
    private void OnResultTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not StackPanel { DataContext: QuickOpenResultItem item }) return;
        if (DataContext is not QuickOpenViewModel viewModel) return;

        viewModel.SelectedResult = item;
        viewModel.ConfirmSelection();
    }
}
