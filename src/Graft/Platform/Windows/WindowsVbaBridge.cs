using System.Runtime.Versioning;
using Graft.Core;
using Graft.Platform.Null;

namespace Graft.Platform.Windows;

/// <summary>
/// <see cref="IVbaBridge"/> の Windows 実装（<b>段階 P1 では未実装のスタブ</b>）。
/// <para>
/// 仕様書_VBA連携 12章の段階分けに従い、P1 では COM 層を入れずに、契約（<see cref="IVbaBridge"/>）・
/// 純ロジック（<c>Core/Vba</c>）・設定・データ保存先の対応だけを先に固める。P2 で ROT の列挙・専用スレッド
/// （<c>ComWorker</c>）・時間切れ・再試行・読み書きをここへ実装し、<see cref="IsSupported"/> を true にする。
/// それまでは何もしない実装へ委譲し、UI からは機能ごと見えない状態にしておく
/// （Windows 実機でも「Excel ブックから追加」が現れて、押すと何も起きない、という半端な状態を作らない）。
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsVbaBridge : IVbaBridge
{
    private readonly NullVbaBridge _inner = new();

    public bool IsSupported => false;

    public string? UnsupportedReason => "Excel VBA 連携は、この版ではまだ使えません（準備中です）。";

    public Task<GraftResult<IReadOnlyList<VbaWorkbookInfo>>> ListOpenWorkbooksAsync(CancellationToken ct = default)
        => _inner.ListOpenWorkbooksAsync(ct);

    public Task<GraftResult<IReadOnlyList<VbaComponentSnapshot>>> ReadComponentsAsync(
        string workbookFullName, IReadOnlyCollection<string>? names, CancellationToken ct = default)
        => _inner.ReadComponentsAsync(workbookFullName, names, ct);

    public Task<GraftResult<IReadOnlyList<VbaWriteOutcome>>> WriteComponentsAsync(
        string workbookFullName, IReadOnlyList<VbaComponentWrite> writes, CancellationToken ct = default)
        => _inner.WriteComponentsAsync(workbookFullName, writes, ct);

    public Task<GraftResult<bool>> SaveWorkbookAsync(string workbookFullName, CancellationToken ct = default)
        => _inner.SaveWorkbookAsync(workbookFullName, ct);

    public Task<GraftResult<bool>> ShowInVbeAsync(
        string workbookFullName, string componentName, int line, CancellationToken ct = default)
        => _inner.ShowInVbeAsync(workbookFullName, componentName, line, ct);

    public Task<GraftResult<bool>> ActivateWorkbookAsync(string workbookFullName, CancellationToken ct = default)
        => _inner.ActivateWorkbookAsync(workbookFullName, ct);
}
