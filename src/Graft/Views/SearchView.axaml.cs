using Avalonia.Controls;
using Avalonia.Input;
using Graft.ViewModels;

namespace Graft.Views;

/// <summary>
/// サイドビューの「検索」（仕様書4.4 ファイル横断検索）。DataContextには
/// <see cref="SearchViewModel"/>を親から割り当てる。v2.0のWPF版からの移植（19章 L3）。
/// </summary>
public partial class SearchView : UserControl
{
    public SearchView()
    {
        InitializeComponent();

        // 仕様書4.4「クリックで該当行へジャンプ」。単一クリック・キーボードの上下移動は
        // いずれもTreeViewの選択変更として届くため、SelectionChangedで一括してジャンプする
        // （VS Code同等の挙動）。ダブルクリックは選択変更の後に届くため自然に内包されるが、
        // 挙動を明示するためDoubleTappedのハンドラも維持する。
        ResultTree.SelectionChanged += (_, _) => TryJumpToSelected();
        ResultTree.DoubleTapped += (_, _) => TryJumpToSelected();
    }

    /// <summary>ShellWindowが検索ビュー表示時にフォーカスを合わせる対象（仕様書4.4）。</summary>
    public TextBox QueryBoxElement => QueryBox;

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
