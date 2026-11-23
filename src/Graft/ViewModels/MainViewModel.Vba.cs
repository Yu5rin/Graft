using Graft.Core.Vba;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;

namespace Graft.ViewModels;

/// <summary>
/// <see cref="MainViewModel"/> の分割ファイル。Excel VBA 連携の窓口（仕様書_VBA連携 3.3）。
/// <para>
/// 【注入の作法】 <c>PlatformServices.Current</c> は差し替え口のない static で、ViewModel が直接参照している。
/// UI テストが <c>FakeVbaBridge</c> を使えるよう、<c>Logger</c> や <see cref="BeforeApplyAsync"/> と同じく
/// プロパティで渡す形にした。既定値は <c>PlatformServices.Current.Vba</c>。ブリッジが差し替わるたびに
/// <see cref="VbaSyncService"/> を作り直し、プロジェクトペインなど使う側へ渡し直す。
/// </para>
/// </summary>
public sealed partial class MainViewModel
{
    private IVbaBridge _vbaBridge = PlatformServices.Current.Vba;
    private VbaSyncService? _vbaSync;
    private AppPaths _vbaPaths = null!;

    /// <summary>Excel との窓口。UI テストは <c>FakeVbaBridge</c> を入れる。</summary>
    public IVbaBridge VbaBridge
    {
        get => _vbaBridge;
        set
        {
            _vbaBridge = value ?? throw new ArgumentNullException(nameof(value));
            RebuildVbaSync();
        }
    }

    /// <summary>
    /// リボンの同期（取込）でブックのファイルを読む処理（仕様書_ブックの情報と依頼の観点 16.4）。既定は実際のファイルを読む
    /// （<see cref="VbaRibbonPackageReader.Read(string, VbaRibbonLimits?)"/>）。テストは、Excel の代役（<c>FakeVbaBridge</c>）が
    /// 開いている「ブック」に実ファイルが無いので差し替える（<see cref="VbaRibbonReader"/> と同じ理由）。
    /// </summary>
    internal Func<string, VbaRibbonReadResult> VbaRibbonPackageReader { get; set; } =
        path => Graft.Core.Vba.VbaRibbonPackageReader.Read(path);

    /// <summary>
    /// リボンの書き換え（仕様書_ブックの情報と依頼の観点 16.5）で書き換えるファイルのパス（紐付けたブックの <c>FullName</c> から）。
    /// 本番はそのまま。テストは、Excel の代役が開いている「ブック」に実ファイルが無いので、一時ファイルの zip へ向ける
    /// （<see cref="VbaRibbonPackageReader"/> と同じ理由）。
    /// </summary>
    internal Func<string, string> VbaRibbonWorkbookFile { get; set; } = static path => path;

    /// <summary>
    /// リボンの書き換えの前の未保存の確認（16.5 の段階 1）。<see cref="BeforeVbaPullAsync"/> と同じ作りで、<c>ShellViewModel</c> が配線する。
    /// </summary>
    public Func<IReadOnlyList<string>, Task<VbaPullSaveOutcome>>? BeforeVbaRibbonRewriteAsync { get; set; }

    /// <summary>Pull／Push の窓口（<see cref="VbaBridge"/> から作る）。</summary>
    public VbaSyncService VbaSync => _vbaSync ??= CreateVbaSync();

    /// <summary>
    /// Pull の前の未保存の確認（仕様 7.2）。<c>BeforeApplyAsync</c> と同じ確認を、ShellViewModel が配線する
    /// （エディタのタブを知っているのは Shell 側のため）。未設定なら確認なしで Pull する。
    /// </summary>
    public Func<IReadOnlyList<string>, Task<VbaPullSaveOutcome>>? BeforeVbaPullAsync { get; set; }

    /// <summary>
    /// 控えを「ミラーへ戻す」前の未保存の確認（仕様書_ブックの情報と依頼の観点 7.5-1）。<see cref="BeforeVbaPullAsync"/> と
    /// 同じ確認の仕組みで、文面だけ戻す操作向けにする（ShellViewModel が配線する）。未設定なら確認なしで戻す。
    /// </summary>
    public Func<IReadOnlyList<string>, Task<VbaPullSaveOutcome>>? BeforeVbaRestoreAsync { get; set; }

    private VbaSyncService CreateVbaSync()
    {
        var paths = _vbaPaths;
        var service = new VbaSyncService(_vbaBridge, new VbaSyncStateStore(paths), paths, PlatformServices.Current.Trash)
        {
            Logger = Logger,
            // 差し替えやすいよう、プロパティの現在値を呼ぶ形で渡す（Shell が後から配線するため）。
            BeforePullAsync = paths2 => BeforeVbaPullAsync?.Invoke(paths2) ?? Task.FromResult(VbaPullSaveOutcome.Nothing),
        };
        service.Rescue.BeforeRestoreAsync = paths2 => BeforeVbaRestoreAsync?.Invoke(paths2) ?? Task.FromResult(VbaPullSaveOutcome.Nothing);
        // リボン（customUI）の同期（仕様書_ブックの情報と依頼の観点 16.3・16.4）。ブックの読み方と設定は、プロパティの現在値を
        // 呼ぶ形で渡す（テストが後から読み方を差し替え、設定画面が設定を差し替えても、作り直さずに効くように）。
        service.Ribbon.PackageReader = path => VbaRibbonPackageReader(path);
        service.Ribbon.IsEnabled = () => _settings.Vba.RibbonEditEnabled;
        // 書き換え（16.5。段階 F3）: 書くファイル・未保存の確認・控えの世代数と容量。どれも呼び出しのたびに現在値を読む。
        service.Ribbon.WorkbookFileOf = path => VbaRibbonWorkbookFile(path);
        service.Ribbon.BeforeRewriteAsync = paths2 => BeforeVbaRibbonRewriteAsync?.Invoke(paths2) ?? Task.FromResult(VbaPullSaveOutcome.Nothing);
        service.Ribbon.MaxBackupGenerations = () => _settings.Vba.WorkbookBackupMaxGenerations;
        service.Ribbon.MaxBackupTotalMB = () => _settings.Vba.WorkbookBackupMaxTotalMB;
        ApplyRescueLimits(service, _settings);
        service.WritingChanged += OnVbaWritingChanged;
        return service;
    }

