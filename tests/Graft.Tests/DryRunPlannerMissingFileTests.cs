using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Core;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 案件2の回帰テスト。実機で「ネットワークドライブ上ではファイルの存在確認自体が
/// 誤って失敗し、SEARCH/REPLACEの照合結果がE101（SEARCH部が見つからない）と誤表示される」
/// 不具合が見つかった。ここではLinux環境でも再現できる「ファイルが単純に存在しない」
/// ケースで、DryRunPlannerが正しくE210（ファイルが見つからない）を返し、E101を返さない
/// ことを確認する。あわせて、FULL形式が絡む正規のケース（新規作成・FULL/SR混在）が
/// この対応で壊れていないことも確認する。
/// </summary>
public class DryRunPlannerMissingFileTests
{
    private static string BuildSrPatch(string path, string search, string replace) =>
        $"<<<< FILE: {path}\n<<<<<<< SEARCH\n{search}\n=======\n{replace}\n>>>>>>> REPLACE\n";

    [Fact(DisplayName = "存在しないファイルへのSEARCH/REPLACEはE101ではなくE210になる")]
    public async Task 存在しないファイルへのSRはE210になる()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        // 意図的にファイルを作らない（実機のネットワークドライブ不具合では「本当は存在するのに
        // 存在確認が誤ってfalseを返す」状況だったが、DryRunPlanner側から見た挙動は
        // 「check.Exists == false」という同じ形になるため、この単純なケースで再現できる）。

        var patchText = BuildSrPatch("missing.txt", "hello", "world");
        var ctx = harness.MakeContext(1);
        var dryRun = await harness.DryRunAsync(patchText, ctx);

        var plan = dryRun.Plans.Single(p => p.Path == "missing.txt");
        plan.CanApply.Should().BeFalse();
        plan.Issues.Should().Contain(i => i.Code == ErrorCode.E210,
            "ファイルが存在しない場合は「見つからない」と分かるE210を返すべき");
        plan.Issues.Should().NotContain(i => i.Code == ErrorCode.E101,
            "E101は『ファイルは読めたが中身が一致しない』という意味に読めるため、" +
            "ファイル自体が存在しない場合に出すのは誤解を招く");
    }

    [Fact(DisplayName = "存在しないファイルへのAPPEND単独は新規作成として引き続き成功する")]
    public async Task 存在しないファイルへのAPPEND単独は成功する()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);

        var patchText = "<<<< APPEND: new.txt\nadded line\n>>>> END\n";
        var ctx = harness.MakeContext(1);
        var dryRun = await harness.DryRunAsync(patchText, ctx);

        var plan = dryRun.Plans.Single(p => p.Path == "new.txt");
        plan.CanApply.Should().BeTrue("APPEND単独は新規ファイル作成として正規に成功するはず（E210の対象外）");
        plan.Operation.Should().Be(EntryOperation.Create);
    }

    [Fact(DisplayName = "存在しないファイルへのFULL単独は新規作成として引き続き成功する")]
    public async Task 存在しないファイルへのFULL単独は成功する()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);

        var patchText = "<<<< FILE: new.txt MODE=FULL\nbrand new content\n>>>> END\n";
        var ctx = harness.MakeContext(1);
        var dryRun = await harness.DryRunAsync(patchText, ctx);

        var plan = dryRun.Plans.Single(p => p.Path == "new.txt");
        plan.CanApply.Should().BeTrue("FULLは新規ファイル作成として正規に成功するはず（E210の対象外）");
        plan.Operation.Should().Be(EntryOperation.Create);
    }

    [Fact(DisplayName = "存在しないファイルへのFULL/SR混在は、FULL適用後の内容にSRが解決されE210にならない")]
    public async Task 存在しないファイルへのFULLとSR混在は成功する()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);

        var patchText = """
            <<<< FILE: new.txt MODE=FULL
            line_a
            line_b
            >>>> END

            <<<< FILE: new.txt
            <<<<<<< SEARCH
            line_b
            =======
            line_c
            >>>>>>> REPLACE
            """;
        var ctx = harness.MakeContext(1);
        var dryRun = await harness.DryRunAsync(patchText, ctx);

        dryRun.Plans.Should().NotContain(p => p.Issues.Any(i => i.Code == ErrorCode.E210),
            "FULLが同じファイルにある場合、FULLの内容に対してSRが解決される正規のケースなので" +
            "E210の対象外（実機不具合対応でこの既存機能を壊していないことの確認）");

        var apply = await harness.ApplyAsync(dryRun, ctx);
        apply.IsSuccess.Should().BeTrue();
        var content = System.Text.Encoding.UTF8.GetString(harness.ReadProjectBytes("new.txt"));
        content.Should().Contain("line_a").And.Contain("line_c").And.NotContain("line_b");
    }
}
