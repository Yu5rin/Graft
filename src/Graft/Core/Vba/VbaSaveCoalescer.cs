namespace Graft.Core.Vba;

/// <summary>まとめて送る 1 回分（同じプロジェクトの、保存されたモジュールの相対パス）。</summary>
/// <param name="ProjectId">プロジェクトの Id。</param>
/// <param name="RelativePaths">ミラー内の正規の相対パス（<c>Modules/Module1.bas</c>）。重複なし・追加された順。</param>
public sealed record VbaSaveBatch(string ProjectId, IReadOnlyList<string> RelativePaths);

/// <summary>
/// 保存の通知を「まとめて 1 回」に畳む（仕様書_ブックの情報と依頼の観点 8.3。機能 D）。純ロジック：COM にもファイルにも
/// 画面にも触れない。時計と遅延は注入できる（テストで 500ms の境界を時間を待たずに確かめるため）。
/// <para>
/// 【なぜ要るか】 「すべて保存」や連続保存では、同じモジュールの保存通知が短い間に何度も来る。通知のたびに Excel へ
/// 送ると、Excel を何度も占有する。最後の通知から <see cref="DefaultQuietPeriod"/> 静かになったら、溜まったモジュールを
/// <b>1 回の送信</b>にまとめる。
/// </para>
/// <para>
/// 【同時に 2 つ走らせない】 送信（<c>flush</c>）の最中に来た保存は、溜めておいて、送信が終わったあとに続けて 1 回処理する。
/// 取り出す単位は 1 つのポンプ（<see cref="PumpAsync"/>）だけが持つので、送信が重なることがない。
/// <c>VbaSyncService</c> のゲートによる直列化とは別に、ここでも重ならないようにしておくのは、積み上がった送信が
/// ゲートの順番待ちで溜まり続けないようにするため。
/// </para>
/// <para>
/// 【呼び出し側（UI スレッド）を待たせない】 <see cref="Add"/> は、ロックの中でメモリ上の表を更新するだけで、すぐ戻る
/// （保存の通知のハンドラが、保存の体感を遅くしないため。数マイクロ秒）。ポンプは、最初に <see cref="Add"/> を呼んだ
/// 同期コンテキスト（UI スレッド）で始まり、送信の続きもそこへ戻る（<c>ConfigureAwait(true)</c>）。
/// 重い仕事（Excel への反映・ファイルの読み込み）は <c>flush</c> の先の非同期処理が行い、UI スレッドを塞がない。
/// </para>
/// </summary>
public sealed class VbaSaveCoalescer : IDisposable
{
    /// <summary>最後の通知からこの時間だけ静かになったら送る（仕様 8.3）。</summary>
    public static readonly TimeSpan DefaultQuietPeriod = TimeSpan.FromMilliseconds(500);

    private sealed class Entry
    {
        public List<string> Paths { get; } = new();

        public DateTimeOffset LastAt { get; set; }
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _pending = new(StringComparer.Ordinal);
    private readonly Func<VbaSaveBatch, CancellationToken, Task<bool>> _flush;
    private readonly TimeSpan _quiet;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Action<Exception>? _onError;
    private readonly CancellationTokenSource _cts = new();
    private bool _running;
    private bool _disposed;
    private TaskCompletionSource _idle = NewIdle(completed: true);

    /// <param name="flush">
    /// 1 回分を送る処理。戻り値は「処理を終えたか」で、false なら「いまは送れない（適用中など）」で、同じ内容を
    /// 新しい通知として溜め直し、もう一度静かになってから呼ぶ。例外は <paramref name="onError"/> に渡して次へ進む
    /// （1 回の失敗でポンプが止まり、以後の保存が一切送られなくなるのを避ける）。
    /// </param>
    /// <param name="quiet">静かな時間。既定は <see cref="DefaultQuietPeriod"/>。</param>
    /// <param name="clock">現在時刻（テストで差し替える）。</param>
    /// <param name="delay">待ち。既定は <see cref="Task.Delay(TimeSpan, CancellationToken)"/>（テストで差し替える）。</param>
    /// <param name="onError"><paramref name="flush"/> が投げた例外の受け取り先。</param>
    public VbaSaveCoalescer(
        Func<VbaSaveBatch, CancellationToken, Task<bool>> flush, TimeSpan? quiet = null, Func<DateTimeOffset>? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null, Action<Exception>? onError = null)
    {
        _flush = flush ?? throw new ArgumentNullException(nameof(flush));
        _quiet = quiet ?? DefaultQuietPeriod;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? Task.Delay;
        _onError = onError;
    }

