using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Features;
using Graft.Platform;

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
    /// 衝突ダイアログ（<c>VbaConflictWindow</c>）を出して、利用者の選択を返す（仕様書_VBA連携 7.5）。<c>MainViewModel</c> が
    /// 自分の同名のデリゲートへつなぐ（ShellWindow が配線する）。未設定、または選ばずに閉じたときは中止として扱う。
    /// </summary>
    public Func<VbaConflictRequest, Task<VbaConflictDecision?>>? ShowConflictAsync { get; set; }

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

    /// <summary>「Excel 側の控えを見る…」（仕様書_ブックの情報と依頼の観点 7.4）。</summary>
    public ICommand ShowRescueCommand { get; private set; } = null!;

    /// <summary>
    /// 控えの窓（<c>VbaRescueWindow</c>）を開く（View が配線する。ViewModel は Window の型を知らない）。
    /// 未設定なら何もしない。
    /// </summary>
    public Func<VbaRescueViewModel, Task>? ShowRescueAsync { get; set; }

    /// <summary>
    /// 控えの窓の ViewModel を作る（<c>MainViewModel</c> が、設定と UI 機能を渡して配線する）。
    /// 未設定なら控えの窓は開けない。
    /// </summary>
    public Func<Project, VbaRescueViewModel?>? CreateRescueViewModel { get; set; }

    private void InitializeVbaCommands()
    {
        AddFromWorkbookCommand = new AsyncRelayCommand(AddFromWorkbookAsync, () => IsVbaFeatureAvailable, context: "Excel ブックからの追加");
        PullFromExcelCommand = new AsyncRelayCommand(PullSelectedAsync, CanOperateOnSelectedVbaProject, context: "Excel からの取込");
        PushToExcelCommand = new AsyncRelayCommand(PushSelectedAsync, CanOperateOnSelectedVbaProject, context: "Excel への反映");
        RestoreFromExcelCommand = new AsyncRelayCommand(RestoreSelectedAsync, CanOperateOnSelectedVbaProject, context: "Excel からの復元");
        ActivateWorkbookCommand = new AsyncRelayCommand(ActivateSelectedAsync, CanActivateSelectedVbaWorkbook, context: "ブックの前面化");
        // 控えはファイルを見るだけなので、Excel が開いていなくても、設定で VBA 連携を切っていても開ける（仕様書 7.4）。
        // この環境で VBA 連携が使えること（控えが作られる環境）と、VBA プロジェクトであることだけを条件にする。
        ShowRescueCommand = new AsyncRelayCommand(ShowRescueForSelectedAsync, CanShowRescue, context: "Excel 側の控えの表示");
    }

    /// <summary>
    /// 適用の最中か、Excel へ書いている最中か（<c>MainViewModel</c> が配線する）。この間は、書き込みと競合する操作
    /// （反映・取込・復元、VBA プロジェクトの削除と場所の変更）を受け付けない（不具合 9）。
    /// </summary>
    public Func<bool> IsVbaBusy { get; set; } = static () => false;

    /// <summary>選択中が VBA プロジェクトで、書き込みと競合する操作を受け付けられない状態か。</summary>
    private bool IsSelectedVbaProjectBusy() => SelectedItem?.Project.Vba is not null && IsVbaBusy();

    /// <summary>
    /// 選択中が VBA プロジェクトで、適用または Excel への書き込みの最中なら、受け付けずに理由を知らせて true を返す
    /// （コマンドの有効・無効の更新が間に合わない経路や、コマンドを通らない呼び出しでも守るため）。
    /// </summary>
    private bool RejectWhileVbaBusy()
    {
        if (!IsSelectedVbaProjectBusy()) return false;
        Notify?.Invoke("適用または Excel への反映の最中のため、終わってからやり直してください。");
        return true;
    }

    private bool CanShowRescue()
        => IsSelectedVbaProject && SelectedItem is { IsDisconnected: false } && CreateRescueViewModel is not null && ShowRescueAsync is not null;

    private bool CanOperateOnSelectedVbaProject()
        => IsVbaFeatureAvailable && SelectedItem is { IsDisconnected: false } item && item.Project.Vba is not null && !IsVbaBusy();

    // 前面化は書き込みと競合しない（Excel のウィンドウを前に出すだけ）ので、反映中も使える。
    private bool CanActivateSelectedVbaWorkbook()
        => IsVbaFeatureAvailable && SelectedItem is { IsDisconnected: false } item && item.Project.Vba is not null;

    private void RaiseVbaStateChanged()
    {
        OnPropertyChanged(nameof(IsVbaFeatureAvailable));
        RaiseVbaSelectionChanged();
        // 機能の有効・無効が変わると、バッジも「VBA 連携オフ」との間で切り替わる（Excel には触れず、保持した結果から描き直す）。
        ReapplyVbaBadges();
    }

    /// <summary>選択が変わった（または一覧が読み直された）とき、右クリックメニューの VBA 項目を更新する。</summary>
    private void RaiseVbaSelectionChanged()
    {
        OnPropertyChanged(nameof(IsSelectedVbaProject));
        OnPropertyChanged(nameof(IsAutoPushChecked));
        OnPropertyChanged(nameof(IsSaveAfterPushChecked));
    }

    // ------------------------------------------------------------------
    // 右クリックメニューのチェック項目（7.6）。VbaLink を ProjectStore 経由で更新する
    // ------------------------------------------------------------------

    /// <summary>
    /// 「適用後に Excel へ自動反映する」（<see cref="VbaLink.AutoPushAfterApply"/>）。メニューの項目に双方向で束縛する。
    /// <para>
    /// 【コマンドではなくプロパティにした理由】 チェック項目は、押すと項目自身が表示を反転する。コマンドで受けて
    /// 一方向に束縛し直すと、反転と通知の順序で見かけと実際の値がずれることがある。双方向のプロパティなら、
    /// 書き込みに失敗したときも通知を投げ直して見かけを実際の値へ戻せる。
    /// </para>
    /// </summary>
    public bool IsAutoPushChecked
    {
        get => SelectedItem?.Project.Vba?.AutoPushAfterApply ?? false;
        set
        {
            if (value == IsAutoPushChecked) return;
            _pendingVbaOption = SetAutoPushAfterApplyAsync(value);
        }
    }

    /// <summary>「反映後にブックを保存する」（<see cref="VbaLink.SaveWorkbookAfterPush"/>）。</summary>
    public bool IsSaveAfterPushChecked
    {
        get => SelectedItem?.Project.Vba?.SaveWorkbookAfterPush ?? false;
        set
        {
            if (value == IsSaveAfterPushChecked) return;
            _pendingVbaOption = SetSaveWorkbookAfterPushAsync(value);
        }
    }

    // 直近のチェック項目の保存。テストと、続けて操作したときの順序の保証に使う（必ず前の保存が終わってから次へ進む）。
    private Task _pendingVbaOption = Task.CompletedTask;

    /// <summary>直近のチェック項目の保存が終わるまで待つ（テスト用。画面の操作は待たない）。</summary>
    public Task WaitForVbaOptionAsync() => _pendingVbaOption;

    /// <summary>選択中のプロジェクトの「適用後に自動反映」を変える。</summary>
    public Task SetAutoPushAfterApplyAsync(bool value)
        => UpdateSelectedVbaLinkAsync(link => link with { AutoPushAfterApply = value });

    /// <summary>選択中のプロジェクトの「反映後にブックを保存」を変える。</summary>
    public Task SetSaveWorkbookAfterPushAsync(bool value)
        => UpdateSelectedVbaLinkAsync(link => link with { SaveWorkbookAfterPush = value });

    private async Task UpdateSelectedVbaLinkAsync(Func<VbaLink, VbaLink> change)
    {
        // 機能を切っているときは、チェックの変更も受け付けない（メニューも無効にしているが、念のため）。
        if (!IsVbaFeatureAvailable || SelectedItem is not { } item || item.Project.Vba is null)
        {
            RaiseVbaSelectionChanged();
            return;
        }

        // 前の保存が終わるのを待つ（連続して押したときに、古い値で上書きし合わないように）。
        await _vbaOptionGate.WaitAsync().ConfigureAwait(true);
        try
        {
            var id = item.Project.Id;
            var updated = await _store.UpdateAsync(id, p => p.Vba is null ? p : p with { Vba = change(p.Vba) }).ConfigureAwait(true);
            if (!updated.IsSuccess)
            {
                RaiseVbaSelectionChanged(); // 見かけのチェックを実際の値へ戻す。
                await ShowFailureAsync("設定を保存できませんでした", updated.Issues).ConfigureAwait(true);
                return;
            }

            // 一覧を読み直して、選択中の Project（VbaLink）を新しい値にする。選択は Id で保たれる。
            await LoadAsync().ConfigureAwait(true);
        }
        finally
        {
            _vbaOptionGate.Release();
        }
    }

    private readonly SemaphoreSlim _vbaOptionGate = new(1, 1);

    // ------------------------------------------------------------------
    // 「VBA」バッジ（7.6）
    // ------------------------------------------------------------------

    private sealed record VbaBadgeCacheEntry(int Unpushed, int DeletePending, bool? WorkbookOpen);

    // プロジェクト Id → 最後に調べた結果。LoadAsync で一覧が作り直されても、ちらつかずに同じ見た目を保つため。
    private readonly Dictionary<string, VbaBadgeCacheEntry> _vbaBadgeCache = new(StringComparer.Ordinal);

    // 調べ直しの直列化。Excel への問い合わせ（COM）が重なって積み上がらないようにする。
    private readonly SemaphoreSlim _vbaBadgeGate = new(1, 1);

    // 開閉の調べ直し（Excel への問い合わせ）を、前面化のたびに連打しないための最小の間隔。
    private const long VbaOpenCheckMinIntervalMs = 3000;
    private long _lastVbaOpenCheckTick = long.MinValue / 2;

    private VbaBadgeState BadgeFor(Project project)
    {
        if (project.Vba is null || _vbaSync?.IsSupported != true) return VbaBadgeState.Hidden;
        _vbaBadgeCache.TryGetValue(project.Id, out var entry);
        return VbaBadgeLogic.Compute(true, _isVbaEnabled(), entry?.WorkbookOpen, entry?.Unpushed ?? 0, entry?.DeletePending ?? 0);
    }

    /// <summary>保持している結果から、すべての行のバッジを描き直す（Excel には触れない）。</summary>
    private void ReapplyVbaBadges()
    {
        foreach (var item in Items) item.VbaBadge = BadgeFor(item.Project);
    }

    /// <summary>
    /// バッジの調べ直しを頼む（待たない）。ウィンドウの前面化時とプロジェクトペインを開いたときに View から呼ぶ。
    /// 定期的な監視（ポーリング）はしない（仕様 7.6）。契機のたびに呼ばれても、前回の開閉の調べから
    /// <see cref="VbaOpenCheckMinIntervalMs"/> 経っていなければ開閉は調べない（件数だけ数え直す）。
    /// </summary>
    /// <param name="checkWorkbookOpen">ブックが開いているかも調べるか（Excel への問い合わせを伴う）。</param>
    public void RequestVbaBadgeRefresh(bool checkWorkbookOpen = true)
    {
        if (_vbaSync?.IsSupported != true || !Items.Any(i => i.Project.Vba is not null)) return;
        if (_vbaBadgeGate.CurrentCount == 0) return; // 調べている最中。その結果で足りる。

        if (checkWorkbookOpen)
        {
            var now = Environment.TickCount64;
            if (now - _lastVbaOpenCheckTick < VbaOpenCheckMinIntervalMs) checkWorkbookOpen = false;
        }

        var check = checkWorkbookOpen;
        _ = SafeHandler.RunAsync("VBA バッジの更新", () => RefreshVbaBadgesAsync(check));
    }

    /// <summary>
    /// 全 VBA プロジェクトのバッジを調べ直して反映する。
    /// <list type="bullet">
    /// <item>未反映・削除保留の件数は <c>GetLocalStatusAsync</c> で数える。ミラーと同期基準を比べるだけで、Excel には接続しない。</item>
    /// <item>ブックが開いているかは、<paramref name="checkWorkbookOpen"/> のときだけ、起動中のブックの一覧を1回だけ取って全プロジェクト分を決める。</item>
    /// </list>
    /// 一覧を取れなかったとき（Excel が応答しない等）は、開閉の表示を前回のまま保つ（調べられなかったことを
    /// 「閉じている」と決めつけない）。件数を数えるファイルの読み込みは、UI を止めないようスレッドプールで行う。
    /// </summary>
    public async Task RefreshVbaBadgesAsync(bool checkWorkbookOpen)
    {
        var service = _vbaSync;
        if (service?.IsSupported != true)
        {
            ReapplyVbaBadges();
            return;
        }

        await _vbaBadgeGate.WaitAsync().ConfigureAwait(true);
        try
        {
            var targets = Items.Where(i => i.Project.Vba is not null).Select(i => i.Project).ToList();
            if (targets.Count == 0) return;

            IReadOnlyList<VbaWorkbookInfo>? open = null;
            if (checkWorkbookOpen && _isVbaEnabled())
            {
                _lastVbaOpenCheckTick = Environment.TickCount64;
                var listed = await service.ListWorkbooksAsync().ConfigureAwait(true);
                if (listed.IsSuccess) open = listed.Value;
            }

            foreach (var project in targets)
            {
                _vbaBadgeCache.TryGetValue(project.Id, out var previous);
                var status = new VbaLocalStatus(previous?.Unpushed ?? 0, previous?.DeletePending ?? 0);
                if (Directory.Exists(project.Root))
                {
                    try
                    {
                        status = await Task.Run(() => service.GetLocalStatusAsync(project)).ConfigureAwait(true);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // 読めなかったときは前回の件数のまま。バッジのために利用者へ問い合わせることはしない。
                    }
                }

                var isOpen = previous?.WorkbookOpen;
                if (open is not null)
                {
                    isOpen = open.Any(w => VbaRotNames.SamePath(w.FullName, project.Vba!.WorkbookFullName));
                }

                _vbaBadgeCache[project.Id] = new VbaBadgeCacheEntry(status.UnpushedCount, status.DeletePendingCount, isOpen);
            }

            ReapplyVbaBadges();
        }
        finally
        {
            _vbaBadgeGate.Release();
        }
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
            // 同じ場所を再び追加したとき（既存のミラーを使う）、既に VBA の紐付けを持つプロジェクトの設定
            // （適用後の自動反映・反映後の保存）を黙って既定へ戻さない。変えるのはブックのパスだけ。
            Vba = p.Vba is { } existing
                ? existing with { WorkbookFullName = chosen.WorkbookFullName }
                : new VbaLink { WorkbookFullName = chosen.WorkbookFullName },
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
        if (RejectWhileVbaBusy()) return;
        var project = item.Project;

        var result = await _vbaSync.PullAsync(project).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            await ShowVbaFailureAsync("Excel から取り込めませんでした", project, result.Issues).ConfigureAwait(true);
            await RefreshVbaBadgesAsync(checkWorkbookOpen: true).ConfigureAwait(true); // ブックが閉じていたらバッジをグレーにする。
            return;
        }

        var pulled = result.Value;
        var resolved = await ResolveConflictsAsync(project, VbaConflictScene.ManualPull, pulled.ConflictItems).ConfigureAwait(true);
        await ShowWarningsAsync("Excel からの取込の注意", result.Issues.Where(i => i.Code != ErrorCode.E812)).ConfigureAwait(true);

        // 取り込み後の件数と、ブックの開閉（Excel と通信できたかどうか）を描き直す（仕様 7.6）。
        await RefreshVbaBadgesAsync(checkWorkbookOpen: true).ConfigureAwait(true);

        var imported = pulled.Pulled.Count + resolved.Adopted;
        if (imported > 0)
        {
            Notify?.Invoke($"Excel から {imported} 件取り込みました。");
        }
        else if (pulled.IsEmpty)
        {
            Notify?.Invoke("Excel 側に新しい変更はありませんでした。");
        }
    }

    private async Task PushSelectedAsync()
    {
        if (_vbaSync is null || SelectedItem is not { } item) return;
        if (RejectWhileVbaBusy()) return;
        await PushProjectAsync(item.Project, only: null).ConfigureAwait(true);
    }

    /// <summary>
    /// 「Excel 側の控えを見る」窓で控えをミラーへ戻したあとの「今すぐ Excel へ反映」。戻した 1 モジュールだけを、
    /// 手動の「Excel へ反映」と同じ流れ（衝突ダイアログ・失敗の案内を含む）で送る。
    /// </summary>
    private async Task PushRestoredFromRescueAsync(Project project, string relativePath)
    {
        if (_vbaSync is null) return;
        if (!IsVbaFeatureAvailable)
        {
            Notify?.Invoke("VBA 連携が設定で無効のため、Excel へ反映できません。");
            return;
        }

        if (RejectWhileVbaBusy()) return;
        await PushProjectAsync(project, new[] { relativePath }).ConfigureAwait(true);
    }

    private async Task ShowRescueForSelectedAsync()
    {
        if (SelectedItem is not { } item || ShowRescueAsync is null || CreateRescueViewModel is null) return;
        var project = item.Project;
        var viewModel = CreateRescueViewModel(project);
        if (viewModel is null) return;

        viewModel.IsBusy = IsVbaBusy;
        viewModel.PushModuleAsync = relative => PushRestoredFromRescueAsync(project, relative);
        await ShowRescueAsync(viewModel).ConfigureAwait(true);
        // 戻すと、ミラーが「未反映」になる。窓を閉じたあとにバッジへ反映する。
        await RefreshVbaBadgesAsync(checkWorkbookOpen: false).ConfigureAwait(true);
    }

    private async Task PushProjectAsync(Project project, IReadOnlyCollection<string>? only)
    {
        if (_vbaSync is null) return;

        var result = await _vbaSync.PushAsync(project, only).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            // 手動の Push は適用ではないので、取り消しは提案しない（仕様 7.2b）。未反映の印が残るので再試行できる。
            await ShowVbaFailureAsync("Excel へ反映できませんでした", project, result.Issues).ConfigureAwait(true);
            await RefreshVbaBadgesAsync(checkWorkbookOpen: true).ConfigureAwait(true);
            return;
        }

        var pushed = result.Value;
        var resolved = await ResolveConflictsAsync(project, VbaConflictScene.ManualPush, pushed.ConflictItems).ConfigureAwait(true);
        var problems = result.Issues.Where(i => i.Code != ErrorCode.E812).ToList();
        if (pushed.Failed.Count > 0 || problems.Count > 0)
        {
            await ShowWarningsAsync("Excel への反映の注意", problems).ConfigureAwait(true);
        }

        await RefreshVbaBadgesAsync(checkWorkbookOpen: true).ConfigureAwait(true);

        var written = pushed.WrittenCount + resolved.Overwritten;
        if (written > 0)
        {
            Notify?.Invoke(pushed.Created.Count > 0
                ? $"Excel へ {written} 件反映しました（新規作成 {pushed.Created.Count} 件）。"
                : $"Excel へ {written} 件反映しました。");
        }
        else if (pushed.Failed.Count == 0 && pushed.Conflicts.Count == 0)
        {
            Notify?.Invoke("Excel へ反映するものはありませんでした。");
        }
    }

    private async Task RestoreSelectedAsync()
    {
        if (_vbaSync is null || SelectedItem is not { } item) return;
        if (RejectWhileVbaBusy()) return;
        var project = item.Project;

        // 対象は「記録があるのにミラーのファイルが無いもの」（バッジの削除保留と同じ基準）。同期状態の DeletePending の
        // 印で拾うと、ファイルを消した直後は印がまだ保存されておらず、バッジは 1 件なのに復元が 0 件になる（不具合 5）。
        var pending = await _vbaSync.GetRestorablePathsAsync(project).ConfigureAwait(true);
        if (pending.Count == 0)
        {
            Notify?.Invoke("Excel から復元するモジュールはありません。");
            return;
        }

        var result = await _vbaSync.PullAsync(project, pending, restoreDeletePending: true).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            await ShowVbaFailureAsync("Excel から復元できませんでした", project, result.Issues).ConfigureAwait(true);
            return;
        }

        await RefreshVbaBadgesAsync(checkWorkbookOpen: true).ConfigureAwait(true);
        Notify?.Invoke(result.Value.Pulled.Count == 0
            ? "Excel 側にも該当のモジュールが無いため、復元できるものはありませんでした。"
            : $"Excel から {result.Value.Pulled.Count} 件復元しました。");
    }

    // ------------------------------------------------------------------
    // VBE で開く（7.6）。エディタの本文と差分ビューの右クリックから呼ぶ
    // ------------------------------------------------------------------

    /// <summary>
    /// 「VBE でこの行を開く」を出してよいか。選択中が VBA プロジェクト（機能が有効でこの環境で使える）で、
    /// 引数のファイルがそのミラーの中の同期対象のモジュールのときだけ true。普通のファイルプロジェクトでは
    /// メニュー項目そのものを出さない（表示条件の判定を ViewModel に置き、View は結果だけ見る）。
    /// </summary>
    /// <param name="relativePath">プロジェクトルートからの相対パス（区切りは バックスラッシュでもスラッシュでもよい）。</param>
    public bool CanShowInVbe(string? relativePath)
    {
        if (string.IsNullOrEmpty(relativePath) || !IsVbaFeatureAvailable) return false;
        if (SelectedItem is not { IsDisconnected: false } item || item.Project.Vba is null) return false;
        return VbaMirrorLayout.TryParse(relativePath.Replace('\\', '/'), out var parsed, out _) && parsed is not null;
    }

    /// <summary>
    /// VBE で、指定のモジュールのコードウィンドウを開いて指定行へ移動する。失敗したときは理由と対処を出す
    /// （ブックが開いていなければ「Excel で開く」ボタンつき）。行はミラーの行番号と同じ
    /// （ミラーは VBE の画面に見える本文そのもので、ヘッダ行が無いため）。
    /// </summary>
    public async Task ShowInVbeAsync(string relativePath, int line)
    {
        if (_vbaSync is null || SelectedItem is not { } item || item.Project.Vba is null) return;
        if (!VbaMirrorLayout.TryParse(relativePath.Replace('\\', '/'), out var parsed, out _) || parsed is null) return;

        var project = item.Project;
        var result = await _vbaSync.ShowInVbeAsync(project.Vba!.WorkbookFullName, parsed.ComponentName, Math.Max(1, line)).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            await ShowVbaFailureAsync("VBE で開けませんでした", project, result.Issues).ConfigureAwait(true);
            await RefreshVbaBadgesAsync(checkWorkbookOpen: true).ConfigureAwait(true);
        }
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
    /// 手動の取込・反映で衝突したモジュールを、衝突ダイアログ（仕様書_VBA連携 7.5）で利用者に選んでもらい、結果を実行する。
    /// 「Graft 側で上書き」は Excel 側の本文を <c>.rescue/</c> に控えてから Push、「Excel 側を採用」は Pull、「中止」は何もしない。
    /// </summary>
    private async Task<(int Adopted, int Overwritten)> ResolveConflictsAsync(
        Project project, VbaConflictScene scene, IReadOnlyList<VbaConflictItem> items)
    {
        if (items.Count == 0 || _vbaSync is null) return (0, 0);

        var request = new VbaConflictRequest(scene, items);
        var decision = (ShowConflictAsync is null ? null : await ShowConflictAsync(request).ConfigureAwait(true)) ?? request.DefaultDecision;
        if (decision.Aborted)
        {
            // 衝突が解決されずに止まる（E812。仕様 7.5）。何もせずに戻るので、理由だけ伝える。
            var issue = GraftIssue.Of(
                ErrorCode.E812,
                $"次のモジュールは、Excel 側とファイル側の両方が変更されているため、{(scene == VbaConflictScene.ManualPull ? "取り込みません" : "反映しません")}でした: " +
                string.Join("、", items.Select(i => i.RelativePath)) + "。",
                severity: Severity.Warning);
            await _dialogs.ShowMessageAsync("Excel 側とファイル側の衝突", FormatIssue(issue)).ConfigureAwait(true);
            return (0, 0);
        }

        var adopted = 0;
        var overwritten = 0;
        var adopt = decision.PathsWith(VbaConflictChoice.AdoptExcel);
        if (adopt.Count > 0)
        {
            var pulled = await _vbaSync.PullAsync(project, adopt, adoptConflicts: adopt).ConfigureAwait(true);
            if (pulled.IsSuccess) adopted = pulled.Value.Pulled.Count;
            else await ShowVbaFailureAsync("Excel から取り込めませんでした", project, pulled.Issues).ConfigureAwait(true);
        }

        var overwrite = decision.PathsWith(VbaConflictChoice.OverwriteExcel);
        if (overwrite.Count > 0)
        {
            var pushed = await _vbaSync.PushAsync(project, overwrite, overwrite).ConfigureAwait(true);
            if (pushed.IsSuccess)
            {
                overwritten = pushed.Value.WrittenCount;
                await ShowWarningsAsync("Excel への反映の注意", pushed.Issues.Where(i => i.Code != ErrorCode.E812 && i.Severity == Severity.Error))
                    .ConfigureAwait(true);
            }
            else
            {
                await ShowVbaFailureAsync("Excel へ反映できませんでした", project, pushed.Issues).ConfigureAwait(true);
            }
        }

        return (adopted, overwritten);
    }
}
