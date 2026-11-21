using System.IO;
using FluentAssertions;
using Graft.Features;
using Graft.UiTests.Vba;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// 「疑わしい点」の修正: <c>PullAsync</c> がゲート（<c>_gate</c>）を持ったまま、利用者の未保存の確認ダイアログを待たない。
/// 修正前は、確認を放置している間、ほかの取込・反映・検査がすべて待たされた。
/// </summary>
public class VbaPullGateFixTests : VbaServiceFixture
{
    private async Task<Project> ProjectWithExcelEditAsync()
    {
        var project = await CreatedProjectAsync(FakeVbaBridge.Std("Module1", "a"), FakeVbaBridge.Std("Module2", "b"));
        Excel("Module1").Code = "a2";
        return project;
    }

    [Fact(DisplayName = "Pull のゲート 再現: 未保存の確認を待っている間も、ほかの検査（CheckAsync）が通る。確認に答えると、そのとき最新の Excel の本文が取り込まれる")]
    public async Task 確認を待つ間ゲートを持たない()
    {
        var project = await ProjectWithExcelEditAsync();
        var asked = new TaskCompletionSource<IReadOnlyList<string>>();
        var answer = new TaskCompletionSource<VbaPullSaveOutcome>();
        Service.BeforePullAsync = paths =>
        {
            asked.TrySetResult(paths);
            return answer.Task;
        };

        var pulling = Service.PullAsync(project);
        (await asked.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().ContainSingle().Which.Should().Be(MirrorFile("Modules/Module1.bas"));

        // 利用者が確認を放置している間に、別の操作はゲートに阻まれない。
        var check = await Service.CheckAsync(project).WaitAsync(TimeSpan.FromSeconds(5));
        check.IsSuccess.Should().BeTrue();
        check.Value.Pull.Should().Equal("Modules/Module1.bas");

        // 待っている間に Excel 側が変わっても、実行するのは確認のあとに作った最新の計画。
        Excel("Module1").Code = "a3";
        answer.SetResult(VbaPullSaveOutcome.Nothing);
        var pull = await pulling.WaitAsync(TimeSpan.FromSeconds(5));

        pull.Value.Pulled.Should().Equal("Modules/Module1.bas");
        File.ReadAllText(MirrorFile("Modules/Module1.bas")).Should().Be("a3\r\n");
    }

    [Fact(DisplayName = "Pull のゲート: 確認を済ませたファイルは二度聞かない。断られたファイルは取り込まず、ほかは取り込む")]
    public async Task 確認は一度だけ()
    {
        var project = await ProjectWithExcelEditAsync();
        Excel("Module2").Code = "b2";
        var asks = new List<IReadOnlyList<string>>();
        Service.BeforePullAsync = paths =>
        {
            asks.Add(paths);
            return Task.FromResult(new VbaPullSaveOutcome(new[] { MirrorFile("Modules/Module1.bas") }, false));
        };

        var pull = await Service.PullAsync(project);

        asks.Should().ContainSingle("確認は 1 回だけ");
        pull.Value.Pulled.Should().Equal("Modules/Module2.bas");
        pull.Value.Skipped.Should().Equal("Modules/Module1.bas");
    }

    [Fact(DisplayName = "Pull のゲート: 確認の要るファイルが無ければ、確認は呼ばれずに取り込む（従来どおり）")]
    public async Task 確認が要らなければ呼ばれない()
    {
        var project = await ProjectWithExcelEditAsync();
        File.Delete(MirrorFile("Modules/Module1.bas")); // 取り込み先のファイルが存在しない＝未保存の編集もない（復元の場合）
        var called = false;
        Service.BeforePullAsync = _ =>
        {
            called = true;
            return Task.FromResult(VbaPullSaveOutcome.Nothing);
        };

        var pull = await Service.PullAsync(project, new[] { "Modules/Module1.bas" }, restoreDeletePending: true);

        called.Should().BeFalse();
        pull.Value.Pulled.Should().Equal("Modules/Module1.bas");
    }
}
