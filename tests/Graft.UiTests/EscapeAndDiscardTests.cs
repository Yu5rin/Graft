using Avalonia.Automation;
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
/// C1: Esc で解析結果が確認なしに消える不具合の回帰テスト。
/// Esc は「いちばん手前のものを1つ閉じる」だけに使い、解析結果は破棄しない。
/// 破棄は「破棄」ボタン（DiscardCommand）で行い、数秒だけ「元に戻す」が出る。
/// </summary>
public class EscapeAndDiscardTests
{
    private static async Task<ShellHarness> OpenWithAnalysisAsync(bool withWindow)
    {
        var h = await ShellHarness.CreateAsync(withWindow: withWindow).ConfigureAwait(true);
        await File.WriteAllTextAsync(h.ProjectPath("a.txt"), "1行目\n2行目\n3行目\n").ConfigureAwait(true);
        await h.RegisterProjectAsync().ConfigureAwait(true);
        await h.ParseAsync("a.txt", "2行目", "2行目（変更後）").ConfigureAwait(true);
        h.Shell.Graft.Blocks.Should().HaveCount(1, "前提: 解析結果が出ている");
        return h;
    }

    [AvaloniaFact(DisplayName = "Escを押しても解析結果は破棄されない")]
    public async Task Escで解析結果は消えない()
    {
        using var h = await OpenWithAnalysisAsync(withWindow: true).ConfigureAwait(true);

        h.Window!.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        h.Shell.Graft.Blocks.Should().HaveCount(1, "Escでは破棄しない（以前は何にフォーカスがあっても消えた）");
        h.Shell.Graft.DiscardCommand.CanExecute(null).Should().BeTrue();
    }

    [AvaloniaFact(DisplayName = "絞り込み欄でEscを押すと、解析結果は残したまま欄の文字だけが消える")]
    public async Task 絞り込み欄のEscは文字だけ消す()
    {
        using var h = await OpenWithAnalysisAsync(withWindow: true).ConfigureAwait(true);
        Dispatcher.UIThread.RunJobs();

        var filter = h.Window!.GetVisualDescendants().OfType<TextBox>()
            .First(t => AutomationProperties.GetName(t) == "ブロック一覧を検索");
        filter.Focus();
        filter.Text = "a.txt";
        Dispatcher.UIThread.RunJobs();
        h.Shell.Graft.FilterText.Should().Be("a.txt", "前提: 絞り込み文字が入っている");

        h.Window!.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        h.Shell.Graft.FilterText.Should().BeEmpty("Escは手前の入力欄の文字を消す");
        h.Shell.Graft.Blocks.Should().HaveCount(1, "欄を消しただけで解析結果は破棄されない");
    }

    [AvaloniaFact(DisplayName = "プロンプトのポップアップが開いているときのEscは、ポップアップだけを閉じる")]
    public async Task ポップアップのEscは閉じるだけ()
    {
        using var h = await OpenWithAnalysisAsync(withWindow: true).ConfigureAwait(true);
        h.Shell.Graft.PromptCopy.Should().NotBeNull();
        h.Shell.Graft.PromptCopy!.IsOpen = true;

        h.Window!.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        h.Shell.Graft.PromptCopy.IsOpen.Should().BeFalse("Escで手前のポップアップが閉じる");
        h.Shell.Graft.Blocks.Should().HaveCount(1, "ポップアップを閉じただけで解析結果は消えない");
    }

    [AvaloniaFact(DisplayName = "破棄すると「元に戻す」が出て、押すと破棄前の解析結果（チェックの状態を含む）に戻る")]
    public async Task 破棄の元に戻す()
    {
        using var h = await OpenWithAnalysisAsync(withWindow: false).ConfigureAwait(true);
        var graft = h.Shell.Graft;
        graft.Blocks[0].IsSelected = false; // 利用者がチェックを外した状態も戻ること。

        await ShellHarness.ExecuteAsync(graft.DiscardCommand).ConfigureAwait(true);

        graft.Blocks.Should().BeEmpty();
        graft.Notice.IsVisible.Should().BeTrue("破棄したら数秒だけ通知が出る");
        graft.Notice.Text.Should().Contain("破棄しました");
        graft.Notice.ActionLabel.Should().Be("元に戻す");

        await ShellHarness.ExecuteAsync(graft.Notice.ActionCommand).ConfigureAwait(true);

        graft.Blocks.Should().HaveCount(1, "破棄前の解析結果へ戻る");
        graft.Blocks[0].IsSelected.Should().BeFalse("外していたチェックの状態も戻る");
        graft.Notice.IsVisible.Should().BeFalse("押したら通知は消える");
        graft.DiscardCommand.CanExecute(null).Should().BeTrue();
    }

    [AvaloniaFact(DisplayName = "破棄のあとに新しく解析した場合、「元に戻す」は新しい解析結果を上書きしない")]
    public async Task 新しい解析結果は上書きしない()
    {
        using var h = await OpenWithAnalysisAsync(withWindow: false).ConfigureAwait(true);
        var graft = h.Shell.Graft;
        await ShellHarness.ExecuteAsync(graft.DiscardCommand).ConfigureAwait(true);

        await h.ParseAsync("a.txt", "3行目", "3行目（変更後）").ConfigureAwait(true);
        await ShellHarness.ExecuteAsync(graft.Notice.ActionCommand).ConfigureAwait(true);

        graft.Blocks.Should().HaveCount(1);
        graft.Blocks[0].Plan.Path.Should().Be("a.txt");
        // 新しい解析（3行目）のままであること。戻すと古い解析（2行目）で上書きされてしまう。
        ((Graft.Core.SearchReplaceBlock)graft.Blocks[0].Plan.Block).Pairs[0].SearchText.Should().Contain("3行目");
    }
}
