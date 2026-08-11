namespace Graft.Core;

/// <summary>
/// 不具合3: 「再起動」ボタンでの自己再起動の順序保証を、Avalonia・OS API から切り離して
/// 単体テストできる形に切り出したもの。
///
/// 【なぜ順序が重要か】多重起動防止（<see cref="SingleInstanceGuard"/>、名前付きMutex）と
/// 自己再起動は本質的に競合する。新プロセスは起動直後に同じ名前のMutexを取得しようとするが、
/// 旧プロセスがまだそれを保持したままだと「既に起動中」と誤検出され、新プロセスはウィンドウを
/// 一切表示せず即座に終了してしまう（<see cref="Views.StartupCoordinator.TryAcquireSingleInstance"/>
/// が意図した「多重起動を防ぐ」動作そのものが、再起動の意図に反して発動してしまう）。
/// これを避けるため、後始末（レイアウト保存・パッチキュー保存・Mutex解放等、
/// <see cref="Views.StartupCoordinator.DisposeAsync"/>が担う一連の処理）が完全に完了したことを
/// 確認してから新プロセスを起動する。
///
/// 実際の後始末・プロセス起動・旧プロセスの終了はAvalonia/OS依存のため、呼び出し側
/// （<c>Graft.App</c>）がデリゲートとして注入する。ここでは「その3つを正しい順序で、
/// 新プロセス起動の失敗があっても旧プロセスの終了だけは必ず行う」という骨組みだけを保証する。
/// </summary>
public static class RestartSequencer
{
    /// <param name="cleanupAndReleaseGuard">
    /// 後始末一式（多重起動防止Mutexの解放を含む）。この完了を待ってから
    /// <paramref name="startNewProcess"/>を呼ぶ。
    /// </param>
    /// <param name="startNewProcess">
    /// 新プロセスの起動を試み、成功したかどうかを返す。例外を投げても
    /// <paramref name="shutdownCurrentProcess"/>の呼び出しは妨げない（起動に失敗したからといって
    /// 旧プロセスをMutexを解放したまま居座らせない）。
    /// </param>
    /// <param name="shutdownCurrentProcess">旧プロセスを終了させる（<paramref name="startNewProcess"/>の成否によらず必ず呼ぶ）。</param>
    /// <returns>新プロセスの起動に成功していれば true。</returns>
    public static async Task<bool> RunAsync(
        Func<Task> cleanupAndReleaseGuard, Func<bool> startNewProcess, Action shutdownCurrentProcess)
    {
        ArgumentNullException.ThrowIfNull(cleanupAndReleaseGuard);
        ArgumentNullException.ThrowIfNull(startNewProcess);
        ArgumentNullException.ThrowIfNull(shutdownCurrentProcess);

        // 1. 後始末＋Mutex解放を完了させる（新プロセス起動より必ず前）。
        await cleanupAndReleaseGuard().ConfigureAwait(true);

        // 2. Mutexが解放された状態で新プロセスを起動する。
        bool started;
        try
        {
            started = startNewProcess();
        }
        catch
        {
            // 起動に失敗しても、旧プロセスは必ず終了させる（下の3.へ進む）。
            started = false;
        }

        // 3. 新プロセスの起動成否によらず、旧プロセスは終了させる。
        shutdownCurrentProcess();

        return started;
    }
}
