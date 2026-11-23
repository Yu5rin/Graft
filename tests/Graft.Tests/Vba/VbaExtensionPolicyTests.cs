using FluentAssertions;
using Graft.Core.Vba;
using Graft.Features;
using Graft.Infra;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// <see cref="VbaExtensionPolicy"/> のテスト（仕様書_VBA連携 4.1・10.1）。
/// VBA プロジェクトのときだけ .bas/.cls/.frm が加わり、<b>リボンの同期が有効なときだけ</b>、リボン（CustomUI/customUI14.xml。
/// 仕様書_ブックの情報と依頼の観点 16.3）の .xml が加わる。ほかのプロジェクトと既存の設定は変わらない。
/// <para>.xml を「リボンの同期が有効なときだけ」にしたのは、利用者が .xml を許可リストから外していても、同期をオフにしていても、
/// VBA プロジェクトだけ .xml が黙って書けてしまうのを防ぐため（PathGuard は拡張子の一覧しか持たず、CustomUI/ の下だけには絞れない）。</para>
/// </summary>
public class VbaExtensionPolicyTests
{
    private static readonly Project VbaProject = new() { Id = "p_a", Root = "/x", Vba = new VbaLink { WorkbookFullName = @"C:\a.xlsm" } };
    private static readonly Project PlainProject = new() { Id = "p_b", Root = "/y" };

    [Fact(DisplayName = "VBA プロジェクトでは .bas/.cls/.frm が加わる（末尾に、この並びで）。リボンの同期が有効なら .xml も続く")]
    public void VBAプロジェクトでは加わる()
    {
        VbaExtensionPolicy.Effective(new[] { ".md", ".txt" }, VbaProject).Should().Equal(".md", ".txt", ".bas", ".cls", ".frm");
        VbaExtensionPolicy.Effective(new[] { ".md", ".txt" }, VbaProject, ribbonSyncEnabled: true)
            .Should().Equal(".md", ".txt", ".bas", ".cls", ".frm", ".xml");
    }

    [Fact(DisplayName = "利用者が .xml を外していても、リボンの同期が有効な VBA プロジェクトではリボンのために足す。同期がオフなら足さない（不具合の修正）。入っていれば重複させない")]
    public void リボンのxml()
    {
        VbaExtensionPolicy.Effective(new[] { ".bas" }, VbaProject, ribbonSyncEnabled: true).Should().Contain(".xml");
        VbaExtensionPolicy.Effective(new[] { ".bas" }, VbaProject, ribbonSyncEnabled: false)
            .Should().NotContain(".xml", "リボンの同期をオフにしているなら、利用者が外した .xml を黙って許可しない");
        VbaExtensionPolicy.Effective(new[] { ".bas" }, VbaProject).Should().NotContain(".xml", "既定は足さない側（安全側）");
        VbaExtensionPolicy.Effective(new[] { ".XML", ".bas" }, VbaProject, ribbonSyncEnabled: true)
            .Count(e => string.Equals(e, ".xml", StringComparison.OrdinalIgnoreCase)).Should().Be(1);
        VbaExtensionPolicy.Effective(new[] { ".bas" }, PlainProject, ribbonSyncEnabled: true).Should().NotContain(".xml", "VBA でないプロジェクトの挙動は変えない");
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

        effective.Should().BeEquivalentTo(".BAS", ".txt", ".cls", ".frm");
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
        VbaExtensionPolicy.Effective(new[] { ".md" }, true).Should().BeEquivalentTo(".md", ".bas", ".cls", ".frm");
        VbaExtensionPolicy.Effective(new[] { ".md" }, true, ribbonSyncEnabled: true).Should().BeEquivalentTo(".md", ".bas", ".cls", ".frm", ".xml");
        VbaExtensionPolicy.Effective(new[] { ".md" }, false).Should().BeEquivalentTo(".md");
    }
}
