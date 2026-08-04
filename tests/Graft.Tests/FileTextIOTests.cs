using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Core;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 仕様書6.4節（ファイル入出力）・6.7節（SafeFileWriter）の単体テスト。
/// UTF-8 BOMあり/なし・Shift_JIS・CRLF/LFの全組み合わせで、読み込み→無変更での書き戻しの
/// バイト列往復一致を検証する。あわせてSafeFileWriter.ReplaceAsyncの安全性も検証する
/// （本パッケージにはSafeFileWriter専用のテストファイルが存在しないため、ここに含める）。
/// </summary>
public class FileTextIOTests
{
    static FileTextIOTests()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private static byte[] Utf8Bytes(string text, bool withBom)
    {
        var body = new UTF8Encoding(false).GetBytes(text);
        if (!withBom) return body;
        return new byte[] { 0xEF, 0xBB, 0xBF }.Concat(body).ToArray();
    }

    private static byte[] ShiftJisBytes(string text) => Encoding.GetEncoding(932).GetBytes(text);

    private static async Task<(string Text, TextShape Shape)> AssertRoundTripAsync(
        TempWorkspace ws, string relativePath, byte[] originalBytes)
    {
        var fullPath = ws.WriteBytes(relativePath, originalBytes);

        var readResult = await FileTextIO.ReadAsync(fullPath);
        readResult.IsSuccess.Should().BeTrue();
        var (text, shape) = readResult.Value;

        var writeResult = await FileTextIO.WriteAsync(fullPath, text, shape);
        writeResult.IsSuccess.Should().BeTrue();

        var roundTripBytes = await File.ReadAllBytesAsync(fullPath);
        roundTripBytes.Should().Equal(originalBytes, "無変更での読み込み→書き戻しはバイト列が完全一致するべき");

        return (text, shape);
    }

    [Fact(DisplayName = "UTF-8・BOM無し・LFの往復でバイト列が完全一致する")]
    public async Task 往復_UTF8_BOM無し_LF()
    {
        using var ws = new TempWorkspace();
        var original = Utf8Bytes("挨拶\nこんにちは、Graftです\n続きの行\n", withBom: false);

        var (_, shape) = await AssertRoundTripAsync(ws, "sample.txt", original);

        shape.HasBom.Should().BeFalse();
        shape.Encoding.CodePage.Should().Be(65001);
        shape.NewLine.Should().Be("\n");
    }

    [Fact(DisplayName = "UTF-8・BOM無し・CRLFの往復でバイト列が完全一致する")]
    public async Task 往復_UTF8_BOM無し_CRLF()
    {
        using var ws = new TempWorkspace();
        var original = Utf8Bytes("挨拶\r\nこんにちは、Graftです\r\n続きの行\r\n", withBom: false);

        var (_, shape) = await AssertRoundTripAsync(ws, "sample.txt", original);

        shape.HasBom.Should().BeFalse();
        shape.NewLine.Should().Be("\r\n");
    }

    [Fact(DisplayName = "UTF-8・BOMあり・LFの往復でバイト列が完全一致する")]
    public async Task 往復_UTF8_BOMあり_LF()
    {
        using var ws = new TempWorkspace();
        var original = Utf8Bytes("挨拶\nこんにちは、Graftです\n続きの行\n", withBom: true);

        var (_, shape) = await AssertRoundTripAsync(ws, "sample.txt", original);

        shape.HasBom.Should().BeTrue();
        shape.NewLine.Should().Be("\n");
    }

    [Fact(DisplayName = "UTF-8・BOMあり・CRLFの往復でバイト列が完全一致する")]
    public async Task 往復_UTF8_BOMあり_CRLF()
    {
        using var ws = new TempWorkspace();
        var original = Utf8Bytes("挨拶\r\nこんにちは、Graftです\r\n続きの行\r\n", withBom: true);

        var (_, shape) = await AssertRoundTripAsync(ws, "sample.txt", original);

        shape.HasBom.Should().BeTrue();
        shape.NewLine.Should().Be("\r\n");
    }

    [Fact(DisplayName = "Shift_JIS・LFの往復でバイト列が完全一致する")]
    public async Task 往復_ShiftJIS_LF()
    {
        using var ws = new TempWorkspace();
        var original = ShiftJisBytes("挨拶\nこんにちは、Graftです\n続きの行\n");

        var (_, shape) = await AssertRoundTripAsync(ws, "sample.txt", original);

        shape.Encoding.CodePage.Should().Be(932);
        shape.HasBom.Should().BeFalse();
        shape.NewLine.Should().Be("\n");
    }

