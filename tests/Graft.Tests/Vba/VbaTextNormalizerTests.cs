using FluentAssertions;
using Graft.Core.Vba;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// <see cref="VbaTextNormalizer"/> のテスト（仕様書_VBA連携 6.3・10.1）。
/// ハッシュが「書いていない差」（改行コード、末尾の改行の数）で変わると、偽の変更・偽の衝突になる。
/// 逆に、意味のある差（行末の空白、空白だけの行）を取りこぼすと本物の変更を見逃す。
/// </summary>
public class VbaTextNormalizerTests
{
    [Fact(DisplayName = "改行は LF・CR・CRLF が混在していても、同じ本文なら同じハッシュになる")]
    public void 改行コードの違いはハッシュに影響しない()
    {
        var crlf = VbaTextNormalizer.Hash("a\r\nb\r\nc");
        VbaTextNormalizer.Hash("a\nb\nc").Should().Be(crlf);
        VbaTextNormalizer.Hash("a\rb\rc").Should().Be(crlf);
        // 1つの本文に3種類の改行が混ざり、末尾も改行で終わっていても同じ。
        VbaTextNormalizer.Hash("a\r\nb\nc\r").Should().Be(crlf, "混在も末尾の改行もハッシュには影響しない");
    }

    [Theory(DisplayName = "末尾の CR/LF はいくつあってもハッシュに影響しない")]
    [InlineData("a")]
    [InlineData("a\r\n")]
    [InlineData("a\n")]
    [InlineData("a\r")]
    [InlineData("a\r\n\r\n\r\n")]
    [InlineData("a\n\n\n\n")]
    [InlineData("a\r\n\n\r")]
    public void 末尾の改行はすべて除かれる(string text)
    {
        VbaTextNormalizer.Hash(text).Should().Be(VbaTextNormalizer.Hash("a"));
    }

    [Fact(DisplayName = "空白だけの行は除かれない（末尾の空白だけの行も別物）")]
    public void 空白だけの行は除かれない()
    {
        VbaTextNormalizer.Hash("a\r\n  ").Should().NotBe(VbaTextNormalizer.Hash("a"));
        VbaTextNormalizer.Hash("a\r\n\r\n\r\nb").Should().NotBe(VbaTextNormalizer.Hash("a\r\nb"));
        VbaTextNormalizer.Hash("a\r\n \r\nb").Should().NotBe(VbaTextNormalizer.Hash("a\r\nb"));
    }

    [Fact(DisplayName = "行末の空白は削除しない（行継続などで意味を持ちうる）")]
    public void 行末の空白は残る()
    {
        VbaTextNormalizer.Hash("x = 1 _\r\n  + 2").Should().NotBe(VbaTextNormalizer.Hash("x = 1_\r\n  + 2"));
        VbaTextNormalizer.Hash("a \r\nb").Should().NotBe(VbaTextNormalizer.Hash("a\r\nb"));
        VbaTextNormalizer.Canonicalize("a \r\nb ").Should().Be("a \r\nb ");
    }

    [Fact(DisplayName = "行頭の空白（インデント）も別物として扱う")]
    public void 行頭の空白は残る()
    {
        VbaTextNormalizer.Hash("  a").Should().NotBe(VbaTextNormalizer.Hash("a"));
    }

    [Fact(DisplayName = "同じ本文は同じハッシュ、違う本文は違うハッシュ。ハッシュは 64 桁の小文字 16 進")]
    public void ハッシュの形式と一致()
    {
        var h = VbaTextNormalizer.Hash("Sub A()\r\nEnd Sub");
        h.Should().MatchRegex("^[0-9a-f]{64}$");
        VbaTextNormalizer.Hash("Sub A()\r\nEnd Sub").Should().Be(h);
        VbaTextNormalizer.Hash("Sub B()\r\nEnd Sub").Should().NotBe(h);
    }

