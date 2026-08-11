using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using FluentAssertions;
using Graft.Platform;
using Graft.Platform.Windows;
using Graft.Themes;
using Graft.UiTests.TestSupport;
using Xunit;

namespace Graft.UiTests;

/// <summary>
/// 利用者からの要望: タイトルバー色のテーマ連動。実際のDWM呼び出し（Windows 11実機）は
/// ここでは検証できないため、代わりに<see cref="TitleBarThemeSync.ApplyAction"/>
/// （TitleBarThemeSync.csのコメント参照）を差し替えて「ウィンドウを開いたとき・
/// テーマ切替時に適用処理が呼ばれる経路が繋がっていること」を検証する。
///
/// 10個のウィンドウ（ApplyPreviewWindow・ShellWindow等）を個別に列挙するテストは書かない。
/// 代わりにテスト内だけで作った未知の<see cref="Window"/>派生（<see cref="ProbeWindow"/>）を
/// 使うことで、「新しいウィンドウ種別を追加しても、この配線に一切手を入れずに自動的に
/// 対象へ含まれる」ことそのものを示す（依頼書のテスト方針章）。
/// </summary>
public class TitleBarThemeSyncTests : IDisposable
{
    private readonly ShownWindowTracker _windows = new();
    private readonly List<(Window Window, bool IsDark, Color Caption, Color Text)> _calls = new();

    public TitleBarThemeSyncTests()
    {
        // TitleBarThemeSync.Initialize()はプロセス全体で一度だけ実際に購読する
        // （TitleBarThemeSync.csのInitialize()コメント参照）。ApplyActionの差し替えは
        // このテストの間だけ有効にし、Disposeで必ず既定へ戻す（他のテストへ影響しないため）。
        TitleBarThemeSync.Initialize();
        TitleBarThemeSync.ApplyAction = (window, isDark, caption, text, _) =>
            _calls.Add((window, isDark, caption, text));
    }

    public void Dispose()
    {
        TitleBarThemeSync.ResetApplyActionToDefault();
        _windows.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>本体のどのウィンドウでもない、テスト専用の未知のWindow派生。</summary>
    private sealed class ProbeWindow : Window
    {
    }

    [AvaloniaFact(DisplayName = "未知のWindow派生を開いただけで、10個の列挙を経ずに適用処理が呼ばれる（新しいウィンドウが自動的に対象になることの証明）")]
    public void 未知のウィンドウを開くと自動的に適用される()
    {
        var window = _windows.Track(new ProbeWindow());
        window.Show();

        var callsForThisWindow = _calls.Where(c => ReferenceEquals(c.Window, window)).ToList();
        callsForThisWindow.Should().HaveCount(1,
            "TitleBarThemeSyncはWindow.WindowOpenedEventのクラスハンドラで拾うため、" +
            "ProbeWindowのような本体に存在しない新しいWindow型でも1回呼ばれるはず");
    }

    [AvaloniaFact(DisplayName = "開いたときに渡される色は、現在のテーマのBgBaseColor/TextPrimaryColorリソースと一致する")]
    public void 開いたときに渡される色はテーマリソースと一致する()
    {
        ThemeManager.SetTheme(AppTheme.Dark);

        var window = _windows.Track(new ProbeWindow());
        window.Show();

        var call = _calls.Last(c => ReferenceEquals(c.Window, window));
        Application.Current!.TryFindResource("BgBaseColor", null, out var expectedCaptionObj);
        Application.Current!.TryFindResource("TextPrimaryColor", null, out var expectedTextObj);
        var expectedCaption = (Color)expectedCaptionObj!;
        var expectedText = (Color)expectedTextObj!;

        call.IsDark.Should().BeTrue("ダークテーマを選択したため");
        call.Caption.Should().Be(expectedCaption, "決め打ちの色ではなく、リソースから引いた値をそのまま渡す必要がある（依頼書3章）");
        call.Text.Should().Be(expectedText);
    }

    [AvaloniaFact(DisplayName = "テーマを切り替えると、開いている全ウィンドウへ再適用される")]
    public void テーマ切替で開いている全ウィンドウへ再適用される()
    {
        ThemeManager.SetTheme(AppTheme.Light);
        var window1 = _windows.Track(new ProbeWindow());
        var window2 = _windows.Track(new ProbeWindow());
        window1.Show();
        window2.Show();

        _calls.Clear(); // Show()自体による初回適用の分は除外し、テーマ切替による分だけを見る。

        ThemeManager.SetTheme(AppTheme.Dark);

        _calls.Should().Contain(c => ReferenceEquals(c.Window, window1) && c.IsDark,
            "テーマ切替時は開いている全ウィンドウへ再適用される必要がある（window1）");
        _calls.Should().Contain(c => ReferenceEquals(c.Window, window2) && c.IsDark,
            "テーマ切替時は開いている全ウィンドウへ再適用される必要がある（window2）");
    }

    [AvaloniaFact(DisplayName = "閉じたウィンドウはテーマ切替時の再適用の対象から外れる")]
    public void 閉じたウィンドウは再適用の対象から外れる()
    {
        ThemeManager.SetTheme(AppTheme.Light);
        var window = new ProbeWindow();
        window.Show();
        window.Close(); // ShownWindowTrackerには乗せない: ここで意図的に閉じるのがテストの本題のため。

        _calls.Clear();
        ThemeManager.SetTheme(AppTheme.Dark);

        _calls.Should().NotContain(c => ReferenceEquals(c.Window, window),
            "Window.WindowClosedEventで追跡から外れるため、閉じたウィンドウへは二度と適用処理が呼ばれないはず");
    }

    [AvaloniaFact(DisplayName = "実際のDWM呼び出し（WindowsTitleBarTheme.Apply）をLinux上で直接呼んでも例外にならない（非Windowsでは何もしない経路の確認）")]
    public void 非WindowsでWindowsTitleBarThemeを直接呼んでも例外にならない()
    {
        // このテストはLinux上（xvfb-run経由のCI含む）で実行される前提。ガード
        // （WindowsTitleBarThemeSupport.ShouldApply）が効いていなければ、dwmapi.dllへの
        // P/InvokeがDllNotFoundExceptionでここで例外になるはず。例外にならないことそのものが
        // 「非Windowsでは何もしない」経路が実際に効いていることの証拠になる。
        var window = _windows.Track(new ProbeWindow());
        window.Show();

        // CA1416（[SupportedOSPlatform("windows")]の呼び出しはWindows上のみ、という警告）は
        // このテストの目的そのもの（非Windowsから呼んでも安全なことの確認）のため意図的に無視する。
#pragma warning disable CA1416
        var act = () => WindowsTitleBarTheme.Apply(window, isDarkMode: true, Colors.Black, Colors.White);
#pragma warning restore CA1416
        act.Should().NotThrow("Linux上ではOperatingSystem.IsWindows()がfalseのため、DWM呼び出し自体に到達してはならない");
    }
}
