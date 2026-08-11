using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
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

        // 利用者の指摘「ファイルをボタンやドラッグ＆ドロップでエクスプローラーに追加できない」
        // への対応。AllowDropとイベント購読はProjectPane.axaml.cs・GraftPanel.axaml.csと同じ作法
        // （添付イベントのためXAML属性構文では指定できず、ここで行う）。
        DragDrop.SetAllowDrop(FileTreeView, true);
        FileTreeView.AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        FileTreeView.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        FileTreeView.AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        FileTreeView.AddHandler(DragDrop.DropEvent, OnDrop);
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

    // ------------------------------------------------------------------
    // 依頼「エクスプローラへ既存のファイルを取り込む手段」1: ドラッグ＆ドロップ。
    // ProjectPane.axaml.cs・GraftPanel.axaml.csと同じ作法（DragEnter/DragOverでDragDropEffects・
    // 視覚的フィードバックを設定し、Dropで実処理をExplorerViewModelへ委譲する）に揃える。
    // 本ビュー固有の点は、落とした「先」がツリー上のどのフォルダかをポインタ位置から判定し、
    // そのフォルダだけを強調表示すること（要件: 「ドラッグ中に配置先が分かる視覚的フィードバック」）。
    // ------------------------------------------------------------------

    /// <summary>現在ハイライト中の配置先フォルダ（nullはプロジェクトルート＝FileTreeView自体を強調）。</summary>
    private FileNodeViewModel? _currentDropHighlight;
    private bool _isHighlightingRoot;

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (!e.Data.Contains(DataFormats.Files)) return;
        UpdateDropHighlight(e);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (!e.Data.Contains(DataFormats.Files))
        {
            e.DragEffects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        // 依頼: 「移動ではなくコピー」を常に強制する（Windowsの慣習に従わない）。
        // ExplorerViewModel.ImportPathsAsync側の判断とあわせて、ドラッグ中のカーソル表示の
        // 時点からも一貫して「コピー」であることを示す。
        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
        UpdateDropHighlight(e);
    }

    private void OnDragLeave(object? sender, RoutedEventArgs e) => ClearDropHighlight();

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var targetNode = FindHitNode(e);
        ClearDropHighlight();
        if (ViewModel is not { } vm) return;

        var items = e.Data.GetFiles();
        if (items is null) return;
        var paths = items.Select(i => i.TryGetLocalPath())
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!)
            .ToList();
        if (paths.Count == 0) return;

        await SafeHandler.RunAsync("ファイルのドロップ取り込み",
            () => vm.ImportPathsAsync(targetNode, paths)).ConfigureAwait(true);
    }

    /// <summary>
    /// ポインタ位置の下にあるノードを配置先として強調する。既に同じ対象を強調済みならなにもしない
    /// （DragOverはドラッグ中ずっと発火し続けるため）。ヒットしたのがファイルの場合は、実際の
    /// 配置先（依頼の要件: 「ファイルの上に落とされた場合はその親フォルダへ」）に合わせて
    /// その親フォルダを強調する（ExplorerViewModel.ResolveTargetDirectoryと同じ規約。
    /// ドロップ実処理自体は生のヒット結果をそのままViewModelへ渡し、そちらで解決するため、
    /// ここでの解決は表示専用のごく単純な判定にとどめている）。
    /// </summary>
    private void UpdateDropHighlight(DragEventArgs e)
    {
        var hit = FindHitNode(e);
        var node = hit is null ? null : hit.IsDirectory ? hit : hit.Parent;
        if (ReferenceEquals(node, _currentDropHighlight)) return;

        ClearDropHighlight();
        _currentDropHighlight = node;
        if (node is not null)
        {
            node.IsDropTarget = true;
        }
        else
        {
            _isHighlightingRoot = true;
            FileTreeView.Classes.Set("dragOverRoot", true);
        }
    }

    private void ClearDropHighlight()
    {
        if (_currentDropHighlight is not null) _currentDropHighlight.IsDropTarget = false;
        _currentDropHighlight = null;
        if (_isHighlightingRoot)
        {
            _isHighlightingRoot = false;
            FileTreeView.Classes.Set("dragOverRoot", false);
        }
    }

    /// <summary>
    /// ドラッグ中のポインタ直下にあるツリー項目を返す。ProjectPane.axaml.csの
    /// OnProjectListPointerPressedと同じ考え方で、ルーティングされたイベントのSourceから
    /// 祖先のTreeViewItemを辿り、そのDataContext（FileNodeViewModel）を取り出す。
    /// 何もヒットしない（ツリーの余白）場合はnull（＝プロジェクトルートへ配置）。
    /// 返すノードはファイル・フォルダのどちらもありうる（実際にどのフォルダへ配置するかの
    /// 解決 = ファイルならその親、はExplorerViewModel.ImportPathsAsyncが
    /// NewFileCommand等と同じResolveTargetDirectoryで行う。ここでは純粋にヒットテストのみ）。
    /// </summary>
    private FileNodeViewModel? FindHitNode(DragEventArgs e)
    {
        var hit = FindAncestor<TreeViewItem>(e.Source as Visual);
        return hit?.DataContext as FileNodeViewModel;
    }

    private static T? FindAncestor<T>(Visual? node) where T : Visual
    {
        while (node is not null and not T)
        {
            node = node.GetVisualParent();
        }

        return node as T;
    }
}
