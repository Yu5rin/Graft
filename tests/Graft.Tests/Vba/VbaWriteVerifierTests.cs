using FluentAssertions;
using Graft.Core;
using Graft.Core.Vba;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// <see cref="VbaWriteVerifier"/> のテスト（仕様書_VBA連携 5.5-4・10.1）。
/// <b>しきい値（行数の一致 ＋ スペース・タブ除去と大文字小文字無視）をここで固定する。</b>
/// 緩めると壊れた書き込みを成功と見なし、厳しくすると VBE の整形のたびに失敗扱いになる。
/// </summary>
public class VbaWriteVerifierTests
{
    [Fact(DisplayName = "一致: 同じ本文")]
    public void 同じ本文は一致()
    {
        VbaWriteVerifier.Verify("Sub A()\r\nEnd Sub", "Sub A()\r\nEnd Sub").Should().Be(VbaWriteVerdict.Match);
    }

    [Fact(DisplayName = "一致: 改行コードと末尾の空行の数が違うだけ（VBE は末尾の空行を保持したりしなかったりする）")]
    public void 改行と末尾の空行の違いは一致()
    {
        VbaWriteVerifier.Verify("Sub A()\r\nEnd Sub\r\n", "Sub A()\nEnd Sub").Should().Be(VbaWriteVerdict.Match);
        VbaWriteVerifier.Verify("a\r\n\r\n\r\n", "a").Should().Be(VbaWriteVerdict.Match);
    }

    [Fact(DisplayName = "整形差: スペースの違いだけ（x=1 → x = 1）")]
    public void スペースだけの違いは整形差()
    {
        VbaWriteVerifier.Verify("x=1\r\ny=2", "x = 1\r\ny = 2").Should().Be(VbaWriteVerdict.FormattingDifference);
    }

    [Fact(DisplayName = "整形差: 行の途中のタブとスペースの違いだけ（行頭の字下げは整形差に含めない）")]
    public void タブの違いは整形差()
    {
        VbaWriteVerifier.Verify("If\ta\tThen", "If  a  Then").Should().Be(VbaWriteVerdict.FormattingDifference);
        VbaWriteVerifier.Verify("\tIf a Then", "    If a Then").Should().Be(VbaWriteVerdict.DifferentContent);
    }

    [Fact(DisplayName = "整形差: 大文字小文字の違いだけ（dim → Dim）")]
    public void 大文字小文字だけの違いは整形差()
    {
        VbaWriteVerifier.Verify("dim x as long", "Dim x As Long").Should().Be(VbaWriteVerdict.FormattingDifference);
    }

    [Fact(DisplayName = "整形差: 行末の空白が VBE に削られた")]
    public void 行末の空白の削除は整形差()
    {
        VbaWriteVerifier.Verify("a = 1  \r\nb = 2", "a = 1\r\nb = 2").Should().Be(VbaWriteVerdict.FormattingDifference);
    }

    [Fact(DisplayName = "別内容: 行数が違う（行が増えた）")]
    public void 行が増えたら別内容()
    {
        VbaWriteVerifier.Verify("a\r\nb", "a\r\n\r\nb").Should().Be(VbaWriteVerdict.DifferentContent);
    }

    [Fact(DisplayName = "別内容: 行数が違う（行が減った）")]
    public void 行が減ったら別内容()
    {
        VbaWriteVerifier.Verify("a\r\nb\r\nc", "a\r\nb").Should().Be(VbaWriteVerdict.DifferentContent);
    }

    [Fact(DisplayName = "別内容: 空白除去・大文字小文字無視で見れば同じでも、行の途中で改行が入れば行数が変わる")]
    public void 改行の位置が違えば別内容()
    {
        VbaWriteVerifier.Verify("ab", "a\r\nb").Should().Be(VbaWriteVerdict.DifferentContent);
    }

    [Fact(DisplayName = "別内容: 行数が同じでも文字が違う")]
    public void 文字が違えば別内容()
    {
        VbaWriteVerifier.Verify("x = 1", "x = 2").Should().Be(VbaWriteVerdict.DifferentContent);
        VbaWriteVerifier.Verify("Sub A()", "Sub B()").Should().Be(VbaWriteVerdict.DifferentContent);
    }

    [Fact(DisplayName = "別内容: 書き込んだはずの本文が空で読み戻された（モジュールが空になった）")]
    public void 空で読み戻されたら別内容()
    {
        VbaWriteVerifier.Verify("Sub A()\r\nEnd Sub", string.Empty).Should().Be(VbaWriteVerdict.DifferentContent);
    }

