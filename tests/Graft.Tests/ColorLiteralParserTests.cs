using FluentAssertions;
using Graft.Editor;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// カラープレビュー機能（検討書「コード中のカラープレビュー」）の色検出・記法保持
/// （<see cref="ColorLiteralParser"/>）のテスト。利用者指示の要件（元の記法を保つ。
/// <c>#FF0000</c>を選び直したら<c>#00FF00</c>、<c>rgb(255,0,0)</c>は<c>rgb(0,255,0)</c>のまま）を
/// 直接検証する。
/// </summary>
public class ColorLiteralParserTests
{
    // ========== 検出 ==========

    [Theory(DisplayName = "16進(3/4/6/8桁)を検出する")]
    [InlineData("#f60", 1)]
    [InlineData("#f60c", 1)]
    [InlineData("#ff6600", 1)]
    [InlineData("#ff6600cc", 1)]
    public void 十六進を検出する(string literal, int expectedCount)
    {
        var matches = ColorLiteralParser.FindAll($"color: {literal};");
        matches.Should().HaveCount(expectedCount);
        matches[0].Kind.Should().Be(ColorNotationKind.Hex);
    }

    [Fact(DisplayName = "直前が英数字・アンダースコアの16進は検出しない")]
    public void 識別子の一部の16進は検出しない()
    {
        ColorLiteralParser.FindAll("abc#ff0000").Should().BeEmpty();
        ColorLiteralParser.FindAll("_#ff0000").Should().BeEmpty();
        ColorLiteralParser.FindAll("1#ff0000").Should().BeEmpty();
    }

    [Fact(DisplayName = "7桁など中途半端な桁数の16進は検出しない")]
    public void 中途半端な桁数は検出しない()
    {
        ColorLiteralParser.FindAll("#ff00001").Should().BeEmpty();
    }

    [Fact(DisplayName = "rgb()のカンマ記法を検出しRGBを正しく読み取る")]
    public void rgbカンマ記法を検出する()
    {
        var matches = ColorLiteralParser.FindAll("rgb(255, 102, 0)");
        matches.Should().HaveCount(1);
        var m = matches[0];
        m.Kind.Should().Be(ColorNotationKind.Rgb);
        m.HasAlpha.Should().BeFalse();
        m.Color.Should().Be(new RgbaColor(255, 255, 102, 0));
    }

    [Fact(DisplayName = "rgba()のカンマ記法（アルファ小数）を検出する")]
    public void rgbaカンマ記法を検出する()
    {
        var matches = ColorLiteralParser.FindAll("rgba(255,102,0,.8)");
        matches.Should().HaveCount(1);
        matches[0].HasAlpha.Should().BeTrue();
        matches[0].Color.A.Should().Be((byte)Math.Round(0.8 * 255));
    }

    [Fact(DisplayName = "rgb()のスペース記法（CSS Color Module Level 4）を検出する")]
    public void rgbスペース記法を検出する()
    {
        var matches = ColorLiteralParser.FindAll("rgb(255 102 0 / 80%)");
        matches.Should().HaveCount(1);
        matches[0].HasAlpha.Should().BeTrue();
        matches[0].Color.Should().Be(new RgbaColor((byte)Math.Round(0.8 * 255), 255, 102, 0));
    }

    [Fact(DisplayName = "hsl()のカンマ記法を検出する")]
    public void hslカンマ記法を検出する()
    {
        var matches = ColorLiteralParser.FindAll("hsl(24, 100%, 50%)");
        matches.Should().HaveCount(1);
        matches[0].Kind.Should().Be(ColorNotationKind.Hsl);
        // hsl(24,100%,50%) はおおよそ橙色(#ff6600近辺)になるはず。
        matches[0].Color.R.Should().BeGreaterThan(200);
        matches[0].Color.G.Should().BeInRange(80, 130);
        matches[0].Color.B.Should().BeLessThan(30);
    }

    [Fact(DisplayName = "hsl()のスペース記法を検出する")]
    public void hslスペース記法を検出する()
    {
        var matches = ColorLiteralParser.FindAll("hsl(24 100% 50% / .8)");
        matches.Should().HaveCount(1);
        matches[0].HasAlpha.Should().BeTrue();
    }

    [Fact(DisplayName = "1行に複数の色リテラルがあればすべて検出する")]
    public void 複数のリテラルを検出する()
    {
        var matches = ColorLiteralParser.FindAll("border: 1px solid #ff0000; background: rgb(0, 255, 0);");
        matches.Should().HaveCount(2);
        matches[0].Kind.Should().Be(ColorNotationKind.Hex);
        matches[1].Kind.Should().Be(ColorNotationKind.Rgb);
        matches[0].Start.Should().BeLessThan(matches[1].Start);
    }

    // ========== 記法保持（利用者指示の核心要件） ==========

    [Fact(DisplayName = "#FF0000(大文字6桁)を選び直すと#00FF00のまま(大文字6桁)になる")]
    public void 大文字6桁の16進は記法を保つ()
    {
        var m = ColorLiteralParser.FindAll("#FF0000")[0];
        m.Format(new RgbaColor(255, 0, 255, 0)).Should().Be("#00FF00");
    }

    [Fact(DisplayName = "#f60(小文字3桁)は短縮できる色なら3桁のまま(小文字)になる")]
    public void 短縮できる色は3桁のまま()
    {
        var m = ColorLiteralParser.FindAll("#f60")[0];
        // 0x00,0x11,0x22は各ニブルが等しい(0,1,2)ため3桁で表現できる。
        m.Format(new RgbaColor(255, 0x00, 0x11, 0x22)).Should().Be("#012");
    }

