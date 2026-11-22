using System.IO;
using Graft.Features;

namespace Graft.ViewModels;

/// <summary>
/// <see cref="ShellViewModel"/> の分割ファイル。Excel VBA 連携のうち、エディタのタブを知っている Shell だけが
/// 担える部分（Pull の前の未保存の確認。仕様書_VBA連携 7.2）。
/// </summary>
public sealed partial class ShellViewModel
{
    /// <summary>
    /// Excel から取り込むモジュールのうち、エディタに未保存の編集があるものを確認して保存させる。
    /// <para>
    /// 【なぜ要るか】 未保存の編集は三方比較に入らない（ディスク上のミラーは同期基準と同じに見えるので Pull され、
    /// 利用者は外部変更の通知（E702）で初めて気付く）。そこで <see cref="EnsureTargetsSavedAsync"/>
    /// （<c>BeforeApplyAsync</c>）と同じ「保存してから続ける」確認を、Pull の前に行う。断られた（保存できなかった）
    /// ファイルは、呼び出し側が Pull の対象から外す。保存したときは、ミラーが変わるので計画を作り直してもらう。
    /// </para>
    /// </summary>
    private Task<VbaPullSaveOutcome> EnsureVbaPullTargetsSavedAsync(IReadOnlyList<string> fullPaths)
        => EnsureVbaTargetsSavedAsync(fullPaths, "Excel から取り込むモジュール", "取り込みを続けます", "取り込まれません", "取り込みません");

    /// <summary>
    /// 控えを「ミラーへ戻す」前の同じ確認（仕様書_ブックの情報と依頼の観点 7.5-1）。戻すと、エディタで編集中の内容が
    /// 控えの内容に置き換わるので、先に保存してもらう（戻す前のミラーは控えとして残る）。
    /// </summary>
    private Task<VbaPullSaveOutcome> EnsureVbaRestoreTargetsSavedAsync(IReadOnlyList<string> fullPaths)
        => EnsureVbaTargetsSavedAsync(fullPaths, "控えを戻すモジュール", "戻す操作を続けます", "戻されません", "戻しません");

    private async Task<VbaPullSaveOutcome> EnsureVbaTargetsSavedAsync(
        IReadOnlyList<string> fullPaths, string subject, string continuation, string declinedResult, string failedResult)
    {
        var unsaved = Editor.FindUnsaved(fullPaths);
        if (unsaved.Count == 0) return VbaPullSaveOutcome.Nothing;

        var names = string.Join("、", unsaved.Select(Path.GetFileName));
        var proceed = await _dialogs.ConfirmAsync(
            "未保存の変更を保存します",
            $"{subject}（{names}）に未保存の変更があります。保存してから{continuation}。" +
            $"保存しない場合、これらのモジュールは{declinedResult}。よろしいですか？")
            .ConfigureAwait(true);
        if (!proceed) return new VbaPullSaveOutcome(unsaved, false);

        var saved = await Editor.SaveFilesAsync(unsaved).ConfigureAwait(true);
        if (!saved.IsSuccess)
        {
            await _dialogs.ShowMessageAsync("保存に失敗しました", $"ファイルの保存に失敗したため、これらのモジュールは{failedResult}。").ConfigureAwait(true);
            return new VbaPullSaveOutcome(unsaved, false);
        }

        return new VbaPullSaveOutcome(Array.Empty<string>(), true);
    }
}
