using System.Text;
using FluentAssertions;
using Graft.Platform.Linux;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// Linuxクリップボード読み取り不具合の修正（<see cref="X11ClipboardReader"/>）のうち、
/// X11に依存しない純粋ロジック（INCR転送の断片結合・テキストのデコード）を検証する。
/// X11実機が要る部分は <see cref="X11ClipboardReaderIntegrationTests"/> を参照。
/// </summary>
public class X11ClipboardTextTests
{
    [Fact(DisplayName = "JoinChunksは複数の断片を順番どおりに結合する")]
    public void 断片を順番どおりに結合する()
    {
        var chunks = new List<byte[]>
        {
            new byte[] { 1, 2, 3 },
            Array.Empty<byte>(),
            new byte[] { 4, 5 },
        };

        var joined = X11ClipboardReader.JoinChunks(chunks);

        joined.Should().Equal(1, 2, 3, 4, 5);
    }

    [Fact(DisplayName = "JoinChunksは断片が無ければ空配列を返す")]
    public void 断片が無ければ空配列になる()
    {
        X11ClipboardReader.JoinChunks(Array.Empty<byte[]>()).Should().BeEmpty();
    }

    [Fact(DisplayName = "JoinChunksは単一の断片ならそのままの内容になる")]
    public void 単一の断片ならそのままの内容になる()
    {
        var chunks = new List<byte[]> { new byte[] { 9, 8, 7 } };

        X11ClipboardReader.JoinChunks(chunks).Should().Equal(9, 8, 7);
    }

    [Fact(DisplayName = "DecodeTextはUTF8_STRING由来のバイト列をUTF-8としてデコードする")]
    public void UTF8としてデコードする()
    {
        var bytes = Encoding.UTF8.GetBytes("パッチ適用テスト");

        X11ClipboardReader.DecodeText(bytes, isUtf8: true).Should().Be("パッチ適用テスト");
    }

    [Fact(DisplayName = "DecodeTextは空のバイト列を空文字列としてデコードする")]
    public void 空のバイト列は空文字列になる()
    {
        X11ClipboardReader.DecodeText(Array.Empty<byte>(), isUtf8: true).Should().Be(string.Empty);
    }

    [Fact(DisplayName = "DecodeTextはSTRING由来のバイト列をISO-8859-1としてデコードする")]
    public void Latin1としてデコードする()
    {
        // ISO-8859-1（Latin1）では各バイトがそのままUnicodeコードポイント（0x00-0xFF）になる。
        var bytes = new byte[] { 0x41, 0xE9, 0x7A }; // 'A', 'é'（Latin1）, 'z'

        X11ClipboardReader.DecodeText(bytes, isUtf8: false).Should().Be("Aéz");
    }
}
