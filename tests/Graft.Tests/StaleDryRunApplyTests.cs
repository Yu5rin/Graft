using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Core;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// ドライランの後にファイルが変わった／チェックの組み合わせが成り立たないのに、本適用が何も確かめず
/// 「成功」として記録していた不具合の回帰テスト（ApplyEngine.Verification.cs参照）。
/// </summary>
public class StaleDryRunApplyTests
{
    private static DryRunResult Deselect(DryRunResult dryRun, Func<BlockPlan, bool> where)
        => dryRun with { Plans = dryRun.Plans.Select(p => where(p) ? p with { IsSelected = false } : p).ToList() };

    private static string Text(ApplyHarness h, string path) => Encoding.UTF8.GetString(h.ReadProjectBytes(path));

    [Fact(DisplayName = "再現1: ドライランの後に対象の行が外部で書き換わっていたら、成功にせずE301で中止する")]
    public async Task ドライラン後に対象行が書き換わると中止する()
    {
        using var ws = new TempWorkspace();
        var h = new ApplyHarness(ws);
        h.WriteProjectText("a.txt", "one\ntwo\nthree\n");
        var ctx = h.MakeContext(1);
        var dryRun = await h.DryRunAsync(
            "<<<< FILE: a.txt\n<<<<<<< SEARCH\ntwo\n=======\nTWO\n>>>>>>> REPLACE\n", ctx);
        dryRun.Plans.Single().CanApply.Should().BeTrue();

        h.WriteProjectText("a.txt", "one\nchanged outside\nthree\n");
        var apply = await h.ApplyAsync(dryRun, ctx);

        apply.IsSuccess.Should().BeFalse("書き換わった行にSEARCHが当たらないのに成功にしてはならない");
        apply.Errors.Should().Contain(i => i.Code == ErrorCode.E301);
        apply.Errors.First().ToDisplayText().Should().Contain("もう一度解析");
        Text(h, "a.txt").Should().Be("one\nchanged outside\nthree\n", "外部での変更はそのまま残る");
        var read = await h.Revisions.ReadAsync(h.ProjectId, 1);
        read.Value.Manifest.Status.Should().NotBe(RevisionStatus.Success, "成功のリビジョンを記録してはならない");
    }

    [Fact(DisplayName = "ドライランの後に同じSEARCHが別の行に当たるようになったら、位置が違うので中止する")]
    public async Task 一致位置が変わったら中止する()
    {
        using var ws = new TempWorkspace();
        var h = new ApplyHarness(ws);
        h.WriteProjectText("a.txt", "one\ntwo\nthree\n");
        var ctx = h.MakeContext(1);
        var dryRun = await h.DryRunAsync(
            "<<<< FILE: a.txt\n<<<<<<< SEARCH\ntwo\n=======\nTWO\n>>>>>>> REPLACE\n", ctx);

        h.WriteProjectText("a.txt", "zero\nzero\none\ntwo\nthree\n");
        var apply = await h.ApplyAsync(dryRun, ctx);

        apply.IsSuccess.Should().BeFalse();
        apply.Errors.Should().Contain(i => i.Code == ErrorCode.E301 && i.ToDisplayText().Contains("行目"));
        Text(h, "a.txt").Should().Be("zero\nzero\none\ntwo\nthree\n");
    }

    [Fact(DisplayName = "ドライランの後に変更がなければ、従来どおり適用できる（位置の照合で誤検知しない）")]
    public async Task 変更がなければ従来どおり適用できる()
    {
        using var ws = new TempWorkspace();
        var h = new ApplyHarness(ws);
        h.WriteProjectText("a.txt", "one\ntwo\nthree\nfour\n");
        var ctx = h.MakeContext(1);
        var dryRun = await h.DryRunAsync(
            "<<<< FILE: a.txt\n<<<<<<< SEARCH\ntwo\n=======\nTWO\n>>>>>>> REPLACE\n<<<<<<< SEARCH\nfour\n=======\nFOUR\n>>>>>>> REPLACE\n", ctx);

        var apply = await h.ApplyAsync(dryRun, ctx);

        apply.IsSuccess.Should().BeTrue(string.Join(", ", apply.Issues.Select(i => i.ToDisplayText())));
        Text(h, "a.txt").Should().Be("one\nTWO\nthree\nFOUR\n");
    }

    [Fact(DisplayName = "再現2: RENAMEのチェックを外して移動先へのSRだけを適用しようとしたら、空のファイルを作らず中止する")]
    public async Task RENAMEを外して移動先のSRだけでは適用できない()
    {
        using var ws = new TempWorkspace();
        var h = new ApplyHarness(ws);
        h.WriteProjectText("a.txt", "AAA\n");
        var ctx = h.MakeContext(1);
        var dryRun = await h.DryRunAsync(
            "<<<< RENAME: a.txt -> b.txt\n\n<<<< FILE: b.txt\n<<<<<<< SEARCH\nAAA\n=======\nBBB\n>>>>>>> REPLACE\n", ctx);
        var partial = Deselect(dryRun, p => p.Block is RenameBlock);

        var apply = await h.ApplyAsync(partial, ctx);

        apply.IsSuccess.Should().BeFalse();
        apply.Errors.Should().Contain(i => i.Code == ErrorCode.E301 && i.ToDisplayText().Contains("RENAME"));
        h.ProjectFileExists("b.txt").Should().BeFalse("空のb.txtを新規作成してはならない");
        Text(h, "a.txt").Should().Be("AAA\n");
    }

