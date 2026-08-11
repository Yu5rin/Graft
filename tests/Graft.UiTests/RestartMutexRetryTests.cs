using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Core;
using Graft.Infra;
using Graft.Views;

namespace Graft.UiTests;

/// <summary>
/// 不具合2（「再起動」ボタンで終了はするが再起動しない）の回帰テスト。
///
/// 原因候補A（多重起動防止に新プロセスが弾かれている）への対処。純粋なリトライロジック自体は
/// tests/Graft.Tests/SingleInstanceAcquireRetryTests.csで検証済み。ここでは
/// <see cref="StartupCoordinator.TryAcquireSingleInstanceAsync"/>が実際にそのロジックへ正しく
/// 配線されていること（Mutex名の組み立て・<see cref="ISingleInstanceGuard"/>との連携）を、
/// 実物のOS名前付きMutexで検証する。
///
/// 別プロセスでMutexを保持する状況を模すため、<see cref="StartupCoordinator"/>の内部が使う
/// <see cref="ISingleInstanceGuard"/>（<c>Graft.Platform.PlatformServices.Current</c>、
/// プロセス内で共有されるシングルトン）は使わず、<see cref="SingleInstanceGuard"/>を直接
/// 独立したMutexハンドルとして保持する（テストプロセス内で複数の<see cref="StartupCoordinator"/>を
/// 使うと、共有されたシングルトンの内部フィールドを互いに上書きしてしまい、実際の別プロセス間
/// 競合を正しく再現できないため）。
/// </summary>
public class RestartMutexRetryTests : IDisposable
{
    // StartupCoordinator.cs の MutexNamePrefix（private const）と同じ文字列。
    // SingleInstanceGuard.BuildInstanceScopedNameは決定的なため、同じprefix・baseDirectoryを
    // 渡せばStartupCoordinatorが内部で組み立てるものと同じMutex名になる。
    private const string MutexNamePrefix = "Graft.SingleInstance.";

    private readonly string _baseDirectory =
        Path.Combine(Path.GetTempPath(), "graft-ui-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_baseDirectory)) Directory.Delete(_baseDirectory, recursive: true);
        }
        catch (IOException)
        {
            // 後始末の失敗は検証結果に影響しない。
        }

        GC.SuppressFinalize(this);
    }

    [AvaloniaFact(DisplayName = "不具合2回帰: 再起動由来ならMutex解放待ちをリトライしてStartupCoordinatorが取得できる")]
    public async Task 再起動由来ならStartupCoordinatorがリトライして取得できる()
    {
        var appPaths = new AppPaths(_baseDirectory);
        var mutexName = SingleInstanceGuard.BuildInstanceScopedName(MutexNamePrefix, appPaths.BaseDirectory);

        // 「別プロセス」役として、StartupCoordinatorとは独立したMutexハンドルで保持する。
        var otherProcessGuard = SingleInstanceGuard.TryAcquire(mutexName);
        otherProcessGuard.Should().NotBeNull("前提として、まず「旧プロセス」役がMutexを保持できている必要がある");

        // 旧プロセスの後始末（Mutex解放）が少し遅れて完了する状況を模す。
        // 名前付きMutexの所有権はOSスレッド単位（ReleaseMutexは取得したのと同じスレッドから
        // 呼ぶ必要がある）のため、Task.Run（スレッドプール）ではなくAvaloniaの単一ディスパッチャ
        // スレッドの同期コンテキストへ戻る形で遅延解放する（SingleInstanceAcquireRetryの
        // ConfigureAwait(true)の注釈と同じ理由）。
        var releaseTask = ReleaseAfterDelayAsync(otherProcessGuard!);

        var coordinator = new StartupCoordinator(_baseDirectory);
        var acquired = await coordinator.TryAcquireSingleInstanceAsync(isRestartLaunch: true);

        await releaseTask;

        acquired.Should().BeTrue(
            "自己再起動由来の新プロセスは、旧プロセスのMutex解放を短時間リトライして待てなければならない");

        await coordinator.DisposeAsync();
    }

    [AvaloniaFact(DisplayName = "不具合2回帰（対照）: 通常の多重起動検知はリトライせず即座に失敗する")]
    public async Task 通常の多重起動検知はリトライしない()
    {
        var appPaths = new AppPaths(_baseDirectory);
        var mutexName = SingleInstanceGuard.BuildInstanceScopedName(MutexNamePrefix, appPaths.BaseDirectory);

        var otherProcessGuard = SingleInstanceGuard.TryAcquire(mutexName);
        otherProcessGuard.Should().NotBeNull();
        try
        {
            var coordinator = new StartupCoordinator(_baseDirectory);
            var acquired = await coordinator.TryAcquireSingleInstanceAsync(isRestartLaunch: false);

            acquired.Should().BeFalse("利用者が2つ目を手動起動した通常の多重起動検知では取得できてはならない");
        }
        finally
        {
            otherProcessGuard!.Dispose();
        }
    }

    private static async Task ReleaseAfterDelayAsync(SingleInstanceGuard guard)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(250));
        guard.Dispose();
    }
}
