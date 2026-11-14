using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.UiTests.TestSupport;

namespace Graft.UiTests;

/// <summary>
/// C3: 適用後の再読込で、エディタの未保存の編集が黙って消える不具合の回帰テスト。
/// 解析して差分を見ている間に対象をエディタで直し、保存しないまま適用した場合を扱う。
/// </summary>
public class ApplyUnsavedEditTests
{
    private static async Task<(ShellHarness H, Graft.ViewModels.EditorTabViewModel Tab)> ParseThenEditAsync()
    {
        var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        await File.WriteAllTextAsync(h.ProjectPath("a.txt"), "1行目\n2行目\n3行目\n").ConfigureAwait(true);
        await h.RegisterProjectAsync().ConfigureAwait(true);

        // 先に解析する（未保存の確認はこの時点で一度済む）。そのあとでエディタを直す。
        await h.ParseAsync("a.txt", "2行目", "2行目（変更後）").ConfigureAwait(true);
        var tab = (await h.Shell.Editor.OpenFileAsync(h.ProjectPath("a.txt")).ConfigureAwait(true)).Value;
        tab.Session.Document.Insert(0, "先頭に追記\n");
        tab.Session.IsModified.Should().BeTrue("前提: 解析のあとに未保存の編集を足した");
        return (h, tab);
    }

    [AvaloniaFact(DisplayName = "解析のあとに未保存の編集を足して適用すると、保存を促され、編集は消えない")]
    public async Task 適用の直前に保存を促し編集は消えない()
    {
        var (h, tab) = await ParseThenEditAsync().ConfigureAwait(true);
        using var _ = h;

        await ShellHarness.ExecuteAsync(h.Shell.Graft.ApplyCommand).ConfigureAwait(true);

        h.Dialogs.Confirms.Should().Contain(c => c.Title == "未保存の変更を保存します", "書き込みの前に保存を促す");
        var disk = await File.ReadAllTextAsync(h.ProjectPath("a.txt")).ConfigureAwait(true);
        disk.Should().Contain("先頭に追記").And.Contain("2行目（変更後）", "編集も適用も両方ディスクに入る");
        tab.Session.Document.Text.Should().Contain("先頭に追記", "エディタの編集は再読込で消えない");
        tab.Session.Document.Text.Should().Contain("2行目（変更後）");
    }

    [AvaloniaFact(DisplayName = "保存を断ると適用は始まらず、ファイルも編集も元のまま")]
    public async Task 保存を断ると適用しない()
    {
        var (h, tab) = await ParseThenEditAsync().ConfigureAwait(true);
        using var _ = h;
        h.Dialogs.ConfirmAnswer = (title, _) => title != "未保存の変更を保存します";

        await ShellHarness.ExecuteAsync(h.Shell.Graft.ApplyCommand).ConfigureAwait(true);

        (await File.ReadAllTextAsync(h.ProjectPath("a.txt")).ConfigureAwait(true)).Should().Be("1行目\n2行目\n3行目\n");
        tab.Session.IsModified.Should().BeTrue("未保存の編集はそのまま残る");
        h.Shell.Graft.HasApplyUndoNotice.Should().BeFalse("適用していない");
    }

    [AvaloniaFact(DisplayName = "再読込の対象に未保存の編集があるタブは、黙って差し替えず「外部で変更されました」の衝突表示に回す")]
    public async Task 再読込は未保存のタブを差し替えない()
    {
        var (h, tab) = await ParseThenEditAsync().ConfigureAwait(true);
        using var _ = h;
        await File.WriteAllTextAsync(h.ProjectPath("a.txt"), "ディスク側が変わった\n").ConfigureAwait(true);

        await h.Shell.Editor.ReloadIfOpenAsync(new[] { h.ProjectPath("a.txt") }).ConfigureAwait(true);

        tab.Session.Document.Text.Should().Contain("先頭に追記", "未保存の編集は黙って消さない");
        tab.HasExternalConflict.Should().BeTrue("衝突の表示で利用者に選ばせる");
    }

    [AvaloniaFact(DisplayName = "未保存の編集が無いタブは、従来どおり再読込される")]
    public async Task 未保存でなければ再読込される()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        await File.WriteAllTextAsync(h.ProjectPath("a.txt"), "元\n").ConfigureAwait(true);
        await h.RegisterProjectAsync().ConfigureAwait(true);
        var tab = (await h.Shell.Editor.OpenFileAsync(h.ProjectPath("a.txt")).ConfigureAwait(true)).Value;
        await File.WriteAllTextAsync(h.ProjectPath("a.txt"), "新\n").ConfigureAwait(true);

        await h.Shell.Editor.ReloadIfOpenAsync(new[] { h.ProjectPath("a.txt") }).ConfigureAwait(true);

        tab.Session.Document.Text.Should().Be("新\n");
        tab.HasExternalConflict.Should().BeFalse();
    }
}
