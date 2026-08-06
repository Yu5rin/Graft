using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
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
        ProjectListBox.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        ProjectListBox.AddHandler(DragDrop.DropEvent, OnDrop);
    }

    /// <summary>F6でのペイン間フォーカス移動先として、外部（ShellWindow）から参照する。</summary>
    public ListBox ListBoxElement => ProjectListBox;

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>フォルダのドラッグ＆ドロップによる登録（仕様書3.2）。</summary>
    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not ProjectPaneViewModel viewModel) return;

        var items = e.Data.GetFiles();
        if (items is null) return;

        await SafeHandler.RunAsync("プロジェクトのドロップ登録", async () =>
        {
            foreach (var item in items)
            {
                var path = item.TryGetLocalPath();
                if (string.IsNullOrEmpty(path)) continue;

                var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folder))
                {
                    await viewModel.RegisterFolderAsync(folder).ConfigureAwait(true);
                }
            }
        }).ConfigureAwait(true);
    }
}
