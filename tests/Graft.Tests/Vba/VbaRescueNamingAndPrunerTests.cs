using FluentAssertions;
using Graft.Core.Vba;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// 控えのファイル名の解析（<see cref="VbaRescueNaming.Parse"/>）と、世代数・容量の掃除の計画（<see cref="VbaRescuePruner"/>）。
/// 仕様書_ブックの情報と依頼の観点 4.4・7.3・11.1。ファイルには触れない純ロジックのテスト。
/// </summary>
public class VbaRescueNamingAndPrunerTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 21, 10, 30, 0, TimeSpan.FromHours(9));

    // ------------------------------------------------------------------
    // 名前
    // ------------------------------------------------------------------

    [Theory(DisplayName = "Parse: 3 種別（接尾辞なし＝Legacy／-auto／-mirror）を日時つきで読む")]
    [InlineData("Mod-20261121-103000.txt", VbaRescueKind.Legacy)]
    [InlineData("Mod-20261121-103000-auto.txt", VbaRescueKind.Auto)]
    [InlineData("Mod-20261121-103000-mirror.txt", VbaRescueKind.Mirror)]
    public void 三種別(string fileName, VbaRescueKind kind)
    {
        var parsed = VbaRescueNaming.Parse(fileName);

        parsed.Should().NotBeNull();
        parsed!.Module.Should().Be("Mod");
        parsed.Kind.Should().Be(kind);
        parsed.At.Should().Be(new DateTime(2026, 11, 21, 10, 30, 0));
        parsed.Sequence.Should().Be(1);
    }

    [Theory(DisplayName = "Parse: 同じ秒の連番（_2 以降）を読む。種別の接尾辞の後ろに付く")]
    [InlineData("Mod-20261121-103000_2.txt", VbaRescueKind.Legacy, 2)]
    [InlineData("Mod-20261121-103000-auto_3.txt", VbaRescueKind.Auto, 3)]
    [InlineData("Mod-20261121-103000-mirror_12.txt", VbaRescueKind.Mirror, 12)]
    public void 連番(string fileName, VbaRescueKind kind, int sequence)
    {
        var parsed = VbaRescueNaming.Parse(fileName)!;

        parsed.Kind.Should().Be(kind);
        parsed.Sequence.Should().Be(sequence);
    }

    [Theory(DisplayName = "Parse: モジュール名に _ や数字を含んでいても、名前と日時の区切りで一意に切れる")]
    [InlineData("My_Module_2-20261121-103000-auto.txt", "My_Module_2")]
    [InlineData("Sheet1-20261121-103000.txt", "Sheet1")]
    [InlineData("日本語モジュール-20261121-103000-mirror_2.txt", "日本語モジュール")]
    public void モジュール名(string fileName, string module)
        => VbaRescueNaming.Parse(fileName)!.Module.Should().Be(module);

    [Theory(DisplayName = "Parse: 解析できない名前は null（利用者が置いたファイルを一覧に出さず、消さない）")]
    [InlineData("memo.txt")]
    [InlineData("Mod-2026112-103000.txt")]
    [InlineData("Mod-20261121-103000-other.txt")]
    [InlineData("Mod-20261121-103000.bak")]
    [InlineData("Mod-99999999-999999.txt")]
    [InlineData("-20261121-103000.txt")]
    [InlineData("")]
    [InlineData(null)]
    public void 解析できない名前(string? fileName)
        => VbaRescueNaming.Parse(fileName).Should().BeNull();

    [Theory(DisplayName = "BuildFileName と Parse は往復する（種別・連番つき）")]
    [InlineData(VbaRescueKind.Legacy, "Mod-20261121-103000.txt")]
    [InlineData(VbaRescueKind.Auto, "Mod-20261121-103000-auto.txt")]
    [InlineData(VbaRescueKind.Mirror, "Mod-20261121-103000-mirror.txt")]
    public void 往復(VbaRescueKind kind, string expected)
    {
        var name = VbaRescueNaming.BuildFileName("Mod", At, kind);

        name.Should().Be(expected);
        VbaRescueNaming.Parse(name)!.Kind.Should().Be(kind);
        var sequenced = VbaRescueNaming.WithSequence(name, 2);
        VbaRescueNaming.Parse(sequenced)!.Should().Match<VbaRescueFileName>(p => p.Kind == kind && p.Sequence == 2 && p.Module == "Mod");
    }

    [Fact(DisplayName = "BuildFileName: 種別を省略すれば従来と同じ名前（Legacy）")]
    public void 従来の名前()
        => VbaRescueNaming.BuildFileName("Module1", At).Should().Be("Module1-20261121-103000.txt");

    // ------------------------------------------------------------------
    // 掃除
    // ------------------------------------------------------------------

    private static VbaRescueEntry Entry(string module, int minute, VbaRescueKind kind, long size = 100, int seq = 1)
    {
        var name = VbaRescueNaming.BuildFileName(module, new DateTimeOffset(2026, 11, 21, 10, minute, 0, TimeSpan.Zero), kind);
        if (seq > 1) name = VbaRescueNaming.WithSequence(name, seq);
        return new VbaRescueEntry(name, VbaRescueNaming.Parse(name)!, size);
    }

    [Fact(DisplayName = "Pruner: 1 モジュールの Auto＋Mirror が世代数を超えたら、古い方から消す。他のモジュールには数えない")]
    public void 世代数の超過()
    {
        var entries = new[]
        {
            Entry("A", 1, VbaRescueKind.Auto), Entry("A", 2, VbaRescueKind.Mirror), Entry("A", 3, VbaRescueKind.Auto), Entry("A", 4, VbaRescueKind.Auto),
            Entry("B", 1, VbaRescueKind.Auto), Entry("B", 2, VbaRescueKind.Auto),
        };

        var plan = VbaRescuePruner.Plan(entries, maxGenerations: 2, maxTotalBytes: long.MaxValue);

        plan.Delete.Select(e => e.FileName).Should().Equal(entries[0].FileName, entries[1].FileName);
        plan.StillOverLimit.Should().BeFalse();
    }

    [Fact(DisplayName = "Pruner: 世代数は Legacy を数えず、Legacy は消さない")]
    public void 世代数にLegacyは入らない()
    {
        var entries = new[]
        {
            Entry("A", 1, VbaRescueKind.Legacy), Entry("A", 2, VbaRescueKind.Legacy), Entry("A", 3, VbaRescueKind.Legacy),
            Entry("A", 4, VbaRescueKind.Auto), Entry("A", 5, VbaRescueKind.Auto),
        };

        var plan = VbaRescuePruner.Plan(entries, maxGenerations: 1, maxTotalBytes: long.MaxValue);

        plan.Delete.Should().ContainSingle().Which.FileName.Should().Be(entries[3].FileName);
    }

    [Fact(DisplayName = "Pruner: 容量が上限を超えたら、Auto と Mirror を全モジュールの古い順に、収まるまで消す")]
    public void 容量の超過()
    {
        var entries = new[]
        {
            Entry("A", 1, VbaRescueKind.Auto, 100), Entry("B", 2, VbaRescueKind.Mirror, 100), Entry("A", 3, VbaRescueKind.Auto, 100),
            Entry("B", 4, VbaRescueKind.Auto, 100), Entry("A", 5, VbaRescueKind.Auto, 100),
        };

        var plan = VbaRescuePruner.Plan(entries, maxGenerations: 20, maxTotalBytes: 300);

        plan.Delete.Select(e => e.FileName).Should().Equal(entries[0].FileName, entries[1].FileName);
        plan.TotalBytesAfter.Should().Be(300);
        plan.StillOverLimit.Should().BeFalse();
    }

    [Fact(DisplayName = "Pruner: Legacy は容量の合計には数えるが、容量を理由にも消さない")]
    public void Legacyは数えるが消さない()
    {
        var entries = new[]
        {
            Entry("A", 1, VbaRescueKind.Legacy, 200), Entry("A", 2, VbaRescueKind.Auto, 100), Entry("B", 3, VbaRescueKind.Auto, 100), Entry("A", 4, VbaRescueKind.Auto, 100),
        };

        var plan = VbaRescuePruner.Plan(entries, maxGenerations: 20, maxTotalBytes: 400);

        plan.Delete.Should().ContainSingle().Which.FileName.Should().Be(entries[1].FileName, "Legacy は残し、Auto の古い順に 1 件で収まる");
    }

    [Fact(DisplayName = "Pruner: 各モジュールの最新の Auto は、容量が小さすぎても消さない（超過のまま）")]
    public void 最新のAutoは残す()
    {
        var entries = new[]
        {
            Entry("A", 1, VbaRescueKind.Auto, 100), Entry("A", 2, VbaRescueKind.Auto, 100), Entry("B", 3, VbaRescueKind.Auto, 100),
        };

        var plan = VbaRescuePruner.Plan(entries, maxGenerations: 20, maxTotalBytes: 10);

        plan.Delete.Select(e => e.FileName).Should().Equal(entries[0].FileName);
        plan.StillOverLimit.Should().BeTrue("最新の 2 件（A の 2 分、B の 3 分）は残るので、まだ超えている");
        plan.TotalBytesAfter.Should().Be(200);
    }

    [Fact(DisplayName = "Pruner: 最新の Auto より新しい Mirror があっても、Mirror のほうが先に（古い順で）消える対象になる。最新の Auto だけが守られる")]
    public void 最新のAutoだけが守られる()
    {
        var entries = new[]
        {
            Entry("A", 1, VbaRescueKind.Auto, 100), Entry("A", 2, VbaRescueKind.Mirror, 100), Entry("A", 3, VbaRescueKind.Auto, 100),
        };

        var plan = VbaRescuePruner.Plan(entries, maxGenerations: 20, maxTotalBytes: 100);

        plan.Delete.Select(e => e.FileName).Should().Equal(entries[0].FileName, entries[1].FileName);
        plan.StillOverLimit.Should().BeFalse();
    }

    [Fact(DisplayName = "Pruner: Legacy だけで上限を超えているときは、何も消さず、超過を知らせる")]
    public void Legacyだけで超過()
    {
        var entries = new[] { Entry("A", 1, VbaRescueKind.Legacy, 600), Entry("B", 2, VbaRescueKind.Legacy, 600) };

        var plan = VbaRescuePruner.Plan(entries, maxGenerations: 1, maxTotalBytes: 1000);

        plan.Delete.Should().BeEmpty();
        plan.StillOverLimit.Should().BeTrue();
        plan.TotalBytesAfter.Should().Be(1200);
    }

    [Fact(DisplayName = "Pruner: 同じ時刻のものも安定した順序（連番、ファイル名）で、何度計算しても同じものを消す")]
    public void 同じ時刻の安定した順序()
    {
        var entries = new[]
        {
            Entry("A", 1, VbaRescueKind.Auto, 100, seq: 3), Entry("A", 1, VbaRescueKind.Auto, 100, seq: 1), Entry("A", 1, VbaRescueKind.Auto, 100, seq: 2),
            Entry("A", 1, VbaRescueKind.Mirror, 100),
        };

        var first = VbaRescuePruner.Plan(entries, maxGenerations: 2, maxTotalBytes: long.MaxValue);
        // 配列に .Reverse() と書くと、C# 14（.NET 10 SDK）の「Span への暗黙変換」により
        // MemoryExtensions.Reverse(Span<T>)（戻り値 void・その場で並べ替え）が選ばれ、
        // CI のランナー（.NET 10 SDK が既定）でだけ CS0023 でコンパイルできなかった（手元の 8.0 SDK では通る）。
        // LINQ の Enumerable.Reverse を明示して、どちらの SDK でも「元の配列を変えずに逆順の列を作る」意味にそろえる。
        var second = VbaRescuePruner.Plan(Enumerable.Reverse(entries).ToArray(), maxGenerations: 2, maxTotalBytes: long.MaxValue);

        first.Delete.Select(e => e.FileName).Should().Equal(second.Delete.Select(e => e.FileName));
        first.Delete.Should().HaveCount(2);
        first.Delete.Select(e => e.FileName).Should().NotContain(entries[0].FileName, "連番の大きい方が新しい");
    }

    [Fact(DisplayName = "Pruner: 何も超えていなければ何も消さない。空の入力でも落ちない")]
    public void 超えていなければ消さない()
    {
        var entries = new[] { Entry("A", 1, VbaRescueKind.Auto), Entry("A", 2, VbaRescueKind.Mirror) };

        VbaRescuePruner.Plan(entries, 20, 1_000_000).Delete.Should().BeEmpty();
        VbaRescuePruner.Plan(Array.Empty<VbaRescueEntry>(), 20, 1).Should().Match<VbaRescuePrunePlan>(p => p.Delete.Count == 0 && !p.StillOverLimit);
    }

    [Fact(DisplayName = "不具合 7: 世代数 1 でも、Mirror が新しければ最新の Auto が押し出されて消える、ということは起きない（各モジュールの最新の Auto は残る）")]
    public void 不具合7_世代数1でも最新のAutoは残る()
    {
        var entries = new[] { Entry("A", 0, VbaRescueKind.Auto), Entry("A", 5, VbaRescueKind.Mirror) };

        var plan = VbaRescuePruner.Plan(entries, maxGenerations: 1, maxTotalBytes: long.MaxValue);

        plan.Delete.Should().BeEmpty("最新の Auto は、世代数を理由にも消さない（戻す直後に戻し元が消える）");
    }

    [Fact(DisplayName = "不具合 7: 古い Auto は消え、最新の Auto は残る。Mirror が世代数ぶん新しく積まれても同じ")]
    public void 不具合7_古いAutoだけが消える()
    {
        var entries = new List<VbaRescueEntry>
        {
            Entry("A", 0, VbaRescueKind.Auto), Entry("A", 1, VbaRescueKind.Auto),
        };
        for (var i = 0; i < 20; i++) entries.Add(Entry("A", 10 + i, VbaRescueKind.Mirror));

        var plan = VbaRescuePruner.Plan(entries, maxGenerations: 20, maxTotalBytes: long.MaxValue);

        plan.Delete.Select(e => e.FileName).Should().Equal(
            new[] { entries[0].FileName }, "最新の Auto（1 分）は残し、古い Auto（0 分）だけを消す");
        plan.Delete.Should().NotContain(e => e.FileName == entries[1].FileName);
    }
}
