using System.Windows.Input;
using Graft.Platform;

namespace Graft.ViewModels;

/// <summary>
/// <see cref="EditorPaneViewModel"/> の分割ファイル（1ファイル400行上限のため）。
/// エディタタブ見出しの右クリックメニュー（「閉じる」は<see cref="EditorTabViewModel.CloseCommand"/>を
/// そのまま使うため対象外）を担う。「フルパスをコピー」「ファイルマネージャで表示」は
/// 差分タブ（<see cref="EditorTabKind.Diff"/>）には適用できないため対象外とする。
/// </summary>
public sealed partial class EditorPaneViewModel
{
    /// <summary>タブ見出し右クリックメニュー「他のタブを閉じる」。</summary>
    public ICommand CloseOtherTabsCommand { get; private set; } = null!;

    /// <summary>D: タブ見出し右クリックメニュー「右側のタブを閉じる」。</summary>
    public ICommand CloseTabsToTheRightCommand { get; private set; } = null!;

    /// <summary>タブ見出し右クリックメニュー「すべてのタブを閉じる」。</summary>
    public ICommand CloseAllTabsCommand { get; private set; } = null!;

    /// <summary>タブ見出し右クリックメニュー「フルパスをコピー」。</summary>
    public ICommand CopyFullPathCommand { get; private set; } = null!;

    /// <summary>タブ見出し右クリックメニュー「ファイルマネージャで表示」。</summary>
    public ICommand RevealInFileManagerCommand { get; private set; } = null!;

    /// <summary>コンストラクタから呼び出し、右クリックメニュー用コマンドを組み立てる。</summary>
    private void InitializeTabActionCommands()
    {
        CloseOtherTabsCommand = new RelayCommand<EditorTabViewModel>(tab =>
        {
            if (tab is not null) _ = CloseOthersAsync(tab);
        });
        // D: 「右側のタブを閉じる」。並べ替え（ドラッグ）後も見た目どおりの右側になるよう、
        // 常にTabsコレクション（表示順そのもの）上でのインデックスを基準にする。
        CloseTabsToTheRightCommand = new RelayCommand<EditorTabViewModel>(
            tab => { if (tab is not null) _ = CloseTabsToTheRightAsync(tab); },
            tab => tab is not null && HasTabsToTheRight(tab));
        CloseAllTabsCommand = new RelayCommand(() => _ = CloseAllAsync());
        CopyFullPathCommand = new RelayCommand<EditorTabViewModel>(tab =>
        {
            // IClipboardAccess.SetTextは失敗しても例外を投げない契約のため、ここでの保護は不要。
            if (tab is { Kind: EditorTabKind.Document }) Ui.Clipboard.SetText(tab.Session.FullPath);
        });
        RevealInFileManagerCommand = new RelayCommand<EditorTabViewModel>(tab =>
        {
            if (tab is { Kind: EditorTabKind.Document }) PlatformServices.Current.FileManager.Reveal(tab.Session.FullPath);
        });
    }

    /// <summary>
    /// タブ見出し右クリックメニュー「他のタブを閉じる」。<paramref name="keep"/>以外のタブを
    /// <see cref="CloseTabAsync"/>で順に閉じる。保存確認でキャンセルされた時点で中断し、falseを返す。
    /// </summary>
    public async Task<bool> CloseOthersAsync(EditorTabViewModel keep)
    {
        foreach (var tab in Tabs.Where(t => !ReferenceEquals(t, keep)).ToList())
        {
            if (!await CloseTabAsync(tab).ConfigureAwait(true)) return false;
        }
        return true;
    }

    /// <summary>
    /// D: タブ見出し右クリックメニュー「右側のタブを閉じる」。「他のタブを閉じる」
    /// （<see cref="CloseOthersAsync"/>）と同じ作法（保存確認でキャンセルされた時点で中断しfalseを
    /// 返す）で、<paramref name="from"/>より右側（<see cref="Tabs"/>上でより後ろ）のタブだけを
    /// 対象にする。閉じる対象を先に <c>ToList()</c> で確定してから1件ずつ閉じるため、閉じるたびに
    /// Tabsが縮んでインデックスがずれても取りこぼさない。
    /// </summary>
    public async Task<bool> CloseTabsToTheRightAsync(EditorTabViewModel from)
    {
        var index = Tabs.IndexOf(from);
        if (index < 0) return true;

        foreach (var tab in Tabs.Skip(index + 1).ToList())
        {
            if (!await CloseTabAsync(tab).ConfigureAwait(true)) return false;
        }
        return true;
    }

    /// <summary>右側（表示順でより後ろ）に閉じられるタブが1件でもあるかどうか（CanExecute用）。</summary>
    private bool HasTabsToTheRight(EditorTabViewModel tab)
    {
        var index = Tabs.IndexOf(tab);
        return index >= 0 && index < Tabs.Count - 1;
    }
}
