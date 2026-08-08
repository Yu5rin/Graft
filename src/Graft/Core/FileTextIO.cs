using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Graft.Core;

/// <summary>
/// テキストファイルの非同期読み書き。6.4節の方針に従い、エンコーディング・BOM・
/// 末尾改行を元ファイルどおり復元する。改行コードそのものの正規化は行わず、
/// 呼び出し側が <see cref="TextShape.NewLine"/> を見て必要に応じて行う。
/// </summary>
public static class FileTextIO
{
    /// <summary>ファイルを読み込み、テキストと見た目（<see cref="TextShape"/>）を返す。</summary>
    public static async Task<GraftResult<(string Text, TextShape Shape)>> ReadAsync(string fullPath, CancellationToken ct = default)
    {
        try
        {
            var ioPath = LongPath.Extended(fullPath);
            var bytes = await File.ReadAllBytesAsync(ioPath, ct).ConfigureAwait(false);
            var shape = EncodingDetector.Detect(bytes);
            var preambleLength = shape.HasBom ? GetBomBytes(shape.Encoding).Length : 0;
            var text = shape.Encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
            return GraftResult<(string, TextShape)>.Ok((text, shape));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return GraftResult<(string, TextShape)>.Fail(ErrorCode.E204, ExceptionMessages.Describe(ex), path: fullPath);
        }
    }

    /// <summary>
    /// Shapeどおりに改行・BOM・末尾改行を復元してファイルへ書き戻す。
    /// 実際の置換処理は <see cref="SafeFileWriter"/> に委譲する。
    /// </summary>
    public static async Task<GraftResult<bool>> WriteAsync(string fullPath, string text, TextShape shape, CancellationToken ct = default)
    {
        var bytes = EncodeWithShape(text, shape);
        return await SafeFileWriter.ReplaceAsync(fullPath, bytes, ct).ConfigureAwait(false);
    }

    /// <summary>テキストのSHA-256ハッシュを16進小文字文字列で返す（先頭に "sha256:" は付けない）。</summary>
    public static string ComputeHash(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>フルハッシュの先頭6文字を短縮表示用に返す。</summary>
    public static string ShortHash(string fullHash)
    {
        if (string.IsNullOrEmpty(fullHash)) return string.Empty;
        return fullHash.Length <= 6 ? fullHash : fullHash[..6];
    }

    private static byte[] EncodeWithShape(string text, TextShape shape)
    {
        var bodyBytes = shape.Encoding.GetBytes(text);
        if (!shape.HasBom) return bodyBytes;

        var bom = GetBomBytes(shape.Encoding);
        if (bom.Length == 0) return bodyBytes;

        var combined = new byte[bom.Length + bodyBytes.Length];
        Buffer.BlockCopy(bom, 0, combined, 0, bom.Length);
        Buffer.BlockCopy(bodyBytes, 0, combined, bom.Length, bodyBytes.Length);
        return combined;
    }

    private static byte[] GetBomBytes(Encoding encoding) => encoding.CodePage switch
    {
        65001 => new byte[] { 0xEF, 0xBB, 0xBF },
        1200 => new byte[] { 0xFF, 0xFE },
        1201 => new byte[] { 0xFE, 0xFF },
        _ => Array.Empty<byte>(),
    };
}
