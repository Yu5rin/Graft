using Avalonia.Controls;
using Avalonia.Input;
using Graft.ViewModels;

namespace Graft.Views;

/// <summary>
/// サイドビューの「エクスプローラ」（仕様書4.2）。プロジェクトルート配下のファイルツリー表示・
/// 操作・監視反映を行う。DataContextには<see cref="ExplorerViewModel"/>を親から割り当てる。
/// v2.0のWPF版からの移植（19章 L3）。SelectedItemがAvaloniaでは双方向バインド可能なため、
/// v2.0のWPF版にあった選択の橋渡しコードは不要になった。
/// </summary>
public partial class ExplorerView : UserControl
{
    public ExplorerView()
    {
        InitializeComponent();

        // ダブルクリックで開く。AvaloniaのTreeViewItemにはMouseDoubleClickに相当する
        // イベントが無いため、TreeView全体でDoubleTappedを拾い、選択中のノードを開く。
        // イベントは祖先へバブリングするが、選択されているノードは常に1つのため
        // v2.0のWPF版のような「選択状態のアイテムだけ処理する」判定は不要。
        FileTreeView.DoubleTapped += OnDoubleTapped;
    }

    private ExplorerViewModel? ViewModel => DataContext as ExplorerViewModel;

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ViewModel is not { SelectedNode: { } node } vm) return;
        if (vm.OpenCommand.CanExecute(node)) vm.OpenCommand.Execute(node);
        e.Handled = true;
    }
}
