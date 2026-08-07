using System.Text;
using FluentAssertions;
using Graft.Platform.Linux;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// Linuxクリップボード書き込み不具合の修正（<see cref="X11ClipboardWriter"/>）のうち、
/// X11に依存しない純粋ロジック（STRINGターゲット用のLatin-1変換）を検証する。
/// X11実機が要る部分は <see cref="X11ClipboardWriterIntegrationTests"/> を参照。
/// </summary>
public class X11ClipboardWriterTextTests
{
    [Fact(DisplayName = "ToLatin1はLatin-1で表現できる文字をそのままバイトへ変換する")]
    public void Latin1で表現できる文字はそのまま変換する()
    {
        // 'A'(0x41), 'é'(0xE9), 'z'(0x7A) はいずれもLatin-1の範囲内。
        var text = "Aéz";

        X11ClipboardWriter.ToLatin1(text).Should().Equal(0x41, 0xE9, 0x7A);
    }

    [Fact(DisplayName = "ToLatin1はLatin-1で表現できない文字を'?'に置き換える")]
    public void 表現できない文字は疑問符になる()
    {
        var text = "A日z"; // '日'(U+65E5) はLatin-1の範囲外。

        var bytes = X11ClipboardWriter.ToLatin1(text);

        bytes.Should().Equal((byte)'A', (byte)'?', (byte)'z');
    }

    [Fact(DisplayName = "ToLatin1は空文字列を空配列に変換する")]
    public void 空文字列は空配列になる()
    {
        X11ClipboardWriter.ToLatin1(string.Empty).Should().BeEmpty();
    }

    [Fact(DisplayName = "ToLatin1は結果の長さが元の文字列長と一致する（サロゲートペアも1文字=1バイトとして'?'になる）")]
    public void 長さは元の文字列と一致する()
    {
        // 絵文字（サロゲートペア、U+1F600）は2つのcharからなり、いずれもLatin-1範囲外のため
        // それぞれが'?'になる（1文字の絵文字が2バイトの'??'として現れる）。
        var text = "A😀z";

        var bytes = X11ClipboardWriter.ToLatin1(text);

        bytes.Length.Should().Be(text.Length);
        bytes.Should().Equal((byte)'A', (byte)'?', (byte)'?', (byte)'z');
    }

    [Fact(DisplayName = "ToLatin1の結果はEncoding.Latin1でのデコードと整合する（往復確認）")]
    public void Latin1デコードと往復で一致する()
    {
        // Latin-1の範囲内の文字のみで構成する（範囲外の文字が混じると'?'に化けて往復しなくなるため）。
        var text = "Aéz Bñy Cüx";

        var bytes = X11ClipboardWriter.ToLatin1(text);
        var decoded = Encoding.Latin1.GetString(bytes);

        decoded.Should().Be(text);
    }
}
