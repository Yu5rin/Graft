using FluentAssertions;
using Graft.Core.Vba;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// <see cref="VbaSaveCoalescer"/>（保存の通知のまとめ。仕様書_ブックの情報と依頼の観点 8.3）。時計と遅延を差し替えて、
/// 500ms の境界を実際には待たずに確かめる。
/// </summary>
public class VbaSaveCoalescerTests
{
    /// <summary>手で進める時計と、手で解く遅延。</summary>
    private sealed class Rig
    {
        private readonly List<(TimeSpan Wait, TaskCompletionSource Done)> _delays = new();
        private readonly object _lock = new();
        private DateTimeOffset _now = new(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);

        public Rig(Func<VbaSaveBatch, int, Task<bool>>? flush = null)
        {
            Flush = flush ?? ((_, _) => Task.FromResult(true));
            Coalescer = new VbaSaveCoalescer(
                async (batch, _) =>
                {
                    int count;
                    lock (_lock)
                    {
                        Batches.Add(batch);
                        count = Batches.Count;
                    }

                    return await Flush(batch, count).ConfigureAwait(false);
                },
                clock: () => _now,
                delay: (wait, _) =>
                {
                    var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    lock (_lock) _delays.Add((wait, source));
                    return source.Task;
                },
                onError: ex => Errors.Add(ex));
        }

        public VbaSaveCoalescer Coalescer { get; }

        public Func<VbaSaveBatch, int, Task<bool>> Flush { get; set; }

        public List<VbaSaveBatch> Batches { get; } = new();

        public List<Exception> Errors { get; } = new();

        public int BatchCount
        {
            get
            {
                lock (_lock) return Batches.Count;
            }
        }

        public int DelayCount
        {
            get
            {
                lock (_lock) return _delays.Count;
            }
        }

        public TimeSpan LastWait
        {
            get
            {
                lock (_lock) return _delays[^1].Wait;
            }
        }

        public void Advance(TimeSpan span) => _now += span;

        /// <summary>待っている遅延をすべて解く（時計は進めない。進めるのは <see cref="Advance"/>）。</summary>
        public void ReleaseDelays()
        {
            List<(TimeSpan Wait, TaskCompletionSource Done)> copy;
            lock (_lock)
            {
                copy = _delays.ToList();
                _delays.Clear();
            }

            foreach (var (_, done) in copy) done.TrySetResult();
        }

        /// <summary>ポンプが次の待ちに入る（＝遅延が 1 件以上たまる）まで待つ。</summary>
        public async Task WaitForDelayAsync()
        {
            for (var i = 0; i < 400 && DelayCount < 1; i++) await Task.Delay(5);
            DelayCount.Should().BeGreaterThanOrEqualTo(1);
        }

        public async Task WaitForBatchesAsync(int count)
        {
            for (var i = 0; i < 400 && BatchCount < count; i++) await Task.Delay(5);
            BatchCount.Should().BeGreaterThanOrEqualTo(count);
        }
    }

