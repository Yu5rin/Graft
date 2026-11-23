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

    [Fact(DisplayName = "F2: リボンを同期しているとき、CollectUnsupported はリボンの 2 パスを E810 にしない。CustomUI/ のほかは理由つきの E810")]
    public void E810の除外()
    {
        // 同期を入れた F2 で、ミラーのリボンのファイルは取込でブックとつながる（仕様書 16.3・16.15）。警告に残すと、取り込んだ
        // ミラーで毎回「同期の対象外」と出てしまう。F1 で「E810 のまま」と固定していたのを、ここで逆向きにした。
        VbaMirrorLayout.CollectUnsupported(new[] { "CustomUI/customUI14.xml", "customui/CUSTOMUI.XML" }, ribbonSynced: true)
            .Should().BeEmpty();

        var others = VbaMirrorLayout.CollectUnsupported(
            new[] { "CustomUI/images/a.png", "CustomUI/foo.xml", "CustomUI/sub/customUI14.xml" }, ribbonSynced: true);
        others.Should().HaveCount(3);
        others.Should().OnlyContain(i => i.Code == ErrorCode.E810 && i.Severity == Severity.Warning
                                         && i.Detail == VbaRibbonMirrorLayout.OtherFileReason);

        // モジュールのパスの判定は変わらない。
        VbaMirrorLayout.CollectUnsupported(new[] { "Modules/A.bas", "memo.txt" }, ribbonSynced: true)
            .Should().ContainSingle().Which.Path.Should().Be("memo.txt");
    }

    [Fact(DisplayName = "F2: リボンを同期していないとき（設定でオフ・未配線）は、従来どおり CustomUI/ はすべて E810（ブックへ届かないものを警告から外さない）")]
    public void 同期していなければE810のまま()
    {
        VbaMirrorLayout.CollectUnsupported(new[] { "CustomUI/customUI14.xml" })
            .Should().ContainSingle().Which.Code.Should().Be(ErrorCode.E810);
        VbaMirrorLayout.CollectUnsupported(new[] { "CustomUI/customUI14.xml" }, ribbonSynced: false)
            .Single().Detail.Should().Contain("同期のフォルダではありません");
    }
}
