using Graft.Editor;

namespace Graft.ViewModels;

/// <summary>
/// <see cref="EditorPaneViewModel"/> の分割ファイル（1ファイル400行上限のため）。
///
/// 製品としての使い勝手3件のうち機能3: Ctrl+Shift+T（キー配線はViews/ShellWindow.Keyboard.cs、
/// ブラウザの「閉じたタブを開き直す」と同じ操作感）で、直前に閉じたタブを新しい順に開き直す。
/// 記録自体（パス・カーソル位置、最大10件）は<see cref="EditorTabManager"/>が保持し
/// （<see cref="ClosedTabRecord"/>参照）、ここではその1件を取り出して実際に開く操作と、
/// 「開き直せる記録が無い」ときの案内を担う。
/// </summary>
public sealed partial class EditorPaneViewModel
{
    /// <summary>
    /// Ctrl+Shift+Tの実体。<see cref="EditorTabManager.TakeNextReopenable"/>が返す記録を
    /// <see cref="OpenFileAsync"/>で開き、カーソル位置（行・桁）を復元する。実体が既に存在しない
    /// ファイルの記録は自動的に読み飛ばす（<see cref="EditorTabManager"/>側の責務）。
    /// 開き直せる記録が1件も無かった場合は、状況が伝わるようメッセージを出す
    /// （記録が最初から無かった場合と、記録はあったが全て存在しないファイルだった場合とで
    /// 文言を分ける）。
    /// </summary>
    public async Task ReopenLastClosedTabAsync(CancellationToken ct = default)
    {
        var record = _manager.TakeNextReopenable(out var skippedMissing);
        if (record is not { } target)
        {
            var message = skippedMissing
                ? "直前に閉じたタブのファイルは既に見つからないため、開き直せませんでした。"
                : "開き直せる、直前に閉じたタブがありません。";
            await _dialogs.ShowMessageAsync("元に戻せません", message).ConfigureAwait(true);
            return;
        }

        var opened = await OpenFileAsync(target.FullPath, preview: false, line: target.CaretLine, ct).ConfigureAwait(true);
        if (!opened.IsSuccess)
        {
            await _dialogs.ShowMessageAsync("タブを開き直せませんでした",
                string.Join(Environment.NewLine, opened.Issues.Select(i => i.ToDisplayText()))).ConfigureAwait(true);
            return;
        }

        opened.Value.CaretColumn = target.CaretColumn;
    }
}
