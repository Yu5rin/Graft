using System;
using System.Linq;
using System.Text;
using FluentAssertions;
using Graft.Core;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 仕様書6.4節（エンコーディング判定）の単体テスト。UTF-8のBOM有無、Shift_JIS、
/// CRLF/LFの組み合わせ、改行混在の検出、判定順（BOM → UTF-8妥当性 → Shift_JIS）を検証する。
/// </summary>
public class EncodingDetectorTests
{
    static EncodingDetectorTests()
    {
        // Shift_JIS（コードページ932）を本テストからも直接使うための登録。
        // EncodingDetector側でも登録されるが、実行順に依存しないようここでも行っておく。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private static byte[] Utf8Bytes(string text, bool withBom)
    {
        var body = new UTF8Encoding(false).GetBytes(text);
        if (!withBom) return body;
        return new byte[] { 0xEF, 0xBB, 0xBF }.Concat(body).ToArray();
    }

    private static byte[] ShiftJisBytes(string text) => Encoding.GetEncoding(932).GetBytes(text);

    [Fact(DisplayName = "UTF-8・BOM無し・LF・日本語を含む本文を正しく判定する")]
    public void UTF8_BOM無し_LF()
    {
        var bytes = Utf8Bytes("こんにちは\nGraftのテストです\n", withBom: false);

        var shape = EncodingDetector.Detect(bytes);

        shape.HasBom.Should().BeFalse();
        shape.Encoding.CodePage.Should().Be(65001);
        shape.NewLine.Should().Be("\n");
        shape.MixedNewLines.Should().BeFalse();
        shape.EndsWithNewLine.Should().BeTrue();
    }

    [Fact(DisplayName = "UTF-8・BOMあり・CRLF・日本語を含む本文を正しく判定する")]
    public void UTF8_BOMあり_CRLF()
    {
        var bytes = Utf8Bytes("こんにちは\r\nGraftのテストです\r\n", withBom: true);

        var shape = EncodingDetector.Detect(bytes);

        shape.HasBom.Should().BeTrue();
        shape.Encoding.CodePage.Should().Be(65001);
        shape.NewLine.Should().Be("\r\n");
        shape.MixedNewLines.Should().BeFalse();
        shape.EndsWithNewLine.Should().BeTrue();
    }

    [Fact(DisplayName = "末尾に改行が無いファイルはEndsWithNewLineがfalseになる")]
    public void 末尾改行なしを判定する()
    {
        var bytes = Utf8Bytes("最終行に改行がありません", withBom: false);

        var shape = EncodingDetector.Detect(bytes);

        shape.EndsWithNewLine.Should().BeFalse();
    }

    [Fact(DisplayName = "Shift_JIS・LF・日本語を含む本文を正しく判定する")]
    public void ShiftJIS_LF()
    {
        var bytes = ShiftJisBytes("日本語のファイルです\nシフトJISで保存します\n");

        var shape = EncodingDetector.Detect(bytes);

        shape.HasBom.Should().BeFalse();
        shape.Encoding.CodePage.Should().Be(932);
        shape.NewLine.Should().Be("\n");
        shape.MixedNewLines.Should().BeFalse();
        shape.EndsWithNewLine.Should().BeTrue();
    }

    [Fact(DisplayName = "Shift_JIS・CRLF・日本語を含む本文を正しく判定する")]
    public void ShiftJIS_CRLF()
    {
        var bytes = ShiftJisBytes("日本語のファイルです\r\nシフトJISで保存します\r\n");

        var shape = EncodingDetector.Detect(bytes);

        shape.HasBom.Should().BeFalse();
        shape.Encoding.CodePage.Should().Be(932);
        shape.NewLine.Should().Be("\r\n");
        shape.MixedNewLines.Should().BeFalse();
    }

    [Fact(DisplayName = "改行コードが混在する場合はMixedNewLinesがtrueになり多数派が優勢と判定される")]
    public void 改行混在を検出する()
    {
        // CRLFが2回、LFが1回。多数派のCRLFが優勢な改行として判定されるべき。
        var text = "line1\r\nline2\nline3\r\n";
        var bytes = Utf8Bytes(text, withBom: false);

        var shape = EncodingDetector.Detect(bytes);

        shape.MixedNewLines.Should().BeTrue();
        shape.NewLine.Should().Be("\r\n");
    }

    [Fact(DisplayName = "CRのみの改行（旧Mac形式）も判定できる")]
    public void CRのみの改行を判定する()
    {
        var bytes = Utf8Bytes("line1\rline2\rline3", withBom: false);

        var shape = EncodingDetector.Detect(bytes);

        shape.NewLine.Should().Be("\r");
        shape.MixedNewLines.Should().BeFalse();
        shape.EndsWithNewLine.Should().BeFalse();
    }

    [Fact(DisplayName = "判定順はBOM優先、次にUTF-8妥当性: ASCIIのみの本文はUTF-8として判定される")]
    public void 判定順_ASCIIのみはUTF8として判定される()
    {
        var bytes = Utf8Bytes("plain ascii text\n", withBom: false);

        var shape = EncodingDetector.Detect(bytes);

        shape.Encoding.CodePage.Should().Be(65001);
        shape.HasBom.Should().BeFalse();
    }
}
