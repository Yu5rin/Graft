using System.Collections.Concurrent;
using System.Runtime.Versioning;

namespace Graft.Platform.Windows;

/// <summary>
/// COM の呼び出しを専用スレッドで直列に実行し、呼び出し全体に時間切れを設ける（仕様書_VBA連携 5.3）。
/// UI スレッドは <c>[STAThread]</c> だが、そこで COM を呼ぶとマクロ実行中などで UI ごと固まるので呼ばない。
/// <para>
/// 【なぜ時間切れが要るか】 <see cref="ComRetry"/> が効くのは「拒否」が返る場合だけ。マクロ実行中・中断モード・
/// VBE のモーダル表示中などは、呼び出しが拒否もされず戻ってこない見込みで（実機で要確認）、
/// <see cref="CancellationToken"/> では進行中の COM 呼び出しを止められない。そこで待つ側が時間で諦める。
/// </para>
/// <para>
/// 【諦めたスレッドの扱い】 戻ってこないスレッドは捨てる（<c>IsBackground = true</c> にして、アプリの終了を妨げない）。
/// 以後の呼び出しは新しいスレッドで行う。まだ始まっていなかった待ち行列の仕事は、新しいスレッドへ移す。
/// 捨てたスレッドの仕事が後から戻ってきても結果は使わない。仕事の中の長い繰り返し（複数モジュールの書き込みなど）
/// は、渡される <see cref="CancellationToken"/> を境目ごとに見て、時間切れ後に続きを進めないようにすること。
/// </para>
/// <para>
/// 【STA か MTA か】 イベントを受けず <c>IMessageFilter</c> も使わないので、自前のメッセージループは要らない。
/// どちらで安定するかは実機検証 9・12 の結果で決めるため、コンストラクタで選べるようにしてある。
/// </para>
/// </summary>
public sealed class StaWorker : IDisposable
{
    private readonly object _gate = new();
    private readonly ApartmentState _apartment;
    private readonly string _name;
    private Worker? _current;
    private bool _disposed;
    private int _abandonedCount;

    /// <param name="apartment">スレッドのアパートメント。<see cref="ApartmentState.Unknown"/> なら指定しない。</param>
    /// <param name="name">スレッド名（診断用）。</param>
    public StaWorker(ApartmentState apartment, string name = "Graft.VbaCom")
    {
        _apartment = apartment;
        _name = name;
    }

    /// <summary>時間切れで捨てたスレッドの数（診断・テスト用）。</summary>
    public int AbandonedCount => Volatile.Read(ref _abandonedCount);

    /// <summary>
    /// 専用スレッドで <paramref name="work"/> を実行する。<paramref name="timeout"/> までに終わらなければ
    /// <see cref="TimeoutException"/> を投げ、そのスレッドを捨てる。<paramref name="work"/> には、時間切れまたは
    /// 呼び出し側のキャンセルで取り消されるトークンを渡す。
    /// </summary>
    public async Task<T> RunAsync<T>(Func<CancellationToken, T> work, TimeSpan timeout, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        ct.ThrowIfCancellationRequested();

        var item = new Item<T>(work);
        Worker worker;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            worker = _current ??= StartWorker();
            worker.Queue.Add(item);
        }

        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delay = Task.Delay(timeout, delayCts.Token);
        var finished = await Task.WhenAny(item.Task, delay).ConfigureAwait(false);
        if (ReferenceEquals(finished, item.Task))
        {
            delayCts.Cancel();
            return await item.Task.ConfigureAwait(false);
        }

        // 呼び出し側のキャンセルか、時間切れか。
        item.Abandon();
        if (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }

        Retire(worker);
        throw new TimeoutException($"COM の呼び出しが {timeout.TotalSeconds:0.#} 秒以内に戻りませんでした。");
    }

    /// <summary>スレッドを捨てる。始まっていない仕事は新しいスレッドへ移す。</summary>
    private void Retire(Worker worker)
    {
        lock (_gate)
        {
            if (!worker.Retired)
            {
                worker.Retired = true;
                Interlocked.Increment(ref _abandonedCount);
            }

            var pending = new List<IItem>();
            while (worker.Queue.TryTake(out var queued)) pending.Add(queued);
            worker.Queue.CompleteAdding();

            if (ReferenceEquals(_current, worker)) _current = null;
            if (pending.Count > 0 && !_disposed)
            {
                var replacement = _current ??= StartWorker();
                foreach (var queued in pending) replacement.Queue.Add(queued);
            }
        }
    }

    private Worker StartWorker()
    {
        var worker = new Worker();
        var thread = new Thread(() => Loop(worker))
        {
            IsBackground = true, // 戻らないスレッドがアプリの終了を妨げないように。
            Name = _name,
        };

        // SetApartmentState は Windows 以外では意味がない（STA は PlatformNotSupportedException になる）。
        if (_apartment != ApartmentState.Unknown && OperatingSystem.IsWindows())
        {
            thread.SetApartmentState(_apartment);
        }

        thread.Start();
        return worker;
    }

    private static void Loop(Worker worker)
    {
        foreach (var item in worker.Queue.GetConsumingEnumerable())
        {
            item.Execute();
        }
    }

    /// <summary>後始末。待ち行列を閉じる（実行中のスレッドは背景スレッドなので、戻らなくても終了を妨げない）。</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_current is { } worker)
            {
                worker.Queue.CompleteAdding();
                _current = null;
            }
        }
    }

    private sealed class Worker
    {
        public BlockingCollection<IItem> Queue { get; } = new();

        public bool Retired { get; set; }
    }

    private interface IItem
    {
        void Execute();
    }

    private sealed class Item<T> : IItem
    {
        private readonly Func<CancellationToken, T> _work;
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _abandoned = new();

        public Item(Func<CancellationToken, T> work) => _work = work;

        public Task<T> Task => _completion.Task;

        /// <summary>時間切れ・キャンセルで見捨てる。結果は使われず、仕事の中の繰り返しは続きを進めない。</summary>
        public void Abandon()
        {
            _completion.TrySetCanceled();
            _abandoned.Cancel();
        }

        public void Execute()
        {
            // 待ち行列にいるうちに見捨てられた仕事は、実行しない。
            if (_completion.Task.IsCompleted) return;
            try
            {
                var result = _work(_abandoned.Token);
                _completion.TrySetResult(result);
            }
            catch (Exception ex)
            {
                _completion.TrySetException(ex);
            }
        }
    }
}
