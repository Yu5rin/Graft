using System.IO;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Features;

namespace Graft.ViewModels;

/// <summary>
/// <see cref="MainViewModel"/> の分割ファイル。Excel VBA 連携のうち、保存時の自動反映（機能 D。
/// 仕様書_ブックの情報と依頼の観点 8章）。エディタでミラーのモジュールを保存したら、そのモジュールだけを Excel へ送る。
/// <para>
/// 【<c>DocumentSession</c> と保存経路に触れない（8.2・8.6）】 契機は、保存が成功したあとに <see cref="EditorPaneViewModel"/> が
/// 上げる <c>TabSaved</c>。ここは購読者が 1 つ増えるだけで、ハンドラが同期で行うのは「条件の判定とキューへの追加」だけ
/// （数マイクロ秒）。ファイルの読み込み・ハッシュ・COM は、すべてキューの先（<see cref="VbaSaveCoalescer"/> の送信）で
/// 非同期に行う。10 万行のファイルの編集・保存の体感（<c>PerformanceTests</c>）を、この機能が遅くしない。
/// VBA でないプロジェクトと、オフ（既定）のプロジェクトは、最初の条件判定（プロジェクトの設定を 1 つ見るだけ）で戻る。
/// </para>
/// <para>
/// 【作業を止めない（8.5）】 ダイアログを出さない。衝突は送らずバッジの印にし、Excel 側の事情で送れなかったときは
/// ステータスバーの通知（同じ理由は 60 秒に 1 回）だけにする。ファイルは保存済みで、バッジの「未反映」に残る。
/// 自動では再試行しない（マクロの実行中などに、保存のたびにタイマーで Excel を叩き続けない）。
/// </para>
/// </summary>
public sealed partial class MainViewModel
{
    private VbaSaveCoalescer? _vbaSaveCoalescer;
    private readonly VbaSaveNoticeThrottle _vbaSaveNotices = new();

    /// <summary>
    /// エディタの保存通知（<c>TabSaved</c>）を購読して、保存時の自動反映につなぐ（<c>ShellViewModel</c> が配線する。
    /// エディタを知っているのは Shell 側のため）。
    /// </summary>
    public void AttachVbaSaveSync(EditorPaneViewModel editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        editor.TabSaved += OnEditorTabSavedForVba;
    }

    /// <summary>購読を外し、待っている通知を捨てる（<c>ShellViewModel.Dispose</c> から）。</summary>
    public void DetachVbaSaveSync(EditorPaneViewModel editor)
    {
        editor.TabSaved -= OnEditorTabSavedForVba;
        _vbaSaveCoalescer?.Dispose();
        _vbaSaveCoalescer = null;
    }

    /// <summary>
    /// テストが「溜まった保存を送り終えるまで」待つための窓口。溜まっているものが無ければ、すぐ完了する。
    /// </summary>
    public Task WhenVbaSaveSyncIdleAsync() => _vbaSaveCoalescer?.WhenIdleAsync() ?? Task.CompletedTask;

    /// <summary>
    /// 保存の通知のハンドラ。<b>同期で行うのは判定とキューへの追加だけ</b>で、すぐ戻る（仕様 8.2）。
    /// </summary>
    internal void OnEditorTabSavedForVba(object? sender, EditorTabViewModel tab)
    {
        // 最初の判定は、選択中のプロジェクトの設定を 1 つ見るだけ。VBA でない・オフ（既定）なら、ここで戻る。
        var project = ProjectPane.SelectedItem?.Project;
        if (project?.Vba is not { AutoPushOnSave: true }) return;
        if (!IsVbaActive(project)) return;

        // 保存した瞬間に適用中のときは何もしない（適用後の Push が、Push 判定のモジュールも同梱するため。仕様 8.2）。
        if (_isApplyInProgress) return;

        var fullPath = tab.Session.FullPath;
        if (string.IsNullOrEmpty(fullPath)) return;
        var relative = Path.GetRelativePath(project.Root, fullPath).Replace('\\', '/');
        // ミラーの外のファイル（相対パスが「..」で始まる・別のドライブで絶対パスのまま）と、先頭が「.」のもの
        // （バージョン管理の隠しフォルダなど。同期の対象外）は送らない。
        if (relative.StartsWith("../", StringComparison.Ordinal) || relative == ".." || Path.IsPathRooted(relative)) return;
        if (relative.Split('/').Any(segment => segment.StartsWith('.'))) return;
        if (!VbaMirrorLayout.TryParse(relative, out var parsed, out _) || parsed is null) return;

        // 経路の対象外（付録A-9）の保存（タブを閉じるときの保存・適用前の保存確認）は TabSaved を通らないので、ここへは来ない。
        _vbaSaveCoalescer ??= new VbaSaveCoalescer(FlushVbaSaveAsync, onError: OnVbaSaveSyncError);
        _vbaSaveCoalescer.Add(project.Id, parsed.RelativePath);
    }

