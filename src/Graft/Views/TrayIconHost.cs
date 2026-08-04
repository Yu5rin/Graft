using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using Graft.Features;
using Graft.Infra;
using Graft.ViewModels;
using Microsoft.Win32;
using AppSettings = Graft.Infra.Settings;

namespace Graft.Views;

/// <summary>
/// 仕様書8.12 トレイ常駐。<see cref="System.Windows.Forms.NotifyIcon"/> は WinForms 参照を
/// 要し配布条件（附録A.3）に反するため使わず、Win32 の <c>Shell_NotifyIcon</c>
/// （<see cref="TrayNativeMethods"/>）を直接利用する。メッセージ受信用に、メインウィンドウとは
/// 別のメッセージ専用ウィンドウ（<c>HWND_MESSAGE</c> 親、<see cref="HwndSource"/>）を自前で持つ。
/// アイコン生成は <see cref="TrayIconRenderer"/> に委譲する。
/// 右クリックメニューはクリップボード監視のON/OFF・直近プロジェクトへの切り替え・設定・終了の
/// 4項目のみとする。
/// </summary>
public sealed class TrayIconHost : IDisposable
{
    private const int IconSize = 32;
    private const int WmApp = 0x8000;
    private const int TrayCallbackMessage = WmApp + 1;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonUp = 0x0205;
    private const int WmContextMenu = 0x007B;
    private const int WmNull = 0x0000;

    private readonly MainViewModel _mainViewModel;
    private readonly SettingsStore _settingsStore;
    private readonly ClipboardWatcher _clipboardWatcher;
    private readonly Action _restoreMainWindow;
    private readonly Action _openSettings;
    private readonly Action _requestExit;

    private AppSettings _settings;
    private HwndSource? _source;
    private IntPtr _hwnd;
    private IntPtr _hIcon;
    private uint _taskbarRestartMessage;
    private bool _added;
    private bool _disposed;

    public TrayIconHost(
        MainViewModel mainViewModel,
        SettingsStore settingsStore,
        ClipboardWatcher clipboardWatcher,
        AppSettings initialSettings,
        Action restoreMainWindow,
        Action openSettings,
        Action requestExit)
    {
        _mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _clipboardWatcher = clipboardWatcher ?? throw new ArgumentNullException(nameof(clipboardWatcher));
        _settings = initialSettings ?? throw new ArgumentNullException(nameof(initialSettings));
        _restoreMainWindow = restoreMainWindow ?? throw new ArgumentNullException(nameof(restoreMainWindow));
        _openSettings = openSettings ?? throw new ArgumentNullException(nameof(openSettings));
        _requestExit = requestExit ?? throw new ArgumentNullException(nameof(requestExit));
    }

    /// <summary>トレイアイコンを追加する。非Windows環境では何もしない。</summary>
    public void Show()
    {
        if (!OperatingSystem.IsWindows() || _source is not null)
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
        _taskbarRestartMessage = TrayNativeMethods.RegisterWindowMessage("TaskbarCreated");

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        AddOrUpdateIcon();
    }

    /// <summary>トレイ通知（バルーン）を表示する。9章「トレイ通知のみ」の挙動で使う。</summary>
    public void ShowBalloon(string title, string text)
    {
        if (!OperatingSystem.IsWindows() || !_added)
        {
            return;
        }

        var data = BuildNotifyIconData(_hIcon);
        data.uFlags |= TrayNativeMethods.NifInfo;
        data.szInfoTitle = title;
        data.szInfo = text;
        data.dwInfoFlags = TrayNativeMethods.NiifInfo;
        TrayNativeMethods.Shell_NotifyIcon(TrayNativeMethods.NimModify, ref data);
    }

    public void Dispose()
    {
        if (_disposed || !OperatingSystem.IsWindows())
        {
            _disposed = true;
            return;
        }

        _disposed = true;
        if (_added)
        {
            var data = BuildNotifyIconData(_hIcon);
            TrayNativeMethods.Shell_NotifyIcon(TrayNativeMethods.NimDelete, ref data);
            _added = false;
        }

        TrayIconRenderer.DestroyIcon(_hIcon);
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
                _restoreMainWindow();
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
        TrayNativeMethods.SetForegroundWindow(_hwnd);

        var menu = new ContextMenu();
        menu.Items.Add(BuildClipboardWatchItem());
        menu.Items.Add(BuildRecentProjectsItem());
        menu.Items.Add(new Separator());
        menu.Items.Add(BuildSimpleItem("設定", "設定を開く", (_, _) => _openSettings()));
        menu.Items.Add(BuildSimpleItem("終了", "Graftを終了", (_, _) => _requestExit()));

        // 定番手順（フォアグラウンド確保 → WM_NULL送出）でメニュー外クリック時に確実に閉じる。
        menu.Closed += (_, _) => TrayNativeMethods.PostMessage(_hwnd, WmNull, IntPtr.Zero, IntPtr.Zero);
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private MenuItem BuildClipboardWatchItem()
    {
        var item = new MenuItem
        {
            Header = "クリップボード監視",
            IsCheckable = true,
            IsChecked = _settings.ClipboardWatch.Enabled,
        };
        AutomationProperties.SetName(item, "クリップボード監視のオン・オフを切り替え");
        item.Click += async (_, _) => await ToggleClipboardWatchAsync().ConfigureAwait(true);
        return item;
    }

    private MenuItem BuildRecentProjectsItem()
    {
        var recent = new MenuItem { Header = "直近のプロジェクトへ切り替え" };
        foreach (var candidate in _mainViewModel.ProjectPane.Items.Take(5))
        {
            var entry = new MenuItem { Header = candidate.Name };
            AutomationProperties.SetName(entry, $"プロジェクト {candidate.Name} へ切り替え");
            entry.Click += (_, _) =>
            {
                _mainViewModel.ProjectPane.SelectedItem = candidate;
                _restoreMainWindow();
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

    private async Task ToggleClipboardWatchAsync()
    {
        var enabled = !_settings.ClipboardWatch.Enabled;
        _settings = _settings with { ClipboardWatch = _settings.ClipboardWatch with { Enabled = enabled } };
        await _settingsStore.SaveAsync(_settings).ConfigureAwait(true);

        if (enabled)
        {
            _clipboardWatcher.Start();
        }
        else
        {
            _clipboardWatcher.Stop();
        }
    }

    // ------------------------------------------------------------------
    // アイコンの登録・更新（生成そのものは TrayIconRenderer に委譲）
    // ------------------------------------------------------------------

    private void AddOrUpdateIcon()
    {
        var lightTaskbar = TrayIconRenderer.DetectLightTaskbar();
        var newIcon = TrayIconRenderer.BuildHIcon(lightTaskbar, IconSize);
        if (newIcon == IntPtr.Zero)
        {
            return;
        }

        var data = BuildNotifyIconData(newIcon);
        var ok = TrayNativeMethods.Shell_NotifyIcon(_added ? TrayNativeMethods.NimModify : TrayNativeMethods.NimAdd, ref data);
        if (ok)
        {
            _added = true;
        }

        var previous = _hIcon;
        _hIcon = newIcon;
        TrayIconRenderer.DestroyIcon(previous);
    }

    private TrayNativeMethods.NOTIFYICONDATA BuildNotifyIconData(IntPtr icon) => new()
    {
        cbSize = Marshal.SizeOf<TrayNativeMethods.NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = TrayNativeMethods.NifMessage | TrayNativeMethods.NifIcon | TrayNativeMethods.NifTip,
        uCallbackMessage = TrayCallbackMessage,
        hIcon = icon,
        szTip = "Graft",
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };
}
