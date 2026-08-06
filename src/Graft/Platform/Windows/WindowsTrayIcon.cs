using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using Microsoft.Win32;

namespace Graft.Platform.Windows;

/// <summary>
/// <see cref="ITrayIcon"/> のWindows実装。移設元は <c>Views/TrayIconHost.cs</c>。
/// <see cref="System.Windows.Forms.NotifyIcon"/> はWinForms参照を要し配布条件（附録A.3）に
/// 反するため使わず、Win32 の <c>Shell_NotifyIcon</c>（<see cref="WindowsTrayNativeMethods"/>）を
/// 直接利用する。メッセージ受信用に、メインウィンドウとは別のメッセージ専用ウィンドウ
/// （<c>HWND_MESSAGE</c> 親、<see cref="HwndSource"/>）を自前で持つ。アイコン生成は
/// <see cref="WindowsTrayIconRenderer"/> に委譲する。移設元と異なる点は、右クリックメニューの
/// 内容が <c>MainViewModel</c> 等への直接依存ではなく <see cref="TrayMenuDescriptor"/> から
/// 組み立てられることのみで、メッセージ処理・アイコン登録のロジック自体は変更していない。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsTrayIcon : ITrayIcon
{
    private const int IconSize = 32;
    private const int WmApp = 0x8000;
    private const int TrayCallbackMessage = WmApp + 1;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonUp = 0x0205;
    private const int WmContextMenu = 0x007B;
    private const int WmNull = 0x0000;

    private TrayMenuDescriptor? _menu;
    private HwndSource? _source;
    private IntPtr _hwnd;
    private IntPtr _hIcon;
    private uint _taskbarRestartMessage;
    private bool _added;
    private bool _disposed;

    public bool IsSupported => true;

    public string? UnsupportedReason => null;

    /// <summary>
    /// 右クリックメニューの内容を設定する。状態（クリップボード監視のON/OFF、直近プロジェクト
    /// 一覧など）が変化するたびに、呼び出し側は本メソッドを再度呼び出すこと
    /// （メニューは開くたびに直近の内容で組み立てる）。
    /// </summary>
    public void Configure(TrayMenuDescriptor menu) => _menu = menu ?? throw new ArgumentNullException(nameof(menu));

    public void Show()
    {
        if (_source is not null)
        {
            return;
        }

        var parameters = new HwndSourceParameters("Graft.TrayMessageWindow")
        {
            WindowStyle = 0,
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE: 表示を伴わないメッセージ専用ウィンドウ
            Width = 0,
            Height = 0,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
        _hwnd = _source.Handle;
        _taskbarRestartMessage = WindowsTrayNativeMethods.RegisterWindowMessage("TaskbarCreated");

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        AddOrUpdateIcon();
    }

    public void ShowBalloon(string title, string text)
    {
        if (!_added)
        {
            return;
        }

        var data = BuildNotifyIconData(_hIcon);
        data.uFlags |= WindowsTrayNativeMethods.NifInfo;
        data.szInfoTitle = title;
        data.szInfo = text;
        data.dwInfoFlags = WindowsTrayNativeMethods.NiifInfo;
        WindowsTrayNativeMethods.Shell_NotifyIcon(WindowsTrayNativeMethods.NimModify, ref data);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_added)
        {
            var data = BuildNotifyIconData(_hIcon);
            WindowsTrayNativeMethods.Shell_NotifyIcon(WindowsTrayNativeMethods.NimDelete, ref data);
            _added = false;
        }

        WindowsTrayIconRenderer.DestroyIcon(_hIcon);
        _hIcon = IntPtr.Zero;

        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _source?.RemoveHook(WndProc);
        _source?.Dispose();
        _source = null;
    }

    // ------------------------------------------------------------------
    // メッセージ処理
    // ------------------------------------------------------------------

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_taskbarRestartMessage != 0 && msg == _taskbarRestartMessage)
        {
            // エクスプローラの再起動でタスクバーが再生成された場合、アイコンを再登録する。
            _added = false;
            AddOrUpdateIcon();
            handled = true;
            return IntPtr.Zero;
        }

        if (msg == TrayCallbackMessage)
        {
            var mouseMsg = lParam.ToInt32();
            if (mouseMsg == WmLButtonUp)
            {
                _menu?.OnRestoreMainWindow();
            }
            else if (mouseMsg is WmRButtonUp or WmContextMenu)
            {
                ShowContextMenu();
            }
            handled = true;
            return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
        {
            Application.Current?.Dispatcher.BeginInvoke(new Action(AddOrUpdateIcon));
        }
    }

    // ------------------------------------------------------------------
    // 右クリックメニュー（クリップボード監視ON/OFF・直近プロジェクト・設定・終了の4項目のみ）
    // ------------------------------------------------------------------

    private void ShowContextMenu()
    {
        if (_menu is not { } menu)
        {
            return;
        }

        WindowsTrayNativeMethods.SetForegroundWindow(_hwnd);

        var contextMenu = new ContextMenu();
        contextMenu.Items.Add(BuildClipboardWatchItem(menu));
        contextMenu.Items.Add(BuildRecentProjectsItem(menu));
        contextMenu.Items.Add(new Separator());
        contextMenu.Items.Add(BuildSimpleItem("設定", "設定を開く", (_, _) => menu.OnOpenSettings()));
        contextMenu.Items.Add(BuildSimpleItem("終了", "Graftを終了", (_, _) => menu.OnExit()));

        // 定番手順（フォアグラウンド確保 → WM_NULL送出）でメニュー外クリック時に確実に閉じる。
        contextMenu.Closed += (_, _) => WindowsTrayNativeMethods.PostMessage(_hwnd, WmNull, IntPtr.Zero, IntPtr.Zero);
        contextMenu.Placement = PlacementMode.MousePoint;
        contextMenu.IsOpen = true;
    }

    private static MenuItem BuildClipboardWatchItem(TrayMenuDescriptor menu)
    {
        var item = new MenuItem
        {
            Header = "クリップボード監視",
            IsCheckable = true,
            IsChecked = menu.ClipboardWatchEnabled,
        };
        AutomationProperties.SetName(item, "クリップボード監視のオン・オフを切り替え");
        item.Click += (_, _) => menu.OnToggleClipboardWatch(!menu.ClipboardWatchEnabled);
        return item;
    }

    private MenuItem BuildRecentProjectsItem(TrayMenuDescriptor menu)
    {
        var recent = new MenuItem { Header = "直近のプロジェクトへ切り替え" };
        foreach (var candidate in menu.RecentProjects)
        {
            var entry = new MenuItem { Header = candidate.Name };
            AutomationProperties.SetName(entry, $"プロジェクト {candidate.Name} へ切り替え");
            entry.Click += (_, _) =>
            {
                candidate.OnSelect();
                menu.OnRestoreMainWindow();
            };
            recent.Items.Add(entry);
        }
        recent.IsEnabled = recent.Items.Count > 0;
        return recent;
    }

    private static MenuItem BuildSimpleItem(string header, string automationName, RoutedEventHandler onClick)
    {
        var item = new MenuItem { Header = header };
        AutomationProperties.SetName(item, automationName);
        item.Click += onClick;
        return item;
    }

    // ------------------------------------------------------------------
    // アイコンの登録・更新（生成そのものは WindowsTrayIconRenderer に委譲）
    // ------------------------------------------------------------------

    private void AddOrUpdateIcon()
    {
        var lightTaskbar = WindowsTrayIconRenderer.DetectLightTaskbar();
        var newIcon = WindowsTrayIconRenderer.BuildHIcon(lightTaskbar, IconSize);
        if (newIcon == IntPtr.Zero)
        {
            return;
        }

        var data = BuildNotifyIconData(newIcon);
        var ok = WindowsTrayNativeMethods.Shell_NotifyIcon(
            _added ? WindowsTrayNativeMethods.NimModify : WindowsTrayNativeMethods.NimAdd, ref data);
        if (ok)
        {
            _added = true;
        }

        var previous = _hIcon;
        _hIcon = newIcon;
        WindowsTrayIconRenderer.DestroyIcon(previous);
    }

    private WindowsTrayNativeMethods.NOTIFYICONDATA BuildNotifyIconData(IntPtr icon) => new()
    {
        cbSize = Marshal.SizeOf<WindowsTrayNativeMethods.NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = WindowsTrayNativeMethods.NifMessage | WindowsTrayNativeMethods.NifIcon | WindowsTrayNativeMethods.NifTip,
        uCallbackMessage = TrayCallbackMessage,
        hIcon = icon,
        szTip = "Graft",
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };
}
