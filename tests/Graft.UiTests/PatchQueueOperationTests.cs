using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Features;
using Graft.UiTests.TestSupport;

namespace Graft.UiTests;

/// <summary>
/// C12: パッチキューの保存（変わるたび）・積んだプロジェクトの記録と結合時の確認・
/// 結合して適用した後の件数・一覧の説明表示の回帰テスト。
/// </summary>
public class PatchQueueOperationTests
{
    private static string Patch(string file, string search, string replace, string note = "")
        => $"""
            <<<< FILE: {file}
            summary: テスト用の変更
            <<<<<<< SEARCH {note}
            {search}
            =======
            {replace}
            >>>>>>> REPLACE
            >>>> END

            """;

    private static async Task<ShellHarness> OpenAsync()
    {
        var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        await File.WriteAllTextAsync(h.ProjectPath("a.txt"), "1行目\n2行目\n3行目\n").ConfigureAwait(true);
        await File.WriteAllTextAsync(h.ProjectPath("b.txt"), "B1\nB2\n").ConfigureAwait(true);
        await h.RegisterProjectAsync().ConfigureAwait(true);
        return h;
    }

    private static async Task QueueAsync(ShellHarness h, string patchText)
    {
        h.Clipboard.Text = patchText;
        await ShellHarness.ExecuteAsync(h.Shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        await ShellHarness.ExecuteAsync(h.Shell.Graft.AddCurrentPatchToQueueCommand).ConfigureAwait(true);
    }

    private static async Task<IReadOnlyList<QueuedBlock>> ReloadFromDiskAsync(ShellHarness h)
    {
        var fresh = new PatchQueue(h.AppPaths);
        await fresh.LoadAsync().ConfigureAwait(true);
        return fresh.Items;
    }

    /// <summary>保存されたqueue.jsonの件数が期待どおりになるまで待つ（保存は裏で行われる）。</summary>
    private static async Task WaitForSavedCountAsync(ShellHarness h, int expected)
    {
        for (var i = 0; i < 300; i++)
        {
            if ((await ReloadFromDiskAsync(h).ConfigureAwait(true)).Count == expected) return;
            await Task.Delay(10).ConfigureAwait(true);
        }
    }

    [AvaloniaFact(DisplayName = "積む・外す・空にするたびにqueue.jsonへ保存される（正常終了を待たない）")]
    public async Task 変わるたびに保存される()
    {
        using var h = await OpenAsync().ConfigureAwait(true);

        await QueueAsync(h, Patch("a.txt", "2行目", "2行目（変更後）")).ConfigureAwait(true);
        await WaitForSavedCountAsync(h, 1).ConfigureAwait(true);
        (await ReloadFromDiskAsync(h).ConfigureAwait(true)).Should().HaveCount(1, "積んだ時点で保存されている");

        await QueueAsync(h, Patch("b.txt", "B2", "B2（変更後）")).ConfigureAwait(true);
        await WaitForSavedCountAsync(h, 2).ConfigureAwait(true);
        (await ReloadFromDiskAsync(h).ConfigureAwait(true)).Should().HaveCount(2);

        h.Shell.Graft.Queue.RemoveCommand.Execute(h.Shell.Graft.Queue.Items[0]);
        await WaitForSavedCountAsync(h, 1).ConfigureAwait(true);
        (await ReloadFromDiskAsync(h).ConfigureAwait(true)).Should().HaveCount(1, "外した時点で保存されている");

        await ShellHarness.ExecuteAsync(h.Shell.Graft.Queue.ClearCommand).ConfigureAwait(true);
        await WaitForSavedCountAsync(h, 0).ConfigureAwait(true);
        (await ReloadFromDiskAsync(h).ConfigureAwait(true)).Should().BeEmpty("空にした時点で保存されている");
    }

    [AvaloniaFact(DisplayName = "キューの項目は積んだときのプロジェクトを覚え、保存して読み直しても残る")]
    public async Task 積んだプロジェクトを記録する()
    {
        using var h = await OpenAsync().ConfigureAwait(true);
        var projectId = h.Shell.Graft.ProjectPane.SelectedItem!.Project.Id;

        await QueueAsync(h, Patch("a.txt", "2行目", "2行目（変更後）")).ConfigureAwait(true);
        await WaitForSavedCountAsync(h, 1).ConfigureAwait(true);

        h.Shell.Graft.PatchQueue.Items[0].ProjectId.Should().Be(projectId);
        (await ReloadFromDiskAsync(h).ConfigureAwait(true))[0].ProjectId.Should().Be(projectId);
    }

    [AvaloniaFact(DisplayName = "別のプロジェクトを選んだ状態で結合しようとすると、警告して確認する。断ると結合しない")]
    public async Task 別プロジェクトでの結合は確認する()
    {
        using var h = await OpenAsync().ConfigureAwait(true);
        await QueueAsync(h, Patch("a.txt", "2行目", "2行目（変更後）")).ConfigureAwait(true);

        var other = Path.Combine(h.Root, "other");
        Directory.CreateDirectory(other);
        await h.Shell.Graft.ProjectPane.RegisterFolderAsync(other).ConfigureAwait(true); // 選択が移る。
        await ShellHarness.WaitUntilAsync(() => h.Shell.Graft.ProjectPane.SelectedItem!.Project.Root == other).ConfigureAwait(true);
        h.Dialogs.ConfirmAnswer = (title, _) => !title.Contains("別のプロジェクト");

        await ShellHarness.ExecuteAsync(h.Shell.Graft.Queue.MergeCommand).ConfigureAwait(true);

        h.Dialogs.Confirms.Should().Contain(c => c.Title.Contains("別のプロジェクト") && c.Message.Contains("結合"));
        h.Shell.Graft.Blocks.Should().BeEmpty("断ったので結合も解析もしない");
        h.Shell.Graft.PatchQueue.Items.Should().HaveCount(1, "キューはそのまま残る");
    }

    [AvaloniaFact(DisplayName = "同じプロジェクトでの結合は確認なしで進み、適用後はキューが空になり件数が残らない")]
    public async Task 結合して適用するとキューが空になる()
    {
        using var h = await OpenAsync().ConfigureAwait(true);
        await QueueAsync(h, Patch("a.txt", "2行目", "2行目（変更後）")).ConfigureAwait(true);
        await QueueAsync(h, Patch("b.txt", "B2", "B2（変更後）")).ConfigureAwait(true);
        h.Shell.Graft.Queue.Items.Should().HaveCount(2);

        await ShellHarness.ExecuteAsync(h.Shell.Graft.Queue.MergeCommand).ConfigureAwait(true);
        await ShellHarness.WaitUntilAsync(() => h.Shell.Graft.Blocks.Count == 2).ConfigureAwait(true);
        h.Dialogs.Confirms.Should().NotContain(c => c.Title.Contains("別のプロジェクト"));
        await ShellHarness.ExecuteAsync(h.Shell.Graft.ApplyCommand).ConfigureAwait(true);

        (await File.ReadAllTextAsync(h.ProjectPath("a.txt")).ConfigureAwait(true)).Should().Contain("2行目（変更後）");
        h.Shell.Graft.PatchQueue.Items.Should().BeEmpty();
        h.Shell.Graft.Queue.Items.Should().BeEmpty("一覧の件数（ツールバーの「キュー（N）」）が残ってはならない");
        await WaitForSavedCountAsync(h, 0).ConfigureAwait(true);
        (await ReloadFromDiskAsync(h).ConfigureAwait(true)).Should().BeEmpty("保存されたキューも空");
    }

    [AvaloniaFact(DisplayName = "キューの一覧は、SEARCH行に書かれた説明を表示する（「（説明なし）」にしない）")]
    public async Task 一覧に説明が出る()
    {
        using var h = await OpenAsync().ConfigureAwait(true);
        await QueueAsync(h, Patch("a.txt", "2行目", "2行目（変更後）", "# 2行目を直す")).ConfigureAwait(true);
        await QueueAsync(h, Patch("b.txt", "B2", "B2（変更後）")).ConfigureAwait(true);

        h.Shell.Graft.Queue.Items[0].DescriptionText.Should().Be("2行目を直す");
        h.Shell.Graft.Queue.Items[1].DescriptionText.Should().Be("変更", "説明が無いときも「（説明なし）」とは出さず、操作の種類から補う");
    }
}
