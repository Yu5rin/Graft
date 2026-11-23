using FluentAssertions;
using Graft.Core.Vba;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// <see cref="VbaRibbonSyncPlanner"/>（ミラーのリボンとブックのファイルの三方比較）のテスト。仕様書_ブックの情報と依頼の観点 16.4 の判定表を
/// 1 行ずつ確かめ、読めないものを「無い」と扱わないこと、要確認（needsReview）、部品名の直しを固定する。
/// </summary>
public class VbaRibbonSyncPlannerTests
{
    private const string A = "<customUI xmlns=\"http://schemas.microsoft.com/office/2009/07/customui\"><ribbon/></customUI>";
    private const string B = "<customUI xmlns=\"http://schemas.microsoft.com/office/2009/07/customui\"><ribbon startFromScratch=\"false\"/></customUI>";
    private const string C = "<customUI xmlns=\"http://schemas.microsoft.com/office/2009/07/customui\"/>";
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 10, 0, 0, TimeSpan.FromHours(9));

    private static string H(string text) => VbaTextNormalizer.Hash(text);

    private static VbaRibbonReadResult Workbook(string? xml14 = null, string partName = "customUI/customUI14.xml", string? problem = null)
    {
        if (xml14 is null && problem is null)
            return new VbaRibbonReadResult(VbaRibbonReadStatus.NoRibbon, null, Array.Empty<VbaRibbonRawPart>());
        var part = new VbaRibbonRawPart(partName, true, problem is null ? xml14 : null, problem);
        return new VbaRibbonReadResult(problem is null ? VbaRibbonReadStatus.Ok : VbaRibbonReadStatus.PartsUnreadable, null, new[] { part });
    }

    private static Dictionary<VbaRibbonFormat, VbaRibbonMirrorFile> Mirror(string? xml14 = null, string? problem = null)
    {
        var map = new Dictionary<VbaRibbonFormat, VbaRibbonMirrorFile>();
        if (xml14 is not null || problem is not null) map[VbaRibbonFormat.Office2010] = new VbaRibbonMirrorFile(problem is null ? xml14 : null, problem);
        return map;
    }

    private static Dictionary<VbaRibbonFormat, VbaRibbonSyncRecord> Record(string mirror, string file, bool deletePending = false, bool needsReview = false, string partName = "customUI/customUI14.xml")
        => new() { [VbaRibbonFormat.Office2010] = new VbaRibbonSyncRecord(partName, H(mirror), H(file), deletePending, needsReview, Now.AddDays(-1)) };

    private static VbaRibbonSyncItem One(VbaRibbonSyncPlan plan) => plan.Items.Should().ContainSingle().Subject;

    [Fact(DisplayName = "M = Bm・F = Bf: 何もしない")]
    public void 変化なし()
    {
        var item = One(VbaRibbonSyncPlanner.Plan(Workbook(A), Mirror(A), Record(A, A), Now));

        item.Action.Should().Be(VbaRibbonSyncAction.None);
        item.Baseline.Should().BeNull();
    }

    [Fact(DisplayName = "ミラーだけ変わった: 反映（リボン未反映）")]
    public void ミラーだけ()
    {
        var plan = VbaRibbonSyncPlanner.Plan(Workbook(A), Mirror(B), Record(A, A), Now);

        One(plan).Action.Should().Be(VbaRibbonSyncAction.Push);
        One(plan).IsNewPart.Should().BeFalse();
        plan.HasPush.Should().BeTrue();
    }

    [Fact(DisplayName = "ファイルだけ変わった: 取込")]
    public void ファイルだけ()
    {
        One(VbaRibbonSyncPlanner.Plan(Workbook(B), Mirror(A), Record(A, A), Now)).Action.Should().Be(VbaRibbonSyncAction.Pull);
    }

    [Fact(DisplayName = "両方変わって本文が同じ: 基準だけ更新")]
    public void 両方同じ()
    {
        var item = One(VbaRibbonSyncPlanner.Plan(Workbook(B), Mirror(B), Record(A, A), Now));

        item.Action.Should().Be(VbaRibbonSyncAction.Rebaseline);
        item.Baseline.Should().Be(new VbaRibbonSyncRecord("customUI/customUI14.xml", H(B), H(B), SyncedAt: Now));
    }

    [Fact(DisplayName = "両方変わって本文が違う: 衝突")]
    public void 衝突()
    {
        var plan = VbaRibbonSyncPlanner.Plan(Workbook(B), Mirror(C), Record(A, A), Now);

        One(plan).Action.Should().Be(VbaRibbonSyncAction.Conflict);
        One(plan).ConflictReason.Should().Be(VbaConflictReason.BothChanged);
        plan.HasConflicts.Should().BeTrue();
    }

    [Fact(DisplayName = "記録なし・ミラーにだけある: 反映（customUI の無いブックへの新規の追加。部品名は既定）")]
    public void 新規の追加()
    {
        var item = One(VbaRibbonSyncPlanner.Plan(Workbook(), Mirror(A), null, Now));

        item.Action.Should().Be(VbaRibbonSyncAction.Push);
        item.IsNewPart.Should().BeTrue();
        item.PartName.Should().Be("customUI/customUI14.xml");
        item.MirrorPath.Should().Be("CustomUI/customUI14.xml");
    }

    [Fact(DisplayName = "記録なし・ブックにだけある: 取込（既存のプロジェクトの最初の取込）")]
    public void 最初の取込()
    {
        One(VbaRibbonSyncPlanner.Plan(Workbook(A), Mirror(), null, Now)).Action.Should().Be(VbaRibbonSyncAction.Pull);
    }

    [Fact(DisplayName = "記録なし・両方ある: 同じなら基準を作るだけ、違えば衝突")]
    public void 記録なし両方()
    {
        var same = One(VbaRibbonSyncPlanner.Plan(Workbook(A), Mirror(A.Replace("><", ">\r\n<")), null, Now));
        same.Action.Should().Be(VbaRibbonSyncAction.Conflict, "改行の位置が違えば本文が違う");

        var crlf = One(VbaRibbonSyncPlanner.Plan(Workbook(A + "\n"), Mirror(A + "\r\n"), null, Now));
        crlf.Action.Should().Be(VbaRibbonSyncAction.Rebaseline, "改行の種類と末尾の改行だけの違いは変更にしない");
        crlf.Baseline.Should().NotBeNull();

        One(VbaRibbonSyncPlanner.Plan(Workbook(A), Mirror(B), null, Now)).Action.Should().Be(VbaRibbonSyncAction.Conflict);
    }

    [Fact(DisplayName = "記録なし・両方無い: 計画に載せない（customUI の無いブックでは何も起きない）")]
    public void 何も無い()
    {
        VbaRibbonSyncPlanner.Plan(Workbook(), Mirror(), null, Now).Items.Should().BeEmpty();
    }

    [Fact(DisplayName = "記録あり・ミラーのファイルが無い・ブックにはある: 削除保留（印を立てた記録）。既に印があれば記録は直さない")]
    public void 削除保留()
    {
        var item = One(VbaRibbonSyncPlanner.Plan(Workbook(A), Mirror(), Record(A, A), Now));
        item.Action.Should().Be(VbaRibbonSyncAction.DeletePending);
        item.Baseline!.DeletePending.Should().BeTrue();

        One(VbaRibbonSyncPlanner.Plan(Workbook(A), Mirror(), Record(A, A, deletePending: true), Now)).Baseline.Should().BeNull();
    }

    [Fact(DisplayName = "記録あり・ミラーにある・ブックに無い: 警告。ミラーが変わっていなければ何もしない、変わっていれば反映（足し直す）")]
    public void ブックから無くなった()
    {
        var same = One(VbaRibbonSyncPlanner.Plan(Workbook(), Mirror(A), Record(A, A), Now));
        same.Action.Should().Be(VbaRibbonSyncAction.None);
        same.Warning.Should().Contain("ブックからリボンの部品が無くなっています");

        var changed = One(VbaRibbonSyncPlanner.Plan(Workbook(), Mirror(B), Record(A, A), Now));
        changed.Action.Should().Be(VbaRibbonSyncAction.Push);
        changed.IsNewPart.Should().BeTrue();
        changed.Warning.Should().NotBeNull();
    }

    [Fact(DisplayName = "記録あり・両方無い: 記録を破棄")]
    public void 記録の破棄()
    {
        One(VbaRibbonSyncPlanner.Plan(Workbook(), Mirror(), Record(A, A), Now)).Action.Should().Be(VbaRibbonSyncAction.DiscardRecord);
    }

    [Fact(DisplayName = "needsReview が立っている: 衝突（要確認）として扱い、自動ではどちらへも動かさない")]
    public void 要確認()
    {
        var item = One(VbaRibbonSyncPlanner.Plan(Workbook(A), Mirror(A), Record(A, A, needsReview: true), Now));

        item.Action.Should().Be(VbaRibbonSyncAction.Conflict);
        item.ConflictReason.Should().Be(VbaConflictReason.NeedsReview);
    }

    [Fact(DisplayName = "ミラーのファイルを読めない: その形式を計画から外して警告（無いと扱って削除保留にしない）")]
    public void ミラーを読めない()
    {
        var item = One(VbaRibbonSyncPlanner.Plan(Workbook(A), Mirror(problem: "使用中"), Record(A, A), Now));

        item.Action.Should().Be(VbaRibbonSyncAction.Skip);
        item.Warning.Should().Contain("CustomUI/customUI14.xml").And.Contain("使用中");
    }

    [Fact(DisplayName = "ブックの部品が壊れている（DTD など）: その形式を計画から外して警告（新規の追加と取り違えない）")]
    public void 部品を読めない()
    {
        var item = One(VbaRibbonSyncPlanner.Plan(Workbook(problem: "DTD を含むため"), Mirror(B), Record(A, A), Now));

        item.Action.Should().Be(VbaRibbonSyncAction.Skip);
        item.Warning.Should().Contain("DTD");
    }

    [Theory(DisplayName = "ブック全体を読めない: リボンの同期を丸ごと見送って理由を返す")]
    [InlineData(VbaRibbonReadStatus.FileNotFound)]
    [InlineData(VbaRibbonReadStatus.Locked)]
    [InlineData(VbaRibbonReadStatus.Corrupt)]
    [InlineData(VbaRibbonReadStatus.Encrypted)]
    public void ブックを読めない(VbaRibbonReadStatus status)
    {
        var plan = VbaRibbonSyncPlanner.Plan(
            new VbaRibbonReadResult(status, "理由", Array.Empty<VbaRibbonRawPart>()), Mirror(A), Record(A, A), Now);

        plan.Items.Should().BeEmpty();
        plan.SkippedReason.Should().Contain("理由");
    }

    [Fact(DisplayName = ".xls（LegacyFormat）は対象外として黙って何もしない（警告にしない）")]
    public void 古い形式()
    {
        var plan = VbaRibbonSyncPlanner.Plan(
            new VbaRibbonReadResult(VbaRibbonReadStatus.LegacyFormat, "対象外", Array.Empty<VbaRibbonRawPart>()), Mirror(A), null, Now);

        plan.Items.Should().BeEmpty();
        plan.SkippedReason.Should().BeNull();
    }

    [Fact(DisplayName = "ブックの部品名が記録と違う（別名）: ブックの現在の値を優先し、記録を直す")]
    public void 部品名の直し()
    {
        var item = One(VbaRibbonSyncPlanner.Plan(Workbook(A, partName: "customUI/ribbon.xml"), Mirror(A), Record(A, A), Now));

        item.Action.Should().Be(VbaRibbonSyncAction.None);
        item.PartName.Should().Be("customUI/ribbon.xml");
        item.Baseline!.PartName.Should().Be("customUI/ribbon.xml");
    }

    [Fact(DisplayName = "削除保留のあとミラーにファイルが戻った: 印を外した記録にする")]
    public void 削除保留の解除()
    {
        var item = One(VbaRibbonSyncPlanner.Plan(Workbook(A), Mirror(A), Record(A, A, deletePending: true), Now));

        item.Action.Should().Be(VbaRibbonSyncAction.None);
        item.Baseline!.DeletePending.Should().BeFalse();
    }

    [Fact(DisplayName = "新旧の 2 形式は別々に判定する（古い形式はミラーのファイル名 CustomUI/customUI.xml）")]
    public void 二つの形式()
    {
        var oldXml = "<customUI xmlns=\"http://schemas.microsoft.com/office/2006/01/customui\"/>";
        var workbook = new VbaRibbonReadResult(VbaRibbonReadStatus.Ok, null, new[]
        {
            new VbaRibbonRawPart("customUI/customUI14.xml", true, A, null),
            new VbaRibbonRawPart("customUI/customUI.xml", false, oldXml, null),
        });
        var mirror = new Dictionary<VbaRibbonFormat, VbaRibbonMirrorFile> { [VbaRibbonFormat.Office2010] = new(B) };

        var plan = VbaRibbonSyncPlanner.Plan(workbook, mirror, Record(A, A), Now);

        plan.Items.Should().HaveCount(2);
        plan.Items.Single(i => i.Format == VbaRibbonFormat.Office2010).Action.Should().Be(VbaRibbonSyncAction.Push);
        var old = plan.Items.Single(i => i.Format == VbaRibbonFormat.Office2007);
        old.Action.Should().Be(VbaRibbonSyncAction.Pull);
        old.MirrorPath.Should().Be("CustomUI/customUI.xml");
    }
}
