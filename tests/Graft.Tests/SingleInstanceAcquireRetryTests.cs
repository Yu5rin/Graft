using FluentAssertions;
using Graft.Core;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 不具合2（「再起動」ボタンで終了はするが再起動しない）の回帰テスト。
///
/// 原因候補A（多重起動防止に新プロセスが弾かれている）への対処。<see cref="SingleInstanceAcquireRetry"/>
/// はMutex操作そのものから切り離した純粋なリトライロジックのため、偽の<c>tryAcquire</c>・
/// <c>delay</c>を注入して実時間を進めずに検証できる。
/// </summary>
public class SingleInstanceAcquireRetryTests
{
    [Fact(DisplayName = "最初の取得が成功すればisRestartLaunchの値に関わらずリトライしない")]
    public async Task 最初の取得が成功すればリトライしない()
    {
        var callCount = 0;
        var delayCount = 0;

        var acquired = await SingleInstanceAcquireRetry.TryAcquireAsync(
            tryAcquire: () => { callCount++; return true; },
            isRestartLaunch: true,
            delay: _ => { delayCount++; return Task.CompletedTask; });

        acquired.Should().BeTrue();
        callCount.Should().Be(1, "1回目で成功しているので追加の呼び出しは不要");
        delayCount.Should().Be(0, "1回目で成功していれば待つ必要はない");
    }

    [Fact(DisplayName = "不具合2回帰: 通常の多重起動検知（isRestartLaunch=false）は1回失敗したら即座に諦める")]
    public async Task 通常の多重起動検知は即座に諦める()
    {
        var callCount = 0;
        var delayCount = 0;

        var acquired = await SingleInstanceAcquireRetry.TryAcquireAsync(
            tryAcquire: () => { callCount++; return false; },
            isRestartLaunch: false,
            delay: _ => { delayCount++; return Task.CompletedTask; });

        acquired.Should().BeFalse();
        callCount.Should().Be(1, "isRestartLaunch=falseではリトライしないため、呼び出しは1回だけのはず");
        delayCount.Should().Be(0, "リトライしないので待機も発生しない");
    }

    [Fact(DisplayName = "不具合2回帰: 再起動由来（isRestartLaunch=true）は数回失敗しても諦めずリトライして成功できる")]
    public async Task 再起動由来なら数回失敗してもリトライして成功する()
    {
        var callCount = 0;
        var delayCount = 0;

        // 1回目・2回目・3回目は旧プロセスがまだMutexを解放していない状況を模し、
        // 4回目（＝1回目の呼び出し＋3回のリトライ）で解放が完了して成功する、という想定。
        var acquired = await SingleInstanceAcquireRetry.TryAcquireAsync(
            tryAcquire: () => { callCount++; return callCount >= 4; },
            isRestartLaunch: true,
            delay: _ => { delayCount++; return Task.CompletedTask; });

        acquired.Should().BeTrue("旧プロセスの解放が少し遅れても、再起動由来ならリトライの範囲内で成功できなければならない");
        callCount.Should().Be(4);
        delayCount.Should().Be(3, "1回目の失敗のあと、成功するまでの3回だけ待機するはず");
    }

    [Fact(DisplayName = "不具合2回帰: 再起動由来でもリトライ回数を使い切れば諦める（無限リトライにはしない）")]
    public async Task 再起動由来でもリトライ回数を使い切れば諦める()
    {
        var callCount = 0;

        var acquired = await SingleInstanceAcquireRetry.TryAcquireAsync(
            tryAcquire: () => { callCount++; return false; }, // 常に失敗（本当に別プロセスが起動中のケース）。
            isRestartLaunch: true,
            retryCount: 3,
            delay: _ => Task.CompletedTask);

        acquired.Should().BeFalse("本当に多重起動しているケースまで永久に待ち続けてはならない");
        callCount.Should().Be(4, "最初の1回 + リトライ3回 = 4回で諦めるはず");
    }

    [Fact(DisplayName = "リトライ間隔にはDefaultRetryDelayが使われる（delay省略時）")]
    public async Task 既定のリトライ間隔が使われる()
    {
        var observedDelays = new List<TimeSpan>();
        var callCount = 0;

        await SingleInstanceAcquireRetry.TryAcquireAsync(
            tryAcquire: () => { callCount++; return callCount >= 2; },
            isRestartLaunch: true,
            delay: d => { observedDelays.Add(d); return Task.CompletedTask; });

        observedDelays.Should().ContainSingle().Which.Should().Be(SingleInstanceAcquireRetry.DefaultRetryDelay);
    }

    [Fact(DisplayName = "tryAcquireにnullを渡すとArgumentNullExceptionになる")]
    public async Task tryAcquireがnullなら例外になる()
    {
        var act = () => SingleInstanceAcquireRetry.TryAcquireAsync(null!, isRestartLaunch: true);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}
