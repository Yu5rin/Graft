using FluentAssertions;
using Graft.Core;
using Graft.Core.PromptFocus;
using Graft.Core.Vba;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>段階 F5 の純ロジック（仕様書_ブックの情報と依頼の観点 16.4・16.8・16.10・16.14）: 適用前の XSD 検査、衝突ダイアログの項目、依頼の観点のリボンの行。</summary>
public sealed class VbaRibbonF5LogicTests
{
    private const string Ribbon14 = "CustomUI/customUI14.xml";

    private static BlockPlan Plan(string path, string? after, BlockKind kind = BlockKind.FullContent, bool selected = true)
        => new()
        {
            Block = kind == BlockKind.Delete
                ? new DeleteBlock { Path = path }
                : new FullContentBlock { Path = path, Content = after ?? string.Empty },
            Path = path,
            Operation = kind == BlockKind.Delete ? EntryOperation.Delete : EntryOperation.Modify,
            CanApply = true,
            IsSelected = selected,
            AfterText = after,
        };

    [Fact(DisplayName = "適用前の検査: CustomUI/*.xml への変更が XSD に合わなければ E818 で止める（行・列つき）。ほかのブロックは止めない")]
    public void リボンのエラーは止める()
    {
        var bad = RibbonPackageBuilder.SampleRibbon.Replace("size=\"large\"", "size=\"big\"", StringComparison.Ordinal);
        var plans = new[] { Plan(Ribbon14, bad), Plan("Modules/Module1.bas", "Sub A()\r\nEnd Sub") };

        var findings = VbaApplyGuard.Check(plans);

        findings.Should().OnlyContain(f => f.Issue.Code == ErrorCode.E818 && f.Plan.Path == Ribbon14);
        findings.Should().NotBeEmpty();
        findings[0].Issue.Detail.Should().StartWith(Ribbon14);
        findings[0].Issue.LineNumber.Should().BeGreaterThan(0);
    }

    [Fact(DisplayName = "適用前の検査: 正しい XML・チェックを外したブロック・設定オフ（checkRibbon=false）は止めない。新規ファイルも同じ検査")]
    public void リボンの正しい変更は止めない()
    {
        VbaApplyGuard.Check(new[] { Plan(Ribbon14, RibbonPackageBuilder.SampleRibbon) }).Should().BeEmpty();
        var bad = "<customUI xmlns=\"http://schemas.microsoft.com/office/2009/07/customui\"><ribbon><tabz/></ribbon></customUI>";
        VbaApplyGuard.Check(new[] { Plan(Ribbon14, bad, selected: false) }).Should().BeEmpty("チェックを外せば見逃す");
        VbaApplyGuard.Check(new[] { Plan(Ribbon14, bad) }, checkRibbon: false).Should().BeEmpty("リボンを同期しないなら検査も要らない");
        VbaApplyGuard.Check(new[] { Plan(Ribbon14, bad) }).Should().NotBeEmpty("新規作成でも、変更でも、同じ検査");
    }

    [Fact(DisplayName = "適用前の検査: CustomUI/ の DELETE は E814、CP932 の検査は XML に掛けない（UTF-8 で書くため）")]
    public void リボンのDELETEとCP932()
    {
        VbaApplyGuard.Check(new[] { Plan(Ribbon14, null, BlockKind.Delete) }).Should().ContainSingle(f => f.Issue.Code == ErrorCode.E814);
        var withEmoji = RibbonPackageBuilder.SampleRibbon.Replace("label=\"業務\"", "label=\"業務😀\"", StringComparison.Ordinal);
        VbaApplyGuard.Check(new[] { Plan(Ribbon14, withEmoji) }).Should().NotContain(f => f.Issue.Code == ErrorCode.E811);
    }

