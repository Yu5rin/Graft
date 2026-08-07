using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Graft.ViewModels;

namespace Graft.Views;

/// <summary>
/// 接ぎ木パネル（仕様書9.2 下部パネル）。9.2の全面改訂によりdiffはエディタ領域の専用タブへ
/// 移設したため、本パネルはブロック一覧・適用サマリ・操作ボタンに絞られる。開閉状態
/// そのものはShellViewModel.IsGraftPanelOpen（XAML側でElementName="Root"を介して参照）に
/// 従うため、コードビハインドはF6ペイン巡回用の参照公開と、4.1節
/// 「ファイルからのパッチ解析」のドラッグ＆ドロップ受付を担う。
/// </summary>
public partial class GraftPanel : UserControl
{
    public GraftPanel()
    {
        InitializeComponent();

        // AllowDropとイベント購読はProjectPane.axaml.csと同じ作法（添付イベントのためXAML属性
        // 構文では指定できず、ここで行う）。パネル全体（Root自身）をドロップ対象にする。
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    /// <summary>F6のペイン巡回・Space判定で使うブロック一覧。</summary>
    public ListBox ListBoxElement => BlockListBox;

    /// <summary>
    /// F6のペイン巡回の4番目の停留先。diffがエディタ領域のタブへ移設され、このパネル内に
    /// diff表示が無くなったため、代わりに本パネル内で最後の操作要素（適用ボタン）を指す。
    /// </summary>
    public Control DiffHost => ApplyButtonElement;

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>
    /// テキストファイルのドラッグ＆ドロップによる解析（仕様書4.1）。複数ファイルをドロップした
    /// 場合は先頭の1件のみを対象にする。バイナリ・1MB超の拒否はMainViewModel側で行う。
    /// </summary>
    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not ShellViewModel shell) return;

        var items = e.Data.GetFiles();
        var first = items?.FirstOrDefault();
        var path = first?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        await SafeHandler.RunAsync("パッチファイルのドロップ解析", async () =>
        {
            await shell.Graft.LoadPatchFromFileAsync(path).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }
}
