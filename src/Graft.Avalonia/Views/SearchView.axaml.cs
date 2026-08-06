using Avalonia.Controls;
using Avalonia.Input;
using Graft.ViewModels;

namespace Graft.Views;

/// <summary>
/// サイドビューの「検索」（仕様書4.4 ファイル横断検索）。DataContextには
/// <see cref="SearchViewModel"/>を親から割り当てる。WPF版からの移植（19章 L3）。
/// </summary>
public partial class SearchView : UserControl
{
    public SearchView()
    {
        InitializeComponent();

        // AvaloniaのTreeViewにはMouseDoubleClickに相当するイベントが無いためDoubleTappedを使う。
        ResultTree.DoubleTapped += (_, _) => TryJumpToSelected();
    }

    private void OnQueryBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not SearchViewModel vm) return;
        if (vm.SearchCommand.CanExecute(null)) vm.SearchCommand.Execute(null);
        e.Handled = true;
    }

    private void OnResultKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        TryJumpToSelected();
        e.Handled = true;
    }

    private void TryJumpToSelected()
    {
        if (DataContext is not SearchViewModel vm) return;
        if (ResultTree.SelectedItem is not SearchHitViewModel hit) return;
        if (vm.JumpCommand.CanExecute(hit)) vm.JumpCommand.Execute(hit);
    }
}
