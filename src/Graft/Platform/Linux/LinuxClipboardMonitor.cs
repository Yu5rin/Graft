using Avalonia.Threading;
using Graft.Core;

namespace Graft.Platform.Linux;

/// <summary>
/// <see cref="IClipboardMonitor"/> のLinux実装（仕様書9章・10章、v2.1 19章 L4）。
///
/// X11のクリップボード変更通知（XFixesSelectionNotify）はWaylandでは受け取れず、
/// またAvaloniaが管理するクリップボード所有権と競合しうる。移植先の環境差を吸収するため、
/// ここでは一定間隔でクリップボードの内容を読み、直前と変わっていればパッチらしさを
/// 判定する方式を採る。読み取った内容はどこにも保持しない（判定に使うのは
/// 直前の内容のハッシュのみ）方針はWindows版と同じ。
///
/// パッチらしさの判定は <see cref="PatchTextDetector"/> をWindows実装と共用し、
/// OS間で挙動が食い違わないようにする。
///
/// 読み取り自体はここでは行わず、コンストラクタで受け取った<see cref="IClipboardAccess"/>へ
/// 委譲する（<see cref="ReadClipboardTextAsync"/>）。<see cref="PlatformServices.Create"/>が
/// 配線する実装（<see cref="AvaloniaClipboardAccess"/>）はLinuxでは<see cref="X11ClipboardReader"/>を
/// 使うため、このポーリングもAvaloniaのX11クリップボード実装が持つ「一度詰まると以後恒久的に
/// 失敗し続ける」不具合の影響を受けない。
/// </summary>
public sealed class LinuxClipboardMonitor : IClipboardMonitor
{
    // 1秒間隔なら貼り付け操作の体感に十分追従でき、常時動作でも負荷が無視できる。
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly IClipboardAccess _clipboard;
    private DispatcherTimer? _timer;
    private int _lastHash;
    private bool _isFirstTick = true;
    private bool _reading;
    private bool _disposed;

    public LinuxClipboardMonitor(IClipboardAccess clipboard)
    {
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
    }

    public bool IsSupported => true;

    public string? UnsupportedReason => null;

    public bool IsEnabled => _timer is { IsEnabled: true };

    public event EventHandler<string>? PatchDetected;

    /// <summary>X11の選択所有権は使わないため、ウィンドウハンドルは不要（何もしない）。</summary>
    public void Attach(IntPtr hwnd)
    {
        // 何もしない。
    }

    public GraftResult<bool> Start()
    {
        if (_disposed) return GraftResult<bool>.Ok(false);
        if (_timer is not null)
        {
            _timer.Start();
            return GraftResult<bool>.Ok(true);
        }

        // 監視開始時点の内容は「変化」とみなさない（起動直後に既存の内容へ反応しないため）。
        // 初回の読み取りは巡回の1回目で行い、そこで得た内容を基準にする。
        _isFirstTick = true;

        _timer = new DispatcherTimer(PollInterval, DispatcherPriority.Background, OnTick);
        _timer.Start();
        return GraftResult<bool>.Ok(true);
    }

    public void Stop() => _timer?.Stop();

    /// <summary>ウィンドウメッセージは使わないため常にfalseを返す。</summary>
    public bool HandleMessage(int msg, IntPtr wParam, IntPtr lParam) => false;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
        _timer = null;
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        // 前回の読み取りが終わる前に次の巡回が来ても、多重に読みにいかない。
        if (_reading) return;
        _reading = true;

        try
        {
            var text = await ReadClipboardTextAsync().ConfigureAwait(true);
            var hash = ComputeHash(text);

            if (_isFirstTick)
            {
                _isFirstTick = false;
                _lastHash = hash;
                return;
            }

            if (hash == _lastHash) return;
            _lastHash = hash;

            if (text is not null && PatchTextDetector.LooksLikePatch(text))
            {
                PatchDetected?.Invoke(this, text);
            }
        }
        finally
        {
            _reading = false;
        }
    }

    private async Task<string?> ReadClipboardTextAsync()
    {
        try
        {
            return await _clipboard.GetTextAsync().ConfigureAwait(true);
        }
        catch (Exception)
        {
            // 他アプリがクリップボードを保持している最中の失敗は次回の巡回で回復する。
            return null;
        }
    }

    /// <summary>内容そのものは保持せず、変化の検知にはハッシュだけを使う。</summary>
    private static int ComputeHash(string? text) => text is null ? 0 : text.GetHashCode(StringComparison.Ordinal);
}
