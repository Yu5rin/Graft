using Avalonia.Controls;
using Avalonia.Threading;

namespace Graft.Platform;

/// <summary>
/// <see cref="ITrayIcon"/> をAvalonia標準の<see cref="TrayIcon"/>で実装する
/// （仕様書8.12、v2.1 19章 L4）。WindowsのシェルNotifyIconとLinuxの
/// StatusNotifierItemの双方をAvaloniaが受け持つため、1つの実装で両OSに対応できる。
/// アイコンは<see cref="AvaloniaLogoRenderer"/>がベクター資源から実行時に描画する
/// （附録A.5「アイコンにラスタ画像を使わない」）。
///
/// v2.0のWPF版との差分: トレイ通知（バルーン）はAvaloniaのTrayIconに対応するAPIが無いため、
/// OSごとの通知手段へ委譲する（<see cref="IDesktopNotifier"/>）。通知手段が無い環境では
/// 何もしない（クリップボード監視の「トレイ通知のみ」設定では見た目上の変化が起きない）。
/// </summary>
public sealed class AvaloniaTrayIcon : ITrayIcon
{
    private const int IconSize = 32;

    private readonly IDesktopNotifier _notifier;
    private TrayIcon? _icon;
    private TrayMenuDescriptor? _menu;
    private bool _disposed;

    public AvaloniaTrayIcon(IDesktopNotifier notifier)
    {
        _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
    }

    public bool IsSupported => true;

    public string? UnsupportedReason => null;

    public void Configure(TrayMenuDescriptor menu)
    {
        _menu = menu ?? throw new ArgumentNullException(nameof(menu));
        if (_icon is not null) _icon.Menu = BuildMenu(menu);
    }

    public void Show()
    {
        if (_disposed || _icon is not null) return;

        _icon = new TrayIcon { ToolTipText = "Graft", IsVisible = true };

        // 背景の明暗はOSごとの判定が必要で、Avaloniaには問い合わせAPIが無い。
        // トレイの背景は暗いことが多いため、明るいプレートの配色を既定にする。
        var bitmap = AvaloniaLogoRenderer.TryRender(lightBackground: false, IconSize);
        if (bitmap is not null) _icon.Icon = new WindowIcon(bitmap);

        if (_menu is not null)
        {
            _icon.Menu = BuildMenu(_menu);
            var restore = _menu.OnRestoreMainWindow;
            _icon.Clicked += (_, _) => restore();
        }
    }

    public void ShowBalloon(string title, string text) => _notifier.Notify(title, text);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // TrayIconの破棄はUIスレッドで行う必要がある。
        Dispatcher.UIThread.Invoke(() =>
        {
            _icon?.Dispose();
            _icon = null;
        });
    }

    private static NativeMenu BuildMenu(TrayMenuDescriptor descriptor)
    {
        var menu = new NativeMenu();

        var watch = new NativeMenuItem("クリップボード監視")
        {
            ToggleType = NativeMenuItemToggleType.CheckBox,
            IsChecked = descriptor.ClipboardWatchEnabled,
        };
        watch.Click += (sender, _) =>
        {
            // 表示状態はAvaloniaが切り替えたあとの値を採用し、設定側へ反映する。
            var isChecked = sender is NativeMenuItem { IsChecked: true };
            descriptor.OnToggleClipboardWatch(isChecked);
        };
        menu.Items.Add(watch);

        if (descriptor.RecentProjects.Count > 0)
        {
            var recent = new NativeMenuItem("直近のプロジェクトへ切り替え") { Menu = new NativeMenu() };
            foreach (var project in descriptor.RecentProjects)
            {
                var item = new NativeMenuItem(project.Name);
                var select = project.OnSelect;
                var restore = descriptor.OnRestoreMainWindow;
                item.Click += (_, _) =>
                {
                    select();
                    restore();
                };
                recent.Menu.Items.Add(item);
            }
            menu.Items.Add(recent);
        }

        menu.Items.Add(new NativeMenuItemSeparator());

        var settings = new NativeMenuItem("設定");
        settings.Click += (_, _) => descriptor.OnOpenSettings();
        menu.Items.Add(settings);

        var exit = new NativeMenuItem("終了");
        exit.Click += (_, _) => descriptor.OnExit();
        menu.Items.Add(exit);

        return menu;
    }
}
