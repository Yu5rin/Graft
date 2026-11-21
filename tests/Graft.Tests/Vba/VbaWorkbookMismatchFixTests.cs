using System.IO;
using FluentAssertions;
using Graft.Core;
using Graft.Features;
using Graft.UiTests.Vba;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// 「疑わしい点」の修正: 同期状態の <c>WorkbookFullName</c> を照合し、別のブックの同期状態は使わない。
/// 修正前は <c>LoadAsync</c> の引数が既定値にしか使われず、任意のフォルダを「既存のミラーを使う」で別のブックに
/// 結び付けると、前のブックの基準で三方比較して、新しいブックの内容を確認なしに取り込みと判定していた
/// （<c>state.WorkbookFullName=A.xlsm</c> のまま B の内容を取込と判定）。
/// </summary>
public class VbaWorkbookMismatchFixTests : VbaServiceFixture
{
    private const string OtherBook = @"C:\Work\別のブック.xlsm";

    [Fact(DisplayName = "同期状態の照合: 記録されたブックが違えば記録を使わず、空の状態（要求したブック）と警告を返す")]
    public async Task 別のブックの記録は使わない()
    {
        var project = await CreatedProjectAsync(FakeVbaBridge.Std("Module1", "x"));

        var other = await States.LoadAsync(project.Id, OtherBook);

        other.Value.Modules.Should().BeEmpty();
        other.Value.WorkbookFullName.Should().Be(OtherBook);
        other.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E812 && i.Severity == Severity.Warning)
            .Which.Detail.Should().Contain(Book).And.Contain("作り直す");
    }

    [Theory(DisplayName = "同期状態の照合: 同じブック（大文字小文字・区切りの違いを除く）なら記録を使い、警告は出ない")]
    [InlineData(@"C:\Work\計画.xlsm")]
    [InlineData(@"c:\work\計画.XLSM")]
    [InlineData(@"C:/Work/計画.xlsm")]
    public async Task 同じブックの記録は使う(string spelling)
    {
        var project = await CreatedProjectAsync(FakeVbaBridge.Std("Module1", "x"));

        var same = await States.LoadAsync(project.Id, spelling);

        same.Value.Modules.Should().ContainKey("Modules/Module1.bas");
        same.Issues.Should().BeEmpty();
    }

    [Fact(DisplayName = "同期状態の照合 再現: 別のブックに結び付けたミラーで取込しても、前のブックの基準では判定せず、違いは取り込まずに衝突として確認する")]
    public async Task 別のブックへ結び付けると確認なしに取り込まない()
    {
        await CreatedProjectAsync(FakeVbaBridge.Std("Module1", "Sub A()\r\nEnd Sub"));
        var linkedToOther = NewProject(new VbaLink { WorkbookFullName = OtherBook });
        Bridge.AddWorkbook(OtherBook, FakeVbaBridge.Std("Module1", "Sub A()\r\n  ' from the other book\r\nEnd Sub"));

        var pull = await Service.PullAsync(linkedToOther);

        pull.IsSuccess.Should().BeTrue();
        pull.Value.Pulled.Should().BeEmpty("以前は前のブックの基準で「Excel 側だけが変わった」と判定して、確認なしに取り込んだ");
        pull.Value.Conflicts.Should().Equal("Modules/Module1.bas");
        pull.Issues.Should().Contain(i => i.Severity == Severity.Warning && i.Detail!.Contains(Book));
        File.ReadAllText(MirrorFile("Modules/Module1.bas")).Should().Be("Sub A()\r\nEnd Sub\r\n", "ミラーは書き換えない");
    }

    [Fact(DisplayName = "同期状態の照合: 別のブックでも本文が同じモジュールは衝突にせず、基準を作り直す（新しいブックの記録として保存される）")]
    public async Task 本文が同じなら基準を作り直す()
    {
        await CreatedProjectAsync(FakeVbaBridge.Std("Module1", "Sub A()\r\nEnd Sub"));
        var linkedToOther = NewProject(new VbaLink { WorkbookFullName = OtherBook });
        Bridge.AddWorkbook(OtherBook, FakeVbaBridge.Std("Module1", "Sub A()\r\nEnd Sub"));

        var pull = await Service.PullAsync(linkedToOther);

        pull.Value.Conflicts.Should().BeEmpty();
        var saved = await States.LoadAsync(linkedToOther.Id, OtherBook);
        saved.Issues.Should().BeEmpty("基準が新しいブックの記録として保存された");
        saved.Value.Modules.Should().ContainKey("Modules/Module1.bas");
    }
}