    /// <summary>
    /// Excel へ書いている最中か（不具合 9）。取り消し・復元の後の反映、手動の反映、コンテキスト収集の「Graft 側で上書き」
    /// は適用ではなく <see cref="IsApplyInProgress"/> が立たないので、ウィンドウを閉じる確認の待ち・プロジェクトペインの
    /// コマンド・設定の反映の保留は、これも見る。
    /// </summary>
    public bool IsVbaWriting => _isVbaPushing || _vbaSync?.IsWritingToExcel == true;

    /// <summary>適用の最中か、Excel へ書いている最中か。この間は、書き込みと競合する操作（反映・復元・プロジェクトの削除や場所の変更）を受け付けない。</summary>
    private bool IsVbaBusy() => _isApplyInProgress || IsVbaWriting;

    /// <summary>
    /// 適用も Excel への書き込みも終わったときに、保留していた設定の反映を行う。適用の最中は設定の差し替えを保留する
    /// （<see cref="UpdateSettings"/>）が、Excel への書き込みも同じ理由（1 回の操作は開始時点の設定のまま最後まで一貫して
    /// 動く。時間切れの設定など）で保留する。
    /// </summary>
    private void FlushPendingSettingsIfIdle()
    {
        if (_isApplyInProgress || IsVbaWriting || _pendingSettings is not { } pending) return;
        _pendingSettings = null;
        ApplySettingsNow(pending);
    }

    private void OnVbaWritingChanged(object? sender, EventArgs e)
    {
        CommandRequery.Invalidate(); // 反映中は、反映・復元・プロジェクトの削除などのコマンドを無効にする。
        FlushPendingSettingsIfIdle();
    }

    private void RebuildVbaSync()
    {
        if (_vbaSync is not null) _vbaSync.WritingChanged -= OnVbaWritingChanged;
        _vbaSync = CreateVbaSync();
        ProjectPane.IsVbaBusy = IsVbaBusy;
        ProjectPane.VbaSync = _vbaSync;
        // 手動の取込・反映の衝突も、適用の流れと同じ衝突ダイアログで解決する（仕様書_VBA連携 7.5）。
        ProjectPane.ShowConflictAsync = request => ShowVbaConflictAsync is null
            ? Task.FromResult<Graft.Core.Vba.VbaConflictDecision?>(null)
            : ShowVbaConflictAsync(request);
        ProjectPane.IsVbaEnabledSetting = () => _settings.Vba.Enabled;
        // 控えの窓（仕様書_ブックの情報と依頼の観点 7.4）。いまの VbaSync と設定で作る。
        ProjectPane.CreateRescueViewModel = project => new VbaRescueViewModel(project, VbaSync.Rescue, _settings, _ui);
        // リボンの書き換えの確認（16.9）。差分の表示に設定と UI 機能が要るので、ここで作る。
        ProjectPane.CreateRibbonRewriteViewModel = plan => new VbaRibbonRewriteViewModel(plan, _settings, _ui);
        ProjectPane.VbaWorkbookBackupDirectoryOf = project => _vbaPaths.GetVbaWorkbookBackupDirectory(project.Id);
        ApplyVbaSettings(_settings);
    }

    /// <summary>
    /// <c>Vba.ComTimeoutMs</c>／<c>Vba.CallTimeoutMs</c> をブリッジへ伝える（次の呼び出しから効く）。
    /// 時間切れの概念を持たないブリッジ（Null など）には何もしない。
    /// </summary>
    private void ApplyVbaSettings(Settings settings)
    {
        if (_vbaBridge is IVbaBridgeTimeouts timeouts)
        {
            timeouts.Configure(settings.Vba.ComTimeoutMs, settings.Vba.CallTimeoutMs);
        }

        // 控えの世代数・容量（反映のたびに掃除で使う）。
        if (_vbaSync is not null) ApplyRescueLimits(_vbaSync, settings);

        // 機能の有効・無効（ボタン・メニュー・パレットの表示）を更新するため、ペインへ再通知する。
        ProjectPane.IsVbaEnabledSetting = () => _settings.Vba.Enabled;
    }

    private static void ApplyRescueLimits(VbaSyncService service, Settings settings)
    {
        service.Rescue.MaxGenerations = settings.Vba.RescueMaxGenerations;
        service.Rescue.MaxTotalMB = settings.Vba.RescueMaxTotalMB;
    }
}