    [Fact(DisplayName = "空の本文のハッシュは、改行だけの本文のハッシュと等しい")]
    public void 空と改行だけは同じ()
    {
        VbaTextNormalizer.Hash(string.Empty).Should().Be(VbaTextNormalizer.Hash("\r\n\r\n"));
    }

    [Fact(DisplayName = "ToCrLf は改行を CRLF に統一し、末尾の改行は残す（Push で VBE に渡す形）")]
    public void ToCrLfは末尾の改行を残す()
    {
        VbaTextNormalizer.ToCrLf("a\nb\rc\r\nd\n").Should().Be("a\r\nb\r\nc\r\nd\r\n");
        VbaTextNormalizer.ToCrLf(string.Empty).Should().BeEmpty();
        VbaTextNormalizer.ToCrLf("\r\r\n\n").Should().Be("\r\n\r\n\r\n");
    }

    [Fact(DisplayName = "ゆるいハッシュ: 行の途中のスペースとタブの違いを無視する（行頭の字下げは残す。詳しくは VbaLooseFormFixTests）")]
    public void ゆるいハッシュは空白を無視する()
    {
        VbaTextNormalizer.LooseHash("x=1").Should().Be(VbaTextNormalizer.LooseHash("x = 1"));
        VbaTextNormalizer.LooseHash("\tIf  a\tThen").Should().Be(VbaTextNormalizer.LooseHash("\tIf a Then"));
        VbaTextNormalizer.LooseHash("a\t=\t1").Should().Be(VbaTextNormalizer.LooseHash("a=1"));
    }

    [Fact(DisplayName = "ゆるいハッシュ: 大文字小文字の違いを無視する")]
    public void ゆるいハッシュは大文字小文字を無視する()
    {
        VbaTextNormalizer.LooseHash("dim x as long").Should().Be(VbaTextNormalizer.LooseHash("Dim x As Long"));
        VbaTextNormalizer.LooseHash("MYVAR = 1").Should().Be(VbaTextNormalizer.LooseHash("myVar = 1"));
    }

    [Fact(DisplayName = "ゆるいハッシュ: 行の増減・文字の違い・改行の位置の違いは別物（改行は除かない）")]
    public void ゆるいハッシュは構造の違いを区別する()
    {
        VbaTextNormalizer.LooseHash("a\r\nb").Should().NotBe(VbaTextNormalizer.LooseHash("ab"));
        VbaTextNormalizer.LooseHash("a\r\nb").Should().NotBe(VbaTextNormalizer.LooseHash("a\r\n\r\nb"));
        VbaTextNormalizer.LooseHash("a = 1").Should().NotBe(VbaTextNormalizer.LooseHash("a = 2"));
    }

    [Fact(DisplayName = "ゆるいハッシュ: 改行コードと末尾の改行の違いも無視する")]
    public void ゆるいハッシュも改行の違いを無視する()
    {
        VbaTextNormalizer.LooseHash("a\nb\n").Should().Be(VbaTextNormalizer.LooseHash("a\r\nb"));
    }

    [Fact(DisplayName = "ゆるいハッシュと厳密なハッシュは別の値（同じ入力でも用途が違う）")]
    public void ゆるいハッシュと厳密なハッシュは別()
    {
        VbaTextNormalizer.LooseHash("abc").Should().NotBe(VbaTextNormalizer.Hash("abc"));
    }

    [Theory(DisplayName = "CountLines: 末尾の空行は数えず、空は 0 行")]
    [InlineData("", 0)]
    [InlineData("\r\n", 0)]
    [InlineData("a", 1)]
    [InlineData("a\r\n", 1)]
    [InlineData("a\nb", 2)]
    [InlineData("a\r\n\r\nb", 3)]
    [InlineData("a\r\nb\r\n\r\n\r\n", 2)]
    public void 行数(string text, int expected)
    {
        VbaTextNormalizer.CountLines(text).Should().Be(expected);
    }
}
