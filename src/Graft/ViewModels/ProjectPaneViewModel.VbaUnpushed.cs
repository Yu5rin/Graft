using System.IO;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Features;

namespace Graft.ViewModels;

/// <summary>
/// Excel（ブック）へ未反映のリボン・モジュールが 1 つ以上あるプロジェクトと、その中身（エクスプローラーの印・閉じるときの確認・
/// ステータスバーの常時表示が使う）。
/// </summary>
/// <param name="Project">プロジェクト。</param>
/// <param name="ModulePaths">未反映のモジュールの相対パス（<c>Modules/Module1.bas</c> など）。</param>
/// <param name="RibbonPaths">未反映のリボンのファイルの相対パス（<c>CustomUI/customUI14.xml</c> など）。</param>
public sealed record VbaUnpushedInfo(Project Project, IReadOnlyList<string> ModulePaths, IReadOnlyList<string> RibbonPaths)
{
    /// <summary>未反映のモジュールがあるか。</summary>
    public bool HasModules => ModulePaths.Count > 0;

    /// <summary>リボンが未反映か。</summary>
    public bool HasRibbon => RibbonPaths.Count > 0;

    /// <summary>ブックのファイル名（ステータスバー・確認の文面に出す）。</summary>
    public string WorkbookName => Path.GetFileName(Project.Vba?.WorkbookFullName.Replace('\\', '/') ?? string.Empty);

    /// <summary>「リボン」「モジュール 3 件」「リボン・モジュール 3 件」の形の中身。</summary>
    public string Contents => HasModules && HasRibbon
        ? $"リボン・モジュール {ModulePaths.Count} 件"
        : HasRibbon ? "リボン" : $"モジュール {ModulePaths.Count} 件";
}

/// <summary>
/// <see cref="ProjectPaneViewModel"/> の分割ファイル。Excel（ブック）への<b>未反映の見落とし</b>への対策の土台:
/// <list type="bullet">
/// <item>未反映のパスを、バッジの数え直し（<c>RefreshVbaBadgesAsync</c>）の結果のキャッシュから取り出す（<see cref="GetVbaUnpushed"/>）。
/// 判定は Excel に触れず、ミラーと同期基準だけで行う（バッジと同じ）。</item>
/// <item>数え直しの末尾で <see cref="VbaUnpushedChanged"/> を通知する（エクスプローラーの印・ステータスバーが読み直す。専用の監視は足さない）。</item>
/// <item>数秒で消えずに残る通知（リボンの反映を中止したとき・ブックが閉じたとき）。</item>
/// <item>VBA プロジェクトのロック（E803）の窓と、「VBE を開いてロックを解除する」。</item>
/// </list>
/// </summary>
public sealed partial class ProjectPaneViewModel
{
    /// <summary>
    /// 未反映の中身（エクスプローラーの印・ステータスバー・閉じるときの確認）が変わったかもしれないことの通知。
    /// バッジの数え直しの末尾で発火する。
    /// </summary>
    public event EventHandler? VbaUnpushedChanged;

    /// <summary>
    /// 数秒で消えずに残る通知を出す（<c>MainViewModel</c> が <c>StatusNoticeViewModel.ShowPersistent</c> を渡す）。
    /// 引数は、識別子・文言・操作の文言・操作・「あとで」の文言。未設定なら、数秒で消える通知（<see cref="Notify"/>）にする。
    /// </summary>
    public Action<string, string, string, Func<Task>, string>? ShowPersistentNotice { get; set; }

    /// <summary>残る通知を、識別子が一致するときだけ消す（原因が解消したとき）。<c>MainViewModel</c> が配線する。</summary>
    public Action<string>? DismissPersistentNotice { get; set; }

    /// <summary>
    /// 選択肢つきの確認窓（<c>VbaChoiceWindow</c>）を出して、選ばれたボタンの識別子を返す（<c>MainViewModel</c> が配線する。
    /// 窓を選ばずに閉じたときは既定のボタンの識別子）。未設定なら、窓を出さずに既定のボタンとして進む。
    /// </summary>
    public Func<VbaChoiceRequest, Task<string>>? ChooseVba { get; set; }

    /// <summary>設定で有効で、この環境で使える VBA 連携のプロジェクトが 1 つ以上あるか（閉じるときの確認の入口の軽い判定）。</summary>
    public bool HasVbaProjects
        => _vbaSync?.IsSupported == true && _isVbaEnabled() && Items.Any(i => i.Project.Vba is not null && !i.IsDisconnected);

