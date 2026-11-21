using FluentAssertions;
using Graft.Core;
using Graft.Core.Vba;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// <see cref="VbaApplyGuard"/> のテスト（仕様書_VBA連携 7.3-2・10.1）。
/// 対象は「チェックが付いている」ブロックだけ。外せばそのブロックは見逃され、ほかは適用できる。
/// </summary>
public class VbaApplyGuardTests
{
    private static BlockPlan Plan(PatchBlock block, string path, bool selected = true, string? afterText = "Sub A()\r\nEnd Sub")
        => new()
        {
            Block = block,
            Path = path,
            Operation = "modify",
            CanApply = true,
            IsSelected = selected,
            AfterText = afterText,
        };

    private static BlockPlan Full(string path, string content, bool selected = true)
        => Plan(new FullContentBlock { Path = path, Content = content }, path, selected, content);

    [Fact(DisplayName = "DELETE は E814 で止まる")]
    public void Deleteは止まる()
    {
        var plan = Plan(new DeleteBlock { Path = "Modules/Old.bas", HeaderLine = 7 }, "Modules/Old.bas", afterText: null);

        var findings = VbaApplyGuard.Check(new[] { plan });

        findings.Should().ContainSingle();
        findings[0].Plan.Should().BeSameAs(plan);
        findings[0].Issue.Code.Should().Be(ErrorCode.E814);
        findings[0].Issue.Severity.Should().Be(Severity.Error);
        findings[0].Issue.LineNumber.Should().Be(7);
        findings[0].Issue.Detail.Should().Contain("削除");
    }

    [Fact(DisplayName = "RENAME は E814 で止まる")]
    public void Renameは止まる()
    {
        var plan = Plan(new RenameBlock { Path = "Modules/Old.bas", ToPath = "Modules/New.bas" }, "Modules/New.bas");

        var findings = VbaApplyGuard.Check(new[] { plan });

        findings.Should().ContainSingle(f => f.Issue.Code == ErrorCode.E814);
        findings[0].Issue.Detail.Should().Contain("名前変更");
    }

    [Fact(DisplayName = "チェックが外れている DELETE／RENAME は対象外（外せば適用できる）")]
    public void チェックが外れていれば対象外()
    {
        var del = Plan(new DeleteBlock { Path = "Modules/Old.bas" }, "Modules/Old.bas", selected: false, afterText: null);
        var ren = Plan(new RenameBlock { Path = "Modules/A.bas", ToPath = "Modules/B.bas" }, "Modules/B.bas", selected: false);

        VbaApplyGuard.Check(new[] { del, ren }).Should().BeEmpty();
    }

    [Fact(DisplayName = "MKDIR は止めない（フォルダは同期の対象外で、同期に影響しない）")]
    public void Mkdirは止めない()
    {
        var plan = Plan(new MkdirBlock { Path = "Modules/sub" }, "Modules/sub", afterText: null);

        VbaApplyGuard.Check(new[] { plan }).Should().BeEmpty();
    }

    [Fact(DisplayName = "CP932 で表せない文字を含む適用後の本文は E811 で止まる")]
    public void Cp932違反は止まる()
    {
        var bad = Full("Modules/Bad.bas", "Sub A()\r\n  ' 説明 — 注\r\nEnd Sub");

        var findings = VbaApplyGuard.Check(new[] { bad });

        findings.Should().ContainSingle();
        findings[0].Issue.Code.Should().Be(ErrorCode.E811);
        findings[0].Issue.Path.Should().Be("Modules/Bad.bas");
        findings[0].Issue.Detail.Should().Contain("U+2014");
    }

    [Fact(DisplayName = "違反のあるブロックだけが列挙され、問題のないブロックは含まれない（外せばほかは適用できる）")]
    public void 違反のあるものだけ列挙()
    {
        var ok = Full("Modules/Ok.bas", "Sub A()\r\nEnd Sub");
        var bad = Full("Classes/Bad.cls", "x = \"\U0001F600\"");
        var unselectedBad = Full("Modules/Bad2.bas", "—", selected: false);

        var findings = VbaApplyGuard.Check(new[] { ok, bad, unselectedBad });

        findings.Select(f => f.Plan.Path).Should().Equal("Classes/Bad.cls");
    }

    [Fact(DisplayName = "複数のブロックの違反をすべて列挙する（DELETE と CP932 違反が混在しても）")]
    public void 複数の違反を列挙()
    {
        var bad1 = Full("Modules/A.bas", "–");
        var bad2 = Full("Modules/B.bas", "•");
        var del = Plan(new DeleteBlock { Path = "Modules/C.bas" }, "Modules/C.bas", afterText: null);

        var findings = VbaApplyGuard.Check(new[] { bad1, del, bad2 });

        findings.Select(f => f.Issue.Code).Should().Equal(ErrorCode.E811, ErrorCode.E814, ErrorCode.E811);
    }

    [Fact(DisplayName = "同期の対象外のパス（ミラーの外）は Excel に反映されないので CP932 を検査しない")]
    public void 対象外のパスは検査しない()
    {
        var notes = Full("notes.md", "— メモ");

        VbaApplyGuard.Check(new[] { notes }).Should().BeEmpty();
    }

    [Fact(DisplayName = "表せる文字（全角チルダ・囲み数字）だけなら止めない")]
    public void 表せる文字は止めない()
    {
        var ok = Full("Modules/A.bas", "' ～ ① ⑳ 日本語");

        VbaApplyGuard.Check(new[] { ok }).Should().BeEmpty();
    }

    [Fact(DisplayName = "適用後の本文が無い（null）ブロックは CP932 の検査をしない")]
    public void 本文が無ければ検査しない()
    {
        var plan = Plan(new FullContentBlock { Path = "Modules/A.bas", Content = string.Empty }, "Modules/A.bas", afterText: null);

        VbaApplyGuard.Check(new[] { plan }).Should().BeEmpty();
    }
}
