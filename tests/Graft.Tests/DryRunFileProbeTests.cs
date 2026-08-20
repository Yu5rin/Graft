using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Core;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 依頼4対応の回帰テスト。ドライラン中に確認した対象ファイルごとの「解決した絶対パス」
/// 「存在するか」「読み取れたサイズ/行数」が<see cref="DryRunResult.FileProbes"/>へ
/// 正しく記録されることを確認する。実際にログへ書き出す側（MainViewModel.
/// DryRunDiagnostics.cs）はAvalonia非依存ではないためGraft.Tests側では検証できず
/// （tests/Graft.UiTestsの担当）、ここではCore層が返すデータの正しさのみを確認する。
/// </summary>
public class DryRunFileProbeTests
{
    private static string BuildSrPatch(string path, string search, string replace) =>
        $"<<<< FILE: {path}\n<<<<<<< SEARCH\n{search}\n=======\n{replace}\n>>>>>>> REPLACE\n";

    [Fact(DisplayName = "存在するファイルへのSRは絶対パス・存在あり・行数が記録される")]
    public async Task 存在するファイルは絶対パスと行数が記録される()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        harness.WriteProjectText("a.txt", "line1\nline2\n");

        var patchText = BuildSrPatch("a.txt", "line1", "line1changed");
        var ctx = harness.MakeContext(1);
        var dryRun = await harness.DryRunAsync(patchText, ctx);

        var probe = dryRun.FileProbes.Single(p => p.Path == "a.txt");
        probe.Exists.Should().BeTrue();
        probe.FullPath.Should().Be(Path.Combine(harness.ProjectRoot, "a.txt"));
        probe.LineCount.Should().Be(2, "line1\\nline2\\n はTextNormalizer.SplitLines後2行になるはず");
    }

    [Fact(DisplayName = "存在しないファイルへのSRは絶対パス・存在なしが記録される（行数はnull）")]
    public async Task 存在しないファイルは存在なしとして記録される()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);

        var patchText = BuildSrPatch("missing.txt", "hello", "world");
        var ctx = harness.MakeContext(1);
        var dryRun = await harness.DryRunAsync(patchText, ctx);

        var probe = dryRun.FileProbes.Single(p => p.Path == "missing.txt");
        probe.Exists.Should().BeFalse();
        probe.FullPath.Should().Be(Path.Combine(harness.ProjectRoot, "missing.txt"));
        probe.LineCount.Should().BeNull();
        probe.SizeBytes.Should().BeNull();
    }

    [Fact(DisplayName = "DELETEブロックの対象ファイルも記録される")]
    public async Task DELETE対象も記録される()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        harness.WriteProjectText("gone.txt", "bye\n");

        var patchText = "<<<< DELETE: gone.txt\n";
        var ctx = harness.MakeContext(1);
        var dryRun = await harness.DryRunAsync(patchText, ctx);

        var probe = dryRun.FileProbes.Single(p => p.Path == "gone.txt");
        probe.Exists.Should().BeTrue();
        probe.SizeBytes.Should().Be(4); // "bye\n" は4バイト
    }

    [Fact(DisplayName = "RENAMEブロックの移動元も記録される")]
    public async Task RENAME移動元も記録される()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        harness.WriteProjectText("old.txt", "content\n");

        var patchText = "<<<< RENAME: old.txt -> renamed.txt\n";
        var ctx = harness.MakeContext(1);
        var dryRun = await harness.DryRunAsync(patchText, ctx);

        var probe = dryRun.FileProbes.Single(p => p.Path == "old.txt");
        probe.Exists.Should().BeTrue();
        probe.FullPath.Should().Be(Path.Combine(harness.ProjectRoot, "old.txt"));
    }
}
