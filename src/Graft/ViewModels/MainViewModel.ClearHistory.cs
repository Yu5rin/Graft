using System.Globalization;
using Graft.Core;
using Graft.Features;
using Graft.Platform;

namespace Graft.ViewModels;

/// <summary>
/// <see cref="MainViewModel"/> の分割ファイル。「履歴だけを削除…」（プロジェクトの右クリックメニュー・コマンドパレット）の実処理。
///
/// 【なぜプロジェクトの削除と別にしたか】 プロジェクトの削除の「履歴も削除する」は、登録ごと外してしまう。
/// 「登録も作業フォルダも今のまま、溜まった履歴だけを空にしたい」（容量が気になる、試し打ちの履歴を捨てたい）という
/// 要望には、削除して登録し直すしかなく、その間にタブ構成などの記憶も失われていた。
///
/// 【ここに置く理由】 止める条件（適用中・Excel へ書き込み中・復元中・中断した適用）と、消したあとの後始末
/// （履歴ペインの読み直し・適用直後の「元に戻す」通知）は、どれも <see cref="MainViewModel"/> が持つ状態で、
/// <see cref="ProjectPaneViewModel"/> は知らない。ProjectPane は選択中の1件をここへ渡すだけにする
/// （<see cref="ProjectPaneViewModel.ClearHistoryHandler"/>）。実際にフォルダを消す処理は
/// <see cref="ProjectStore.ClearHistoryAsync"/>（ゲートの中で back/&lt;Id&gt;/ を消す）。
/// </summary>
public sealed partial class MainViewModel
{
    private async Task ClearProjectHistoryAsync(Project project)
    {
        // 【止める条件】 消している最中に、同じ back/<Id>/ へ書く操作が走ると、消し残しや「履歴だけ半分ある」状態になる。
        // 適用（書き込み中）・Excel への反映・取り消しや復元は、どれも back/<Id>/ を読み書きする。
        if (_isApplyInProgress || IsVbaBusy() || IsVbaPushing || ApplyCommand is AsyncRelayCommand { IsExecuting: true })
        {
            await _dialogs.ShowMessageAsync(
                "履歴を削除できません",
                "適用または Excel への反映の最中です。終わってからやり直してください。").ConfigureAwait(true);
            return;
        }

        if (History.IsOperationBlocked?.Invoke() == true || History.State == HistoryPaneState.Loading)
        {
            await _dialogs.ShowMessageAsync(
                "履歴を削除できません",
                "履歴の取り消し・復元、または読み込みの最中です。終わってからやり直してください。").ConfigureAwait(true);
            return;
        }

        // 中断した適用（in_progress のまま残ったリビジョン）がある間は消さない。その履歴は、起動時に勧める
        // ロールバック（取り消し）の手がかりで、消すと「途中まで書き換わったファイル」を元に戻す手段が無くなる。
        // 後から成功した適用がある残骸（Superseded）は、実害が無いので止める理由にしない。
        var interrupted = await _revisionStore.FindInterruptedAsync(project.Id).ConfigureAwait(true);
        if (interrupted.IsSuccess && interrupted.Value.Offer.Count > 0)
        {
            var labels = string.Join("、", interrupted.Value.Offer.Select(r => $"r{r.Manifest.Revision}"));
            await _dialogs.ShowMessageAsync(
                "履歴を削除できません",
                $"適用が途中で終わったままの変更（{labels}）があります。先に履歴の画面で取り消してから、やり直してください。").ConfigureAwait(true);
            return;
        }

        // 件数と容量を数えて、何が消えるかを確認に出す。読めなかったとき（履歴の一覧が壊れているなど）は、
        // 数字を出せないので、そのことを伝えて続けるかを聞く（消すこと自体は、壊れた履歴を片付ける手段にもなる）。
        var listed = await _revisionStore.ListAsync(project.Id).ConfigureAwait(true);
        var name = project.DisplayName;
        string summary;
        if (listed.IsSuccess)
        {
            if (listed.Value.Count == 0)
            {
                await _dialogs.ShowMessageAsync("履歴を削除", $"『{name}』には削除する変更履歴がありません。").ConfigureAwait(true);
                return;
            }

            summary = $"（{listed.Value.Count}件・合計 {FormatMegabytes(listed.Value.Sum(r => r.SizeBytes))}）";
        }
        else
        {
            summary = "（件数と容量は読み取れませんでした）";
        }

        var message = $"『{name}』の変更履歴{summary}を削除します。ファイルそのものは変わりません。削除すると、取り消し・ここまで戻すができなくなります。";
        if (project.Vba is not null)
        {
            message += "\nExcel との同期記録と Excel 側の控えは消しません。";
        }

        // 既定（Enter）はキャンセル。戻せない操作なので、確認を読まずに Enter を押しただけでは実行されないようにする。
        if (!await _dialogs.ConfirmDestructiveAsync("履歴を削除", message, "削除する").ConfigureAwait(true)) return;

        // 確認を待っている間に、適用などが始まっていないかを見直す（確認は数十秒かかりうる）。
        if (_isApplyInProgress || IsVbaBusy() || IsVbaPushing)
        {
            await _dialogs.ShowMessageAsync(
                "履歴を削除できません",
                "適用または Excel への反映が始まったため、削除しませんでした。終わってからやり直してください。").ConfigureAwait(true);
            return;
        }

        // 消し方は世代整理と同じ（設定の「古い履歴の整理にごみ箱を使う」に従う）。
        var cleared = await _projectStore
            .ClearHistoryAsync(project.Id, _settings.Backup.UseRecycleBin, PlatformServices.Current.Trash)
            .ConfigureAwait(true);
        if (!cleared.IsSuccess)
        {
            await _dialogs.ShowMessageAsync(
                "履歴を削除できませんでした",
                string.Join(Environment.NewLine, cleared.Errors.Select(i => i.ToDisplayText()))).ConfigureAwait(true);
            return;
        }

        // 削除の途中で失敗した（権限など）ときも、残った分を履歴ペインに反映するため、読み直しは必ず行う。
        await ReloadHistoryAfterClearAsync(project).ConfigureAwait(true);

        if (cleared.Issues.Count > 0)
        {
            await _dialogs.ShowMessageAsync(
                "履歴の削除で問題が発生しました",
                string.Join(Environment.NewLine, cleared.Issues.Select(i => i.ToDisplayText()))).ConfigureAwait(true);
            return;
        }

        Notice.Show($"『{name}』の変更履歴を削除しました");
    }

