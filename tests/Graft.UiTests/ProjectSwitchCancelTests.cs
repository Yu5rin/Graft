using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.UiTests.TestSupport;

namespace Graft.UiTests;

/// <summary>
/// C4: プロジェクトを切り替えるとき、未保存の確認で「キャンセル」しても切り替わってしまう不具合の回帰テスト。
/// キャンセルしたら選択は元のプロジェクトへ戻り、解析結果の破棄などの切り替え後の処理も動かない。
/// </summary>
public class ProjectSwitchCancelTests
{
    private static async Task<(ShellHarness H, string Other, Graft.ViewModels.EditorTabViewModel Tab)> SetUpAsync()
    {
        var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        var other = Path.Combine(h.Root, "other");
        Directory.CreateDirectory(other);
        await File.WriteAllTextAsync(Path.Combine(other, "b.txt"), "b\n").ConfigureAwait(true);
        await File.WriteAllTextAsync(h.ProjectPath("a.txt"), "1行目\n2行目\n").ConfigureAwait(true);

        await h.Shell.Graft.ProjectPane.RegisterFolderAsync(other).ConfigureAwait(true);
        await h.RegisterProjectAsync().ConfigureAwait(true); // 最後に登録した a が選択中になる。
        h.Shell.Graft.ProjectPane.SelectedItem!.Project.Root.Should().Be(h.ProjectDirectory, "前提");

        await h.ParseAsync("a.txt", "2行目", "2行目（変更後）").ConfigureAwait(true);
        var tab = (await h.Shell.Editor.OpenFileAsync(h.ProjectPath("a.txt")).ConfigureAwait(true)).Value;
        tab.Session.Document.Insert(0, "X");
        return (h, other, tab);
    }

    [AvaloniaFact(DisplayName = "未保存の確認でキャンセルすると、プロジェクトは切り替わらず、選択も解析結果もタブも元のまま")]
    public async Task キャンセルすると切り替わらない()
    {
        var (h, other, tab) = await SetUpAsync().ConfigureAwait(true);
        using var _ = h;
        h.Dialogs.ThreeWayAnswer = (_, _) => null;
        var pane = h.Shell.Graft.ProjectPane;
        var target = pane.Items.Single(i => i.Project.Root == other);

        await pane.SelectAsync(target).ConfigureAwait(true);

        h.Dialogs.ThreeWays.Should().NotBeEmpty("未保存の確認が出る");
        pane.SelectedItem!.Project.Root.Should().Be(h.ProjectDirectory, "キャンセルしたので選択は元のプロジェクトへ戻る");
        h.Shell.Graft.Blocks.Should().HaveCount(1, "解析結果は破棄されない（切り替えが確定していない）");
        h.Shell.Editor.Tabs.Should().Contain(tab, "タブも閉じない");
        tab.Session.IsModified.Should().BeTrue();
        h.Shell.IsProjectSwitchBusy.Should().BeFalse();
    }

    [AvaloniaFact(DisplayName = "未保存の確認で「保存しない」を選ぶと切り替わり、解析結果も破棄される")]
    public async Task 保存しないを選ぶと切り替わる()
    {
        var (h, other, _) = await SetUpAsync().ConfigureAwait(true);
        using var _h = h;
        h.Dialogs.ThreeWayAnswer = (_, _) => false;
        var pane = h.Shell.Graft.ProjectPane;
        var target = pane.Items.Single(i => i.Project.Root == other);

        await pane.SelectAsync(target).ConfigureAwait(true);
        await ShellHarness.WaitUntilAsync(() => h.Shell.Graft.Blocks.Count == 0).ConfigureAwait(true);

        pane.SelectedItem!.Project.Root.Should().Be(other);
        h.Shell.Graft.Blocks.Should().BeEmpty("切り替えが確定してから解析結果を破棄する");
        h.Shell.Editor.Tabs.Should().BeEmpty();
    }

    [AvaloniaFact(DisplayName = "未保存が無ければ、確認なしでこれまでどおり切り替わる")]
    public async Task 未保存が無ければそのまま切り替わる()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        var other = Path.Combine(h.Root, "other");
        Directory.CreateDirectory(other);
        await h.Shell.Graft.ProjectPane.RegisterFolderAsync(other).ConfigureAwait(true);
        await h.RegisterProjectAsync().ConfigureAwait(true);
        var pane = h.Shell.Graft.ProjectPane;

        await pane.SelectAsync(pane.Items.Single(i => i.Project.Root == other)).ConfigureAwait(true);

        pane.SelectedItem!.Project.Root.Should().Be(other);
        h.Dialogs.ThreeWays.Should().BeEmpty();
    }
}