    [Fact(DisplayName = "#f60(3桁)は短縮できない色を選ぶと6桁へ広がる")]
    public void 短縮できない色は6桁へ広がる()
    {
        var m = ColorLiteralParser.FindAll("#f60")[0];
        // 0x12は上位ニブル(1)と下位ニブル(2)が異なるため3桁で表現できない。
        m.Format(new RgbaColor(255, 0x12, 0x34, 0x56)).Should().Be("#123456");
    }

    [Fact(DisplayName = "#f60c(アルファ付き4桁)はアルファを保ったまま書き戻す")]
    public void アルファ付き4桁はアルファを保つ()
    {
        var m = ColorLiteralParser.FindAll("#f60c")[0];
        m.HasAlpha.Should().BeTrue();
        var result = m.Format(new RgbaColor(0x11, 0x00, 0x11, 0x22));
        result.Should().Be("#012" + "1"); // RGB=012、A=0x11→"1"(短縮可)
    }

    [Fact(DisplayName = "rgb(255,0,0)(カンマの後にスペース無し)を選び直してもrgb(0,255,0)のまま(スペース無し)になる")]
    public void rgbのスペース無し記法を保つ()
    {
        var m = ColorLiteralParser.FindAll("rgb(255,0,0)")[0];
        m.Format(new RgbaColor(255, 0, 255, 0)).Should().Be("rgb(0,255,0)");
    }

    [Fact(DisplayName = "rgb(255, 0, 0)(カンマの後にスペースあり)を選び直すとスペースありのまま保たれる")]
    public void rgbのスペースあり記法を保つ()
    {
        var m = ColorLiteralParser.FindAll("rgb(255, 0, 0)")[0];
        m.Format(new RgbaColor(255, 0, 255, 0)).Should().Be("rgb(0, 255, 0)");
    }

    [Fact(DisplayName = "rgba()のアルファは値だけ書き換わり記法(小数)は保たれる")]
    public void rgbaのアルファ記法を保つ()
    {
        var m = ColorLiteralParser.FindAll("rgba(255,0,0,0.5)")[0];
        var result = m.Format(new RgbaColor(255, 0, 0, 0));
        result.Should().StartWith("rgba(0,0,0,");
        result.Should().NotContain("%"); // 元が小数表記なら小数のまま。
    }

    [Fact(DisplayName = "rgb()のスペース/スラッシュ記法・パーセントアルファを保つ")]
    public void rgbのスラッシュ記法とパーセントアルファを保つ()
    {
        var m = ColorLiteralParser.FindAll("rgb(255 0 0 / 50%)")[0];
        var result = m.Format(new RgbaColor(128, 10, 20, 30));
        result.Should().Be("rgb(10 20 30 / 50%)");
    }

    [Fact(DisplayName = "元がrgb()なら書き換え後もrgb()のまま(hsl()やhexにならない)")]
    public void 記法の種類そのものは変わらない()
    {
        var m = ColorLiteralParser.FindAll("rgb(1, 2, 3)")[0];
        var result = m.Format(new RgbaColor(255, 200, 100, 50));
        result.Should().StartWith("rgb(");
    }

    [Fact(DisplayName = "hsl(24, 100%, 50%)は書き換え後もカンマ記法・パーセント記法のまま")]
    public void hslのカンマ記法を保つ()
    {
        var m = ColorLiteralParser.FindAll("hsl(24, 100%, 50%)")[0];
        var result = m.Format(new RgbaColor(255, 0, 0, 255)); // 青
        result.Should().MatchRegex(@"^hsl\(\d+, \d+%, \d+%\)$");
    }

    [Fact(DisplayName = "アルファを持たないrgb()にアルファは追加されない")]
    public void アルファ無しリテラルにアルファは追加されない()
    {
        var m = ColorLiteralParser.FindAll("rgb(1,2,3)")[0];
        m.HasAlpha.Should().BeFalse();
        var result = m.Format(new RgbaColor(128, 9, 9, 9));
        result.Should().Be("rgb(9,9,9)");
    }

    // ========== 往復（検出→整形→再検出） ==========

    [Theory(DisplayName = "整形結果を再度検出しても同じ色として読み取れる(往復の一貫性)")]
    [InlineData("#ff6600")]
    [InlineData("#f60")]
    [InlineData("rgb(255, 102, 0)")]
    [InlineData("rgba(255,102,0,.8)")]
    [InlineData("hsl(24, 100%, 50%)")]
    public void 整形結果の往復が一貫する(string original)
    {
        var m = ColorLiteralParser.FindAll(original)[0];
        var newColor = new RgbaColor(200, 10, 20, 30);
        var rewritten = m.Format(newColor);
        var reparsed = ColorLiteralParser.FindAll(rewritten);
        reparsed.Should().HaveCount(1);
        reparsed[0].Kind.Should().Be(m.Kind);
        if (m.HasAlpha)
        {
            reparsed[0].Color.A.Should().BeCloseTo(newColor.A, 3);
        }
        // hslは度数・パーセントを整数に丸めて書き戻すため、RGBへ再変換すると数値の丸め由来で
        // ±数レベルの誤差が出ることがある(色空間の変換誤差。記法保持のテストではないため許容する)。
        var tolerance = m.Kind == ColorNotationKind.Hsl ? (byte)3 : (byte)0;
        reparsed[0].Color.R.Should().BeCloseTo(newColor.R, tolerance);
        reparsed[0].Color.G.Should().BeCloseTo(newColor.G, tolerance);
        reparsed[0].Color.B.Should().BeCloseTo(newColor.B, tolerance);
    }
}
