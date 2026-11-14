using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Graft.UiTests.TestSupport;

namespace Graft.UiTests;

/// <summary>
/// C7: 旧キーの案内ダイアログをやめ、入力欄の外でもCtrl+V／F／H／G／W／Tabが働くようにする。
/// </summary>
public class ShellKeyboardTests
{
    private static readonly RawInputModifiers Ctrl = RawInputModifiers.Control;

    [AvaloniaFact(DisplayName = "入力欄の外のCtrl+Vは、案内ダイアログを出さずクリップボードを解析する")]
    public async Task CtrlVで解析する()
    {
        using var h = await ShellHarness.CreateAsync(withWindow: true).ConfigureAwait(true);
        await File.WriteAllTextAsync(h.ProjectPath("a.txt"), "1行目\n2行目\n").ConfigureAwait(true);
        await h.RegisterProjectAsync().ConfigureAwait(true);
        h.Clipboard.Text = ShellHarness.BuildPatch("a.txt", "2行目", "2行目（変更後）");
        h.Window!.Focus();

        h.Window.KeyPressQwerty(PhysicalKey.V, Ctrl);
        await ShellHarness.WaitUntilAsync(() => h.Shell.Graft.Blocks.Count > 0).ConfigureAwait(true);

        h.Shell.Graft.Blocks.Should().HaveCount(1);
        h.Dialogs.Messages.Should().NotContain(m => m.Title == "このキーは変更されました");
    }

    [AvaloniaFact(DisplayName = "旧キー（Ctrl+Z・Ctrl+H・数字キー）を押しても、案内ダイアログは出ない")]
    public async Task 旧キーで案内は出ない()
    {
        using var h = await ShellHarness.CreateAsync(withWindow: true).ConfigureAwait(true);
        h.Window!.Focus();

        h.Window.KeyPressQwerty(PhysicalKey.Z, Ctrl);
        h.Window.KeyPressQwerty(PhysicalKey.Digit1, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        h.Dialogs.Messages.Should().BeEmpty();
    }

    [AvaloniaFact(DisplayName = "フォーカスがエディタの外にあっても、Ctrl+Wでアクティブなタブが閉じる")]
    public async Task エディタの外のCtrlWでタブが閉じる()
    {
        using var h = await ShellHarness.CreateAsync(withWindow: true).ConfigureAwait(true);
        await File.WriteAllTextAsync(h.ProjectPath("a.txt"), "abc\n").ConfigureAwait(true);
        await h.RegisterProjectAsync().ConfigureAwait(true);
        await h.Shell.Editor.OpenFileAsync(h.ProjectPath("a.txt")).ConfigureAwait(true);
        h.Window!.Focus();
        h.Shell.Editor.Tabs.Should().HaveCount(1);

        h.Window.KeyPressQwerty(PhysicalKey.W, Ctrl);
        await ShellHarness.WaitUntilAsync(() => h.Shell.Editor.Tabs.Count == 0).ConfigureAwait(true);

        h.Shell.Editor.Tabs.Should().BeEmpty();
    }

    [AvaloniaFact(DisplayName = "フォーカスがエディタの外にあっても、Ctrl+Fで検索が開く")]
    public async Task エディタの外のCtrlFで検索が開く()
    {
        using var h = await ShellHarness.CreateAsync(withWindow: true).ConfigureAwait(true);
        await File.WriteAllTextAsync(h.ProjectPath("a.txt"), "abc\n").ConfigureAwait(true);
        await h.RegisterProjectAsync().ConfigureAwait(true);
        await h.Shell.Editor.OpenFileAsync(h.ProjectPath("a.txt")).ConfigureAwait(true);
        h.Window!.Focus();

        h.Window.KeyPressQwerty(PhysicalKey.F, Ctrl);
        Dispatcher.UIThread.RunJobs();

        ((Graft.Views.EditorPane)h.Window.EditorHost.Child!).Search.IsOpen.Should().BeTrue();
    }

    [AvaloniaFact(DisplayName = "入力欄にフォーカスがあっても、Ctrl+,で設定が開く")]
    public async Task 入力欄の中でもCtrlカンマで設定が開く()
    {
        using var h = await ShellHarness.CreateAsync(withWindow: true).ConfigureAwait(true);
        h.Shell.ShowSideView(Graft.ViewModels.SideViewKind.Explorer, focus: false);
        Dispatcher.UIThread.RunJobs();
        var filter = h.Window!.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "FilterBox");
        filter.Focus().Should().BeTrue();

        h.Window!.KeyPressQwerty(PhysicalKey.Comma, Ctrl);

        h.OpenSettingsCount.Should().Be(1);
    }
}
