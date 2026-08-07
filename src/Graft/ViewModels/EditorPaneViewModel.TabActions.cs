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
}