    /// <summary>
    /// 履歴を消したあとの後始末。履歴ペインを読み直して空にし、消えたリビジョンを指す画面の状態を捨てる。
    /// <list type="bullet">
    /// <item>適用直後の「rN として適用しました — 元に戻す」の通知が、消したプロジェクトの適用についてのものなら消す。
    /// 残すと、押しても取り消す対象が無く、取り消せない旨のエラー（E405）になる。</item>
    /// <item>プレビュー中のパッチの「rN で適用済み」の印も外す。r5 が消えたのに「r5 で適用済み」と出し続けないため
    /// （適用の可否は、適用の時点で改めて判定される）。</item>
    /// </list>
    /// </summary>
    private async Task ReloadHistoryAfterClearAsync(Project project)
    {
        if (string.Equals(_lastAppliedNoticeProjectId, project.Id, StringComparison.Ordinal))
        {
            DismissApplyUndoNotice();
            LastAppliedRevision = null;
            _lastAppliedNoticeProjectId = null;
        }

        if (_alreadyAppliedRevision is not null
            && string.Equals(ProjectPane.SelectedItem?.Project.Id, project.Id, StringComparison.Ordinal))
        {
            SetAlreadyAppliedRevision(null);
        }

        // 履歴ペインは選択中のプロジェクトのものなので、別のプロジェクトを選んでいるときは触らない。
        if (string.Equals(ProjectPane.SelectedItem?.Project.Id, project.Id, StringComparison.Ordinal))
        {
            await History.LoadAsync(project.Id, project.Root).ConfigureAwait(true);
        }
    }

    /// <summary>容量の表示。0 より大きいのに 0.0 と出ないよう、下限を 0.1 MB にする。</summary>
    private static string FormatMegabytes(long bytes)
    {
        var mb = bytes / 1024d / 1024d;
        if (bytes > 0 && mb < 0.1) mb = 0.1;
        return mb.ToString("0.0", CultureInfo.InvariantCulture) + " MB";
    }
}
