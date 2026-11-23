using FluentAssertions;
using Graft.Core.Vba;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// ブックの丸ごとの控え（リボンの書き換えの前。仕様書_ブックの情報と依頼の観点 16.11）の名前（<see cref="VbaWorkbookBackupNaming"/>）と
/// 掃除（<see cref="VbaWorkbookBackupPruner"/>）のテスト。
/// </summary>
public class VbaWorkbookBackupTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 23, 10, 30, 5, TimeSpan.FromHours(9));

    [Fact(DisplayName = "名前: <ブック名>-<日時>-ribbon<元の拡張子>。フォルダは除き、拡張子は元のまま")]
    public void 名前()
    {
        VbaWorkbookBackupNaming.BuildFileName(@"C:\Users\a\計画.xlsm", At).Should().Be("計画-20261123-103005-ribbon.xlsm");
        VbaWorkbookBackupNaming.BuildFileName("アドイン.xlam", At).Should().Be("アドイン-20261123-103005-ribbon.xlam");
    }

    [Fact(DisplayName = "名前: ファイル名に使えない文字は _ に。拡張子が無ければ .xlsm とみなす")]
    public void 使えない文字()
    {
        VbaWorkbookBackupNaming.BuildFileName("a|b?.xlsb", At).Should().Be("a_b_-20261123-103005-ribbon.xlsb");
        VbaWorkbookBackupNaming.BuildFileName("noext", At).Should().Be("noext-20261123-103005-ribbon.xlsm");
    }

    [Fact(DisplayName = "連番と解析の往復（ブック名に - と数字を含んでも切れる）")]
    public void 往復()
    {
        var name = VbaWorkbookBackupNaming.BuildFileName("売上-2026-01.xlsm", At);
        var second = VbaWorkbookBackupNaming.WithSequence(name, 2);

        second.Should().Be("売上-2026-01-20261123-103005-ribbon_2.xlsm");
        VbaWorkbookBackupNaming.Parse(name).Should().Be(new VbaWorkbookBackupFileName("売上-2026-01", new DateTime(2026, 11, 23, 10, 30, 5), 1, ".xlsm"));
        VbaWorkbookBackupNaming.Parse(second)!.Sequence.Should().Be(2);
    }

    [Theory(DisplayName = "解析できないファイル（利用者が置いたもの・形だけ合う日時）は null")]
    [InlineData("計画.xlsm")]
    [InlineData("計画-20261123-103005.xlsm")]
    [InlineData("計画-99999999-999999-ribbon.xlsm")]
    [InlineData("計画-20261123-103005-ribbon_1.xlsm")]
    [InlineData("計画-20261123-103005-ribbon")]
    [InlineData("")]
    public void 解析できない(string fileName)
    {
        VbaWorkbookBackupNaming.Parse(fileName).Should().BeNull();
    }

    private static VbaWorkbookBackupEntry Entry(int minute, long size, int seq = 1)
    {
        var name = VbaWorkbookBackupNaming.BuildFileName("計画.xlsm", new DateTimeOffset(2026, 11, 23, 10, minute, 0, TimeSpan.Zero));
        if (seq > 1) name = VbaWorkbookBackupNaming.WithSequence(name, seq);
        return new VbaWorkbookBackupEntry(name, VbaWorkbookBackupNaming.Parse(name)!, size);
    }

    [Fact(DisplayName = "掃除: 世代数を超えた古い方から消す（古い順に返す）")]
    public void 世代数()
    {
        var entries = Enumerable.Range(0, 7).Select(i => Entry(i, 10)).ToArray();

        var plan = VbaWorkbookBackupPruner.Plan(entries, maxGenerations: 5, maxTotalBytes: 1_000_000);

        plan.Delete.Should().Equal(entries[0], entries[1]);
        plan.StillOverLimit.Should().BeFalse();
        plan.TotalBytesAfter.Should().Be(50);
    }

    [Fact(DisplayName = "掃除: 容量を超えたら古い順に収まるまで消す。同じ秒は連番で並べる")]
    public void 容量()
    {
        var entries = new[] { Entry(1, 100), Entry(1, 100, seq: 2), Entry(2, 100) };

        var plan = VbaWorkbookBackupPruner.Plan(entries, maxGenerations: 5, maxTotalBytes: 150);

        plan.Delete.Should().Equal(entries[0], entries[1]);
        plan.TotalBytesAfter.Should().Be(100);
    }

    [Fact(DisplayName = "掃除: 最新の 1 件は、容量の上限を超えていても消さない（戻すための最後の手段）")]
    public void 最新は残す()
    {
        var entries = new[] { Entry(1, 500), Entry(2, 900) };

        var plan = VbaWorkbookBackupPruner.Plan(entries, maxGenerations: 1, maxTotalBytes: 100);

        plan.Delete.Should().Equal(entries[0]);
        plan.StillOverLimit.Should().BeTrue();
        plan.TotalBytesAfter.Should().Be(900);
    }

    [Fact(DisplayName = "掃除: 世代数 0 以下は 1 に丸める")]
    public void 世代数の丸め()
    {
        var entries = new[] { Entry(1, 1), Entry(2, 1) };

        VbaWorkbookBackupPruner.Plan(entries, maxGenerations: 0, maxTotalBytes: 100).Delete.Should().Equal(entries[0]);
    }
}