    [Fact(DisplayName = "期待も読み戻しも空なら一致")]
    public void 両方空は一致()
    {
        VbaWriteVerifier.Verify(string.Empty, string.Empty).Should().Be(VbaWriteVerdict.Match);
    }

    [Fact(DisplayName = "ToIssue: 一致と整形差は課題にしない（null）、別内容は E809 のエラー")]
    public void 判定を問題にする()
    {
        VbaWriteVerifier.ToIssue(VbaWriteVerdict.Match, "Module1").Should().BeNull();
        VbaWriteVerifier.ToIssue(VbaWriteVerdict.FormattingDifference, "Module1").Should().BeNull("整形差は E809 の警告にしない");

        var error = VbaWriteVerifier.ToIssue(VbaWriteVerdict.DifferentContent, "Module1");
        error!.Code.Should().Be(ErrorCode.E809);
        error.Severity.Should().Be(Severity.Error);
    }

    [Fact(DisplayName = "FindTrailingExtra: 先頭が完全に一致して末尾に行が増えただけなら、増えた行を返す")]
    public void 末尾の余分な行を見つける()
    {
        var extra = VbaWriteVerifier.FindTrailingExtra("a\r\nb", "a\r\nb\r\n()");
        extra.Should().NotBeNull();
        extra!.KeptLineCount.Should().Be(2);
        extra.ExtraLines.Should().Equal("()");

        VbaWriteVerifier.FindTrailingExtra("a\r\nb", "a\nb\n()\n(2)\n")!.ExtraLines.Should().Equal("()", "(2)");
    }

    [Theory(DisplayName = "FindTrailingExtra: 先頭部分が違う・増えていない・書いた本文が空なら null（取り除かない）")]
    [InlineData("a\r\nb", "a\r\nB\r\n()")] // 途中の行が変わっている（大文字小文字でも許さない）
    [InlineData("a\r\nb", "a \r\nb\r\n()")] // 行末の空白の差も厳密に違う
    [InlineData("a\r\nb", "a\r\nb")] // 増えていない
    [InlineData("a\r\nb", "a\r\nb\r\n\r\n")] // 末尾の空行は増えたうちに入らない
    [InlineData("a\r\nb\r\nc", "a\r\nb")] // 減っている
    [InlineData("", "()")] // 先頭部分が無い
    public void 取り除かない形(string expected, string readBack)
        => VbaWriteVerifier.FindTrailingExtra(expected, readBack).Should().BeNull();

    [Fact(DisplayName = "Describe: 行数・最初に違う行・末尾に増えた行を要約する")]
    public void 差分を要約する()
    {
        var diff = VbaWriteVerifier.Describe("a\r\nb", "a\r\nb\r\n()");
        diff.ExpectedLines.Should().Be(2);
        diff.ActualLines.Should().Be(3);
        diff.FirstDifferentLine.Should().Be(3);
        diff.ExpectedLine.Should().BeNull();
        diff.ActualLine.Should().Be("()");
        diff.ExtraTail.Should().Equal("()");

        var summary = diff.ToSummary();
        summary.Should().Contain("書いた 2 行／読み直した 3 行").And.Contain("3 行目").And.Contain("末尾に増えた行").And.Contain("「()」");
    }

    [Fact(DisplayName = "Describe: 最初に違う行は整形差を飛ばして探し、前後とも長ければ省略する。消えた行も挙げる")]
    public void 整形差を飛ばして最初に違う行を挙げる()
    {
        var longLine = "x = \"" + new string('あ', 100) + "\"";
        var diff = VbaWriteVerifier.Describe("dim a\r\n" + longLine + "\r\nc\r\nd", "Dim a\r\nx = \"zz\"\r\nc");
        diff.FirstDifferentLine.Should().Be(2, "1 行目は大文字小文字だけの違い（整形差）");
        diff.MissingTail.Should().Equal("d");

        var summary = diff.ToSummary();
        summary.Should().Contain("…").And.NotContain(new string('あ', 100)).And.Contain("末尾で消えた行");
    }

    [Fact(DisplayName = "ToIssue: 別内容の詳細に差分の要約が付く")]
    public void 別内容の詳細に要約が付く()
    {
        var diff = VbaWriteVerifier.Describe("a", "a\r\n()");
        var error = VbaWriteVerifier.ToIssue(VbaWriteVerdict.DifferentContent, "Module1", diff)!;
        error.Detail.Should().Contain("Module1").And.Contain("書いた 1 行／読み直した 2 行").And.Contain("「()」");
    }
}
