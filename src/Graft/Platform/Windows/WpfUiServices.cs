using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Threading;

namespace Graft.Platform.Windows;

/// <summary>
/// <see cref="IUiServices"/> のWPF実装。<see cref="System.Windows.Clipboard"/>・
/// <see cref="SystemParameters"/>・<see cref="DispatcherTimer"/>を使う（仕様書19章・20章 L3）。
/// ViewModel層をWPF/Avalonia間でソース共有するための、唯一のWPF固有実装である。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WpfUiServices : IUiServices
{
    public WpfUiServices()
    {
        Clipboard = new WpfClipboardAccess();
        Screens = new WpfScreenInfo();
    }

    public IClipboardAccess Clipboard { get; }

    public IScreenInfo Screens { get; }

    public IUiTimer CreateTimer(TimeSpan interval, Action onTick) => new WpfUiTimer(interval, onTick);
}

/// <summary>
/// <see cref="System.Windows.Clipboard"/>を使うクリップボード実装。移設元は
/// <c>MainViewModel</c>・<c>ExplorerViewModel</c>等が個別に持っていた例外保護であり、
/// ここへ一元化した（挙動は変えていない）。
/// </summary>
internal sealed class WpfClipboardAccess : IClipboardAccess
{
    public void SetText(string text)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
        }
        catch (ExternalException)
        {
            // 他プロセスがクリップボードを占有している場合は静かに諦める。
        }
    }

    public string? GetText()
    {
        try
        {
            return System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : null;
        }
        catch (ExternalException)
        {
            return null;
        }
    }
}

/// <summary><see cref="SystemParameters"/>を使う画面構成の問い合わせ実装。</summary>
internal sealed class WpfScreenInfo : IScreenInfo
{
    public UiRect VirtualScreen => new(
        SystemParameters.VirtualScreenLeft,
        SystemParameters.VirtualScreenTop,
        SystemParameters.VirtualScreenWidth,
        SystemParameters.VirtualScreenHeight);

    public UiRect PrimaryWorkArea => new(
        SystemParameters.WorkArea.Left,
        SystemParameters.WorkArea.Top,
        SystemParameters.WorkArea.Width,
        SystemParameters.WorkArea.Height);

    public bool IsAnimationEnabled => SystemParameters.ClientAreaAnimation;
}

/// <summary><see cref="DispatcherTimer"/>をラップした反復タイマー実装。</summary>
internal sealed class WpfUiTimer : IUiTimer
{
    private readonly DispatcherTimer _timer;

    public WpfUiTimer(TimeSpan interval, Action onTick)
    {
        _timer = new DispatcherTimer { Interval = interval };
        _timer.Tick += (_, _) => onTick();
    }

    public void Restart()
    {
        _timer.Stop();
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    public void Dispose() => _timer.Stop();
}