    /// <summary>まだ送っていない（待っている）モジュールの数（プロジェクトをまたいだ合計）。</summary>
    public int PendingCount
    {
        get
        {
            lock (_lock) return _pending.Values.Sum(e => e.Paths.Count);
        }
    }

    /// <summary>溜まったものをすべて送り終え、ポンプが止まったときに完了する（テストと終了処理用）。</summary>
    public Task WhenIdleAsync()
    {
        lock (_lock) return _idle.Task;
    }

    /// <summary>
    /// 保存の通知を 1 件溜める。同じモジュールの通知は 1 件にまとまり、そのプロジェクトの「最後の通知の時刻」を更新する
    /// （＝静かな時間の数え直し）。すぐ戻る。
    /// </summary>
    public void Add(string projectId, string relativePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectId);
        ArgumentException.ThrowIfNullOrEmpty(relativePath);

        lock (_lock)
        {
            if (_disposed) return;
            Enqueue(projectId, new[] { relativePath });
            if (_running) return;
            _running = true;
            _idle = NewIdle(completed: false);
        }

        // ロックの外で始める（最初の await までは呼び出し側のスレッドで同期に進むので、ロックを持ったまま進めない）。
        _ = PumpAsync();
    }

    private void Enqueue(string projectId, IEnumerable<string> paths)
    {
        if (!_pending.TryGetValue(projectId, out var entry)) _pending[projectId] = entry = new Entry();
        foreach (var path in paths)
        {
            if (!entry.Paths.Contains(path, StringComparer.OrdinalIgnoreCase)) entry.Paths.Add(path);
        }

        entry.LastAt = _clock();
    }

    private async Task PumpAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                VbaSaveBatch? batch = null;
                var wait = TimeSpan.Zero;
                lock (_lock)
                {
                    if (_pending.Count == 0)
                    {
                        // 「空になったと判断する」ことと「止まったと記録する」ことを同じロックの中で行う。
                        // 分けると、その間に来た Add が「まだ動いている」と見て戻り、誰にも送られないまま残る。
                        _running = false;
                        _idle.TrySetResult();
                        return;
                    }

                    var now = _clock();
                    string? dueId = null;
                    var soonest = TimeSpan.MaxValue;
                    foreach (var (id, entry) in _pending)
                    {
                        var remaining = entry.LastAt + _quiet - now;
                        if (remaining <= TimeSpan.Zero)
                        {
                            dueId = id;
                            break;
                        }

                        if (remaining < soonest) soonest = remaining;
                    }

                    if (dueId is not null)
                    {
                        batch = new VbaSaveBatch(dueId, _pending[dueId].Paths.ToArray());
                        _pending.Remove(dueId);
                    }
                    else
                    {
                        wait = soonest;
                    }
                }

                if (batch is null)
                {
                    await _delay(wait, _cts.Token).ConfigureAwait(true);
                    continue;
                }

                var finished = true;
                try
                {
                    finished = await _flush(batch, _cts.Token).ConfigureAwait(true);
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _onError?.Invoke(ex);
                }

                if (!finished)
                {
                    // いまは送れない。新しい通知として溜め直し、もう一度静かになってから呼ぶ（待ちながら忙しく回らない）。
                    lock (_lock) Enqueue(batch.ProjectId, batch.RelativePaths);
                }
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // 終了処理（Dispose）による中断。
        }
        finally
        {
            // 通常の終わり方は上で済んでいる。ここは中断（Dispose）や想定外の例外で抜けたときの後始末。
            lock (_lock)
            {
                if (_running)
                {
                    _running = false;
                    _idle.TrySetResult();
                }
            }
        }
    }

    private static TaskCompletionSource NewIdle(bool completed)
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (completed) source.SetResult();
        return source;
    }

    /// <summary>待っている通知を捨てて、ポンプを止める（アプリの終了時）。</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _pending.Clear();
        }

        // CancellationTokenSource は Dispose しない。ポンプがまだ Token を読むことがあり、解放済みだと例外になる
        // （保持するのは小さなオブジェクト 1 つで、アプリの終了時にしか呼ばれない）。
        _cts.Cancel();
    }
}
