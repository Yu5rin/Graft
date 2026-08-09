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

        DataContextChanged += OnDataContextChanged;
    }

    private ExplorerViewModel? ViewModel => DataContext as ExplorerViewModel;

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ViewModel is not { SelectedNode: { } node } vm) return;
        if (vm.OpenCommand.CanExecute(node)) vm.OpenCommand.Execute(node);
        e.Handled = true;
    }

    // 課題2: DataContextは通常ShellWindow構築時に一度だけ割り当てられ、以後変わらない想定だが、
    // 念のため差し替え時に購読を張り替える（二重購読・古いDataContextへの購読残りの防止）。
    private ExplorerViewModel? _subscribedViewModel;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_subscribedViewModel is not null) _subscribedViewModel.FocusRequested -= OnFocusRequested;
        _subscribedViewModel = ViewModel;
        if (_subscribedViewModel is not null) _subscribedViewModel.FocusRequested += OnFocusRequested;
    }

    /// <summary>
    /// 課題2: 削除・削除の取り消し（Ctrl+Z）の直後は、ツリーの再構築
    /// （ExplorerViewModel.ReconcileDirectoryAsync/RefreshAsync）で選択中だった項目の
    /// コンテナ（TreeViewItem）が作り直され、フォーカスを失う（対象自身が消える・
    /// 復元されるだけでなく、兄弟のコンテナも<see cref="Collections.ObjectModel.ObservableCollection{T}.Clear"/>
    /// のReset通知で丸ごと作り直されるため）。エクスプローラにフォーカスがある状態での
    /// 連続したCtrl+Z（ShellWindow.Keyboard.cs・ExplorerView.axamlのTreeView.KeyBindings）が
    /// 引き続き届くよう、<see cref="ExplorerViewModel.FocusRequested"/>のたびにツリー自体へ
    /// フォーカスを戻す。
    /// </summary>
    private void OnFocusRequested(object? sender, EventArgs e) => FileTreeView.Focus();
}