    [Fact(DisplayName = "再現3: FULLとSRが混在するファイルでFULLのチェックを外したら、何も変えずに成功にせず中止する")]
    public async Task FULLを外してSRだけでは適用できない()
    {
        using var ws = new TempWorkspace();
        var h = new ApplyHarness(ws);
        h.WriteProjectText("mix.txt", "old\n");
        var ctx = h.MakeContext(1);
        var dryRun = await h.DryRunAsync(
            "<<<< FILE: mix.txt MODE=FULL\nline_a\nline_b\n>>>> END\n\n" +
            "<<<< FILE: mix.txt\n<<<<<<< SEARCH\nline_b\n=======\nline_c\n>>>>>>> REPLACE\n", ctx);
        var partial = Deselect(dryRun, p => p.Block is FullContentBlock);

        var apply = await h.ApplyAsync(partial, ctx);

        apply.IsSuccess.Should().BeFalse();
        apply.Errors.Should().Contain(i => i.Code == ErrorCode.E301 && i.ToDisplayText().Contains("MODE=FULL"));
        Text(h, "mix.txt").Should().Be("old\n");
    }

    [Fact(DisplayName = "新規作成のつもりのファイルが、ドライランの後に外部で作られていたら、上書きせず中止する")]
    public async Task 新規作成の対象が後から作られたら中止する()
    {
        using var ws = new TempWorkspace();
        var h = new ApplyHarness(ws);
        var ctx = h.MakeContext(1);
        var dryRun = await h.DryRunAsync("<<<< FILE: new.txt MODE=FULL\nfrom patch\n>>>> END\n", ctx);
        dryRun.Plans.Single().Operation.Should().Be(EntryOperation.Create);

        h.WriteProjectText("new.txt", "someone else\n");
        var apply = await h.ApplyAsync(dryRun, ctx);

        apply.IsSuccess.Should().BeFalse();
        apply.Errors.Should().Contain(i => i.Code == ErrorCode.E301);
        Text(h, "new.txt").Should().Be("someone else\n", "外部で作られた内容を黙って上書きしてはならない");
    }

    [Fact(DisplayName = "食い違いで中止したとき、同じ適用で先に書いた別ファイルもロールバックされる")]
    public async Task 食い違いで中止すると先に書いたファイルも戻る()
    {
        using var ws = new TempWorkspace();
        var h = new ApplyHarness(ws);
        h.WriteProjectText("a.txt", "one\n");
        h.WriteProjectText("b.txt", "two\n");
        var ctx = h.MakeContext(1);
        var dryRun = await h.DryRunAsync(
            "<<<< FILE: a.txt\n<<<<<<< SEARCH\none\n=======\nONE\n>>>>>>> REPLACE\n\n" +
            "<<<< FILE: b.txt\n<<<<<<< SEARCH\ntwo\n=======\nTWO\n>>>>>>> REPLACE\n", ctx);

        h.WriteProjectText("b.txt", "changed outside\n");
        var apply = await h.ApplyAsync(dryRun, ctx);

        apply.IsSuccess.Should().BeFalse();
        Text(h, "a.txt").Should().Be("one\n", "先に書いたa.txtは元に戻る");
        Text(h, "b.txt").Should().Be("changed outside\n");
    }

    [Fact(DisplayName = "RENAMEと、その移動先への変更の両方のチェックを外せば、残りの変更だけを適用できる")]
    public async Task 依存する変更をまとめて外せば適用できる()
    {
        using var ws = new TempWorkspace();
        var h = new ApplyHarness(ws);
        h.WriteProjectText("a.txt", "AAA\n");
        h.WriteProjectText("c.txt", "CCC\n");
        var ctx = h.MakeContext(1);
        var dryRun = await h.DryRunAsync(
            "<<<< RENAME: a.txt -> b.txt\n\n<<<< FILE: b.txt\n<<<<<<< SEARCH\nAAA\n=======\nBBB\n>>>>>>> REPLACE\n\n" +
            "<<<< FILE: c.txt\n<<<<<<< SEARCH\nCCC\n=======\nDDD\n>>>>>>> REPLACE\n", ctx);
        var partial = Deselect(dryRun, p => p.Path == "b.txt");

        ApplyEngine.CheckSelection(partial.Plans).Should().BeEmpty();
        var apply = await h.ApplyAsync(partial, ctx);

        apply.IsSuccess.Should().BeTrue(string.Join(", ", apply.Issues.Select(i => i.ToDisplayText())));
        Text(h, "a.txt").Should().Be("AAA\n");
        Text(h, "c.txt").Should().Be("DDD\n");
        h.ProjectFileExists("b.txt").Should().BeFalse();
    }

    [Fact(DisplayName = "全部にチェックが付いている通常の状態では、CheckSelectionは何も言わない")]
    public async Task 通常の選択では問題なし()
    {
        using var ws = new TempWorkspace();
        var h = new ApplyHarness(ws);
        h.WriteProjectText("a.txt", "AAA\n");
        var dryRun = await h.DryRunAsync(
            "<<<< RENAME: a.txt -> b.txt\n\n<<<< FILE: b.txt\n<<<<<<< SEARCH\nAAA\n=======\nBBB\n>>>>>>> REPLACE\n", h.MakeContext(1));

        ApplyEngine.CheckSelection(dryRun.Plans).Should().BeEmpty();
    }
}
