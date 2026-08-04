using System.Text.RegularExpressions;

namespace Graft.Core;

/// <summary>
/// パッチ解析で共通利用する文字列ユーティリティ。仕様書4章の各種規則を担当する。
/// パーサ本体（<see cref="PatchParser"/>）から呼び出される純粋関数のみを置く。
/// </summary>
internal static class PatchTextUtil
{
    private static readonly Regex DriveLetterPattern = new(@"^[A-Za-z]:", RegexOptions.Compiled);

    /// <summary>
    /// 仕様書4.7のパス表記ルールに従い正規化する。区切りを "/" に統一し、
    /// 絶対パス・".." を含むパスを拒否する。実ファイル系の検証（存在確認・
    /// ルート外判定の実挙動）は PathGuard の責務のため、ここでは形の判定のみ行う。
    /// </summary>
    public static bool TryNormalizePath(string raw, out string normalized)
    {
        var trimmed = raw.Trim().Replace('\\', '/');
        normalized = trimmed;
        if (trimmed.Length == 0) return false;
        if (trimmed.StartsWith("/", StringComparison.Ordinal)) return false;
        if (DriveLetterPattern.IsMatch(trimmed)) return false;

        var segments = trimmed.Split('/');
        if (segments.Any(s => s == "..")) return false;

        normalized = trimmed;
        return true;
    }

    /// <summary>
    /// 行がブロックマーカーの形をしているかどうかを判定する。エスケープされていない
    /// マーカーが本文中に出現した場合の破損検出（4.9・E006）に使う。
    /// </summary>
    public static bool LooksLikeMarker(string line)
    {
        var trimmed = line.TrimEnd();
        return trimmed.StartsWith("<<<<", StringComparison.Ordinal)
            || trimmed.StartsWith(">>>>", StringComparison.Ordinal)
            || trimmed == "=======";
    }

    /// <summary>
    /// 行頭のエスケープ（\&lt;&lt;&lt;&lt; / \&gt;&gt;&gt;&gt; / \=======）を1つ取り除く。
    /// 該当しない場合は null を返す。
    /// </summary>
    public static string? TryUnescapeMarkerLine(string line)
    {
        if (line.StartsWith("\\<<<<", StringComparison.Ordinal)) return line[1..];
        if (line.StartsWith("\\>>>>", StringComparison.Ordinal)) return line[1..];
        if (line.StartsWith("\\=======", StringComparison.Ordinal)) return line[1..];
        return null;
    }

    /// <summary>OCCURRENCE 属性の値（"2" や "ALL"）を解釈する。不正な値は既定（Single）とする。</summary>
    public static OccurrenceSpec ParseOccurrence(string value)
    {
        if (string.Equals(value, "ALL", StringComparison.OrdinalIgnoreCase))
            return new OccurrenceSpec { All = true };
        if (int.TryParse(value, out var index) && index >= 1)
            return new OccurrenceSpec { Index = index };
        return OccurrenceSpec.Single;
    }

    /// <summary>
    /// PATCH メタの base 行（"path@hash, path2@hash2"）をパースする。
    /// 実ファイルとの整合確認は行わず、辞書化のみ行う。
    /// </summary>
    public static Dictionary<string, string> ParseBaseHashes(string value)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = entry.Trim();
            if (trimmed.Length == 0) continue;
            var atIdx = trimmed.LastIndexOf('@');
            if (atIdx <= 0 || atIdx == trimmed.Length - 1) continue;
            var path = trimmed[..atIdx].Trim().Replace('\\', '/');
            var hash = trimmed[(atIdx + 1)..].Trim();
            if (path.Length == 0 || hash.Length == 0) continue;
            result[path] = hash;
        }
        return result;
    }

    /// <summary>
    /// 切断検出（4.10）で使う末尾N行を取得する。末尾の空行1つ（末尾改行由来）は除く。
    /// </summary>
    public static IReadOnlyList<string> GetTailLines(string rawText, int count)
    {
        var lines = SplitRawLines(rawText).ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines.Skip(Math.Max(0, lines.Count - count)).ToArray();
    }

    /// <summary>改行コードによらず行配列へ分割する（"\r\n" "\r" "\n" のいずれも許容）。</summary>
    public static string[] SplitRawLines(string text)
        => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
}
