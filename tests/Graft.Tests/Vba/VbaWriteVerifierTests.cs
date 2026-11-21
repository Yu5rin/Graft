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

    [Fact(DisplayName = "整形差: タブとスペースの違いだけ")]
    public void タブの違いは整形差()
    {
        VbaWriteVerifier.Verify("\tIf a Then", "    If a Then").Should().Be(VbaWriteVerdict.FormattingDifference);
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

    [Fact(DisplayName = "ToIssue: 一致は null、整形差は E809 の警告、別内容は E809 のエラー")]
    public void 判定を問題にする()
    {
        VbaWriteVerifier.ToIssue(VbaWriteVerdict.Match, "Module1").Should().BeNull();

        var warning = VbaWriteVerifier.ToIssue(VbaWriteVerdict.FormattingDifference, "Module1");
        warning!.Code.Should().Be(ErrorCode.E809);
        warning.Severity.Should().Be(Severity.Warning);
        warning.Detail.Should().Contain("Module1").And.Contain("整形");

        var error = VbaWriteVerifier.ToIssue(VbaWriteVerdict.DifferentContent, "Module1");
        error!.Code.Should().Be(ErrorCode.E809);
        error.Severity.Should().Be(Severity.Error);
    }
}
