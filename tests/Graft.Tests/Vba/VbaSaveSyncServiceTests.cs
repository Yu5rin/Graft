using System.IO;
using FluentAssertions;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Features;
using Graft.UiTests.Vba;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// 保存時の自動反映（機能 D。仕様書_ブックの情報と依頼の観点 8.4・8.5）の同期層。<see cref="VbaSyncService.PushSavedAsync"/> が
/// 「保存したモジュールだけ」を Excel から読む（全件読みにならない）こと、衝突は送らないこと、反映前の自動控えが
/// 手動の反映と同じに残ることを、<see cref="FakeVbaBridge"/> で固定する。
/// </summary>
public class VbaSaveSyncServiceTests : VbaServiceFixture
{
    private const string Module1 = "Modules/Module1.bas";
    private const string Module2 = "Modules/Module2.bas";

    private async Task<Project> ThreeModulesAsync()
        => await CreatedProjectAsync(
            FakeVbaBridge.Std("Module1", "Sub A()\r\nEnd Sub"),
            FakeVbaBridge.Std("Module2", "Sub B()\r\nEnd Sub"),
            FakeVbaBridge.Cls("Cls1", "Option Explicit"));

    [Fact(DisplayName = "絞った読み取り: 保存したモジュールの名前だけを ReadComponentsAsync に渡す（全件読みにならない）")]
    public async Task 名前を絞って読む()
    {
        var project = await ThreeModulesAsync();
        File.WriteAllText(MirrorFile(Module1), "Sub A()\r\n  x = 1\r\nEnd Sub\r\n");
        Bridge.ReadNameRequests.Clear();

        var push = await Service.PushSavedAsync(project, new[] { Module1 });

        push.IsSuccess.Should().BeTrue();
        push.Value.Pushed.Should().Equal(Module1);
        Bridge.ReadNameRequests.Should().ContainSingle("Excel は 1 回だけ読む")
            .Which.Should().Equal("Module1");
        Excel("Module1").Code.Should().Contain("x = 1");
        Excel("Module2").Code.Should().Be("Sub B()\r\nEnd Sub", "保存していないモジュールには触れない");
    }

    [Fact(DisplayName = "絞った読み取り: ミラーの他のファイルは走査しない（対象外のファイルへの E810 を出さない）")]
    public async Task ミラーの他のファイルは読まない()
    {
        var project = await ThreeModulesAsync();
        File.WriteAllText(MirrorFile(Module1), "Sub A()\r\n  x = 1\r\nEnd Sub\r\n");
        // 同期の対象外のファイルがあると、全体を走査する経路は E810 の警告を出す。絞った経路は走査しないので出さない。
        File.WriteAllText(Path.Combine(MirrorRoot, "memo.txt"), "対象外");

        var narrow = await Service.PushSavedAsync(project, new[] { Module1 });
        File.WriteAllText(MirrorFile(Module1), "Sub A()\r\n  x = 2\r\nEnd Sub\r\n");
        var full = await Service.PushAsync(project, new[] { Module1 });

        narrow.Issues.Should().NotContain(i => i.Code == ErrorCode.E810);
        full.Issues.Should().Contain(i => i.Code == ErrorCode.E810, "対照: 従来の経路は全体を走査する");
    }

    [Fact(DisplayName = "絞らない Push は従来どおり全体を読む（names が null）")]
    public async Task 絞らないPushは全体を読む()
    {
        var project = await ThreeModulesAsync();
        File.WriteAllText(MirrorFile(Module1), "Sub A()\r\n  x = 1\r\nEnd Sub\r\n");
        Bridge.ReadNameRequests.Clear();

        await Service.PushAsync(project);
        await Service.PushAsync(project, new[] { Module1 });

        Bridge.ReadNameRequests.Should().HaveCount(2).And.OnlyContain(n => n == null, "手動の反映は絞らない（8.4）");
    }

    [Fact(DisplayName = "衝突: 送らない。結果の Conflicts に載り、Excel 側の本文は変わらない")]
    public async Task 衝突は送らない()
    {
        var project = await ThreeModulesAsync();
        Excel("Module1").Code = "Sub A()\r\n  ' Excel で編集\r\nEnd Sub";
        File.WriteAllText(MirrorFile(Module1), "Sub A()\r\n  ' Graft で編集\r\nEnd Sub\r\n");

        var push = await Service.PushSavedAsync(project, new[] { Module1 });

        push.IsSuccess.Should().BeTrue();
        push.Value.Conflicts.Should().Equal(Module1);
        push.Value.Pushed.Should().BeEmpty();
        Excel("Module1").Code.Should().Contain("Excel で編集");
        Bridge.WriteRequests.Should().BeEmpty("衝突のときは Excel へ書かない");
    }

    [Fact(DisplayName = "反映前の自動控えが、手動の反映と同じに -auto として残る")]
    public async Task 自動控えが残る()
    {
        var project = await ThreeModulesAsync();
        File.WriteAllText(MirrorFile(Module1), "Sub A()\r\n  x = 1\r\nEnd Sub\r\n");

        await Service.PushSavedAsync(project, new[] { Module1 });

        RescueFileNames().Should().Equal("Module1-20261121-100000-auto.txt");
        File.ReadAllText(Path.Combine(Paths.GetVbaRescueDirectory(project.Id), "Module1-20261121-100000-auto.txt"))
            .Should().Be("Sub A()\r\nEnd Sub");
    }

