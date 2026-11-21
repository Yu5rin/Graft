using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Graft.Core;
using Graft.Core.Vba;

namespace Graft.Platform.Windows;

/// <summary>
/// <see cref="IVbaBridge"/> の Windows 実装（仕様書_VBA連携 5章）。Excel の COM を <c>dynamic</c> の遅延バインディング
/// （IDispatch）で呼び、PIA（<c>Microsoft.Office.Interop.Excel</c>）は参照しない。
/// <para>
/// 【遅延バインディングにする理由】 Office のバージョンと bitness に依存させず、単一ファイル配布と Linux ビルドを
/// 崩さないため。Graft（x64）から 32bit の Excel を操作しても、Excel はアウトプロセスの COM サーバーなので
/// プロセス間通信で動く（IDispatch のマーシャリングは標準で、型ライブラリの登録にも依存しない）。
/// </para>
/// <para>
/// 【呼び出しの作法】 すべての COM 呼び出しは専用スレッド（<see cref="StaWorker"/>）で直列に行い、呼び出し全体に
/// <c>Vba.CallTimeoutMs</c> の時間切れを設ける（E805）。拒否（RPC_E_CALL_REJECTED など）は <see cref="ComRetry"/> で
/// 200ms 間隔・<c>Vba.ComTimeoutMs</c> まで再試行する。取得した COM オブジェクトは <see cref="ComScope"/> で必ず解放し、
/// プロパティを連鎖して書かない（中間オブジェクトの解放漏れは Excel のプロセスが残る原因になる）。
/// </para>
/// <para>
/// 【判定の置き場所】 COM に触れない判定（ROT の表示名とパスの照合、HRESULT の分類、書き込み後の一致判定、
/// プロジェクトの状態値の解釈）は <c>Core/Vba</c> の純ロジックに置いてあり、ここはそれらを呼ぶだけ。
/// 実機でしか確かめられないのは、COM の呼び出し順・解放・実際の Excel と VBE の挙動である。
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsVbaBridge : IVbaBridge, IVbaBridgeTimeouts, IDisposable
{
    private const string ExcelProgId = "Excel.Application";

    // STA か MTA かは実機検証 9・12 の結果で決める（仕様 5.3）。決まるまでは STA。
    // イベントを受けず IMessageFilter も使わないので、自前のメッセージループは要らない。
    private readonly StaWorker _worker = new(ApartmentState.STA);
    private readonly Lazy<bool> _excelInstalled = new(DetectExcelInstalled);
    private int _comTimeoutMs = 5000;
    private int _callTimeoutMs = 30000;

    /// <summary>
    /// Windows で、Excel が登録されていれば使える。登録の有無は ProgID の引き当てで調べる（レジストリを読むだけで
    /// Excel は起動しない）。調べられないときは、機能を隠して困るよりは出して案内する側に倒して true にする。
    /// </summary>
    public bool IsSupported => OperatingSystem.IsWindows() && _excelInstalled.Value;

    public string? UnsupportedReason => IsSupported
        ? null
        : "Excel VBA 連携は、Microsoft Excel がインストールされていない環境では使えません。";

    /// <inheritdoc />
    public void Configure(int comTimeoutMs, int callTimeoutMs)
    {
        Volatile.Write(ref _comTimeoutMs, Math.Max(200, comTimeoutMs));
        Volatile.Write(ref _callTimeoutMs, Math.Max(1000, callTimeoutMs));
    }

    public void Dispose() => _worker.Dispose();

    private static bool DetectExcelInstalled()
    {
        try
        {
            return Type.GetTypeFromProgID(ExcelProgId, throwOnError: false) is not null;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            return true;
        }
    }

    // ------------------------------------------------------------------
    // IVbaBridge
    // ------------------------------------------------------------------

    public Task<GraftResult<IReadOnlyList<VbaWorkbookInfo>>> ListOpenWorkbooksAsync(CancellationToken ct = default)
        => InvokeAsync(token => ListWorkbooksCore(token), ct);

    public Task<GraftResult<IReadOnlyList<VbaComponentSnapshot>>> ReadComponentsAsync(
        string workbookFullName, IReadOnlyCollection<string>? names, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workbookFullName);
        return InvokeAsync(token => ReadComponentsCore(workbookFullName, names, token), ct);
    }

    public async Task<GraftResult<IReadOnlyList<VbaWriteOutcome>>> WriteComponentsAsync(
        string workbookFullName, IReadOnlyList<VbaComponentWrite> writes, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workbookFullName);
        ArgumentNullException.ThrowIfNull(writes);

        // 仕事の途中経過（書き終えたモジュールの結果）を、時間切れで諦めた後にも返せるよう共有しておく。
        var progress = new WriteProgress();
        return await InvokeAsync(
            token => WriteComponentsCore(workbookFullName, writes, progress, token),
            ct,
            onTimeout: () => progress.BuildTimeoutResult(writes)).ConfigureAwait(false);
    }

    public Task<GraftResult<bool>> SaveWorkbookAsync(string workbookFullName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workbookFullName);
        return InvokeAsync(token => SaveWorkbookCore(workbookFullName, token), ct);
    }

    public Task<GraftResult<bool>> ShowInVbeAsync(
        string workbookFullName, string componentName, int line, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workbookFullName);
        ArgumentException.ThrowIfNullOrWhiteSpace(componentName);
        return InvokeAsync(token => ShowInVbeCore(workbookFullName, componentName, line, token), ct);
    }

    public Task<GraftResult<bool>> ActivateWorkbookAsync(string workbookFullName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workbookFullName);
        return InvokeAsync(token => ActivateWorkbookCore(workbookFullName, token), ct);
    }

    // ------------------------------------------------------------------
    // 共通の実行枠
    // ------------------------------------------------------------------

    /// <summary>
    /// 専用スレッドで <paramref name="work"/> を実行し、時間切れと COM の例外を <see cref="GraftResult{T}"/> の問題へ
    /// 写す。例外のまま呼び出し元へ漏らさない（利用できない・失敗は例外ではなく問題で返す。IVbaBridge の方針）。
    /// </summary>
    private async Task<GraftResult<T>> InvokeAsync<T>(
        Func<CancellationToken, GraftResult<T>> work, CancellationToken ct, Func<GraftResult<T>>? onTimeout = null)
    {
        var callTimeout = TimeSpan.FromMilliseconds(Volatile.Read(ref _callTimeoutMs));
        try
        {
            return await _worker.RunAsync(work, callTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return onTimeout?.Invoke() ?? GraftResult<T>.Fail(ComFailures.Unresponsive(callTimeout));
        }
        catch (ComBusyException)
        {
            return GraftResult<T>.Fail(ComFailures.Busy());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or MemberAccessException
                                       or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException
                                       or InvalidComObjectException)
        {
            return GraftResult<T>.Fail(ComFailures.FromException(ex));
        }
    }

    /// <summary>1回の呼び出しの間だけ使う、COM オブジェクトの入れ物と再試行の設定。</summary>
    private sealed class Session : IDisposable
    {
        private readonly int _comTimeoutMs;
        private readonly CancellationToken _abandoned;

        public Session(int comTimeoutMs, CancellationToken abandoned)
        {
            _comTimeoutMs = comTimeoutMs;
            _abandoned = abandoned;
        }

        public ComScope Scope { get; } = new();

        /// <summary>時間切れで見捨てられた呼び出しか。複数モジュールの繰り返しの境目で見て、続きを進めない。</summary>
        public bool IsAbandoned => _abandoned.IsCancellationRequested;

        public CancellationToken Abandoned => _abandoned;

        /// <summary>
        /// 見捨てられても中断しない入れ物（拒否の再試行の上限だけは同じ）。1 モジュールの「控え→消す→書く→読み直す」
        /// のように、途中で止めると Excel 側が壊れたまま残る処理で使う。取得した COM オブジェクトはこの入れ物が
        /// 別に持つので、使い終えたら Dispose する。
        /// </summary>
        public Session Committed() => new(_comTimeoutMs, CancellationToken.None);

        /// <summary>拒否されたときの再試行の上限（<c>Vba.ComTimeoutMs</c>）。</summary>
        public int ComTimeoutMs => _comTimeoutMs;

        /// <summary>COM の呼び出しを、拒否されたら再試行しながら実行する。</summary>
        public T Call<T>(Func<T> action) => ComRetry.Run(action, _comTimeoutMs, _abandoned);

        public object? Own(object? value) => Scope.Own(value);

        public void Dispose() => Scope.Dispose();
    }

    private Session NewSession(CancellationToken abandoned)
        => new(Volatile.Read(ref _comTimeoutMs), abandoned);
}
