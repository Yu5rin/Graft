using System.Diagnostics;
using FluentAssertions;
using Graft.Platform.Windows;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// 不具合 10（待ち行列で待った時間も時間切れに数えられ、動いているスレッドが捨てられる）の回帰テスト。
/// 修正前は、時間切れ 1 秒で、前の仕事が 0.7 秒・次の仕事が 0.6 秒のとき、次の仕事は 0.7 秒に始まって 1.0 秒で
/// <see cref="TimeoutException"/> になり（<c>abandoned=1</c>）、まだ動いているスレッドが捨てられて、
/// その後の仕事が同時に動いていた。
/// </summary>
public class VbaStaWorkerQueueFixTests
{
    [Fact(DisplayName = "不具合10 再現: 時間切れ 1 秒、前の仕事 0.7 秒、次の仕事 0.6 秒でも、次の仕事は時間切れにならず、スレッドも捨てない")]
    public async Task 待ち行列の待ちは時間切れに数えない()
    {
        using var worker = new StaWorker(ApartmentState.Unknown);
        var timeout = TimeSpan.FromSeconds(1);

        var a = worker.RunAsync(_ => { Thread.Sleep(700); return "a"; }, timeout);
        var b = worker.RunAsync(_ => { Thread.Sleep(600); return "b"; }, timeout);

        (await a).Should().Be("a");
        (await b).Should().Be("b");
        worker.AbandonedCount.Should().Be(0, "動いているスレッドを捨てない");
    }

    [Fact(DisplayName = "不具合10: 後ろで待っていた仕事が始まったあとに戻らなければ、始まりから数えた時間切れで諦める（待った時間の分は延びる）")]
    public async Task 時間切れは実行の開始から数える()
    {
        using var worker = new StaWorker(ApartmentState.Unknown);
        using var release = new ManualResetEventSlim(false);
        var timeout = TimeSpan.FromMilliseconds(600);

        // 前の仕事（約 400ms）は時間切れ内に終わる。後ろの仕事は、その終わりから 600ms 戻らない。
        var first = worker.RunAsync(_ => { Thread.Sleep(400); return 1; }, timeout);
        var watch = Stopwatch.StartNew();
        var hangs = worker.RunAsync(_ => { release.Wait(); return 2; }, timeout);

        (await first).Should().Be(1);
        var act = () => hangs;
        await act.Should().ThrowAsync<TimeoutException>();
        watch.Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(850),
            "前の仕事の待ち（約 400ms）を数えずに、始まりから 600ms 経ってから諦める。待ちを数えていた以前は約 600ms で諦めた");
        worker.AbandonedCount.Should().Be(1);

        release.Set();
    }

    [Fact(DisplayName = "不具合10: まだ始まっていない仕事は、呼び出し側のキャンセルで始まらずに捨てられる（動いている前の仕事は捨てない）")]
    public async Task 待っている間のキャンセルは始まらずに捨てられる()
    {
        using var worker = new StaWorker(ApartmentState.Unknown);
        using var cts = new CancellationTokenSource();
        var ranSecond = false;

        var first = worker.RunAsync(_ => { Thread.Sleep(300); return 1; }, TimeSpan.FromSeconds(10));
        var second = worker.RunAsync(_ => { ranSecond = true; return 2; }, TimeSpan.FromSeconds(10), cts.Token);
        cts.CancelAfter(50);

        await ((Func<Task>)(() => second)).Should().ThrowAsync<OperationCanceledException>();
        (await first).Should().Be(1);
        await worker.RunAsync(_ => 3, TimeSpan.FromSeconds(5)); // 前の仕事が終わったあとの順番を通す。
        ranSecond.Should().BeFalse("待っている間にキャンセルされた仕事は実行しない");
        worker.AbandonedCount.Should().Be(0);
    }

    [Fact(DisplayName = "不具合10: 前の仕事が固まっても、後ろで待っていた仕事は、前の仕事の時間切れのあとに新しいスレッドで始まり、自分の時間切れで数え直す")]
    public async Task 固まった仕事の後ろの仕事は新しいスレッドで数え直す()
    {
        using var worker = new StaWorker(ApartmentState.Unknown);
        using var release = new ManualResetEventSlim(false);
        var timeout = TimeSpan.FromMilliseconds(300);

        var stuck = worker.RunAsync(_ => { release.Wait(); return 0; }, timeout);
        var queued = worker.RunAsync(_ => { Thread.Sleep(100); return "done"; }, timeout);

        await ((Func<Task>)(() => stuck)).Should().ThrowAsync<TimeoutException>();
        (await queued).Should().Be("done", "固まった仕事の時間切れを待っていた分は、後ろの仕事の時間切れに数えない");
        worker.AbandonedCount.Should().Be(1);

        release.Set();
    }
}
