using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.UiTests.TestSupport;

namespace Graft.UiTests;

/// <summary>
/// C14: フォルダの名前を変えると配下の開いているタブが古いパスのままになる不具合の回帰テスト。
/// 名前変更ではタブのパスを付け替え、削除では配下のタブを閉じる（未保存があれば確認する）。
/// </summary>
public class FolderTabFollowTests
{
    private static async Task<(ShellHarness H, Graft.ViewModels.FileNodeViewModel Folder)> OpenAsync()
    {
        var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        Directory.CreateDirectory(h.ProjectPath("src/sub"));
        Directory.CreateDirectory(h.ProjectPath("src2"));
        await File.WriteAllTextAsync(h.ProjectPath("src/a.txt"), "A\n").ConfigureAwait(true);
        await File.WriteAllTextAsync(h.ProjectPath("src/sub/b.txt"), "B\n").ConfigureAwait(true);
        await File.WriteAllTextAsync(h.ProjectPath("src2/c.txt"), "C\n").ConfigureAwait(true);
        await h.RegisterProjectAsync().ConfigureAwait(true);
        await ShellHarness.WaitUntilAsync(() => h.Shell.Explorer.RootNodes.Count > 0).ConfigureAwait(true);
        var folder = h.Shell.Explorer.RootNodes.Single(n => n.Name == "src");
        return (h, folder);
    }

    [AvaloniaFact(DisplayName = "フォルダの名前を変えると、配下で開いているタブ（入れ子を含む）のパスが新しい場所へ付け替わる")]
    public async Task 名前変更でタブのパスが付け替わる()
    {
        var (h, folder) = await OpenAsync().ConfigureAwait(true);
        using var _ = h;
        var tabA = (await h.Shell.Editor.OpenFileAsync(h.ProjectPath("src/a.txt")).ConfigureAwait(true)).Value;
        var tabB = (await h.Shell.Editor.OpenFileAsync(h.ProjectPath("src/sub/b.txt")).ConfigureAwait(true)).Value;
        var tabC = (await h.Shell.Editor.OpenFileAsync(h.ProjectPath("src2/c.txt")).ConfigureAwait(true)).Value;
        h.Dialogs.PromptAnswer = (_, _, _) => "lib";

        h.Shell.Explorer.RenameCommand.Execute(folder);
        await ShellHarness.WaitUntilAsync(() => Directory.Exists(h.ProjectPath("lib"))).ConfigureAwait(true);
        await Task.Delay(100).ConfigureAwait(true);

        tabA.Session.FullPath.Should().Be(h.ProjectPath("lib/a.txt"));
        tabB.Session.FullPath.Should().Be(h.ProjectPath("lib/sub/b.txt"));
        tabB.Session.RelativePath.Should().Be("lib/sub/b.txt");
        tabC.Session.FullPath.Should().Be(h.ProjectPath("src2/c.txt"), "名前が前方一致するだけの別フォルダ（src2）は巻き込まない");

        // 付け替え後に保存すると、新しい場所のファイルに書かれる（古い場所に新しくファイルが作られない）。
        tabA.Session.Document.Insert(0, "X");
        await tabA.Session.SaveAsync().ConfigureAwait(true);
        (await File.ReadAllTextAsync(h.ProjectPath("lib/a.txt")).ConfigureAwait(true)).Should().Be("XA\n");
        Directory.Exists(h.ProjectPath("src")).Should().BeFalse();
    }

    [AvaloniaFact(DisplayName = "フォルダを削除すると、配下で開いているタブも閉じる")]
    public async Task 削除で配下のタブが閉じる()
    {
        var (h, folder) = await OpenAsync().ConfigureAwait(true);
        using var _ = h;
        await h.Shell.Editor.OpenFileAsync(h.ProjectPath("src/a.txt")).ConfigureAwait(true);
        await h.Shell.Editor.OpenFileAsync(h.ProjectPath("src/sub/b.txt")).ConfigureAwait(true);
        var other = (await h.Shell.Editor.OpenFileAsync(h.ProjectPath("src2/c.txt")).ConfigureAwait(true)).Value;

        h.Shell.Explorer.DeleteCommand.Execute(folder);
        await ShellHarness.WaitUntilAsync(() => !Directory.Exists(h.ProjectPath("src"))).ConfigureAwait(true);
        await Task.Delay(100).ConfigureAwait(true);

        h.Shell.Editor.Tabs.Should().ContainSingle().Which.Should().BeSameAs(other, "配下のタブだけが閉じる");
    }

    [AvaloniaFact(DisplayName = "配下に未保存のタブがあるフォルダの削除は、保存確認を出し、キャンセルすると削除しない")]
    public async Task 未保存があれば確認しキャンセルで削除しない()
    {
        var (h, folder) = await OpenAsync().ConfigureAwait(true);
        using var _ = h;
        var tab = (await h.Shell.Editor.OpenFileAsync(h.ProjectPath("src/a.txt")).ConfigureAwait(true)).Value;
        tab.Session.Document.Insert(0, "X");
        h.Dialogs.ThreeWayAnswer = (_, _) => null;

        h.Shell.Explorer.DeleteCommand.Execute(folder);
        await ShellHarness.WaitUntilAsync(() => h.Dialogs.ThreeWays.Count > 0).ConfigureAwait(true);
        await Task.Delay(100).ConfigureAwait(true);

        h.Dialogs.ThreeWays.Should().ContainSingle(d => d.Message.Contains("a.txt"));
        Directory.Exists(h.ProjectPath("src")).Should().BeTrue("キャンセルしたので削除されない");
        h.Shell.Editor.Tabs.Should().Contain(tab);
        tab.Session.IsModified.Should().BeTrue();
    }
}
