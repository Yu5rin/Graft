using Avalonia;
using Avalonia.Input;
using Avalonia.VisualTree;
using AvaloniaEdit.Document;
using Graft.ViewModels;

namespace Graft.Views;

/// <summary>
/// <see cref="EditorPane"/> の分割ファイル（1ファイル400行上限のため）。
/// 仕様書9.2「diffのエディタタブ化」・4.8「diffからジャンプ」を担う。DiffHost（DiffView）の
/// 表示切替はここで行うが、diffの折りたたみ・並列/統合表示・空白可視化・文字単位ハイライト・
/// インライン編集そのものはDiffView/DiffViewModelをそのまま再利用する。
/// </summary>
public partial class EditorPane
{
    /// <summary>タブが無い状態（9.2）。エディタ・DiffHost・HistoryDiffHostのいずれも空にする。</summary>
    private void ApplyEmptyTab()
    {
        // 課題#82: ApplyDocumentTabと同じ理由でDocument代入の前に外す
        // （FoldingSupport.PrepareForDocumentSwapのクラスコメント【課題#82】節参照）。
        _folding.PrepareForDocumentSwap();
        // 課題#72: ApplyDocumentTabと同じく、この代入を契機にWrapIndentSupportが自動で
        // 入れ直す（WrapIndentSupportのクラスコメント参照）。
        Editor.Document = new TextDocument();
        Editor.IsEnabled = false;
        Editor.IsVisible = true;
        MarkdownPreviewHost.IsVisible = false;
        DiffHost.IsVisible = false;
        DiffHost.DataContext = null;
        HistoryDiffHost.IsVisible = false;
        HistoryDiffHost.DataContext = null;
        ApplyWordWrapOption();
        _bridge.Attach(Editor.Document, string.Empty, syntaxEnabled: false);
        _brackets.Attach(Editor.Document, string.Empty);
        _folding.Attach(Editor.Document, string.Empty);
        _markdownColorizer.SetEnabled(false);
        if (_viewModel is not null) Search.Attach(Editor, _viewModel.Ui);
    }

    /// <summary>
    /// 9.2/4.8: 差分タブの表示。エディタは隠し（読み取り専用のため編集対象にしない）、
    /// DiffHost（DiffView）へDataContextを渡すだけに留める。
    /// </summary>
    private void ApplyDiffTab(EditorTabViewModel tab)
    {
        Editor.IsVisible = false;
        Editor.IsEnabled = false;
        MarkdownPreviewHost.IsVisible = false;
        _markdownColorizer.SetEnabled(false);
        DiffHost.IsVisible = true;
        DiffHost.DataContext = tab.Diff;
        HistoryDiffHost.IsVisible = false;
        HistoryDiffHost.DataContext = null;
    }

    /// <summary>
    /// 修正1: 履歴差分タブの表示。エディタ・通常のDiffHostはともに隠し、HistoryDiffHost
    /// （HistoryDiffView）へDataContextを渡すだけに留める。
    /// </summary>
    private void ApplyHistoryDiffTab(EditorTabViewModel tab)
    {
        Editor.IsVisible = false;
        Editor.IsEnabled = false;
        MarkdownPreviewHost.IsVisible = false;
        _markdownColorizer.SetEnabled(false);
        DiffHost.IsVisible = false;
        DiffHost.DataContext = null;
        HistoryDiffHost.IsVisible = true;
        HistoryDiffHost.DataContext = tab.HistoryDiff;
    }

    /// <summary>
    /// 4.8「diffからジャンプ」: diff表示の行をダブルクリックすると、該当ファイルの該当行を
    /// エディタで開く。ダブルクリックされた行のDataContext（<see cref="DiffLineViewModel"/>）を
    /// 突き止めて<see cref="DiffViewModel.RequestJump"/>へ渡すだけに留める（実際にファイルを
    /// 開く処理はDiffViewModel.JumpRequestedを購読するShellViewModel側で行う）。省略行の
    /// 展開ボタンは自身でクリックを処理する（Handled）ため、ここへは届かない。
    /// </summary>
    private void OnDiffDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DiffHost.DataContext is not DiffViewModel vm) return;
        if (FindDiffRow(e.Source as Visual) is not { } row) return;

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
}
