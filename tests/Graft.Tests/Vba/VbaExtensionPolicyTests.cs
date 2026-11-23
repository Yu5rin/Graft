using FluentAssertions;
using Graft.Core.Vba;
using Graft.Features;
using Graft.Infra;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// <see cref="VbaExtensionPolicy"/> のテスト（仕様書_VBA連携 4.1・10.1）。
/// VBA プロジェクトのときだけ .bas/.cls/.frm と、リボン（CustomUI/customUI14.xml。仕様書_ブックの情報と依頼の観点 16.3）の .xml が加わり、
/// ほかのプロジェクトと既存の設定は変わらない。
/// </summary>
public class VbaExtensionPolicyTests
{
    private static readonly Project VbaProject = new() { Id = "p_a", Root = "/x", Vba = new VbaLink { WorkbookFullName = @"C:\a.xlsm" } };
    private static readonly Project PlainProject = new() { Id = "p_b", Root = "/y" };

    [Fact(DisplayName = "VBA プロジェクトでは .bas/.cls/.frm とリボンの .xml が加わる（末尾に、この並びで）")]
    public void VBAプロジェクトでは加わる()
    {
        var effective = VbaExtensionPolicy.Effective(new[] { ".md", ".txt" }, VbaProject);

        effective.Should().Equal(".md", ".txt", ".bas", ".cls", ".frm", ".xml");
    }

    [Fact(DisplayName = "利用者が .xml を外していても、VBA プロジェクトではリボンのために足す。入っていれば重複させない（段階 F2）")]
    public void リボンのxml()
    {
        VbaExtensionPolicy.Effective(new[] { ".bas" }, VbaProject).Should().Contain(".xml");
        VbaExtensionPolicy.Effective(new[] { ".XML", ".bas" }, VbaProject)
            .Count(e => string.Equals(e, ".xml", StringComparison.OrdinalIgnoreCase)).Should().Be(1);
        VbaExtensionPolicy.Effective(new[] { ".bas" }, PlainProject).Should().NotContain(".xml", "VBA でないプロジェクトの挙動は変えない");
    }

    [Fact(DisplayName = "ほかのプロジェクトには加わらない（同じ一覧がそのまま返る）")]
    public void ほかのプロジェクトには加わらない()
    {
        var allowed = new[] { ".md", ".txt" };

        VbaExtensionPolicy.Effective(allowed, PlainProject).Should().BeSameAs(allowed);
        VbaExtensionPolicy.Effective(allowed, (Project?)null).Should().BeSameAs(allowed);
    }

    [Fact(DisplayName = "既存の設定を壊さない: 元の一覧は変更されず、元の並びが先頭に残る")]
    public void 既存の設定を壊さない()
    {
        var allowed = new List<string> { ".py", ".md" };

        var effective = VbaExtensionPolicy.Effective(allowed, VbaProject);

        allowed.Should().Equal(".py", ".md");
        effective.Take(2).Should().Equal(".py", ".md");
    }

    [Fact(DisplayName = "既に入っている拡張子は大文字小文字を区別せず重複させない")]
    public void 重複させない()
    {
        var effective = VbaExtensionPolicy.Effective(new[] { ".BAS", ".txt", ".cls" }, VbaProject);

        effective.Should().BeEquivalentTo(".BAS", ".txt", ".cls", ".frm", ".xml");
        effective.Count(e => string.Equals(e, ".bas", StringComparison.OrdinalIgnoreCase)).Should().Be(1);
    }

    [Fact(DisplayName = "既定の許可拡張子（Settings.Safety）に .bas/.cls/.frm は含まれない（全体の既定は変えない）")]
    public void 全体の既定は変えない()
    {
        new SafetySettings().AllowedExtensions.Should().NotContain(new[] { ".bas", ".cls", ".frm" });
    }

    [Fact(DisplayName = "bool 版も同じ結果（Project 型に依存しない呼び出し口）")]
    public void bool版()
    {
        VbaExtensionPolicy.Effective(new[] { ".md" }, true).Should().BeEquivalentTo(".md", ".bas", ".cls", ".frm", ".xml");
        VbaExtensionPolicy.Effective(new[] { ".md" }, false).Should().BeEquivalentTo(".md");
    }
}
