using System.ComponentModel;
using Avalonia.Interactivity;
using AvaloniaEdit.Document;
using Graft.Features;
using Graft.ViewModels;

namespace Graft.Views;

/// <summary>
/// <see cref="EditorPane"/> の分割ファイル（1ファイル400行上限のため）。
/// エディタ本文（AvaloniaEdit の TextArea）の右クリックメニューを担う。「切り取り」「コピー」
/// 「貼り付け」は AvaloniaEdit の <see cref="AvaloniaEdit.TextEditor"/> 自身のメソッドをそのまま
/// 呼ぶだけに留め、「選択範囲の修正依頼プロンプトをコピー」は「AIに聞く→貼る→接ぎ木」の往復を
/// 最短にするための機能で、プロンプトの組み立て自体は
/// <see cref="PromptTemplateStore.BuildSelectionFixRequestPrompt"/>（UIに依存しない純粋メソッド）
/// へ委譲する。差分タブ表示中はEditor自体が非表示（<see cref="ApplyDiffTab"/>）のため右クリック
/// メニューに到達できないが、念のためここでも選択の有無とタブ種別の両方を確認する。
/// </summary>
public partial class EditorPane
{
    /// <summary>右クリックメニューを開く直前。選択が無ければ修正依頼項目を無効化する。</summary>
    private void OnEditorContextMenuOpening(object? sender, CancelEventArgs e)
    {
        var hasSelection = _viewModel?.ActiveTab is { Kind: EditorTabKind.Document } && Editor.SelectionLength > 0;
        CopyFixRequestPromptMenuItem.IsEnabled = hasSelection;
    }

    private void OnCutClicked(object? sender, RoutedEventArgs e) => Editor.Cut();

    private void OnCopyClicked(object? sender, RoutedEventArgs e) => Editor.Copy();

    private void OnPasteClicked(object? sender, RoutedEventArgs e) => Editor.Paste();

    /// <summary>
    /// 選択範囲から修正依頼プロンプトを組み立ててクリップボードへコピーする。選択が無い、または
    /// 差分タブ表示中（<see cref="EditorTabKind.Diff"/>）は何もしない。
    /// </summary>
    private void OnCopyFixRequestPromptClicked(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not { ActiveTab: { Kind: EditorTabKind.Document } tab }) return;
        if (Editor.SelectionLength <= 0) return;

        var (startLine, endLine) = SelectionLineRange(Editor.Document, Editor.SelectionStart, Editor.SelectionLength);
        var extension = Path.GetExtension(tab.Session.FileName);
        var prompt = PromptTemplateStore.BuildSelectionFixRequestPrompt(
            tab.Session.RelativePath, startLine, endLine, Editor.SelectedText, extension);

        _viewModel.Ui.Clipboard.SetText(prompt);
    }

    /// <summary>
    /// 選択範囲の開始行・終了行（1始まり）を求める。終了行は選択末尾の文字（選択長が0より大きい
    /// 前提のため必ず1文字は含む）が属する行とする。これにより「次の行の先頭（列1）」でマウスを
    /// 離した選択でも、実際に選んでいない行を終了行に含めない。
    /// </summary>
    private static (int Start, int End) SelectionLineRange(TextDocument document, int selectionStart, int selectionLength)
    {
        var startLine = document.GetLineByOffset(selectionStart).LineNumber;
        var lastCharOffset = Math.Max(selectionStart, selectionStart + selectionLength - 1);
        var endLine = document.GetLineByOffset(lastCharOffset).LineNumber;
        return (startLine, endLine);
    }
}
