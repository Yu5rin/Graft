using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Graft.Features;
using Graft.Infra;
using Graft.ViewModels;
using Microsoft.Win32;

namespace Graft.Views;

/// <summary>
/// 仕様書8.12 トレイ常駐。<see cref="System.Windows.Forms.NotifyIcon"/> は WinForms 参照を
/// 要し配布条件（附録A.3）に反するため使わず、Win32 の <c>Shell_NotifyIcon</c> を直接
/// P/Invoke する。メッセージ受信用に、メインウィンドウとは別のメッセージ専用ウィンドウ
/// （<c>HWND_MESSAGE</c> 親、<see cref="HwndSource"/>）を自前で持つ。
/// トレイアイコンはラスタ画像を使わず、<c>Themes/Logo.xaml</c> のベクター素材を
/// <see cref="RenderTargetBitmap"/> で描画し、GDI経由でHICONを組み立てて生成する。
/// タスクバーのライト／ダークテーマを検出し、可読性のためプレート色を反転させて切り替える。
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

    private Settings _settings;
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
        Settings initialSettings,
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
        _taskbarRestartMessage = RegisterWindowMessage("TaskbarCreated");

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
        data.uFlags |= NifInfo;
        data.szInfoTitle = title;
        data.szInfo = text;
        data.dwInfoFlags = NiifInfo;
        Shell_NotifyIcon(NimModify, ref data);
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
            Shell_NotifyIcon(NimDelete, ref data);
            _added = false;
        }

        if (_hIcon != IntPtr.Zero)
        {
            DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }

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
        SetForegroundWindow(_hwnd);

        var menu = new ContextMenu();
        menu.Items.Add(BuildClipboardWatchItem());
        menu.Items.Add(BuildRecentProjectsItem());
        menu.Items.Add(new Separator());
        menu.Items.Add(BuildSimpleItem("設定", "設定を開く", (_, _) => _openSettings()));
        menu.Items.Add(BuildSimpleItem("終了", "Graftを終了", (_, _) => _requestExit()));

        // 8.10相当: メニュー外クリックで確実に閉じるための定番手順（フォアグラウンド確保 → WM_NULL送出）。
        menu.Closed += (_, _) => PostMessage(_hwnd, WmNull, IntPtr.Zero, IntPtr.Zero);
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
    // アイコン生成（Themes/Logo.xaml のベクター素材からHICONを組み立てる。ラスタ画像は使わない）
    // ------------------------------------------------------------------

    private void AddOrUpdateIcon()
    {
        var lightTaskbar = DetectLightTaskbar();
        var newIcon = BuildHIcon(lightTaskbar);
        if (newIcon == IntPtr.Zero)
        {
            return;
        }

        var data = BuildNotifyIconData(newIcon);
        var ok = Shell_NotifyIcon(_added ? NimModify : NimAdd, ref data);
        if (ok)
        {
            _added = true;
        }

        var previous = _hIcon;
        _hIcon = newIcon;
        if (previous != IntPtr.Zero)
        {
            DestroyIcon(previous);
        }
    }

    private NOTIFYICONDATA BuildNotifyIconData(IntPtr icon) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = NifMessage | NifIcon | NifTip,
        uCallbackMessage = TrayCallbackMessage,
        hIcon = icon,
        szTip = "Graft",
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    /// <summary>Themes/Logo.xaml のジオメトリ資源を、タスクバーの明暗に応じた配色で描画しHICONを作る。</summary>
    private static IntPtr BuildHIcon(bool lightTaskbar)
    {
        var visual = BuildLogoVisual(lightTaskbar, IconSize);
        var bitmap = new RenderTargetBitmap(IconSize, IconSize, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var stride = IconSize * 4;
        var pixels = new byte[stride * IconSize];
        bitmap.CopyPixels(pixels, stride, 0);

        return CreateHIconFromPixels(pixels, IconSize);
    }

    /// <summary>
    /// ダークタスクバーでは既定配色（明るいプレート＋濃い幹）、ライトタスクバーではプレートと
    /// 幹の配色を反転させ、どちらのタスクバー上でも視認できるようにする。
    /// </summary>
    private static DrawingVisual BuildLogoVisual(bool lightTaskbar, int size)
    {
        var resources = Application.Current.Resources;
        var background = (Geometry)resources["LogoBackgroundGeometry"];
        var trunk = (Geometry)resources["LogoTrunkGeometry"];
        var stalk = (Geometry)resources["LogoStalkGeometry"];
        var leafUpper = (Geometry)resources["LogoLeafUpperGeometry"];
        var leafLower = (Geometry)resources["LogoLeafLowerGeometry"];
        var veinUpper = (Geometry)resources["LogoVeinUpperGeometry"];
        var veinLower = (Geometry)resources["LogoVeinLowerGeometry"];

        var cream = (Color)resources["LogoBackgroundColor"];
        var trunkColor = (Color)resources["LogoTrunkColor"];
        var leafColor = (Color)resources["LogoLeafColor"];

        var plateBrush = FrozenBrush(lightTaskbar ? trunkColor : cream);
        var glyphBrush = FrozenBrush(lightTaskbar ? cream : trunkColor);
        var leafBrush = FrozenBrush(leafColor);

        var visual = new DrawingVisual { Transform = new ScaleTransform(size / 256.0, size / 256.0) };
        using (var dc = visual.RenderOpen())
        {
            dc.DrawGeometry(plateBrush, null, background);
            dc.DrawGeometry(glyphBrush, null, trunk);
            dc.DrawGeometry(leafBrush, null, stalk);
            dc.DrawGeometry(leafBrush, null, leafUpper);
            dc.DrawGeometry(leafBrush, null, leafLower);
            dc.DrawGeometry(plateBrush, null, veinUpper);
            dc.DrawGeometry(plateBrush, null, veinLower);
        }
        return visual;
    }

    private static SolidColorBrush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// タスクバー（エクスプローラ）のライト／ダーク設定を読み取り専用で参照する。書き込みは行わない
    /// （附録A.5）。取得できない場合はダーク扱いとする。
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool DetectLightTaskbar()
    {
        const string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        const string valueName = "SystemUsesLightTheme";
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            if (key?.GetValue(valueName) is int value)
            {
                return value != 0;
            }
        }
        catch (Exception)
        {
            // 読み取り専用の最善努力の参照。取得できない場合は既定（ダーク）にフォールバックする。
        }
        return false;
    }

    /// <summary>
    /// PBGRA32のピクセル配列から、アルファブレンド対応のHICONを組み立てる。ANDマスクは全て0
    /// （＝マスクなし）とし、32bpp色ビットマップのアルファチャンネルのみで透過を表現する
    /// （Windows XP以降の標準的な手法）。
    /// </summary>
    private static IntPtr CreateHIconFromPixels(byte[] pixelsBgra, int size)
    {
        var screenDc = GetDC(IntPtr.Zero);
        IntPtr colorBitmap;
        try
        {
            var header = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = size,
                biHeight = -size,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
            };
            colorBitmap = CreateDIBSection(screenDc, ref header, 0, out var bits, IntPtr.Zero, 0);
            if (colorBitmap == IntPtr.Zero || bits == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }
            Marshal.Copy(pixelsBgra, 0, bits, pixelsBgra.Length);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }

        var maskStrideBytes = ((size + 15) / 16) * 2;
        var maskBits = new byte[maskStrideBytes * size];
        var maskBitmap = CreateBitmap(size, size, 1, 1, maskBits);

        var iconInfo = new ICONINFO { fIcon = true, hbmMask = maskBitmap, hbmColor = colorBitmap };
        var hIcon = CreateIconIndirect(ref iconInfo);

        DeleteObject(colorBitmap);
        if (maskBitmap != IntPtr.Zero)
        {
            DeleteObject(maskBitmap);
        }
        return hIcon;
    }

    // ------------------------------------------------------------------
    // Win32 P/Invoke（8.12: Shell_NotifyIconを直接使用。附録A.5によりラスタ画像は使わず、
    // HICONはここで自前描画から組み立てる）
    // ------------------------------------------------------------------

    private const int NimAdd = 0x0;
    private const int NimModify = 0x1;
    private const int NimDelete = 0x2;
    private const int NifMessage = 0x1;
    private const int NifIcon = 0x2;
    private const int NifTip = 0x4;
    private const int NifInfo = 0x10;
    private const int NiifInfo = 0x1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(
        IntPtr hdc, ref BITMAPINFOHEADER pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int nWidth, int nHeight, uint nPlanes, uint nBitCount, byte[]? lpvBits);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref ICONINFO piconinfo);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
