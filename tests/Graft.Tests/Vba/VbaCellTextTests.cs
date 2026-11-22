using FluentAssertions;
using Graft.Core.Vba;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// <see cref="VbaCellText"/>（<c>Range.Value2</c> の値 → 1 セルの短い文字列）のテスト。
/// 仕様書_ブックの情報と依頼の観点 5.5・11.1。COM には触れない。
/// </summary>
public class VbaCellTextTests
{
    [Theory(DisplayName = "エラー値 7 種は、負の整数から #N/A などの名前になる")]
    [InlineData(-2146826246, "#N/A")]
    [InlineData(-2146826281, "#DIV/0!")]
    [InlineData(-2146826273, "#VALUE!")]
    [InlineData(-2146826265, "#REF!")]
    [InlineData(-2146826259, "#NAME?")]
    [InlineData(-2146826252, "#NUM!")]
    [InlineData(-2146826288, "#NULL!")]
    public void エラー値(int value, string expected)
    {
        VbaCellText.Render(value, 60).Should().Be(expected);
    }

    [Fact(DisplayName = "エラー値の表に無い整数は、そのまま数として出る")]
    public void 通常の整数()
    {
        VbaCellText.Render(42, 60).Should().Be("42");
        VbaCellText.Render(-5, 60).Should().Be("-5");
    }

    [Fact(DisplayName = "null（Empty）と空文字は空")]
    public void 空()
    {
        VbaCellText.Render(null, 60).Should().BeEmpty();
        VbaCellText.Render(string.Empty, 60).Should().BeEmpty();
    }

    [Fact(DisplayName = "文字列の改行・タブ・連続する空白は空白 1 つに畳まれ、前後は落とされる")]
    public void 文字列の畳み込み()
    {
        VbaCellText.Render("  一行目\r\n二行目\t三行目   四行目 \n", 60).Should().Be("一行目 二行目 三行目 四行目");
    }

    [Fact(DisplayName = "最大文字数を超える文字列は「…」つきで切る。ちょうどなら切らない")]
    public void 文字列の切り詰め()
    {
        VbaCellText.Render("あいうえおかきくけこ", 5).Should().Be("あいうえお…");
        VbaCellText.Render("あいうえお", 5).Should().Be("あいうえお");
    }

    [Fact(DisplayName = "切り詰めの境目がサロゲートペア（絵文字）の途中なら、ペアを割らない")]
    public void サロゲートを割らない()
    {
        // "ab😀cd": 😀 は 2 つの UTF-16 要素。3 文字で切ると 😀 の前半になるので、手前で切る。
        var text = VbaCellText.Render("ab😀cd", 3);

        text.Should().Be("ab…");
    }

    [Fact(DisplayName = "数値: 整数値の double は小数点なし、小数は不変カルチャ、日付の連番はそのまま")]
    public void 数値()
    {
        VbaCellText.Render(46000d, 60).Should().Be("46000");
        VbaCellText.Render(250.5d, 60).Should().Be("250.5");
        VbaCellText.Render(0.1d, 60).Should().Be("0.1");
        VbaCellText.Render(-3d, 60).Should().Be("-3");
        VbaCellText.Render(45123.6041666667d, 60).Should().Be("45123.6041666667", "日付と時刻の連番は変換しない（日付の判別はしない）");
        VbaCellText.Render(1234567.89d, 60).Should().NotContain(",", "桁区切りは入れない");
    }

    [Fact(DisplayName = "数値: 指数表記にならない（1E-05 は 10 進で出す）")]
    public void 指数表記を避ける()
    {
        VbaCellText.Render(0.00001d, 60).Should().Be("0.00001");
    }

    [Fact(DisplayName = "真偽値は TRUE／FALSE")]
    public void 真偽値()
    {
        VbaCellText.Render(true, 60).Should().Be("TRUE");
        VbaCellText.Render(false, 60).Should().Be("FALSE");
    }

    [Fact(DisplayName = "数式つき: = で始まる数式は「=式 → 値」、定数セルの Formula（= で始まらない）は値だけ")]
    public void 数式つき()
    {
        VbaCellText.RenderWithFormula("=D2*E2", 2500d, 60).Should().Be("=D2*E2 → 2500");
        VbaCellText.RenderWithFormula("甲社", "甲社", 60).Should().Be("甲社");
        VbaCellText.RenderWithFormula("=A1", null, 60).Should().Be("=A1", "値が空なら数式だけ");
        VbaCellText.RenderWithFormula("=SUM(A1:A9)", -2146826259, 60).Should().Be("=SUM(A1:A9) → #NAME?");
    }

    [Theory(DisplayName = "不具合 10: 新しい版のエラー値（#SPILL!・#CALC! など）も名前になる。表に無いが範囲内の整数は #ERROR!（負の整数のまま出さない）")]
    [InlineData(-2146826245, "#GETTING_DATA")]
    [InlineData(-2146826243, "#SPILL!")]
    [InlineData(-2146826242, "#CONNECT!")]
    [InlineData(-2146826241, "#BLOCKED!")]
    [InlineData(-2146826240, "#UNKNOWN!")]
    [InlineData(-2146826239, "#FIELD!")]
    [InlineData(-2146826238, "#CALC!")]
    [InlineData(-2146826200, "#ERROR!")]
    public void 新しいエラー値(int value, string expected)
    {
        VbaCellText.Render(value, 60).Should().Be(expected);
        VbaCellText.Render(-2146826187, 60).Should().Be("-2146826187", "範囲の外の整数は、ふつうの数");
    }
}
