using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FluentAssertions;
using Graft.UiTests.TestSupport;

namespace Graft.UiTests;

/// <summary>
/// C2: アプリを閉じるとき、未保存のタブの確認が無い不具合の回帰テスト。
/// ×ボタン／トレイの「終了」に相当する window.Close() に対して、保存して終了／保存せず終了／キャンセルが働く。
/// </summary>
public class CloseConfirmationTests
{
    private static async Task<(ShellHarness H, Graft.ViewModels.EditorTabViewModel Tab)> OpenModifiedAsync()
    {
        var h = await ShellHarness.CreateAsync(withWindow: true).ConfigureAwait(true);
        await File.WriteAllTextAsync(h.ProjectPath("a.txt"), "abc\n").ConfigureAwait(true);
        await h.RegisterProjectAsync().ConfigureAwait(true);
        var tab = (await h.Shell.Editor.OpenFileAsync(h.ProjectPath("a.txt")).ConfigureAwait(true)).Value;
        tab.Session.Document.Insert(0, "X");
        tab.Session.IsModified.Should().BeTrue("前提");
        return (h, tab);
    }

    [AvaloniaFact(DisplayName = "未保存のタブがあるまま閉じようとすると確認が出て、キャンセルすると閉じない")]
    public async Task キャンセルすると閉じない()
    {
        var (h, tab) = await OpenModifiedAsync().ConfigureAwait(true);
        using var _ = h;
        h.Dialogs.ThreeWayAnswer = (_, _) => null;

        h.Window!.Close();
        await ShellHarness.WaitUntilAsync(() => h.Dialogs.ThreeWays.Count > 0).ConfigureAwait(true);
        Dispatcher.UIThread.RunJobs();

        h.Dialogs.ThreeWays.Should().ContainSingle(d => d.Title == "変更の保存" && d.Message.Contains("a.txt"));
        h.Window.IsVisible.Should().BeTrue("キャンセルしたので閉じない");
        tab.Session.IsModified.Should().BeTrue("編集は残る");
    }

    [AvaloniaFact(DisplayName = "「保存して終了」を選ぶと保存してから閉じる")]
    public async Task 保存して終了する()
    {
        var (h, _) = await OpenModifiedAsync().ConfigureAwait(true);
        using var _h = h;
        h.Dialogs.ThreeWayAnswer = (_, _) => true;

        h.Window!.Close();
        await ShellHarness.WaitUntilAsync(() => !h.Window.IsVisible).ConfigureAwait(true);

        h.Window.IsVisible.Should().BeFalse("確認のあと閉じる");
        (await File.ReadAllTextAsync(h.ProjectPath("a.txt")).ConfigureAwait(true)).Should().Be("Xabc\n", "保存してから終了");
    }

    [AvaloniaFact(DisplayName = "「保存せずに終了」を選ぶと保存せずに閉じる")]
    public async Task 保存せずに終了する()
    {
        var (h, _) = await OpenModifiedAsync().ConfigureAwait(true);
        using var _h = h;
        h.Dialogs.ThreeWayAnswer = (_, _) => false;

        h.Window!.Close();
        await ShellHarness.WaitUntilAsync(() => !h.Window.IsVisible).ConfigureAwait(true);

        h.Window.IsVisible.Should().BeFalse();
        (await File.ReadAllTextAsync(h.ProjectPath("a.txt")).ConfigureAwait(true)).Should().Be("abc\n", "保存していない");
    }

    [AvaloniaFact(DisplayName = "トレイに常駐する設定のときは、閉じても終了しないので確認を出さない")]
    public async Task トレイ常駐なら確認しない()
    {
        var (h, tab) = await OpenModifiedAsync().ConfigureAwait(true);
        using var _ = h;
        h.Window!.CloseBehavior = "tray";
        h.Window.IsTraySupported = true;

        h.Window.Close();
        Dispatcher.UIThread.RunJobs();

        h.Dialogs.ThreeWays.Should().BeEmpty("終了しない（隠れるだけ）ので確認は不要");
        h.Window.IsVisible.Should().BeFalse("トレイへ隠れる");
        tab.Session.IsModified.Should().BeTrue("編集は残っている");
    }

    [AvaloniaFact(DisplayName = "未保存のタブが無ければ、確認なしで閉じる")]
    public async Task 未保存が無ければ確認しない()
    {
        using var h = await ShellHarness.CreateAsync(withWindow: true).ConfigureAwait(true);

        h.Window!.Close();
        Dispatcher.UIThread.RunJobs();

        h.Dialogs.ThreeWays.Should().BeEmpty();
        h.Window.IsVisible.Should().BeFalse();
    }

    [AvaloniaFact(DisplayName = "適用の実行中に閉じようとすると、適用が終わるまで閉じるのを保留する")]
    public async Task 適用中は閉じるのを保留する()
    {
        using var h = await ShellHarness.CreateAsync(withWindow: true).ConfigureAwait(true);
        await File.WriteAllTextAsync(h.ProjectPath("a.txt"), "1行目\n2行目\n").ConfigureAwait(true);
        await h.RegisterProjectAsync().ConfigureAwait(true);
        await h.ParseAsync("a.txt", "2行目", "2行目（変更後）").ConfigureAwait(true);

        // 適用の途中（確認ダイアログで止まっている間）に閉じようとする。
        var release = new TaskCompletionSource<bool>();
        h.Dialogs.ConfirmAnswerAsync = (title, _) => title == "適用の確認" ? release.Task : null;
        var applying = ShellHarness.ExecuteAsync(h.Shell.Graft.ApplyCommand);
        await ShellHarness.WaitUntilAsync(() => h.Shell.Graft.IsApplyInProgress).ConfigureAwait(true);

        h.Window!.Close();
        Dispatcher.UIThread.RunJobs();
        h.Window.IsVisible.Should().BeTrue("適用の実行中は閉じない");

        release.SetResult(true);
        await applying.ConfigureAwait(true);
        await ShellHarness.WaitUntilAsync(() => !h.Window.IsVisible).ConfigureAwait(true);

        h.Window.IsVisible.Should().BeFalse("適用が終わったら閉じる");
        (await File.ReadAllTextAsync(h.ProjectPath("a.txt")).ConfigureAwait(true)).Should().Contain("2行目（変更後）");
    }
}
