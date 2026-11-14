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
/// RENAME の移動先が既に存在するときの扱い（データを失う不具合の回帰テスト）。
/// 以前は警告なしで移動先を上書きし、元の内容はバックアップにも残らなかった。
/// </summary>
public class RenameDestinationTests
{
    [Fact(DisplayName = "RENAMEの移動先が既に存在するときは、ドライランでE218になり適用できない")]
    public async Task 移動先が存在するとドライランで止まる()
    {
        using var ws = new TempWorkspace();
        var h = new ApplyHarness(ws);
        h.WriteProjectText("a.txt", "AAA\n");
        h.WriteProjectText("b.txt", "BBB\n");

        var ctx = h.MakeContext(1);
        var dryRun = await h.DryRunAsync("<<<< RENAME: a.txt -> b.txt\n", ctx);

        var plan = dryRun.Plans.Single();
        plan.CanApply.Should().BeFalse();
        plan.Issues.Should().Contain(i => i.Code == ErrorCode.E218 && i.Severity == Severity.Error);

        var apply = await h.ApplyAsync(dryRun, ctx);
        apply.IsSuccess.Should().BeFalse();
        Encoding.UTF8.GetString(h.ReadProjectBytes("a.txt")).Should().Be("AAA\n");
        Encoding.UTF8.GetString(h.ReadProjectBytes("b.txt")).Should().Be("BBB\n", "既存の移動先は上書きされてはならない");
    }

    [Fact(DisplayName = "移動先がフォルダでも上書きせずE218になる")]
    public async Task 移動先がフォルダでも止まる()
    {
        using var ws = new TempWorkspace();
        var h = new ApplyHarness(ws);
        h.WriteProjectText("a.txt", "AAA\n");
        Directory.CreateDirectory(Path.Combine(h.ProjectRoot, "dir.txt"));

        var dryRun = await h.DryRunAsync("<<<< RENAME: a.txt -> dir.txt\n", h.MakeContext(1));

        dryRun.Plans.Single().Issues.Should().Contain(i => i.Code == ErrorCode.E218);
    }

    [Fact(DisplayName = "ドライランの後で移動先ができても、本適用はE218で止まり移動先は無傷")]
    public async Task ドライラン後に移動先ができても本適用で止まる()
    {
        using var ws = new TempWorkspace();
        var h = new ApplyHarness(ws);
        h.WriteProjectText("a.txt", "AAA\n");
        var ctx = h.MakeContext(1);
        var dryRun = await h.DryRunAsync("<<<< RENAME: a.txt -> b.txt\n", ctx);
        dryRun.Plans.Single().CanApply.Should().BeTrue();

        h.WriteProjectText("b.txt", "後から作られた\n");
        var apply = await h.ApplyAsync(dryRun, ctx);

        apply.IsSuccess.Should().BeFalse();
        apply.Errors.Should().Contain(i => i.Code == ErrorCode.E218);
        Encoding.UTF8.GetString(h.ReadProjectBytes("b.txt")).Should().Be("後から作られた\n");
        h.ProjectFileExists("a.txt").Should().BeTrue();
    }

    [Fact(DisplayName = "移動先が空いていれば従来どおり改名できる")]
    public async Task 移動先が空いていれば改名できる()
    {
        using var ws = new TempWorkspace();
        var h = new ApplyHarness(ws);
        h.WriteProjectText("a.txt", "AAA\n");
        var ctx = h.MakeContext(1);
        var dryRun = await h.DryRunAsync("<<<< RENAME: a.txt -> b.txt\n", ctx);

        var apply = await h.ApplyAsync(dryRun, ctx);

        apply.IsSuccess.Should().BeTrue(string.Join(", ", apply.Issues.Select(i => i.ToDisplayText())));
        h.ProjectFileExists("a.txt").Should().BeFalse();
        Encoding.UTF8.GetString(h.ReadProjectBytes("b.txt")).Should().Be("AAA\n");
    }

    [Fact(DisplayName = "移動先が同じパッチ内で別の場所へ改名される移動元なら、ドライランでは止めない")]
    public async Task 移動先が先に空く連鎖改名はドライランで止めない()
    {
        using var ws = new TempWorkspace();
        var h = new ApplyHarness(ws);
        h.WriteProjectText("a.txt", "AAA\n");
        h.WriteProjectText("b.txt", "BBB\n");

        // b.txt -> c.txt を先に書けば、a.txt -> b.txt の時点では b.txt は空いている。
        var patch = "<<<< RENAME: b.txt -> c.txt\n\n<<<< RENAME: a.txt -> b.txt\n";
        var ctx = h.MakeContext(1);
        var dryRun = await h.DryRunAsync(patch, ctx);
        dryRun.Plans.Should().OnlyContain(p => p.CanApply);

        var apply = await h.ApplyAsync(dryRun, ctx);
        apply.IsSuccess.Should().BeTrue(string.Join(", ", apply.Issues.Select(i => i.ToDisplayText())));
        Encoding.UTF8.GetString(h.ReadProjectBytes("b.txt")).Should().Be("AAA\n");
        Encoding.UTF8.GetString(h.ReadProjectBytes("c.txt")).Should().Be("BBB\n");
    }

    [Fact(DisplayName = "大文字小文字だけが違う改名は、同じファイルの名前の付け替えなので占有扱いにしない")]
    public void 大文字小文字だけの違いは同一ファイルとして許す()
    {
        using var ws = new TempWorkspace();
        var dir = ws.CreateDirectory("d");
        var lower = Path.Combine(dir, "a.txt");
        File.WriteAllText(lower, "x");
        var upper = Path.Combine(dir, "A.txt");

        // 大文字小文字を区別しない環境では upper も存在するように見えるが、一覧に載る綴りは1つだけ。
        // 区別する環境（Linux）では upper は存在しない。どちらでも「占有されていない」。
        RenameTarget.IsOccupied(lower, upper).Should().BeFalse();
    }

    [Fact(DisplayName = "綴りだけが違う別々のファイルが両方ある環境では、上書きになるので占有扱いにする")]
    public void 綴りだけ違う別ファイルが両方あれば占有扱い()
    {
        using var ws = new TempWorkspace();
        var dir = ws.CreateDirectory("d");
        var lower = Path.Combine(dir, "a.txt");
        File.WriteAllText(lower, "x");
        var upper = Path.Combine(dir, "A.txt");
        File.WriteAllText(upper, "y");
        // 区別しない環境では2つ目の書き込みが1つ目を上書きして1ファイルになる。その場合は同一ファイル。
        var distinct = Directory.EnumerateFileSystemEntries(dir).Count() == 2;

        RenameTarget.IsOccupied(lower, upper).Should().Be(distinct);
    }
}