    [Fact(DisplayName = "複数のモジュールを 1 回で送る: Excel の読み取りも書き込みも 1 回")]
    public async Task 複数をまとめて送る()
    {
        var project = await ThreeModulesAsync();
        File.WriteAllText(MirrorFile(Module1), "Sub A()\r\n  x = 1\r\nEnd Sub\r\n");
        File.WriteAllText(MirrorFile(Module2), "Sub B()\r\n  y = 1\r\nEnd Sub\r\n");
        Bridge.ReadNameRequests.Clear();
        Bridge.WriteRequests.Clear();

        var push = await Service.PushSavedAsync(project, new[] { Module1, Module2 });

        push.Value.Pushed.Should().BeEquivalentTo(new[] { Module1, Module2 });
        Bridge.ReadNameRequests.Should().ContainSingle().Which.Should().BeEquivalentTo(new[] { "Module1", "Module2" });
        Bridge.WriteRequests.Should().ContainSingle();
    }

    [Fact(DisplayName = "保存したのが同期済みと同じ内容なら、何も書かない")]
    public async Task 変わっていなければ書かない()
    {
        var project = await ThreeModulesAsync();

        var push = await Service.PushSavedAsync(project, new[] { Module1 });

        push.IsSuccess.Should().BeTrue();
        push.Value.WrittenCount.Should().Be(0);
        Bridge.WriteRequests.Should().BeEmpty();
    }

    [Fact(DisplayName = "対象外のモジュールの同期の記録を、絞った読み取りで壊さない（記録の破棄・削除保留の取り違えが起きない）")]
    public async Task 対象外の記録を壊さない()
    {
        var project = await ThreeModulesAsync();
        var before = await StateAsync();
        File.WriteAllText(MirrorFile(Module1), "Sub A()\r\n  x = 1\r\nEnd Sub\r\n");

        await Service.PushSavedAsync(project, new[] { Module1 });

        var after = await StateAsync();
        after.Modules.Keys.Should().BeEquivalentTo(before.Modules.Keys, "Module2 と Cls1 の記録は残る");
        after.Modules[Module2].Should().Be(before.Modules[Module2]);
    }

    [Fact(DisplayName = "Excel に接続できないとき（E804）: 失敗として返す（例外にしない）。書き込みは起きない")]
    public async Task 接続できないと失敗で返る()
    {
        var project = await ThreeModulesAsync();
        File.WriteAllText(MirrorFile(Module1), "Sub A()\r\n  x = 1\r\nEnd Sub\r\n");
        Bridge.CloseWorkbook(Book);

        var push = await Service.PushSavedAsync(project, new[] { Module1 });

        push.IsSuccess.Should().BeFalse();
        push.Issues.Should().Contain(i => i.Code == ErrorCode.E804);
        Bridge.WriteRequests.Should().BeEmpty();
    }

    [Fact(DisplayName = "反映の最中は IsWritingToExcel が立ち、終わると下りる（閉じる確認などの既存の排他と整合する）")]
    public async Task 反映中の旗()
    {
        var project = await ThreeModulesAsync();
        File.WriteAllText(MirrorFile(Module1), "Sub A()\r\n  x = 1\r\nEnd Sub\r\n");
        var observed = false;
        Bridge.BeforeWrite = () =>
        {
            observed = Service.IsWritingToExcel;
            return Task.CompletedTask;
        };

        await Service.PushSavedAsync(project, new[] { Module1 });

        observed.Should().BeTrue();
        Service.IsWritingToExcel.Should().BeFalse();
    }

    [Fact(DisplayName = "控えの掃除: 保存時の自動反映では間引く（30 秒に 1 回）。手動の反映は従来どおり毎回掃除する")]
    public async Task 掃除は保存時だけ間引く()
    {
        var project = await ThreeModulesAsync();
        Service.Rescue.MaxGenerations = 1;

        // 保存時の自動反映を 3 回（時計は固定で、同じ 30 秒の中）。最初の 1 回だけ掃除が走るので、世代は上限を超えて残る。
        for (var i = 1; i <= 3; i++)
        {
            File.WriteAllText(MirrorFile(Module1), $"Sub A()\r\n  x = {i}\r\nEnd Sub\r\n");
            (await Service.PushSavedAsync(project, new[] { Module1 })).IsSuccess.Should().BeTrue();
        }

        RescueFileNames().Should().HaveCount(3, "掃除を間引いているので、上限（1 世代）を一時的に超えて残る");

        // 手動の反映は毎回掃除するので、上限の 1 世代に収まる。
        File.WriteAllText(MirrorFile(Module1), "Sub A()\r\n  x = 4\r\nEnd Sub\r\n");
        (await Service.PushAsync(project, new[] { Module1 })).IsSuccess.Should().BeTrue();

        RescueFileNames().Should().HaveCount(1);
    }
}
