using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Graft.ViewModels;

namespace Graft.Views;

/// <summary>
/// 左ペイン上段「プロジェクト一覧」（仕様書3.2・8.2）。
/// D&Dまたはフォルダ選択ボタンでプロジェクトを登録できる。DataContextには
/// <see cref="ProjectPaneViewModel"/> を親（ShellWindow）から割り当てる。
/// v2.0のWPF版からの移植（19章 L3）。ドラッグ＆ドロップはWPFの
/// <c>DataFormats.FileDrop</c>＋<c>string[]</c>ではなく、Avaloniaの
/// <see cref="DataFormats.Files"/>＋<c>IStorageItem</c>列挙で受け取る。
/// </summary>
public partial class ProjectPane : UserControl
{
    public ProjectPane()
    {
        InitializeComponent();

        // AllowDropとイベント購読はXAMLではなくここで行う。AvaloniaのDragDropは
        // 添付イベント（DragDrop.DragEnterEvent等）であり、XAMLの属性構文では
        // ハンドラを直接指定できないため。
        DragDrop.SetAllowDrop(ProjectListBox, true);
        ProjectListBox.AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        ProjectListBox.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        ProjectListBox.AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        ProjectListBox.AddHandler(DragDrop.DropEvent, OnDrop);

        // 要望3: 右クリックメニュー。既定では右クリックだけでは選択行が変わらないため
        // （左クリックのみ選択を更新する）、ContextMenuが開く前に明示的にその行を選択状態へ
        // 更新する（HistoryPane.axaml.csのOnRevisionListPointerPressedと同じトンネル段階での
        // 先取り処理）。
        ProjectListBox.AddHandler(PointerPressedEvent, OnProjectListPointerPressed, RoutingStrategies.Tunnel);

        // 要望2: プロジェクト名のダブルクリックでエクスプローラへ切り替える。単クリックの
        // 既存挙動（ListBoxのSelectedItemバインドによる選択）はそのまま変えず、ダブルタップの
        // 通知だけを追加する。
        ProjectListBox.DoubleTapped += OnDoubleTapped;
    }

    /// <summary>F6でのペイン間フォーカス移動先として、外部（ShellWindow）から参照する。</summary>
    public ListBox ListBoxElement => ProjectListBox;

    private void OnProjectListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(ProjectListBox);
        if (!point.Properties.IsRightButtonPressed) return;
        if (FindAncestor<ListBoxItem>(e.Source as Visual) is not { DataContext: ProjectListItemViewModel item }) return;

        ProjectListBox.SelectedItem = item;
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not ProjectPaneViewModel viewModel) return;
        if (FindAncestor<ListBoxItem>(e.Source as Visual) is not { DataContext: ProjectListItemViewModel item }) return;

        // ダブルタップの前段で単クリック相当の選択も届くため、通常はProjectListBox.SelectedItemが
        // 既にitemを指しているはずだが、念のため明示的に揃えてからアクティブ化を通知する。
        ProjectListBox.SelectedItem = item;
        viewModel.NotifyActivated(item.Project);
    }

    private static T? FindAncestor<T>(Visual? node) where T : Visual
    {
        while (node is not null and not T)
        {
            node = node.GetVisualParent();
        }

        return node as T;
    }

    // 要望7: ドラッグ中に「ここに落とせる」ことが視覚的に分かるよう、フォルダを含む
    // ドラッグ操作の間だけListBoxへdragOverクラスを付ける（背景色が変わるスタイルは
    // ProjectPane.axaml側で定義）。DragEnter/DragLeaveの対（Overは毎回発火するため
    // ここでは付け外ししない）で管理する。
    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (!e.Data.Contains(DataFormats.Files)) return;
        ProjectListBox.Classes.Set("dragOver", true);
    }

    private void OnDragLeave(object? sender, RoutedEventArgs e) => ProjectListBox.Classes.Set("dragOver", false);

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>フォルダのドラッグ＆ドロップによる登録（仕様書3.2）。複数フォルダを一括登録できる。
    /// ファイルが落とされた場合は、その親フォルダを登録する（フォルダと同じ扱いに揃え、
    /// 「対応していません」という素っ気ない拒否より親切なため）。</summary>
    private async void OnDrop(object? sender, DragEventArgs e)
    {
        ProjectListBox.Classes.Set("dragOver", false);
        if (DataContext is not ProjectPaneViewModel viewModel) return;

        var items = e.Data.GetFiles();
        if (items is null) return;

        await SafeHandler.RunAsync("プロジェクトのドロップ登録", async () =>
        {
            foreach (var item in items)
            {
                var folder = ResolveDropTarget(item.TryGetLocalPath());
                if (!string.IsNullOrEmpty(folder))
                {
                    await viewModel.RegisterFolderAsync(folder).ConfigureAwait(true);
                }
            }
        }).ConfigureAwait(true);
    }

    /// <summary>
    /// 要望7: ドロップされた1件のローカルパスから、実際に登録すべきフォルダを決める。
    /// フォルダそのものが落とされた場合はそのまま、ファイルが落とされた場合はその親フォルダを
    /// 返す（「対応していません」と素っ気なく拒否するより、フォルダと同じ扱いに揃えて登録できた
    /// 方が親切なため）。どちらも取れない場合はnull。
    /// DragEventArgsを直接使わないpublic staticな形にしているのは、Avaloniaのドラッグ＆ドロップの
    /// 実イベント（IDataObject等）をテストから安定して合成するのが難しいため、この判定ロジック
    /// 自体だけを独立してテストできるようにするための切り出し（ProjectPaneDragDropTests参照）。
    /// </summary>
    public static string? ResolveDropTarget(string? localPath)
    {
        if (string.IsNullOrEmpty(localPath)) return null;
        return Directory.Exists(localPath) ? localPath : Path.GetDirectoryName(localPath);
    }
}
