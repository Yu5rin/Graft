using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Graft.Platform.Linux;

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
        Clipboard = new AvaloniaClipboardAccess(ResolveLinuxReader());
        Screens = new AvaloniaScreenInfo();
    }

    public IClipboardAccess Clipboard { get; }

    public IScreenInfo Screens { get; }

    public IUiTimer CreateTimer(TimeSpan interval, Action onTick) => new AvaloniaUiTimer(interval, onTick);

    /// <summary>
    /// Linux環境でのみ、自前のX11クリップボードリーダー（プロセス内で共有する既定インスタンス、
    /// <see cref="X11ClipboardReader.Shared"/>）を返す。<see cref="PlatformServices.Create"/>が
    /// 生成する<see cref="Linux.LinuxPlatformServices"/>のクリップボード監視とも同じ接続を共有する
    /// （AvaloniaUiServicesクラスの説明を参照）。それ以外のOS、またはX11に接続できない環境では
    /// nullを返し、読み取りは従来どおりAvalonia経由（タイムアウト付き）へ静かにフォールバックする。
    /// </summary>
    private static X11ClipboardReader? ResolveLinuxReader()
        => OperatingSystem.IsLinux() ? X11ClipboardReader.Shared : null;

    /// <summary>
    /// デスクトップライフタイムのメインウィンドウを起点に<see cref="TopLevel"/>を解決する。
    /// 起動直後（MainWindowの割り当て前）にも画面情報が必要になるため、未設定の場合は
    /// 開いているウィンドウのいずれかで代用する。ここでnullを返すと画面構成が「無し」と
    /// みなされ、ウィンドウの復元サイズが最小サイズまで縮んでしまう。
    /// </summary>
    internal static TopLevel? ResolveTopLevel()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return null;
        }

        var window = desktop.MainWindow ?? desktop.Windows.FirstOrDefault();
        return window is null ? null : TopLevel.GetTopLevel(window);
    }
}

/// <summary>
/// <see cref="TopLevel.Clipboard"/>（<see cref="IClipboard"/>）への橋渡し。
/// 書き込みは完了を待たない（失敗しても呼び出し側へは伝えない契約のため待つ必要がない）。
/// 読み取りは<see cref="IClipboardAccess.GetTextAsync"/>のとおり非同期のまま扱う。
/// 同期的に待つとX11では取得に失敗する（応答を処理するイベントループごと止まるため）。
///
/// Linuxでは読み取りをAvalonia経由にせず、可能な限り<see cref="X11ClipboardReader"/>を使う。
/// AvaloniaのX11クリップボード実装は内部で要求を直列に処理しており、一度でも要求が完了しない
/// まま残ると（所有アプリが応答しない瞬間に読んだ場合等）以後のすべての読み取りが永久に
/// 失敗し続ける不具合が実機で確認された（呼び出し側でタイムアウトさせても内部の詰まりは
/// 解消されない）。<see cref="X11ClipboardReader"/>は専用の接続・スレッドで読み取りを行い、
/// 1回の失敗・タイムアウトが次回以降に影響しないよう作られている。
/// </summary>
public sealed class AvaloniaClipboardAccess : IClipboardAccess
{
    // 使う自前リーダー。<see cref="LinuxPlatformServices"/>や<see cref="AvaloniaUiServices"/>から
    // 配線時に注入される。nullの場合（Windows等、またはLinuxでもX11に接続できない環境）は
    // 従来どおりAvalonia経由で読み取る。
    private readonly X11ClipboardReader? _linuxReader;

    public AvaloniaClipboardAccess(X11ClipboardReader? linuxReader = null)
    {
        _linuxReader = linuxReader;
    }

    public void SetText(string text)
    {
        var clipboard = AvaloniaUiServices.ResolveTopLevel()?.Clipboard;
        if (clipboard is null) return;
        _ = SetTextAndSwallowAsync(clipboard, text);
    }

    // クリップボードの所有アプリが応答しない場合、要求は完了しないまま残りうる。
    // 待ち続けると操作が戻らなくなるため、上限を設けて「取得できなかった」に倒す。
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    public async Task<string?> GetTextAsync()
    {
        if (_linuxReader is not null)
        {
            return await _linuxReader.ReadTextAsync(ReadTimeout).ConfigureAwait(true);
        }

        var clipboard = AvaloniaUiServices.ResolveTopLevel()?.Clipboard;
        if (clipboard is null) return null;

        try
        {
            var read = clipboard.GetTextAsync();
            var completed = await Task.WhenAny(read, Task.Delay(ReadTimeout)).ConfigureAwait(true);
            return completed == read ? await read.ConfigureAwait(true) : null;
        }
        catch (Exception)
        {
            // 他アプリが所有権を持ったまま応答しない等の失敗は、取得できなかった扱いにする。
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
