using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Threading;

namespace Graft.Platform;

/// <summary>
/// <see cref="IUiServices"/> のAvalonia実装。クリップボードは<see cref="TopLevel"/>経由の
/// 非同期APIしか持たないため、<see cref="IClipboardAccess"/>の同期シグネチャへ橋渡しする
/// （挙動の詳細は各実装のコメントを参照）。画面情報は<see cref="IClassicDesktopStyleApplicationLifetime.MainWindow"/>
/// の<see cref="TopLevel.Screens"/>から取得する（仕様書19章・20章 L3）。
/// 名前空間は既存のWindows実装（<c>Graft.Platform.Windows</c>）に倣いつつ、Avalonia本体の
/// ルート名前空間「Avalonia」との衝突を避けるため<c>Graft.Platform</c>直下に置く。
/// </summary>
public sealed class AvaloniaUiServices : IUiServices
{
    public AvaloniaUiServices()
    {
        Clipboard = new AvaloniaClipboardAccess();
        Screens = new AvaloniaScreenInfo();
    }

    public IClipboardAccess Clipboard { get; }

    public IScreenInfo Screens { get; }

    public IUiTimer CreateTimer(TimeSpan interval, Action onTick) => new AvaloniaUiTimer(interval, onTick);

    /// <summary>デスクトップライフタイムのメインウィンドウを起点に<see cref="TopLevel"/>を解決する。</summary>
    internal static TopLevel? ResolveTopLevel()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return null;
        }
        var mainWindow = desktop.MainWindow;
        return mainWindow is null ? null : TopLevel.GetTopLevel(mainWindow);
    }
}

/// <summary>
/// <see cref="TopLevel.Clipboard"/>（<see cref="IClipboard"/>、非同期API）を同期シグネチャへ
/// 橋渡しする実装。書き込みは完了を待たない（UIスレッドを塞がないことを優先し、失敗しても
/// 呼び出し側へは伝えない契約のため待つ必要がない）。読み取りは呼び出し元がその場で結果を
/// 必要とするため待ち合わせるが、Avalonia本体のクリップボード実装（Win32/X11/macOS）は
/// 実質的に同期完了するTaskを返すため、通常はUIスレッドをブロックしない。
/// 将来バックエンドが真に非同期化された場合は<see cref="IClipboardAccess"/>自体を
/// 非同期シグネチャへ変更する必要がある（統合担当への報告事項）。
/// </summary>
internal sealed class AvaloniaClipboardAccess : IClipboardAccess
{
    public void SetText(string text)
    {
        var clipboard = AvaloniaUiServices.ResolveTopLevel()?.Clipboard;
        if (clipboard is null) return;
        _ = SetTextAndSwallowAsync(clipboard, text);
    }

    public string? GetText()
    {
        var clipboard = AvaloniaUiServices.ResolveTopLevel()?.Clipboard;
        if (clipboard is null) return null;
        try
        {
            return clipboard.GetTextAsync().GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task SetTextAndSwallowAsync(IClipboard clipboard, string text)
    {
        try
        {
            await clipboard.SetTextAsync(text).ConfigureAwait(true);
        }
        catch (Exception)
        {
            // 失敗しても例外を投げない契約（IClipboardAccess.SetText）のため、ここで飲み込む。
        }
    }
}

/// <summary>
/// <see cref="Screens"/>を使う画面構成の問い合わせ実装。仮想画面は全モニタの
/// <see cref="Avalonia.Platform.Screen.Bounds"/>を合成した矩形とする。
/// </summary>
internal sealed class AvaloniaScreenInfo : IScreenInfo
{
    public UiRect VirtualScreen
    {
        get
        {
            var screens = ResolveScreens();
            if (screens is null || screens.All.Count == 0) return default;

            var union = screens.All[0].Bounds;
            for (var i = 1; i < screens.All.Count; i++)
            {
                union = union.Union(screens.All[i].Bounds);
            }
            return ToUiRect(union);
        }
    }

    public UiRect PrimaryWorkArea
    {
        get
        {
            var primary = ResolveScreens()?.Primary;
            return primary is null ? default : ToUiRect(primary.WorkingArea);
        }
    }

    // Avaloniaには本稿執筆時点でWPFのSystemParameters.ClientAreaAnimationに相当する
    // 「アニメーションを表示する」設定の問い合わせAPIが無いため、既定で有効として扱う
    // （統合担当への報告事項）。
    public bool IsAnimationEnabled => true;

    private static Avalonia.Controls.Screens? ResolveScreens() => AvaloniaUiServices.ResolveTopLevel()?.Screens;

    private static UiRect ToUiRect(PixelRect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);
}

/// <summary><see cref="DispatcherTimer"/>をラップした反復タイマー実装。</summary>
internal sealed class AvaloniaUiTimer : IUiTimer
{
    private readonly DispatcherTimer _timer;

    public AvaloniaUiTimer(TimeSpan interval, Action onTick)
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
