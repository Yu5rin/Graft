using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Graft.ViewModels;

namespace Graft.Views;

/// <summary>
/// 修正1: 履歴差分タブの中身。DataContextには<see cref="HistoryDiffViewModel"/>を受け取る。
/// ファイルごとに独立した<see cref="DiffView"/>を並べる構造のため、4.8「diffからジャンプ」の
/// ダブルタップは、タップされた行の祖先にある<see cref="DiffView"/>自身のDataContext
/// （そのファイル専用のDiffViewModel）を辿って処理する（EditorPane.Diff.axaml.csの
/// OnDiffDoubleTappedは単一のDiffHostを前提にしているため、ここでは個別に実装する）。
/// </summary>
public partial class HistoryDiffView : UserControl
{
    public HistoryDiffView()
    {
        InitializeComponent();
        AddHandler(DoubleTappedEvent, OnDoubleTapped);
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        var source = e.Source as Visual;
        if (FindAncestor<DiffView>(source)?.DataContext is not DiffViewModel vm) return;
        if (FindDiffRow(source) is not { } row) return;

        vm.RequestJump(row);
        e.Handled = true;
    }

    private static DiffLineViewModel? FindDiffRow(Visual? source)
    {
        while (source is not null)
        {
            if (source is StyledElement { DataContext: DiffLineViewModel row }) return row;
            source = source.GetVisualParent();
        }
        return null;
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
