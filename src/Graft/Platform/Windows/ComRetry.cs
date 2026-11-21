using System.Reflection;
using System.Runtime.InteropServices;
using Graft.Core.Vba;

namespace Graft.Platform.Windows;

/// <summary>
/// 再試行の上限（<c>Vba.ComTimeoutMs</c>）まで拒否され続けた。呼び出し側は E805 にする。
/// </summary>
public sealed class ComBusyException : Exception
{
    /// <summary>最後に拒否された COM の例外を包む。</summary>
    public ComBusyException(Exception inner)
        : base("Excel が呼び出しを受け付けませんでした（セル編集中・ダイアログ表示中など）。", inner)
    {
    }
}

/// <summary>
/// <c>RPC_E_CALL_REJECTED</c>（0x80010001）／<c>RPC_E_SERVERCALL_RETRYLATER</c>（0x8001010A）の再試行
/// （仕様書_VBA連携 5.3）。200ms 間隔で、<c>Vba.ComTimeoutMs</c>（既定 5 秒）まで繰り返す。
/// <para>
/// 【なぜ <c>IMessageFilter</c> を使わないか】 COM 標準の再試行（<c>IMessageFilter.RetryRejectedCall</c>）は、
/// 登録が STA スレッドごとで、実装量に見合わない。自前の再試行なら、時間切れの扱い（<see cref="ComBusyException"/>
/// → E805）を呼び出し側で揃えられる。拒否された呼び出しは相手で実行されていないので、書き込み系でも安全に再試行できる。
/// </para>
/// <para>
/// 時計と待ちは差し替えられるようにしてあり、Linux のテストで実際の待ちなしに挙動を確かめられる。
/// </para>
/// </summary>
public static class ComRetry
{
    /// <summary>再試行の間隔。</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(200);

    /// <summary>この例外が「待てば通る可能性がある」拒否か。リフレクション経由で包まれた場合も見る。</summary>
    public static bool IsRetryable(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is COMException com) return VbaComHResult.IsRetryLater(com.HResult);
            if (current is not TargetInvocationException) return false;
        }

        return false;
    }

    /// <summary>実時間で再試行する。</summary>
    public static T Run<T>(Func<T> action, int timeoutMs, CancellationToken ct = default)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        return Run(action, TimeSpan.FromMilliseconds(timeoutMs), ct, static t => Thread.Sleep(t), () => watch.Elapsed);
    }

    /// <summary>
    /// 時計と待ちを差し替えられる版。<paramref name="elapsed"/> は最初の試行からの経過時間。
    /// 次の待ちを入れると上限を超える場合は、待たずに <see cref="ComBusyException"/> を投げる
    /// （上限を超えて待ち続けないため）。
    /// </summary>
    public static T Run<T>(
        Func<T> action, TimeSpan budget, CancellationToken ct,
        Action<TimeSpan> sleep, Func<TimeSpan> elapsed)
    {
        ArgumentNullException.ThrowIfNull(action);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return action();
            }
            catch (Exception ex) when (IsRetryable(ex))
            {
                // 次の待ちを入れても上限に収まるときだけ再試行する。
                if (elapsed() + Interval > budget) throw new ComBusyException(ex);
                sleep(Interval);
            }
        }
    }
}
