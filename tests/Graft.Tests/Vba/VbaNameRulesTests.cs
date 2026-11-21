using FluentAssertions;
using Graft.Core.Vba;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// <see cref="VbaNameRules"/> のテスト（仕様書_VBA連携 6.2・10.1）。
/// 新規作成の名前は厳しく、既存の名前（ミラーのファイル名にできるか）は緩く見る、という二段構えを固定する。
/// </summary>
public class VbaNameRulesTests
{
    [Fact(DisplayName = "31 文字は通り、32 文字は拒否される（境界）")]
    public void 文字数の境界()
    {
        VbaNameRules.ValidateNewName("A" + new string('b', 30)).IsValid.Should().BeTrue();

        var tooLong = VbaNameRules.ValidateNewName("A" + new string('b', 31));
        tooLong.IsValid.Should().BeFalse();
        tooLong.Reason.Should().Contain("31");
    }

    [Theory(DisplayName = "先頭が数字・アンダースコア・記号の名前は拒否される")]
    [InlineData("1Module")]
    [InlineData("_Module")]
    [InlineData("-Module")]
    public void 先頭は英字(string name)
    {
        VbaNameRules.ValidateNewName(name).IsValid.Should().BeFalse();
    }

    [Theory(DisplayName = "英数字とアンダースコアだけの名前は通る")]
    [InlineData("Module1")]
    [InlineData("modUtil")]
    [InlineData("Cls_Order_2")]
    [InlineData("A")]
    public void 通る名前(string name)
    {
        var check = VbaNameRules.ValidateNewName(name);
        check.IsValid.Should().BeTrue();
        check.Reason.Should().BeNull();
    }

    [Theory(DisplayName = "使えない文字（空白・ハイフン・ドット・記号）を含む名前は拒否される")]
    [InlineData("My Module")]
    [InlineData("My-Module")]
    [InlineData("My.Module")]
    [InlineData("A$")]
    [InlineData("A/B")]
    public void 使えない文字(string name)
    {
        var check = VbaNameRules.ValidateNewName(name);
        check.IsValid.Should().BeFalse();
        check.Reason.Should().Contain("英数字");
    }

    [Theory(DisplayName = "日本語の名前は新規作成では拒否される（先頭でも途中でも）")]
    [InlineData("集計")]
    [InlineData("Mod集計")]
    [InlineData("ｱｲｳ")]
    public void 日本語は新規作成で拒否(string name)
    {
        VbaNameRules.ValidateNewName(name).IsValid.Should().BeFalse();
    }

    [Fact(DisplayName = "空の名前は拒否される")]
    public void 空の名前()
    {
        VbaNameRules.ValidateNewName(string.Empty).IsValid.Should().BeFalse();
    }

    [Theory(DisplayName = "Windows の予約名は新規作成で拒否される（大文字小文字を問わない）")]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("Prn")]
    [InlineData("AUX")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("com9")]
    [InlineData("LPT1")]
    [InlineData("lpt9")]
    public void 予約名は新規作成で拒否(string name)
    {
        var check = VbaNameRules.ValidateNewName(name);
        check.IsValid.Should().BeFalse();
        check.Reason.Should().Contain("予約名");
        VbaNameRules.IsWindowsReservedName(name).Should().BeTrue();
    }

    [Theory(DisplayName = "予約名に似た名前は予約名ではない")]
    [InlineData("COM0")]
    [InlineData("COM10")]
    [InlineData("LPT0")]
    [InlineData("CONSOLE")]
    [InlineData("Auxiliary")]
    public void 予約名に似た名前(string name)
    {
        VbaNameRules.IsWindowsReservedName(name).Should().BeFalse();
        VbaNameRules.ValidateNewName(name).IsValid.Should().BeTrue();
    }

    [Theory(DisplayName = "VBA の予約語（Module・Class など）は検査しない（VBE が拒否して E808 になる）")]
    [InlineData("Module")]
    [InlineData("Class")]
    [InlineData("Sub")]
    public void 予約語は検査しない(string name)
    {
        VbaNameRules.ValidateNewName(name).IsValid.Should().BeTrue();
    }

    [Theory(DisplayName = "既存のコンポーネント名は、日本語や 31 文字超でもミラーのファイル名にできる")]
    [InlineData("集計処理")]
    [InlineData("Sheet1")]
    [InlineData("ThisWorkbook")]
    [InlineData("12345678901234567890123456789012345")]
    public void 既存の名前はミラーにできる(string name)
    {
        VbaNameRules.CanMirror(name).Should().BeTrue();
    }

    [Theory(DisplayName = "ファイル名にできない既存の名前（空・ドット・予約名・禁止記号・前後の空白・制御文字）")]
    [InlineData("")]
    [InlineData("a.b")]
    [InlineData("CON")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a:b")]
    [InlineData("a*b")]
    [InlineData("a?b")]
    [InlineData(" a")]
    [InlineData("a ")]
    [InlineData("a\tb")]
    public void 既存の名前でもミラーにできないもの(string name)
    {
        VbaNameRules.CanMirror(name).Should().BeFalse();
    }
}