    private void OnVbaSaveSyncError(Exception ex)
        => Logger?.Warn("vba-save-sync", $"保存時の自動反映で想定外のエラーが起きました: {ExceptionMessages.Describe(ex)}");

    /// <summary>
    /// まとめた保存を Excel へ送る（<see cref="VbaSaveCoalescer"/> の送信）。戻り値が false なら、いまは送れないので
    /// 溜め直して、もう一度静かになってから呼んでもらう。
    /// </summary>
    private async Task<bool> FlushVbaSaveAsync(VbaSaveBatch batch, CancellationToken ct)
    {
        // 待っている間に、プロジェクトの設定やプロジェクトそのものが変わっていることがある。最新の定義を引き直す。
        var project = ProjectPane.Items.Select(i => i.Project).FirstOrDefault(p => p.Id == batch.ProjectId);
        if (project?.Vba is not { AutoPushOnSave: true } || !IsVbaActive(project)) return true; // 切られた。送らない。

        // 適用の最中、または取り消し・復元の後の反映の最中は、終わってから送る（Excel への書き込みを重ねない）。
        // 手動の反映（IsWritingToExcel）は VbaSyncService のゲートで順番に並ぶので、ここで止めなくてよい。
        if (_isApplyInProgress || _isVbaPushing) return false;

        var result = await VbaSync.PushSavedAsync(project, batch.RelativePaths, ct).ConfigureAwait(true);
        HandleVbaSaveSyncResult(project, batch, result);
        return true;
    }

    private void HandleVbaSaveSyncResult(Project project, VbaSaveBatch batch, GraftResult<VbaPushResult> result)
    {
        if (!result.IsSuccess)
        {
            // 1 件も書けずに全体が失敗した（ブックが開いていない・応答しない・マクロの実行中・ロックなど）。
            var reason = result.Issues.FirstOrDefault(i => i.Severity == Severity.Error) ?? result.Issues.FirstOrDefault();
            if (reason is not null) NotifyVbaSaveNotSent(project, reason, module: null);

            // ブックが閉じられていたときは、バッジをグレーにするため開閉も調べ直す。
            ProjectPane.RequestVbaBadgeRefresh(checkWorkbookOpen: result.Issues.Any(i => i.Code == ErrorCode.E804));
            return;
        }

        var value = result.Value;

        // 衝突は送らず、印だけ付ける。印は、次に同じモジュールが衝突なしで反映できたとき（または手動の反映で解決したとき）に消える。
        if (value.Conflicts.Count > 0) ProjectPane.MarkVbaSaveConflicts(project.Id, value.Conflicts);
        ProjectPane.ClearVbaSaveConflicts(
            project.Id, batch.RelativePaths.Except(value.Conflicts, StringComparer.OrdinalIgnoreCase).ToList());

        foreach (var warning in result.Issues.Where(i => i.Severity == Severity.Warning && i.Code != ErrorCode.E812))
        {
            Logger?.Warn("vba-save-sync", warning.ToDisplayText(), targetPath: warning.Path);
        }

        // モジュールごとの失敗（E811 の文字コード・E809 の書き戻し・E808・E807 のブック保存など）は通知だけ。
        foreach (var error in result.Issues.Where(i => i.Severity == Severity.Error && i.Code != ErrorCode.E812))
        {
            var module = error.Path is null ? null : Path.GetFileNameWithoutExtension(error.Path);
            NotifyVbaSaveNotSent(project, error, module);
        }

        // 成功した分は通知を出さない。プロジェクトペインのバッジの「未反映」の件数が減るだけにする（仕様 8.5）。
        ProjectPane.RequestVbaBadgeRefresh(checkWorkbookOpen: false);
    }

    /// <summary>反映しなかった理由を、ステータスバーの通知（同じ理由は 60 秒に 1 回まで）とログで伝える。</summary>
    private void NotifyVbaSaveNotSent(Project project, GraftIssue reason, string? module)
    {
        Logger?.Warn(
            "vba-save-sync",
            $"保存時の自動反映を行いませんでした（{reason.Code}）: {reason.ToDisplayText()}",
            targetPath: reason.Path ?? project.Root);

        if (!_vbaSaveNotices.TryShow(reason.Code, module)) return;
        Notice.Show(VbaSaveNoticeThrottle.BuildText(reason.Code, module, reason.Detail));
    }
}
