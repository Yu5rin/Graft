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

    /// <summary>Pull／Push の窓口（<see cref="VbaBridge"/> から作る）。</summary>
    public VbaSyncService VbaSync => _vbaSync ??= CreateVbaSync();

    /// <summary>
    /// Pull の前の未保存の確認（仕様 7.2）。<c>BeforeApplyAsync</c> と同じ確認を、ShellViewModel が配線する
    /// （エディタのタブを知っているのは Shell 側のため）。未設定なら確認なしで Pull する。
    /// </summary>
    public Func<IReadOnlyList<string>, Task<VbaPullSaveOutcome>>? BeforeVbaPullAsync { get; set; }

    private VbaSyncService CreateVbaSync()
    {
        var paths = _vbaPaths;
        var service = new VbaSyncService(_vbaBridge, new VbaSyncStateStore(paths), paths, PlatformServices.Current.Trash)
        {
            Logger = Logger,
            // 差し替えやすいよう、プロパティの現在値を呼ぶ形で渡す（Shell が後から配線するため）。
            BeforePullAsync = paths2 => BeforeVbaPullAsync?.Invoke(paths2) ?? Task.FromResult(VbaPullSaveOutcome.Nothing),
        };
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

        // 機能の有効・無効（ボタン・メニュー・パレットの表示）を更新するため、ペインへ再通知する。
        ProjectPane.IsVbaEnabledSetting = () => _settings.Vba.Enabled;
    }
}
