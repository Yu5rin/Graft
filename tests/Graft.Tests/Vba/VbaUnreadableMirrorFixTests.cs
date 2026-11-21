using System.IO;
using FluentAssertions;
using Graft.Core;
using Graft.UiTests.Vba;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// 「疑わしい点」の修正: 読めないミラーのファイルを「無い」と扱わない。
/// 修正前は <c>ReadMirrorAsync</c> が読めなかったファイルを黙って落として計画に渡したので、記録があれば削除保留の印が付き、
/// 記録が無く Excel にあるモジュールは取り込み（既存のファイルを新規の形で上書き）になっていた。
/// 読めないファイルは、ロックや権限で起きる。Linux のテストでは、リンク先の無いシンボリックリンクで再現する
/// （一覧には出るが、読むと IOException になる）。
/// </summary>
public class VbaUnreadableMirrorFixTests : VbaServiceFixture
{
    /// <summary>ミラーのファイルを、読めないもの（リンク先が無いシンボリックリンク）に差し替える。</summary>
    private void MakeUnreadable(string relative)
    {
        var path = MirrorFile(relative);
        File.Delete(path);
        File.CreateSymbolicLink(path, Path.Combine(Ws.RootPath, "no-such-target"));
    }

    [Fact(DisplayName = "読めないミラー 再現: 記録のあるファイルが読めなくても、削除保留の印を付けず、Pull もせず、警告にする")]
    public async Task 読めなくても削除保留にならない()
    {
        var project = await CreatedProjectAsync(FakeVbaBridge.Std("Module1", "a"), FakeVbaBridge.Std("Module2", "b"));
        Excel("Module1").Code = "a2"; // Excel 側も変わっている。読めるなら Pull になるところ。
        MakeUnreadable("Modules/Module1.bas");

        var pull = await Service.PullAsync(project);

        pull.IsSuccess.Should().BeTrue();
        pull.Value.Pulled.Should().BeEmpty("読めないモジュールは計画から外す");
        pull.Issues.Should().Contain(i => i.Severity == Severity.Warning && i.Detail!.Contains("Modules/Module1.bas") && i.Detail.Contains("同期の対象から外します"));
        (await StateAsync()).Modules["Modules/Module1.bas"].DeletePending.Should().BeFalse("読めないだけで、無いわけではない");
        (await Service.GetLocalStatusAsync(project)).DeletePendingCount.Should().Be(0);
        (await Service.GetRestorablePathsAsync(project)).Should().BeEmpty();
    }

    [Fact(DisplayName = "読めないミラー 再現: 記録が無く Excel にあるモジュールのファイルが読めないとき、既存のファイルを新規の形で上書きする取り込みをしない")]
    public async Task 読めないファイルを取り込みで上書きしない()
    {
        var project = await CreatedProjectAsync(FakeVbaBridge.Std("Module1", "a"));
        // 記録を消して「記録が無く、Excel にはある」状態にする。
        await States.SaveAsync(project.Id, (await StateAsync()) with { Modules = new Dictionary<string, Graft.Core.Vba.VbaModuleState>() });
        MakeUnreadable("Modules/Module1.bas");

        var pull = await Service.PullAsync(project);

        pull.Value.Pulled.Should().BeEmpty();
        new FileInfo(MirrorFile("Modules/Module1.bas")).LinkTarget.Should().NotBeNull("リンクのまま。取り込みで置き換えない");
    }

    [Fact(DisplayName = "読めないミラー: 読めないモジュールは Push も検査（CheckAsync）の対象から外れ、ほかのモジュールは通常どおり動く")]
    public async Task ほかのモジュールは動く()
    {
        var project = await CreatedProjectAsync(FakeVbaBridge.Std("Module1", "a"), FakeVbaBridge.Std("Module2", "b"));
        File.WriteAllText(MirrorFile("Modules/Module2.bas"), "b edited\r\n");
        MakeUnreadable("Modules/Module1.bas");

        var push = await Service.PushAsync(project);

        push.Value.Pushed.Should().Equal("Modules/Module2.bas");
        Excel("Module1").Code.Should().Be("a");
        var check = await Service.CheckAsync(project);
        check.Value.Pull.Should().BeEmpty();
        check.Value.Conflicts.Should().BeEmpty();
    }

    [Fact(DisplayName = "読めないミラー: 読めるようになれば、通常どおり同期の対象に戻る")]
    public async Task 読めるようになれば戻る()
    {
        var project = await CreatedProjectAsync(FakeVbaBridge.Std("Module1", "a"));
        MakeUnreadable("Modules/Module1.bas");
        (await Service.PullAsync(project)).Value.IsEmpty.Should().BeTrue();

        File.Delete(MirrorFile("Modules/Module1.bas"));
        File.WriteAllText(MirrorFile("Modules/Module1.bas"), "a edited\r\n");

        (await Service.PushAsync(project)).Value.Pushed.Should().Equal("Modules/Module1.bas");
    }
}