    [Fact(DisplayName = "解析結果への警告: 止めない問題（E818 の警告）をブロックの問題に足す。リボンのブロックが無ければ同じ一覧をそのまま返す")]
    public void リボンの警告を添える()
    {
        var noId = RibbonPackageBuilder.SampleRibbon.Replace("<button idMso=\"Copy\" onAction=\"Module1.OnCopy\"/>", "<button label=\"x\" onAction=\"OnRun\"/>", StringComparison.Ordinal);
        var plans = new[] { Plan(Ribbon14, noId) };

        var shown = VbaApplyGuard.AddRibbonWarnings(plans);

        shown[0].Issues.Should().NotBeEmpty().And.OnlyContain(i => i.Code == ErrorCode.E818 && i.Severity == Severity.Warning);
        VbaApplyGuard.Check(plans).Should().BeEmpty("警告だけなら止めない");

        var plain = new[] { Plan("Modules/Module1.bas", "Sub A()\r\nEnd Sub") };
        VbaApplyGuard.AddRibbonWarnings(plain).Should().BeSameAs(plain);
    }

    [Fact(DisplayName = "衝突の項目: パスで IsRibbon を見分ける。リボンだけの要求では「この適用を取り消す」を出さない（16.10・16.16-12）")]
    public void 衝突の項目()
    {
        var ribbon = VbaConflictItem.ForRibbon(Ribbon14, "<a/>", "<b/>", VbaConflictReason.BothChanged);
        var module = new VbaConflictItem("Modules/Module1.bas", VbaModuleKind.Standard, "Module1", "a", "b");
        ribbon.IsRibbon.Should().BeTrue();
        module.IsRibbon.Should().BeFalse();

        new VbaConflictRequest(VbaConflictScene.AfterApply, new[] { ribbon }).CanUndoApply.Should().BeFalse();
        new VbaConflictRequest(VbaConflictScene.AfterApply, new[] { ribbon, module }).CanUndoApply.Should().BeTrue();
        new VbaConflictRequest(VbaConflictScene.AfterApply, new[] { ribbon }).DefaultDecision.PathsWith(VbaConflictChoice.Skip).Should().Equal(Ribbon14);
        VbaConflictDiff.BuildPlan(ribbon, 3).Description.Should().Contain("ブックのファイル側");
    }

    [Fact(DisplayName = "依頼の観点: ミラーに CustomUI/*.xml があるときだけ、VBA の前提にリボンの行を 1 回足す。無ければ従来と 1 バイトも同じ")]
    public void 観点のリボンの行()
    {
        var focus = new PromptFocus { Id = "x", Name = "読みやすく", Group = PromptFocusGroups.All[0], Body = "## 読みやすく\n- 直す。", VbaAddendum = "- VBA 追記。" };
        var selected = new[] { focus };

        var without = PromptFocusComposer.Compose(selected, isVba: true, hasWorkbookInfo: false);
        PromptFocusComposer.Compose(selected, true, false, ribbonFiles: null).Should().Be(without);
        PromptFocusComposer.Compose(selected, true, false, ribbonFiles: Array.Empty<string>()).Should().Be(without);
        without.Should().NotContain("リボン");

        var with = PromptFocusComposer.Compose(selected, true, false, new[] { Ribbon14 });
        with.Should().Contain("- リボン（CustomUI/customUI14.xml）を直すときは、そのファイルへの search/replace で答えてください。");
        with.Should().Contain("id は重複させず").And.Contain("標準モジュールの Public な Sub／Function").And.Contain("画像は足せない");
        with.Split("リボン（CustomUI").Length.Should().Be(2, "前提に 1 回だけ");
        with.TrimEnd().Should().Contain("確かめられないものを推測で使わず、質問してください。", "前提の最終行は変えない");
        with.Replace(PromptFocusComposer.RibbonPremiseLine(new[] { Ribbon14 }) + "\n", string.Empty).Should().Be(without);

        PromptFocusComposer.Compose(selected, isVba: false, hasWorkbookInfo: false, ribbonFiles: new[] { Ribbon14 })
            .Should().Be(PromptFocusComposer.Compose(selected, false, false), "VBA でなければ足さない");
        PromptFocusComposer.RibbonPremiseLine(new[] { Ribbon14, "CustomUI/customUI.xml" }).Should().Contain("CustomUI/customUI14.xml・CustomUI/customUI.xml");
    }
}
