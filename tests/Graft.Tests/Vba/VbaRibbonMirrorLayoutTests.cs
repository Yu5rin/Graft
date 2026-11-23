using FluentAssertions;
using Graft.Core;
using Graft.Core.Vba;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// <see cref="VbaRibbonMirrorLayout"/>（ミラーのリボンのファイルのパスと、形式ごとの定数）のテスト。仕様書_ブックの情報と依頼の観点 16.3。
/// </summary>
public class VbaRibbonMirrorLayoutTests
{
    [Theory(DisplayName = "2 つのパスを受け付け、正規の綴りに直す（大文字小文字・区切り・先頭の ./ は問わない）")]
    [InlineData("CustomUI/customUI14.xml", VbaRibbonFormat.Office2010, "CustomUI/customUI14.xml")]
    [InlineData("customui/CUSTOMUI14.XML", VbaRibbonFormat.Office2010, "CustomUI/customUI14.xml")]
    [InlineData("CustomUI\\customUI.xml", VbaRibbonFormat.Office2007, "CustomUI/customUI.xml")]
    [InlineData("./CustomUI/customUI.xml", VbaRibbonFormat.Office2007, "CustomUI/customUI.xml")]
    public void 受け付ける(string path, VbaRibbonFormat format, string normalized)
    {
        VbaRibbonMirrorLayout.TryParse(path, out var result).Should().BeTrue();
        result.Should().Be(new VbaRibbonMirrorPath(format, normalized));
    }

    [Theory(DisplayName = "CustomUI/ のほかのファイル・入れ子・ミラーの外・ほかのフォルダは受け付けない")]
    [InlineData("CustomUI/foo.xml")]
    [InlineData("CustomUI/images/a.png")]
    [InlineData("CustomUI/sub/customUI14.xml")]
    [InlineData("../CustomUI/customUI14.xml")]
    [InlineData("CustomUI/../customUI14.xml")]
    [InlineData("/CustomUI/customUI14.xml")]
    [InlineData("C:/CustomUI/customUI14.xml")]
    [InlineData("customUI14.xml")]
    [InlineData("Modules/customUI14.xml")]
    [InlineData("")]
    public void 受け付けない(string path)
    {
        VbaRibbonMirrorLayout.TryParse(path, out var result).Should().BeFalse();
        result.Should().BeNull();
    }

    [Fact(DisplayName = "CustomUI/ の中かどうか（E810 の理由を付け分けるため）")]
    public void フォルダの中()
    {
        VbaRibbonMirrorLayout.IsUnderRibbonFolder("CustomUI/images/a.png").Should().BeTrue();
        VbaRibbonMirrorLayout.IsUnderRibbonFolder("customui/foo.xml").Should().BeTrue();
        VbaRibbonMirrorLayout.IsUnderRibbonFolder("CustomUI").Should().BeFalse();
        VbaRibbonMirrorLayout.IsUnderRibbonFolder("Modules/A.bas").Should().BeFalse();
    }

    [Fact(DisplayName = "形式ごとの定数: 名前空間・関係の種類・既定の部品名（読み取り 15.2 と同じ値）")]
    public void 形式ごとの定数()
    {
        VbaRibbonMirrorLayout.Namespace(VbaRibbonFormat.Office2010).Should().Be("http://schemas.microsoft.com/office/2009/07/customui");
        VbaRibbonMirrorLayout.Namespace(VbaRibbonFormat.Office2007).Should().Be("http://schemas.microsoft.com/office/2006/01/customui");
        VbaRibbonMirrorLayout.RelationshipType(VbaRibbonFormat.Office2010).Should().Be(VbaRibbonPackageReader.RelationshipTypeNew);
        VbaRibbonMirrorLayout.RelationshipType(VbaRibbonFormat.Office2007).Should().Be(VbaRibbonPackageReader.RelationshipTypeOld);
        VbaRibbonMirrorLayout.DefaultPartName(VbaRibbonFormat.Office2010).Should().Be("customUI/customUI14.xml");
        VbaRibbonMirrorLayout.DefaultPartName(VbaRibbonFormat.Office2007).Should().Be("customUI/customUI.xml");
    }

    [Fact(DisplayName = "F1 では VbaMirrorLayout.CollectUnsupported を変えない（リボンの 2 パスはまだ E810。除外は同期を入れる F2 で）")]
    public void E810はF2まで変えない()
    {
        // ミラーの CustomUI/customUI14.xml は、F2 で同期を入れるまで Excel へは届かない。届かないものを警告から外すと、
        // 利用者が「反映された」と思い込むので、F1 では従来どおり E810 のままにする（仕様書 16.15 の段階 F2 の「E810 の除外」）。
        VbaMirrorLayout.CollectUnsupported(new[] { "CustomUI/customUI14.xml" })
            .Should().ContainSingle().Which.Code.Should().Be(ErrorCode.E810);
    }
}
