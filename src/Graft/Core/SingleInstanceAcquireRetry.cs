namespace Graft.Core;

/// <summary>
/// 不具合2（「再起動」ボタンで終了はするが再起動しない）: 自己再起動の直後、旧プロセスの
/// 多重起動防止Mutex解放がOSレベルでまだ間に合っていない場合の保険としてのリトライ。
///
/// <see cref="Core.RestartSequencer"/>は「後始末（Mutex解放を含む）の完了 → 新プロセス起動」の
/// 順序を保証しているため通常はリトライ無しで成功するはずだが、OSによるハンドル解放の完了通知
/// にはわずかな遅延がありうる。自己再起動由来の起動（<c>isRestartLaunch: true</c>）に限り
/// 短時間・少回数のリトライを行い、通常の多重起動検知（利用者が2つ目を手動起動した場合、
/// <c>isRestartLaunch: false</c>）ではリトライを一切行わない（誤って通常の多重起動検知を
/// 遅らせないため）。
///
/// 実際のMutex操作（<see cref="Views.StartupCoordinator"/>・<see cref="ISingleInstanceGuard"/>）から
/// 切り離した純粋なロジックとして切り出し、偽の<c>tryAcquire</c>・<c>delay</c>を注入して
/// 単体テストできるようにする（<see cref="Core.RestartSequencer"/>と同じ考え方）。
/// </summary>
public static class SingleInstanceAcquireRetry
{
    /// <summary>既定のリトライ回数。</summary>
    public const int DefaultRetryCount = 5;

    /// <summary>既定のリトライ間隔。</summary>
    public static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// <paramref name="tryAcquire"/>を最大1回 + <paramref name="retryCount"/>回まで呼び出す。
    /// 最初の呼び出しで成功すれば即座にtrueを返す（<paramref name="isRestartLaunch"/>の値に
    /// 関わらずリトライは発生しない）。最初が失敗し、かつ<paramref name="isRestartLaunch"/>が
    /// falseの場合は即座にfalseを返す（リトライしない）。
    /// </summary>
    /// <param name="tryAcquire">Mutex等の取得を1回試みる処理。trueなら取得成功。</param>
    /// <param name="isRestartLaunch">自己再起動由来の起動かどうか。</param>
    /// <param name="retryCount">最初の失敗後にリトライする最大回数。</param>
    /// <param name="retryDelay">リトライ間隔。省略時は<see cref="DefaultRetryDelay"/>。</param>
    /// <param name="delay">
    /// テスト用に差し替え可能な待機処理。省略時は<see cref="Task.Delay(TimeSpan)"/>
    /// （実時間を進めずにリトライの回数・条件だけを検証できるようにするため）。
    /// </param>
    /// <remarks>
    /// 待機後の<paramref name="tryAcquire"/>呼び出しは、意図的に<c>ConfigureAwait(false)</c>を
    /// 使わず、呼び出し元の同期コンテキスト（Avaloniaの場合UIスレッド）へ戻ってから行う。
    /// 名前付きMutexの所有権はOSスレッド単位であり（<see cref="System.Threading.Mutex.
    /// ReleaseMutex"/>は取得したのと同じスレッドから呼ぶ必要がある）、リトライ中の取得を
    /// スレッドプールの別スレッドで行ってしまうと、その後の解放（<see cref="ISingleInstanceGuard.
    /// Dispose"/>は通常UIスレッドから呼ばれる）が
    /// 「Object synchronization method was called from an unsynchronized block of code」で
    /// 失敗しうる。呼び出し元の単一スレッドの同期コンテキストへ戻すことで、取得と解放が
    /// 常に同じスレッドで行われるようにする。
    /// </remarks>
    public static async Task<bool> TryAcquireAsync(
        Func<bool> tryAcquire,
        bool isRestartLaunch,
        int retryCount = DefaultRetryCount,
        TimeSpan? retryDelay = null,
        Func<TimeSpan, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(tryAcquire);
        if (tryAcquire()) return true;
        if (!isRestartLaunch) return false;

        var effectiveDelay = retryDelay ?? DefaultRetryDelay;
        var wait = delay ?? Task.Delay;

        for (var attempt = 0; attempt < retryCount; attempt++)
        {
            await wait(effectiveDelay).ConfigureAwait(true);
            if (tryAcquire()) return true;
        }

        return false;
    }
}
