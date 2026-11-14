using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using FluentAssertions;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;

namespace Graft.UiTests;

/// <summary>
/// C6: 「開く」操作が開閉の切り替えになっていた不具合の回帰テスト。
/// ツールバーの「履歴」・Ctrl+Shift+H／E／F・コマンドパレットの各ビューは、表示して
/// フォーカスを移すだけで、すでに表示中でも畳まない。畳むのはサイドバーのアイコンの再クリックだけ。
/// </summary>
public class OpenSideViewTests
{
    [AvaloniaFact(DisplayName = "「履歴」を繰り返し開いても、サイドビューは畳まれない")]
    public async Task 履歴を開くは畳まない()
    {
        // 履歴を開く要求を受けて表示するのはShellWindow（View）なので、ウィンドウつきで試す。
        using var h = await ShellHarness.CreateAsync(withWindow: true).ConfigureAwait(true);
        var shell = h.Shell;

        shell.Graft.ShowHistoryCommand.Execute(null);
        shell.IsHistoryActive.Should().BeTrue();
        shell.Graft.ShowHistoryCommand.Execute(null);

        shell.IsHistoryActive.Should().BeTrue("すでに履歴が表示中でも、「開く」は畳まない");
        shell.IsSideViewCollapsed.Should().BeFalse();
    }

    [AvaloniaFact(DisplayName = "サイドバーのアイコンの再クリックは、これまでどおりサイドビューを畳む")]
    public async Task アイコンの再クリックは畳む()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        var shell = h.Shell;

        shell.SelectSideViewCommand.Execute(SideViewKind.Explorer);
        shell.IsExplorerActive.Should().BeTrue();
        shell.SelectSideViewCommand.Execute(SideViewKind.Explorer);

        shell.IsSideViewCollapsed.Should().BeTrue("アイコンをもう一度押したときだけ畳む");
    }

    [AvaloniaFact(DisplayName = "コマンドパレットの各ビューへの切り替えは、表示中でも畳まない")]
    public async Task コマンドパレットのビュー切り替えは畳まない()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        var shell = h.Shell;

        shell.ShowSideViewCommand.Execute(SideViewKind.Explorer);
        shell.ShowSideViewCommand.Execute(SideViewKind.Explorer);

        shell.IsExplorerActive.Should().BeTrue();
    }

    [AvaloniaFact(DisplayName = "畳まれていても、「開く」で展開される")]
    public async Task 畳まれていれば展開される()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        var shell = h.Shell;
        shell.SelectSideViewCommand.Execute(SideViewKind.Project);
        shell.IsSideViewCollapsed.Should().BeTrue("前提: 畳んだ");

        shell.ShowSideView(SideViewKind.Search);

        shell.IsSideViewCollapsed.Should().BeFalse();
        shell.IsSearchActive.Should().BeTrue();
    }

    [AvaloniaFact(DisplayName = "Ctrl+Shift+Fを繰り返し押すと、検索ビューは表示されたまま検索欄にフォーカスが移る")]
    public async Task キーボードで検索を開く()
    {
        using var h = await ShellHarness.CreateAsync(withWindow: true).ConfigureAwait(true);
        var window = h.Window!;
        var mods = RawInputModifiers.Control | RawInputModifiers.Shift;

        window.KeyPressQwerty(PhysicalKey.F, mods);
        window.KeyPressQwerty(PhysicalKey.F, mods);
        await ShellHarness.WaitUntilAsync(() => false, 100).ConfigureAwait(true);
        Dispatcher.UIThread.RunJobs();

        h.Shell.IsSearchActive.Should().BeTrue("2回押しても畳まれない");
        window.FocusManager!.GetFocusedElement().Should().BeSameAs(window.SearchViewControl.QueryBoxElement);
    }

    [AvaloniaFact(DisplayName = "Ctrl+Shift+Eを繰り返し押すと、エクスプローラは表示されたまま")]
    public async Task キーボードでエクスプローラを開く()
    {
        using var h = await ShellHarness.CreateAsync(withWindow: true).ConfigureAwait(true);
        var window = h.Window!;
        var mods = RawInputModifiers.Control | RawInputModifiers.Shift;

        window.KeyPressQwerty(PhysicalKey.E, mods);
        window.KeyPressQwerty(PhysicalKey.E, mods);
        Dispatcher.UIThread.RunJobs();

        h.Shell.IsExplorerActive.Should().BeTrue("2回押しても畳まれない");
    }
}
