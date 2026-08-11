using FluentAssertions;
using Graft.Core;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 不具合3（設定画面「データ保存先の移行」完了ダイアログの「再起動」）の回帰テスト。
///
/// 再起動は多重起動防止（<see cref="SingleInstanceGuard"/>、名前付きMutex）と本質的に競合する。
/// 新プロセスは起動直後に同じ名前のMutexを取得しようとするため、旧プロセスがまだそれを
/// 保持したまま新プロセスを起動すると、新プロセスは「既に起動中」と誤検出してウィンドウを
/// 一切表示せず即座に終了してしまう。<see cref="RestartSequencer.RunAsync"/>が
/// 「後始末（Mutex解放を含む）の完了 → 新プロセス起動 → 旧プロセス終了」の順序を厳守することを、
/// 実際にプロセスを起動せずに（呼び出し順序を記録するだけの）デリゲートで検証する。
/// </summary>
public class RestartSequencerTests
{
    [Fact(DisplayName = "後始末（Mutex解放を含む）の完了→新プロセス起動→旧プロセス終了、の順に必ず呼ばれる")]
    public async Task 後始末完了後にのみ新プロセスを起動しその後で旧プロセスを終了する()
    {
        var calls = new List<string>();

        var started = await RestartSequencer.RunAsync(
            cleanupAndReleaseGuard: async () =>
            {
                // 実際のDisposeAsync同様、非同期の後始末（Mutex解放はこの中で起きる）を模す。
                await Task.Delay(10);
                calls.Add("cleanup-and-release-guard");
            },
            startNewProcess: () =>
            {
                calls.Add("start-new-process");
                return true;
            },
            shutdownCurrentProcess: () => calls.Add("shutdown-current-process"));

        started.Should().BeTrue();
        calls.Should().Equal("cleanup-and-release-guard", "start-new-process", "shutdown-current-process");
    }

    [Fact(DisplayName = "新プロセスの起動に失敗しても（falseを返しても）旧プロセスは必ず終了させる")]
    public async Task 新プロセス起動失敗でも旧プロセスは終了する()
    {
        var calls = new List<string>();

        var started = await RestartSequencer.RunAsync(
            cleanupAndReleaseGuard: () =>
            {
                calls.Add("cleanup-and-release-guard");
                return Task.CompletedTask;
            },
            startNewProcess: () =>
            {
                calls.Add("start-new-process");
                return false; // 実行ファイルが見つからない等の失敗を模す。
            },
            shutdownCurrentProcess: () => calls.Add("shutdown-current-process"));

        started.Should().BeFalse();
        calls.Should().Equal("cleanup-and-release-guard", "start-new-process", "shutdown-current-process");
    }

    [Fact(DisplayName = "新プロセスの起動が例外を投げても、旧プロセスの終了は必ず行われる（プロセスを落とさない）")]
    public async Task 新プロセス起動が例外を投げても旧プロセスは終了する()
    {
        var calls = new List<string>();

        var started = await RestartSequencer.RunAsync(
            cleanupAndReleaseGuard: () =>
            {
                calls.Add("cleanup-and-release-guard");
                return Task.CompletedTask;
            },
            startNewProcess: () =>
            {
                calls.Add("start-new-process");
                throw new InvalidOperationException("プロセス起動失敗を模した例外");
            },
            shutdownCurrentProcess: () => calls.Add("shutdown-current-process"));

        started.Should().BeFalse();
        calls.Should().Equal("cleanup-and-release-guard", "start-new-process", "shutdown-current-process");
    }

    [Fact(DisplayName = "新プロセスの起動は、後始末（cleanupAndReleaseGuard）が完了するまで呼ばれない")]
    public async Task 後始末完了前は新プロセスを起動しない()
    {
        var cleanupCompleted = false;
        var startedWhileCleanupIncomplete = false;

        await RestartSequencer.RunAsync(
            cleanupAndReleaseGuard: async () =>
            {
                await Task.Delay(30); // Mutex解放を含む後始末が完了するまでの間を模す。
                cleanupCompleted = true;
            },
            startNewProcess: () =>
            {
                // ここに来た時点でcleanupCompletedがfalseなら、Mutexがまだ解放されていない
                // タイミングで新プロセスを起動してしまったことになる（＝新プロセスが
                // 「既に起動中」と誤検出されうる不具合の再現）。
                startedWhileCleanupIncomplete = !cleanupCompleted;
                return true;
            },
            shutdownCurrentProcess: () => { });

        startedWhileCleanupIncomplete.Should().BeFalse(
            "新プロセスは旧プロセスの後始末（Mutex解放を含む）が完全に終わってから起動しなければならない");
    }
}
