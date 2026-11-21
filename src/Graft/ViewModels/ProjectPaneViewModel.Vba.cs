using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Features;

namespace Graft.ViewModels;

/// <summary>
/// <see cref="ProjectPaneViewModel"/> の分割ファイル。Excel VBA 連携のうち、プロジェクトペインから始める操作
/// （仕様書_VBA連携 7.1 の作成フロー、7.2・7.2b の手動 Pull／Push、7.6 のブックの前面化）。
/// <para>
/// 【ここに置く理由】 操作の対象は「選択中のプロジェクト」で、右クリックメニュー・追加ボタン・コマンドパレットの
/// どれからも同じコマンドを呼ぶ。履歴ペインのメニューと同じ作り（<c>CommandParameter</c> を使わず
/// <see cref="ProjectPaneViewModel.SelectedItem"/> を直接見る）に揃えた。Pull・Push の中身は
/// <see cref="VbaSyncService"/> にあり、ここは結果を利用者に伝えるだけにしてある。
/// </para>
/// </summary>
public sealed partial class ProjectPaneViewModel
{
    private VbaSyncService? _vbaSync;
    private Func<bool> _isVbaEnabled = static () => true;

    /// <summary>
    /// Pull／Push の窓口。<see cref="Graft.Platform.IVbaBridge"/> が差し替わるたびに <c>MainViewModel</c> が作り直して渡す
    /// （仕様 3.3）。null なら機能そのものが無い扱い。
    /// </summary>
    public VbaSyncService? VbaSync
    {
        get => _vbaSync;
        set
        {
            if (!SetProperty(ref _vbaSync, value)) return;
            RaiseVbaStateChanged();
        }
    }

    /// <summary>設定 <c>Vba.Enabled</c> の読み取り口（設定は実行中に変わるので、値ではなく問い合わせで持つ）。</summary>
    public Func<bool> IsVbaEnabledSetting
    {
        get => _isVbaEnabled;
        set
        {
            _isVbaEnabled = value ?? throw new ArgumentNullException(nameof(value));
            RaiseVbaStateChanged();
        }
    }

    /// <summary>
    /// 「Excel ブックから追加」の選択画面を開いて、確定されたかを返す（View が配線する。ViewModel は Window の型を知らない）。
    /// 未設定なら追加の操作は何もしない。
    /// </summary>
    public Func<VbaWorkbookPickerViewModel, Task<bool>>? ShowWorkbookPickerAsync { get; set; }

