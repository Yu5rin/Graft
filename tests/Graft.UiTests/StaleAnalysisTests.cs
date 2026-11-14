using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;

namespace Graft.UiTests;

/// <summary>
/// C15: 解析中にプロジェクトを切り替えると、古い結果が反映される不具合の回帰テスト。
/// 解析（ドライラン）は途中で未保存の保存確認を待つことがあり、その間に別のプロジェクトへ
/// 切り替えても、待っていた古い要求の結果を切り替え先の画面へ入れてはならない。
/// </summary>
public class StaleAnalysisTests
{
    [AvaloniaFact(DisplayName = "保存確認の待ち中にプロジェクトを切り替えたら、古い解析結果は切り替え先に反映されない")]
    public async Task 待ち中の切り替えで古い結果は捨てられる()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        var other = Path.Combine(h.Root, "other");
        Directory.CreateDirectory(other);
        await h.Shell.Graft.ProjectPane.RegisterFolderAsync(other).ConfigureAwait(true);
        await File.WriteAllTextAsync(h.ProjectPath("a.txt"), "1行目\n2行目\n").ConfigureAwait(true);
        await h.RegisterProjectAsync().ConfigureAwait(true); // 選択中はa側。
        var pane = h.Shell.Graft.ProjectPane;
        var tab = (await h.Shell.Editor.OpenFileAsync(h.ProjectPath("a.txt")).ConfigureAwait(true)).Value;
        tab.Session.Document.Insert(0, "X"); // 未保存 → 解析の途中で保存確認が出る。

        // 保存確認を、こちらが許可するまで止めておく。
        var release = new TaskCompletionSource<bool>();
        h.Dialogs.ConfirmAnswerAsync = (title, _) => title == "未保存の変更を保存します" ? release.Task : null;
        h.Dialogs.ThreeWayAnswer = (_, _) => false; // 切り替え時の未保存確認は「保存しない」。

        h.Clipboard.Text = ShellHarness.BuildPatch("a.txt", "2行目", "2行目（変更後）");
        h.Shell.Graft.PasteAndParseCommand.Execute(null);
        await ShellHarness.WaitUntilAsync(() => h.Dialogs.Confirms.Count > 0).ConfigureAwait(true);
        h.Dialogs.Confirms.Should().NotBeEmpty("前提: 解析が保存確認の待ちで止まっている");

        // 待っている間に別のプロジェクトへ切り替える。
        await pane.SelectAsync(pane.Items.Single(i => i.Project.Root == other)).ConfigureAwait(true);
        release.SetResult(true);
        await Task.Delay(300).ConfigureAwait(true);

        pane.SelectedItem!.Project.Root.Should().Be(other);
        h.Shell.Graft.Blocks.Should().BeEmpty("元のプロジェクトの解析結果を、切り替え先の画面へ入れてはならない");
        h.Shell.Graft.State.Should().NotBe(CenterPaneState.Content);
        h.Shell.Graft.ApplyCommand.CanExecute(null).Should().BeFalse("古い文脈で適用できてしまってはならない");
    }
}