    [Fact(DisplayName = "1 回の保存: 500ms 静かになるまで送らず、500ms 経ったら 1 回だけ送る")]
    public async Task 静かになってから送る()
    {
        var rig = new Rig();
        rig.Coalescer.Add("p1", "Modules/Module1.bas");

        await rig.WaitForDelayAsync();
        rig.LastWait.Should().Be(TimeSpan.FromMilliseconds(500));
        rig.BatchCount.Should().Be(0, "まだ静かになっていない");

        rig.Advance(TimeSpan.FromMilliseconds(499));
        rig.ReleaseDelays();
        await rig.WaitForDelayAsync();
        rig.BatchCount.Should().Be(0, "499ms ではまだ送らない（境界）");
        rig.LastWait.Should().Be(TimeSpan.FromMilliseconds(1));

        rig.Advance(TimeSpan.FromMilliseconds(1));
        rig.ReleaseDelays();
        await rig.Coalescer.WhenIdleAsync();

        rig.Batches.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new VbaSaveBatch("p1", new[] { "Modules/Module1.bas" }));
    }

    [Fact(DisplayName = "連続保存: 同じモジュールの通知が続くと静かな時間が数え直され、最後に 1 回だけ送る")]
    public async Task 連続保存は1回になる()
    {
        var rig = new Rig();
        rig.Coalescer.Add("p1", "Modules/Module1.bas");
        await rig.WaitForDelayAsync();

        for (var i = 0; i < 5; i++)
        {
            rig.Advance(TimeSpan.FromMilliseconds(300));
            rig.Coalescer.Add("p1", "Modules/Module1.bas");
            rig.ReleaseDelays();
            await rig.WaitForDelayAsync();
            rig.BatchCount.Should().Be(0, "300ms おきに保存が続く間は送らない");
        }

        rig.Advance(TimeSpan.FromMilliseconds(500));
        rig.ReleaseDelays();
        await rig.Coalescer.WhenIdleAsync();

        rig.Batches.Should().ContainSingle().Which.RelativePaths.Should().Equal("Modules/Module1.bas");
    }

    [Fact(DisplayName = "複数のモジュールの保存: 1 回の送信にまとまる（重複は 1 件）")]
    public async Task 複数モジュールが1回にまとまる()
    {
        var rig = new Rig();
        rig.Coalescer.Add("p1", "Modules/Module1.bas");
        rig.Coalescer.Add("p1", "Classes/Cls1.cls");
        rig.Coalescer.Add("p1", "modules/module1.bas");
        await rig.WaitForDelayAsync();

        rig.Advance(TimeSpan.FromMilliseconds(500));
        rig.ReleaseDelays();
        await rig.Coalescer.WhenIdleAsync();

        rig.Batches.Should().ContainSingle().Which.RelativePaths.Should().Equal("Modules/Module1.bas", "Classes/Cls1.cls");
    }

    [Fact(DisplayName = "プロジェクトが違えば、別々の送信になる")]
    public async Task プロジェクトごとに分かれる()
    {
        var rig = new Rig();
        rig.Coalescer.Add("p1", "Modules/Module1.bas");
        rig.Coalescer.Add("p2", "Modules/Module1.bas");
        await rig.WaitForDelayAsync();

        rig.Advance(TimeSpan.FromMilliseconds(500));
        rig.ReleaseDelays();
        await rig.Coalescer.WhenIdleAsync();

        rig.Batches.Select(b => b.ProjectId).Should().BeEquivalentTo(new[] { "p1", "p2" });
    }

    [Fact(DisplayName = "反映の最中に来た保存: 同時に走らせず、終わったあとに続けて 1 回処理する")]
    public async Task 反映中の保存は終わってから処理される()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var concurrent = 0;
        var maxConcurrent = 0;
        var rig = new Rig();
        rig.Flush = async (_, n) =>
        {
            var now = Interlocked.Increment(ref concurrent);
            maxConcurrent = Math.Max(maxConcurrent, now);
            try
            {
                if (n == 1) await release.Task;
                return true;
            }
            finally
            {
                Interlocked.Decrement(ref concurrent);
            }
        };

        rig.Coalescer.Add("p1", "Modules/Module1.bas");
        await rig.WaitForDelayAsync();
        rig.Advance(TimeSpan.FromMilliseconds(500));
        rig.ReleaseDelays();
        await rig.WaitForBatchesAsync(1);

        // 送信の最中に、同じモジュールとほかのモジュールの保存が来る。
        rig.Coalescer.Add("p1", "Modules/Module1.bas");
        rig.Coalescer.Add("p1", "Modules/Module2.bas");
        rig.Coalescer.PendingCount.Should().Be(2);
        rig.BatchCount.Should().Be(1, "走っている間は次を始めない");

        release.SetResult(true);
        await rig.WaitForDelayAsync();
        rig.Advance(TimeSpan.FromMilliseconds(500));
        rig.ReleaseDelays();
        await rig.Coalescer.WhenIdleAsync();

        rig.Batches.Should().HaveCount(2);
        rig.Batches[1].RelativePaths.Should().BeEquivalentTo(new[] { "Modules/Module1.bas", "Modules/Module2.bas" });
        maxConcurrent.Should().Be(1, "同時に 2 つ走らせない");
    }

    [Fact(DisplayName = "送れなかった（false）ときは溜め直し、もう一度静かになってから呼ぶ")]
    public async Task 送れないときは溜め直す()
    {
        var rig = new Rig((_, n) => Task.FromResult(n >= 2));
        rig.Coalescer.Add("p1", "Modules/Module1.bas");
        await rig.WaitForDelayAsync();

        rig.Advance(TimeSpan.FromMilliseconds(500));
        rig.ReleaseDelays();
        await rig.WaitForBatchesAsync(1);
        await rig.WaitForDelayAsync();
        rig.LastWait.Should().Be(TimeSpan.FromMilliseconds(500), "溜め直したので、改めて 500ms 静かになるのを待つ");

        rig.Advance(TimeSpan.FromMilliseconds(500));
        rig.ReleaseDelays();
        await rig.Coalescer.WhenIdleAsync();

        rig.Batches.Should().HaveCount(2);
        rig.Batches[1].RelativePaths.Should().Equal("Modules/Module1.bas");
    }

    [Fact(DisplayName = "送信が例外を投げても、ポンプは止まらず、次の保存が送られる")]
    public async Task 例外でも止まらない()
    {
        var rig = new Rig((_, n) => n == 1 ? throw new InvalidOperationException("壊れた") : Task.FromResult(true));
        rig.Coalescer.Add("p1", "Modules/Module1.bas");
        await rig.WaitForDelayAsync();
        rig.Advance(TimeSpan.FromMilliseconds(500));
        rig.ReleaseDelays();
        await rig.Coalescer.WhenIdleAsync();
        rig.Errors.Should().ContainSingle().Which.Message.Should().Be("壊れた");

        rig.Coalescer.Add("p1", "Modules/Module2.bas");
        await rig.WaitForDelayAsync();
        rig.Advance(TimeSpan.FromMilliseconds(500));
        rig.ReleaseDelays();
        await rig.Coalescer.WhenIdleAsync();

        rig.Batches.Should().HaveCount(2);
    }

    [Fact(DisplayName = "Add はすぐ戻る（送信が止まっていても、呼び出し側を待たせない）")]
    public async Task Addはすぐ戻る()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rig = new Rig((_, _) => gate.Task);
        rig.Coalescer.Add("p1", "Modules/Module1.bas");
        await rig.WaitForDelayAsync();
        rig.Advance(TimeSpan.FromMilliseconds(500));
        rig.ReleaseDelays();
        await rig.WaitForBatchesAsync(1);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 100; i++) rig.Coalescer.Add("p1", $"Modules/M{i}.bas");
        watch.Stop();

        watch.ElapsedMilliseconds.Should().BeLessThan(50, "送信が止まっていても Add は表を更新するだけ");
        gate.SetResult(true);
        rig.Coalescer.Dispose();
    }

    [Fact(DisplayName = "Dispose のあとの Add は何もしない")]
    public void 廃棄後は何もしない()
    {
        var rig = new Rig();
        rig.Coalescer.Dispose();

        rig.Coalescer.Add("p1", "Modules/Module1.bas");

        rig.Coalescer.PendingCount.Should().Be(0);
    }
}
