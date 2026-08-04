using System.Windows.Controls;
using System.Windows.Input;
using Graft.ViewModels;

namespace Graft.Views;

/// <summary>
/// サイドビュー「検索」（4.4節・Ctrl+Shift+F）。結果ツリーの操作（ダブルクリック/Enterで
/// ジャンプ）のみを担い、検索の実体は<see cref="SearchViewModel"/>（ひいては
/// <c>Features/CrossFileSearch.cs</c>）に委ねる。DataContextは統合担当が
/// <see cref="SearchViewModel"/>を割り当てる想定。
/// </summary>
public partial class SearchView : UserControl
{
    public SearchView()
    {
        InitializeComponent();
    }

    private void OnQueryBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Return || DataContext is not SearchViewModel vm) return;
        if (vm.SearchCommand.CanExecute(null)) vm.SearchCommand.Execute(null);
        e.Handled = true;
    }

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        TryJumpToSelected();
    }

    private void OnResultKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Return) return;
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
