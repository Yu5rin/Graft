using Graft.Core;
using Graft.Core.Vba;

namespace Graft.Platform.Null;

/// <summary>
/// <see cref="IVbaBridge"/> の何もしない実装。Excel の COM を使えない環境（Linux・未対応 OS、および
/// P1 の段階の Windows）で使う。仕様書2.3のとおり利用不可でも例外を投げず、
/// <see cref="IPlatformService.IsSupported"/> が false であることを表明し、呼ばれたときは
/// E706（この環境では使えない機能です）の失敗を返す。UI は <c>IsSupported</c> を見て
/// 「Excel ブックから追加」や設定パネルの中身を出さない。
/// </summary>
public sealed class NullVbaBridge : IVbaBridge
{
    private const string Reason = "Excel VBA 連携は、この環境では使えません（Windows と Excel が必要です）。";

    public bool IsSupported => false;

    public string? UnsupportedReason => Reason;

    public Task<GraftResult<IReadOnlyList<VbaWorkbookInfo>>> ListOpenWorkbooksAsync(CancellationToken ct = default)
        => Task.FromResult(GraftResult<IReadOnlyList<VbaWorkbookInfo>>.Fail(ErrorCode.E706, Reason));

    public Task<GraftResult<IReadOnlyList<VbaComponentSnapshot>>> ReadComponentsAsync(
        string workbookFullName, IReadOnlyCollection<string>? names, CancellationToken ct = default)
        => Task.FromResult(GraftResult<IReadOnlyList<VbaComponentSnapshot>>.Fail(ErrorCode.E706, Reason));

    public Task<GraftResult<IReadOnlyList<VbaWriteOutcome>>> WriteComponentsAsync(
        string workbookFullName, IReadOnlyList<VbaComponentWrite> writes, CancellationToken ct = default)
        => Task.FromResult(GraftResult<IReadOnlyList<VbaWriteOutcome>>.Fail(ErrorCode.E706, Reason));

    public Task<GraftResult<bool>> SaveWorkbookAsync(string workbookFullName, CancellationToken ct = default)
        => Task.FromResult(GraftResult<bool>.Fail(ErrorCode.E706, Reason));

    public Task<GraftResult<bool>> ShowInVbeAsync(
        string workbookFullName, string componentName, int line, CancellationToken ct = default)
        => Task.FromResult(GraftResult<bool>.Fail(ErrorCode.E706, Reason));

    public Task<GraftResult<bool>> ActivateWorkbookAsync(string workbookFullName, CancellationToken ct = default)
        => Task.FromResult(GraftResult<bool>.Fail(ErrorCode.E706, Reason));

    public Task<GraftResult<VbaWorkbookInfoSnapshot>> ReadWorkbookInfoAsync(
        string workbookFullName, VbaWorkbookInfoOptions options, CancellationToken ct = default)
        => Task.FromResult(GraftResult<VbaWorkbookInfoSnapshot>.Fail(ErrorCode.E706, Reason));

    public Task<GraftResult<VbaWorkbookRewriteState>> InspectWorkbookForRewriteAsync(string workbookFullName, CancellationToken ct = default)
        => Task.FromResult(GraftResult<VbaWorkbookRewriteState>.Fail(ErrorCode.E706, Reason));

    public Task<GraftResult<VbaWorkbookReopenTicket>> SaveAndCloseWorkbookAsync(string workbookFullName, CancellationToken ct = default)
        => Task.FromResult(GraftResult<VbaWorkbookReopenTicket>.Fail(ErrorCode.E706, Reason));

    public Task<GraftResult<VbaWorkbookReopenOutcome>> ReopenWorkbookAsync(VbaWorkbookReopenTicket ticket, CancellationToken ct = default)
        => Task.FromResult(GraftResult<VbaWorkbookReopenOutcome>.Fail(ErrorCode.E706, Reason));

    // 券を出さない実装なので、捨てるものも無い。
    public Task DiscardReopenTicketAsync(VbaWorkbookReopenTicket ticket) => Task.CompletedTask;
}
