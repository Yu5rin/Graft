using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.UiTests.TestSupport;

namespace Graft.UiTests;

/// <summary>
/// C9: 成功を知らせるだけの内容を、OKを押すまで閉じないダイアログではなく、
/// ステータスバーの数秒で消える通知にする。押せる部分（「元に戻す」）だけをリンクに見せる。
/// </summary>
public class StatusNoticeTests
{
    [AvaloniaFact(DisplayName = "キューへ追加しても確認ダイアログは出ず、ステータスバーの通知になる")]
    public async Task キューへ追加は通知になる()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        await File.WriteAllTextAsync(h.ProjectPath("a.txt"), "1行目\n2行目\n").ConfigureAwait(true);
        await h.RegisterProjectAsync().ConfigureAwait(true);
        await h.ParseAsync("a.txt", "2行目", "2行目（変更後）").ConfigureAwait(true);

        await ShellHarness.ExecuteAsync(h.Shell.Graft.AddCurrentPatchToQueueCommand).ConfigureAwait(true);

        h.Dialogs.Messages.Should().NotContain(m => m.Title == "キューへ追加しました", "成功の知らせはダイアログにしない");
        h.Shell.Graft.Notice.IsVisible.Should().BeTrue();
        h.Shell.Graft.Notice.Text.Should().Contain("1件をキューへ追加しました");
        h.Shell.Graft.Notice.HasAction.Should().BeFalse("押す操作の無い通知にリンクは付けない");
    }

    [AvaloniaFact(DisplayName = "修正依頼文をコピーしても、ダイアログは出ずステータスバーの通知になる")]
    public async Task 修正依頼のコピーは通知になる()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        await File.WriteAllTextAsync(h.ProjectPath("a.txt"), "1行目\n2行目\n").ConfigureAwait(true);
        await h.RegisterProjectAsync().ConfigureAwait(true);
        await h.ParseAsync("a.txt", "存在しない行", "置換後").ConfigureAwait(true);
        h.Shell.Graft.CopyRecoveryPromptCommand.CanExecute(null).Should().BeTrue("前提: 失敗ブロックがある");

        await ShellHarness.ExecuteAsync(h.Shell.Graft.CopyRecoveryPromptCommand).ConfigureAwait(true);

        h.Dialogs.Messages.Should().NotContain(m => m.Title == "修正依頼プロンプトをコピーしました");
        h.Shell.Graft.Notice.IsVisible.Should().BeTrue();
        h.Shell.Graft.Notice.Text.Should().Contain("修正依頼文をクリップボードへコピーしました");
        h.Clipboard.Text.Should().NotBeNullOrEmpty();
    }

    [AvaloniaFact(DisplayName = "プロンプトをコピーしても、ダイアログは出ずステータスバーの通知になる")]
    public async Task プロンプトのコピーは通知になる()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        await h.RegisterProjectAsync().ConfigureAwait(true);
        var prompt = h.Shell.Graft.PromptCopy!;

        await prompt.QuickCopyAsync().ConfigureAwait(true);

        h.Dialogs.Messages.Should().NotContain(m => m.Title == "プロンプトをコピーしました");
        h.Shell.Graft.Notice.Text.Should().Contain("クリップボードへコピーしました");
    }

    [AvaloniaFact(DisplayName = "適用後の通知は、押せない文言と「元に戻す」に分かれている（全体をリンクにしない）")]
    public async Task 適用後の通知は文言とリンクに分かれる()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        await File.WriteAllTextAsync(h.ProjectPath("a.txt"), "1行目\n2行目\n").ConfigureAwait(true);
        await h.RegisterProjectAsync().ConfigureAwait(true);
        await h.ParseAsync("a.txt", "2行目", "2行目（変更後）").ConfigureAwait(true);

        await ShellHarness.ExecuteAsync(h.Shell.Graft.ApplyCommand).ConfigureAwait(true);

        h.Shell.Graft.HasApplyUndoNotice.Should().BeTrue();
        h.Shell.Graft.ApplyUndoNoticeMessage.Should().Be("r1 として適用しました", "リンクにしない文言の部分");
    }
}
