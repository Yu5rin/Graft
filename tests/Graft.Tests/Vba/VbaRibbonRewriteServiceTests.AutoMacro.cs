using System.IO;
using FluentAssertions;
using Graft.Core.Vba;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// 準備（16.5 の段階 1〜3）が、ミラーの VBA を走査して <c>Auto_Open</c>・<c>Auto_Close</c> の有無を計画に載せること
/// （実機で、COM の Open・Close ではこの 2 つが動かないと確かめた。6-34 の 2-a・2-c）。
/// </summary>
public sealed partial class VbaRibbonRewriteServiceTests
{
    [Fact(DisplayName = "開いているブック: ミラーの標準モジュールに Auto_Open があれば、計画に載せて確認の文面に「動きません」を添える。無ければ添えない")]
    public async Task 準備でAutoOpenを見つける()
    {
        await ArrangeOpenAsync(XmlA);
        WriteMirror(Ribbon14, XmlB);

        var without = await PlanAsync();
        without.AutoMacroNames.Should().BeEmpty();
        VbaRibbonRewriteText.FullText(without).Should().NotContain("Auto_Open");

        var module = Path.Combine(_mirror, "Modules", "Module1.bas");
        File.WriteAllText(module, File.ReadAllText(module) + "\r\nPublic Sub Auto_Open()\r\nEnd Sub\r\n");
        var with = await PlanAsync();

        with.AutoMacroNames.Should().Equal("Auto_Open");
        VbaRibbonRewriteText.OpenNotices(with).Should().Contain(VbaRibbonRewriteText.AutoOpenNotice);
        VbaRibbonRewriteText.FullText(with).Should().Contain("Auto_Open は、Graft の開き直しでは動きません（Workbook_Open は動きます）");
    }

    [Fact(DisplayName = "開いていないブック: Auto_Open があっても計画に載せない（閉じも開きもしないので、動く・動かないの話にならない）")]
    public async Task 開いていないときはAutoOpenを載せない()
    {
        await ArrangeAsync(XmlA);
        WriteMirror(Ribbon14, XmlB);
        var module = Path.Combine(_mirror, "Modules", "Module1.bas");
        File.WriteAllText(module, File.ReadAllText(module) + "\r\nPublic Sub Auto_Open()\r\nEnd Sub\r\n");

        var plan = await PlanAsync();

        plan.IsOpen.Should().BeFalse();
        plan.AutoMacroNames.Should().BeEmpty();
        VbaRibbonRewriteText.FullText(plan).Should().NotContain("Auto_Open");
    }
}