    [Fact(DisplayName = "Shift_JIS・CRLFの往復でバイト列が完全一致する")]
    public async Task 往復_ShiftJIS_CRLF()
    {
        using var ws = new TempWorkspace();
        var original = ShiftJisBytes("挨拶\r\nこんにちは、Graftです\r\n続きの行\r\n");

        var (_, shape) = await AssertRoundTripAsync(ws, "sample.txt", original);

        shape.Encoding.CodePage.Should().Be(932);
        shape.NewLine.Should().Be("\r\n");
    }

    [Fact(DisplayName = "改行混在ファイルの往復でも混在状態のままバイト列が完全一致する")]
    public async Task 往復_改行混在()
    {
        using var ws = new TempWorkspace();
        var original = Utf8Bytes("先頭行\r\n中間の行\n末尾行\r\n", withBom: false);

        var (_, shape) = await AssertRoundTripAsync(ws, "sample.txt", original);

        shape.MixedNewLines.Should().BeTrue();
    }

    [Fact(DisplayName = "末尾改行の有無を往復後も保持する")]
    public async Task 往復_末尾改行なし()
    {
        using var ws = new TempWorkspace();
        var original = Utf8Bytes("末尾に改行のない日本語の内容です", withBom: false);

        var (_, shape) = await AssertRoundTripAsync(ws, "sample.txt", original);

        shape.EndsWithNewLine.Should().BeFalse();
    }

    // ---- SafeFileWriter.ReplaceAsync（本テストファイルにまとめて配置） ----

    [Fact(DisplayName = "ReplaceAsyncは新規ファイルの作成にも成功し内容が正しい")]
    public async Task ReplaceAsyncは新規ファイルを作成できる()
    {
        using var ws = new TempWorkspace();
        var fullPath = Path.Combine(ws.RootPath, "new", "created.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var content = new UTF8Encoding(false).GetBytes("新規作成されたファイルです");

        var result = await SafeFileWriter.ReplaceAsync(fullPath, content);

        result.IsSuccess.Should().BeTrue();
        (await File.ReadAllBytesAsync(fullPath)).Should().Equal(content);
    }

    [Fact(DisplayName = "ReplaceAsyncは既存ファイルを正しい内容へ置換する")]
    public async Task ReplaceAsyncは既存ファイルを置換する()
    {
        using var ws = new TempWorkspace();
        var fullPath = ws.WriteText("target.txt", "旧内容");
        var newContent = new UTF8Encoding(false).GetBytes("新しい内容に置き換わりました");

        var result = await SafeFileWriter.ReplaceAsync(fullPath, newContent);

        result.IsSuccess.Should().BeTrue();
        (await File.ReadAllBytesAsync(fullPath)).Should().Equal(newContent);
    }

    [Fact(DisplayName = "ReplaceAsync成功後は一時ファイルが残らない")]
    public async Task ReplaceAsync成功後に一時ファイルが残らない()
    {
        using var ws = new TempWorkspace();
        var fullPath = ws.WriteText("target.txt", "旧内容");
        var newContent = Encoding.UTF8.GetBytes("新内容");

        var result = await SafeFileWriter.ReplaceAsync(fullPath, newContent);

        result.IsSuccess.Should().BeTrue();
        var directory = Path.GetDirectoryName(fullPath)!;
        var leftovers = Directory.GetFiles(directory).Where(f => Path.GetFileName(f).Contains("graft-tmp"));
        leftovers.Should().BeEmpty("一時ファイルは置換完了後に残ってはならない");
    }

    [Fact(DisplayName = "書き込みが完了する前に中断しても元ファイルの内容は破損しない")]
    public async Task 書き込み中断でも元ファイルは破損しない()
    {
        using var ws = new TempWorkspace();
        var fullPath = ws.WriteText("target.txt", "破損してはいけない元の内容");
        var originalBytes = await File.ReadAllBytesAsync(fullPath);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var newContent = Encoding.UTF8.GetBytes("書き込まれてはいけない新内容");

        // 一時ファイルへの書き込み段階で中断させる。ReplaceAsyncは一時ファイルへ書き出し
        // 終えるまで元ファイルには一切触れない設計であるため、例外発生後も元ファイルは無傷のはず。
        var act = async () => await SafeFileWriter.ReplaceAsync(fullPath, newContent, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        (await File.ReadAllBytesAsync(fullPath)).Should().Equal(originalBytes,
            "書き込み一時ファイルの段階で中断された場合、元ファイルには一切触れていないはず");
    }
}