    private static string RibbonNoticeTag(string projectId) => "vba-ribbon:" + projectId;

    private void RaiseVbaUnpushedChanged()
    {
        // 解消したプロジェクトの「残る通知」は消す（反映・取り消し・取込でリボンが未反映でなくなったあと、案内が残り続けないように）。
        if (DismissPersistentNotice is { } dismiss)
        {
            foreach (var item in Items.Where(i => i.Project.Vba is not null))
            {
                if (!_vbaBadgeCache.TryGetValue(item.Project.Id, out var entry) || !entry.RibbonUnpushed)
                {
                    dismiss(RibbonNoticeTag(item.Project.Id));
                }
            }
        }

        VbaUnpushedChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 未反映（リボンまたはモジュール）のあるプロジェクトの一覧（一覧の並び順）。保持している数え直しの結果から作るので、
    /// Excel にもファイルにも触れない。機能が設定で切られている・この環境で使えないときは空（反映の操作ができないため）。
    /// </summary>
    public IReadOnlyList<VbaUnpushedInfo> GetVbaUnpushed()
    {
        if (_vbaSync?.IsSupported != true || !_isVbaEnabled()) return Array.Empty<VbaUnpushedInfo>();

        var list = new List<VbaUnpushedInfo>();
        foreach (var item in Items)
        {
            if (item.Project.Vba is null || item.IsDisconnected) continue;
            if (!_vbaBadgeCache.TryGetValue(item.Project.Id, out var entry)) continue;

            var modules = entry.ModulePaths ?? Array.Empty<string>();
            var ribbon = entry.RibbonPaths ?? Array.Empty<string>();
            // リボンの未反映の旗はあるが、パスを持たない（読めなかった回の前回の値など）ときも、リボン未反映として扱う。
            if (entry.RibbonUnpushed && ribbon.Count == 0) ribbon = new[] { VbaRibbonMirrorLayout.ToRelativePath(VbaRibbonFormat.Office2010) };
            if (modules.Count == 0 && ribbon.Count == 0) continue;
            list.Add(new VbaUnpushedInfo(item.Project, modules, ribbon));
        }

        return list;
    }

    /// <summary>
    /// 指定の未反映を、そのプロジェクトを選んで反映する（ステータスバーの常時表示・閉じるときの確認から呼ぶ）。
    /// モジュールがあれば「Excel へ反映」（成功すれば、続けてリボンの確認も出る）、リボンだけなら「リボンをブックへ反映…」。
    /// </summary>
    /// <returns>反映の操作を始められたか（プロジェクトを選べなかった・処理中で受け付けなかったときは false）。</returns>
    public async Task<bool> PushUnpushedAsync(VbaUnpushedInfo info)
    {
        var item = Items.FirstOrDefault(i => i.Project.Id == info.Project.Id);
        if (item is null) return false;

        if (!ReferenceEquals(SelectedItem, item))
        {
            await SelectAsync(item).ConfigureAwait(true);
            // 切り替えの確認（開いているタブの未保存の確認）でキャンセルされたときは、選択が戻っている。
            if (!ReferenceEquals(SelectedItem, item)) return false;
        }

        if (RejectWhileVbaBusy()) return false;
        if (info.HasModules) await PushSelectedAsync().ConfigureAwait(true);
        else await PushRibbonSelectedAsync().ConfigureAwait(true);
        return true;
    }

    /// <summary>
    /// 閉じるときの確認で「反映してから閉じる」を選んだときの反映。プロジェクトを選び直さず（切り替えの確認を出さない）、
    /// モジュール → リボンの順に、既存の流れで反映する。リボンの確認窓は出る（既定は「中止」）。
    /// </summary>
    /// <returns>すべて反映できたか（失敗・中止があれば false。その場合は閉じない）。</returns>
    public async Task<bool> PushForCloseAsync(VbaUnpushedInfo info)
    {
        if (_vbaSync is null || RejectWhileVbaBusy()) return false;
        var project = info.Project;

        if (info.HasModules)
        {
            var vba = await PushProjectAsync(project, only: null).ConfigureAwait(true);
            if (!vba.Completed) return false;
        }

        if (info.HasRibbon)
        {
            var end = await PushRibbonAsync(project, new RibbonPushOptions { Automatic = false }).ConfigureAwait(true);
            if (end is not (RibbonPushEnd.Rewritten or RibbonPushEnd.NothingToDo)) return false;
        }

        // 数え直して、本当に残っていないかを確かめる（成功と数えたのに、衝突で反映しなかったものなどが残っていないか）。
        await RefreshVbaBadgesAsync(checkWorkbookOpen: false).ConfigureAwait(true);
        return GetVbaUnpushed().All(u => u.Project.Id != project.Id);
    }

    // ------------------------------------------------------------------
    // 残る通知（リボンの反映の中止・ブックが閉じた）
    // ------------------------------------------------------------------

    /// <summary>
    /// リボンの反映の確認で「中止」したあとの案内。数秒で消える通知だけだと、気付かないうちにリボン未反映のまま閉じてしまう
    /// （利用者の報告）ので、［今すぐ反映］［あとで］を添えて残す。「あとで」を押しても、未反映のあいだはステータスバーの常時表示が残る。
    /// </summary>
    private void NotifyRibbonStillUnpushed(Project project, string? because = null)
    {
        var name = project.Vba is { } link ? Path.GetFileName(link.WorkbookFullName.Replace('\\', '/')) : project.DisplayName;
        var full = $"リボンの反映を中止しました（{name}{(because is null ? string.Empty : "／" + because)}）。リボンは未反映のままです。";
        if (ShowPersistentNotice is { } show)
        {
            show(RibbonNoticeTag(project.Id), full, "今すぐ反映", () => PushRibbonLaterAsync(project), "あとで");
            return;
        }

        Notify?.Invoke(full);
    }

    /// <summary>ブックが「開 → 閉」に変わったのを見つけたときの案内。いまなら、ブックを開き直さずにリボンを反映できる。</summary>
    private void NotifyRibbonWorkbookClosed(Project project)
    {
        var name = project.Vba is { } link ? Path.GetFileName(link.WorkbookFullName.Replace('\\', '/')) : project.DisplayName;
        var text = $"ブック（{name}）が閉じています。今ならリボンを、ブックを開き直さずに反映できます";
        if (ShowPersistentNotice is { } show)
        {
            show(RibbonNoticeTag(project.Id), text, "反映する", () => PushRibbonLaterAsync(project), "あとで");
            return;
        }

        Notify?.Invoke(text);
    }

    private async Task PushRibbonLaterAsync(Project project)
    {
        if (_vbaSync is null || RejectWhileVbaBusy()) return;
        await PushRibbonAsync(project, null).ConfigureAwait(true);
    }

    // ------------------------------------------------------------------
    // VBA プロジェクトのロック（E803）
    // ------------------------------------------------------------------

    /// <summary>
    /// 「VBE を開いてロックを解除する」。VBE を前面に出すだけで、VBA プロジェクトには触れない（ロックされていても動く）。
    /// 失敗したときは理由を出す。ロックの窓を閉じずに何度でも押せる。
    /// </summary>
    internal async Task ShowVbeForUnlockAsync(Project project)
    {
        if (_vbaSync is null || project.Vba is null) return;
        var result = await _vbaSync.ShowVbeAsync(project.Vba.WorkbookFullName).ConfigureAwait(true);
        if (!result.IsSuccess) await ShowVbaFailureAsync("VBE を開けませんでした", project, result.Issues).ConfigureAwait(true);
    }

    /// <summary>
    /// ロック（E803）の窓（<see cref="VbaLockedText"/>）を出して、選ばれた識別子を返す。「VBE を開いてロックを解除する」は
    /// 窓を閉じずに実行される。窓が配線されていない環境では、既定の識別子を返す。
    /// </summary>
    internal async Task<string> ChooseWhenVbaLockedAsync(
        Project project, VbaLockedScene scene, string lead, int? undoRevision, IReadOnlyList<GraftIssue> issues)
    {
        var issueText = string.Join(
            Environment.NewLine + Environment.NewLine,
            issues.Where(i => i.Code == ErrorCode.E803).Take(1).Select(FormatIssue));
        var request = new VbaChoiceRequest(
            VbaLockedText.Title,
            VbaLockedText.Message(scene, lead, undoRevision, issueText),
            VbaLockedText.Options(scene, undoRevision, () => ShowVbeForUnlockAsync(project)));
        if (ChooseVba is null) return request.Default.Id;
        var id = await ChooseVba(request).ConfigureAwait(true);
        return request.Options.Any(o => o.Id == id && o.KeepOpenAction is null) ? id : request.Default.Id;
    }
}