    /// <summary>
    /// ブックを Excel で開く（E804 の「Excel で開く」ボタン）。既定は OS の関連付けで開く。テストで差し替える。
    /// </summary>
    public Action<string> OpenWorkbookInExcel { get; set; } = static path =>
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            // 開けなかったときは、利用者が自分で開く。ここで黙るのは、案内のダイアログがすでに出ているため。
        }
    };

    /// <summary>
    /// この環境で機能が使えて、設定でも有効か。「Excel ブックから追加」ボタンとコマンドパレットの項目の表示に使う
    /// （使えない環境では、ボタンもパレットの項目も出さない。仕様 7.1）。
    /// </summary>
    public bool IsVbaFeatureAvailable => _vbaSync?.IsSupported == true && _isVbaEnabled();

    /// <summary>
    /// 選択中が VBA プロジェクトで、この環境で機能が使えるか。右クリックメニューの VBA 項目の表示に使う。
    /// 設定で無効のときも表示はして、コマンドだけ無効にする（普通のファイルプロジェクトとして扱うため）。
    /// </summary>
    public bool IsSelectedVbaProject => _vbaSync?.IsSupported == true && SelectedItem?.Project.Vba is not null;

    /// <summary>「Excel ブックから追加」。</summary>
    public ICommand AddFromWorkbookCommand { get; private set; } = null!;

    /// <summary>「Excel から取込」。</summary>
    public ICommand PullFromExcelCommand { get; private set; } = null!;

    /// <summary>「Excel へ反映」。</summary>
    public ICommand PushToExcelCommand { get; private set; } = null!;

    /// <summary>「Excel から復元」（ミラーから消えて削除保留になったモジュールを Excel から戻す）。</summary>
    public ICommand RestoreFromExcelCommand { get; private set; } = null!;

    /// <summary>「ブックを前面化」。</summary>
    public ICommand ActivateWorkbookCommand { get; private set; } = null!;

    private void InitializeVbaCommands()
    {
        AddFromWorkbookCommand = new AsyncRelayCommand(AddFromWorkbookAsync, () => IsVbaFeatureAvailable, context: "Excel ブックからの追加");
        PullFromExcelCommand = new AsyncRelayCommand(PullSelectedAsync, CanOperateOnSelectedVbaProject, context: "Excel からの取込");
        PushToExcelCommand = new AsyncRelayCommand(PushSelectedAsync, CanOperateOnSelectedVbaProject, context: "Excel への反映");
        RestoreFromExcelCommand = new AsyncRelayCommand(RestoreSelectedAsync, CanOperateOnSelectedVbaProject, context: "Excel からの復元");
        ActivateWorkbookCommand = new AsyncRelayCommand(ActivateSelectedAsync, CanOperateOnSelectedVbaProject, context: "ブックの前面化");
    }

    private bool CanOperateOnSelectedVbaProject()
        => IsVbaFeatureAvailable && SelectedItem is { IsDisconnected: false } item && item.Project.Vba is not null;

    private void RaiseVbaStateChanged()
    {
        OnPropertyChanged(nameof(IsVbaFeatureAvailable));
        OnPropertyChanged(nameof(IsSelectedVbaProject));
    }

    // ------------------------------------------------------------------
    // 作成フロー（7.1）
    // ------------------------------------------------------------------

    private async Task AddFromWorkbookAsync()
    {
        if (_vbaSync is null || ShowWorkbookPickerAsync is null) return;

        var picker = new VbaWorkbookPickerViewModel(_vbaSync, _dialogs);
        if (!await ShowWorkbookPickerAsync(picker).ConfigureAwait(true) || picker.Result is not { } chosen) return;

        // ミラーと同期基準は選択画面の確定で作成済み。ここでプロジェクトとして登録し、VBA の紐付けを付ける。
        // 紐付けは ProjectOverrides ではなく VbaLink（仕様 4.1）。
        var registered = await _store.RegisterAsync(chosen.MirrorRoot, chosen.ProjectName).ConfigureAwait(true);
        if (!registered.IsSuccess)
        {
            await ShowFailureAsync("Excel ブックのプロジェクトを追加できませんでした", registered.Issues).ConfigureAwait(true);
            return;
        }

        var updated = await _store.UpdateAsync(registered.Value.Id, p => p with
        {
            Vba = new VbaLink { WorkbookFullName = chosen.WorkbookFullName },
            // 同じブックの再追加で、利用者が編集した常設コンテキストを既定文で上書きしない。
            StandingContext = string.IsNullOrWhiteSpace(p.StandingContext) ? VbaStandingContext.Default : p.StandingContext,
        }).ConfigureAwait(true);
        if (!updated.IsSuccess)
        {
            await ShowFailureAsync("Excel ブックのプロジェクトを追加できませんでした", updated.Issues).ConfigureAwait(true);
            return;
        }

        await LoadAsync().ConfigureAwait(true);
        SelectedItem = Items.FirstOrDefault(i => i.Project.Id == registered.Value.Id);
        Notify?.Invoke(chosen.ModuleCount >= 0
            ? $"「{updated.Value.DisplayName}」を追加しました（モジュール {chosen.ModuleCount} 件）。"
            : $"「{updated.Value.DisplayName}」を追加しました（既存のミラーを使います）。");
    }

    // ------------------------------------------------------------------
    // 手動の Pull／Push（7.2・7.2b）
    // ------------------------------------------------------------------

    private async Task PullSelectedAsync()
    {
        if (_vbaSync is null || SelectedItem is not { } item) return;
        var project = item.Project;

        var result = await _vbaSync.PullAsync(project).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            await ShowVbaFailureAsync("Excel から取り込めませんでした", project, result.Issues).ConfigureAwait(true);
            return;
        }

        var pulled = result.Value;
        await ShowConflictNoticeAsync(pulled.Conflicts, "取り込みませんでした").ConfigureAwait(true);
        await ShowWarningsAsync("Excel からの取込の注意", result.Issues.Where(i => i.Code != ErrorCode.E812)).ConfigureAwait(true);

        if (pulled.Pulled.Count > 0)
        {
            Notify?.Invoke($"Excel から {pulled.Pulled.Count} 件取り込みました。");
        }
        else if (pulled.IsEmpty)
        {
            Notify?.Invoke("Excel 側に新しい変更はありませんでした。");
        }
    }

    private async Task PushSelectedAsync()
    {
        if (_vbaSync is null || SelectedItem is not { } item) return;
        var project = item.Project;

        var result = await _vbaSync.PushAsync(project).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            // 手動の Push は適用ではないので、取り消しは提案しない（仕様 7.2b）。未反映の印が残るので再試行できる。
            await ShowVbaFailureAsync("Excel へ反映できませんでした", project, result.Issues).ConfigureAwait(true);
            return;
        }

        var pushed = result.Value;
        await ShowConflictNoticeAsync(pushed.Conflicts, "反映しませんでした").ConfigureAwait(true);
        var problems = result.Issues.Where(i => i.Code != ErrorCode.E812).ToList();
        if (pushed.Failed.Count > 0 || problems.Count > 0)
        {
            await ShowWarningsAsync("Excel への反映の注意", problems).ConfigureAwait(true);
        }

        if (pushed.WrittenCount > 0)
        {
            Notify?.Invoke(pushed.Created.Count > 0
                ? $"Excel へ {pushed.WrittenCount} 件反映しました（新規作成 {pushed.Created.Count} 件）。"
                : $"Excel へ {pushed.WrittenCount} 件反映しました。");
        }
        else if (pushed.Failed.Count == 0 && pushed.Conflicts.Count == 0)
        {
            Notify?.Invoke("Excel へ反映するものはありませんでした。");
        }
    }

    private async Task RestoreSelectedAsync()
    {
        if (_vbaSync is null || SelectedItem is not { } item) return;
        var project = item.Project;

        var status = await _vbaSync.GetLocalStatusAsync(project).ConfigureAwait(true);
        if (status.DeletePendingCount == 0)
        {
            Notify?.Invoke("Excel から復元するモジュールはありません。");
            return;
        }

        // 削除保留のモジュールだけを対象にする（名前の一覧は同期状態から求める。通常の Pull は触らない）。
        var loaded = await new VbaSyncStateStore(_store.Paths).LoadAsync(project.Id, project.Vba!.WorkbookFullName).ConfigureAwait(true);
        var pending = loaded.Value.Modules.Where(kv => kv.Value.DeletePending).Select(kv => kv.Key).ToList();

        var result = await _vbaSync.PullAsync(project, pending, restoreDeletePending: true).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            await ShowVbaFailureAsync("Excel から復元できませんでした", project, result.Issues).ConfigureAwait(true);
            return;
        }

        Notify?.Invoke($"Excel から {result.Value.Pulled.Count} 件復元しました。");
    }

    private async Task ActivateSelectedAsync()
    {
        if (_vbaSync is null || SelectedItem is not { } item) return;
        var project = item.Project;

        var result = await _vbaSync.ActivateWorkbookAsync(project.Vba!.WorkbookFullName).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            await ShowVbaFailureAsync("ブックを前面に出せませんでした", project, result.Issues).ConfigureAwait(true);
        }
    }

    // ------------------------------------------------------------------
    // 結果の伝え方
    // ------------------------------------------------------------------

    /// <summary>
    /// 失敗の表示。E804（ブックが開いていない）は「Excel で開く」ボタンつきで、ブックのパスと一緒に出す
    /// （仕様 9章）。それ以外は理由と対処（<see cref="GraftIssue.ToDisplayText"/>）をそのまま出す。
    /// </summary>
    private async Task ShowVbaFailureAsync(string title, Project project, IReadOnlyList<GraftIssue> issues)
    {
        var text = string.Join(Environment.NewLine + Environment.NewLine, issues.Select(FormatIssue));
        if (issues.Any(i => i.Code == ErrorCode.E804) && project.Vba is { } link)
        {
            var open = await _dialogs.ShowActionMessageAsync(title, text, "Excel で開く").ConfigureAwait(true);
            if (open) OpenWorkbookInExcel(link.WorkbookFullName);
            return;
        }

        await _dialogs.ShowMessageAsync(title, text).ConfigureAwait(true);
    }

    /// <summary>理由に、対処（トラストセンターの手順など）を添えた表示文。</summary>
    private static string FormatIssue(GraftIssue issue) => $"{issue.ToDisplayText()}{Environment.NewLine}対処: {issue.Remedy}";

    private Task ShowWarningsAsync(string title, IEnumerable<GraftIssue> issues)
    {
        var list = issues.ToList();
        return list.Count == 0
            ? Task.CompletedTask
            : _dialogs.ShowMessageAsync(title, string.Join(Environment.NewLine + Environment.NewLine, list.Select(FormatIssue)));
    }

    /// <summary>
    /// 衝突の通知。衝突を解決する画面（衝突ダイアログ）は P3 で入るので、P2 では取り込まず（反映せず）、
    /// 通知だけにとどめる。どちらを残すか決めて、残さない側の変更を手で戻してから、もう一度実行してもらう。
    /// </summary>
    private Task ShowConflictNoticeAsync(IReadOnlyList<string> conflicts, string outcome)
    {
        if (conflicts.Count == 0) return Task.CompletedTask;

        var issue = GraftIssue.Of(
            ErrorCode.E812,
            $"次のモジュールは、Excel 側とファイル側の両方が変更されているため、{outcome}: {string.Join("、", conflicts)}。" +
            "衝突を解決する画面は、今後の版で追加されます。それまでは、どちらを残すかを決めて、" +
            "残さない側の変更を手で取り消してから、もう一度実行してください。",
            severity: Severity.Warning);
        return _dialogs.ShowMessageAsync("Excel 側とファイル側の衝突", FormatIssue(issue));
    }
}
