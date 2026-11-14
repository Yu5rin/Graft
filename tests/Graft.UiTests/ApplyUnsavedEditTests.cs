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

    [AvaloniaFact(DisplayName = "保存後にやり直した結果、適用できなくなったら、適用せずに止めて知らせる")]
    public async Task 保存後に適用できなくなったら止めて知らせる()
    {
        var (h, tab) = await ParseThenEditAsync().ConfigureAwait(true);
        using var _ = h;
        // 未保存の編集で、SEARCHの対象行そのものを書き換える（保存すると一致しなくなる）。
        var doc = tab.Session.Document;
        var at = doc.Text.IndexOf("2行目", StringComparison.Ordinal);
        doc.Replace(at, "2行目".Length, "二行目");

        await ShellHarness.ExecuteAsync(h.Shell.Graft.ApplyCommand).ConfigureAwait(true);

        h.Dialogs.Messages.Should().Contain(m => m.Message.Contains("保存した内容に合わせて解析し直したところ、適用できない変更があります"));
        h.Dialogs.Confirms.Should().NotContain(c => c.Title == "適用の確認", "適用の確認まで進めない");
        var disk = await File.ReadAllTextAsync(h.ProjectPath("a.txt")).ConfigureAwait(true);
        disk.Should().Contain("二行目", "利用者の編集は保存されて残る").And.NotContain("2行目（変更後）", "パッチは適用されない");
        tab.Session.Document.Text.Should().Contain("二行目");
        h.Shell.Graft.HasApplyUndoNotice.Should().BeFalse("適用していない");
        h.Shell.Graft.Blocks.Should().ContainSingle().Which.Plan.CanApply.Should().BeFalse("やり直した結果が画面に残る");
    }

    [AvaloniaFact(DisplayName = "保存が起きたときだけドライランをやり直し、起きなかったときはやり直さない")]
    public async Task 保存が起きたときだけやり直す()
    {
        // 保存なし: 確認で断ると、解析結果（ブロックの実体）は解析時のまま。
        using (var h = await ShellHarness.CreateAsync().ConfigureAwait(true))
        {
            await File.WriteAllTextAsync(h.ProjectPath("a.txt"), "1行目\n2行目\n3行目\n").ConfigureAwait(true);
            await h.RegisterProjectAsync().ConfigureAwait(true);
            await h.ParseAsync("a.txt", "2行目", "2行目（変更後）").ConfigureAwait(true);
            var before = h.Shell.Graft.Blocks[0];
            h.Dialogs.ConfirmAnswer = (title, _) => title != "適用の確認";

            await ShellHarness.ExecuteAsync(h.Shell.Graft.ApplyCommand).ConfigureAwait(true);

            h.Dialogs.Confirms.Should().Contain(c => c.Title == "適用の確認", "前提: 確認まで進んだ");
            h.Shell.Graft.Blocks[0].Should().BeSameAs(before, "保存が起きなかったので、ドライランはやり直さない");
        }

        // 保存あり: 同じく確認で断っても、保存後の内容で解析し直されている。
        var (h2, _) = await ParseThenEditAsync().ConfigureAwait(true);
        using (h2)
        {
            var before = h2.Shell.Graft.Blocks[0];
            h2.Dialogs.ConfirmAnswer = (title, _) => title != "適用の確認";

            await ShellHarness.ExecuteAsync(h2.Shell.Graft.ApplyCommand).ConfigureAwait(true);

            h2.Shell.Graft.Blocks[0].Should().NotBeSameAs(before, "保存が起きたのでやり直した");
            h2.Shell.Graft.Blocks[0].Plan.CanApply.Should().BeTrue();
        }
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
