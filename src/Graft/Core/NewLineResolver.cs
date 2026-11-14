using System.IO;

namespace Graft.Core;

/// <summary>
/// 新規ファイル（および改行が1つも無い既存ファイルへの追記）の改行コードを決める。
/// <para>
/// 【なぜ必要か】 以前は常にCRLFで作っていたため、Linux/macOSやLFで統一したプロジェクトで
/// MODE=FULLの新規ファイルだけがCRLFになり、LFのファイル群にCRLFが混ざった
/// （実測: <c>import os\r\nprint(1)\r\n</c>）。
/// </para>
/// 決め方は次の順。
/// <list type="number">
///   <item>設定（<see cref="EncodingSettings.NewFileNewLine"/>）が <c>crlf</c> / <c>lf</c> ならそれ。</item>
///   <item>既定の <c>auto</c> のときは、作るファイルと同じフォルダ、無ければ親フォルダをプロジェクトの
///         ルートまでたどり、既存のテキストファイルで優勢な改行。</item>
///   <item>それも分からなければOSの既定（Windowsなら<c>\r\n</c>、それ以外は<c>\n</c>）。</item>
/// </list>
/// 走査は軽く済ませる。1フォルダにつき最大5つのテキストファイル（先頭64KBだけ読む）、
/// 見に行くファイルの候補は最大40個で打ち切る。フォルダに数万ファイルがあっても、
/// ドライランのたびに全件を開くことはない。
/// </summary>
public static class NewLineResolver
{
    /// <summary>1フォルダで数えるテキストファイルの最大数。</summary>
    private const int MaxFilesPerDirectory = 5;

    /// <summary>1フォルダで中身を見に行く候補の最大数（バイナリ等を読み飛ばす分を含む）。</summary>
    private const int MaxCandidatesPerDirectory = 40;

    /// <summary>1ファイルから読むバイト数。改行コードの傾向は先頭部分で十分に分かる。</summary>
    private const int SampleBytes = 64 * 1024;

    /// <summary>これより大きいファイルは見に行かない（巨大なログ等で時間を使わないため）。</summary>
    private const long MaxFileBytes = 1024 * 1024;

    /// <summary>OSの既定の改行コード。</summary>
    public static string OsDefault => OperatingSystem.IsWindows() ? "\r\n" : "\n";

    /// <summary>
    /// 新規ファイルの改行コードを決める。
    /// </summary>
    /// <param name="setting">設定値（<c>auto</c> / <c>crlf</c> / <c>lf</c>。大文字小文字は問わない）。</param>
    /// <param name="fileFullPath">作る（追記する）ファイルの絶対パス。</param>
    /// <param name="projectRoot">プロジェクトルートの絶対パス。これより上は見ない。</param>
    /// <param name="osDefault">OSの既定。テストで差し替えるための引数。</param>
    public static string Resolve(string? setting, string fileFullPath, string projectRoot, string? osDefault = null)
    {
        var fallback = osDefault ?? OsDefault;
        switch (setting?.Trim().ToLowerInvariant())
        {
            case "crlf": return "\r\n";
            case "lf": return "\n";
        }

        return DetectFromNeighbors(fileFullPath, projectRoot) ?? fallback;
    }

    private static string? DetectFromNeighbors(string fileFullPath, string projectRoot)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
            var dir = Path.GetDirectoryName(Path.GetFullPath(fileFullPath));

            // ルートの外へ出ない。ルートより上のフォルダは、プロジェクトの外の別物の可能性がある。
            while (!string.IsNullOrEmpty(dir) && IsSameOrUnder(dir, root))
            {
                var found = DetectInDirectory(dir);
                if (found is not null) return found;
                if (string.Equals(Path.TrimEndingDirectorySeparator(dir), root, PathComparison)) break;
                dir = Path.GetDirectoryName(dir);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // 走査に失敗しても新規ファイルの作成を止める理由にはならない。OSの既定に任せる。
        }

        return null;
    }

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static bool IsSameOrUnder(string dir, string root)
    {
        var d = Path.TrimEndingDirectorySeparator(dir);
        return string.Equals(d, root, PathComparison)
            || d.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);
    }

    /// <summary>フォルダ内の数ファイルを見て、優勢な改行コードを返す。分からなければnull。</summary>
    private static string? DetectInDirectory(string dir)
    {
        if (!Directory.Exists(dir)) return null;

        int crlf = 0, lf = 0, usable = 0, candidates = 0;
        var buffer = new byte[SampleBytes];
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            if (++candidates > MaxCandidatesPerDirectory || usable >= MaxFilesPerDirectory) break;

            try
            {
                var info = new FileInfo(file);
                if (info.Length == 0 || info.Length > MaxFileBytes) continue;

                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var read = stream.Read(buffer, 0, buffer.Length);
                if (Array.IndexOf(buffer, (byte)0, 0, Math.Min(read, 4096)) >= 0) continue; // バイナリ

                var (c, l) = Count(buffer, read);
                if (c + l == 0) continue; // 改行の無いファイルは手がかりにならない
                crlf += c;
                lf += l;
                usable++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 読めないファイルは読み飛ばす。
            }
        }

        if (crlf == lf) return null; // 手がかりなし、または拮抗。上の階層かOSの既定に任せる。
        return crlf > lf ? "\r\n" : "\n";
    }

    private static (int Crlf, int Lf) Count(byte[] buffer, int length)
    {
        int crlf = 0, lf = 0;
        for (var i = 0; i < length; i++)
        {
            if (buffer[i] != (byte)'\n') continue;
            if (i > 0 && buffer[i - 1] == (byte)'\r') crlf++;
            else lf++;
        }

        return (crlf, lf);
    }
}
