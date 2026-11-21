using System.Security.Cryptography;
using System.Text;

namespace Graft.Core.Vba;

/// <summary>
/// ミラーフォルダの既定の名前と、データ保存先の移行で Root を付け替える計算（仕様書_VBA連携 4.2・4.4）。
/// 純ロジックで、ファイルにも COM にも触れない。
/// </summary>
public static class VbaMirrorFolderName
{
    /// <summary>フォルダ名に使うブック名の最大長。</summary>
    public const int MaxBookNameLength = 40;

    /// <summary>パスのハッシュの桁数（16 進）。</summary>
    public const int HashLength = 8;

    // Windows のファイル名に使えない文字。OS によって結果が変わらないよう固定の一覧を使う。
    private static readonly char[] InvalidChars = { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };

    /// <summary>
    /// 既定のフォルダ名 <c>&lt;ブック名&gt;-&lt;ブックのフルパスの短いハッシュ&gt;</c> を作る。
    /// <para>
    /// ブック名は拡張子を除き、ファイル名に使えない文字を <c>_</c> に置き換え、40 文字までに切る。
    /// 短いハッシュは、フルパスを正規化して小文字にした文字列の SHA-256 の先頭 8 桁（16 進）。
    /// 同名のブックが別の場所にあっても衝突せず、同じブックを再追加すれば同じフォルダになる。
    /// </para>
    /// <para>
    /// 【<c>Path.GetFullPath</c> を使わない理由】 ブックのパスは Excel が返す Windows の絶対パス
    /// （<c>C:\Work\計画.xlsm</c>）で、Linux 上の <c>GetFullPath</c> は作業フォルダを前置してしまい、
    /// 結果が環境で変わる。Excel のパスは既に絶対パスなので、区切りの統一と <c>.</c>／<c>..</c> の解決だけを
    /// 自前で行い、どの OS でも同じフォルダ名になるようにした。
    /// </para>
    /// </summary>
    public static string Build(string workbookFullName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workbookFullName);

        var normalized = NormalizePath(workbookFullName);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))
            .ToLowerInvariant()[..HashLength];
        return SanitizeBookName(workbookFullName) + "-" + hash;
    }

    /// <summary>
    /// ブックのパスの正規化（区切りを <c>\</c> にそろえ、<c>.</c>／<c>..</c> を解決し、小文字にする）。
    /// ハッシュの入力に使う。UNC パスの先頭の <c>\\</c> は保つ。
    /// </summary>
    public static string NormalizePath(string path)
    {
        var s = path.Trim().Replace('/', '\\');
        var prefix = s.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\" : string.Empty;
        var segments = new List<string>();
        foreach (var segment in s[prefix.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..")
            {
                if (segments.Count > 0) segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(segment);
        }

        return (prefix + string.Join('\\', segments)).ToLowerInvariant();
    }

    private static string SanitizeBookName(string workbookFullName)
    {
        var fileName = workbookFullName.Trim().Replace('/', '\\');
        var lastSeparator = fileName.LastIndexOf('\\');
        if (lastSeparator >= 0) fileName = fileName[(lastSeparator + 1)..];

        var dot = fileName.LastIndexOf('.');
        var name = dot > 0 ? fileName[..dot] : fileName;

        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            builder.Append(Array.IndexOf(InvalidChars, c) >= 0 || char.IsControl(c) ? '_' : c);
        }

        var cleaned = builder.ToString();
        if (cleaned.Length > MaxBookNameLength)
        {
            var cut = MaxBookNameLength;
            // サロゲートペアを途中で切らない。
            if (char.IsHighSurrogate(cleaned[cut - 1])) cut--;
            cleaned = cleaned[..cut];
        }

        // Windows のフォルダ名は末尾が空白・ドットだと作れない。
        cleaned = cleaned.TrimEnd(' ', '.');
        return cleaned.Length == 0 ? "book" : cleaned;
    }

    /// <summary>
    /// データ保存先の移行で、ミラーの Root を新しい保存先へ付け替える。
    /// <paramref name="root"/> が旧 <paramref name="oldMirrorsDirectory"/>（旧 <c>VbaMirrorsDirectory</c>）の
    /// 配下にあれば、新 <paramref name="newMirrorsDirectory"/> の同じ相対位置を返す。
    /// 配下でなければ（利用者が自分で選んだ場所のミラー。移行しても動かない）null。
    /// <para>
    /// 付け替えると Root が変わるので、プロジェクトの Id（Root から導く）も変わる。Id の計算と
    /// <c>projects.json</c>・<c>back/</c>・<c>vba-sync/</c> の書き換えは呼び出し側（移行処理）の仕事。
    /// </para>
    /// </summary>
    public static string? RelocateRoot(string root, string oldMirrorsDirectory, string newMirrorsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(oldMirrorsDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(newMirrorsDirectory);

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var oldFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(oldMirrorsDirectory));
        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var oldWithSeparator = oldFull + Path.DirectorySeparatorChar;
        if (!rootFull.StartsWith(oldWithSeparator, comparison)) return null;

        var relative = rootFull[oldWithSeparator.Length..];
        return Path.Combine(Path.TrimEndingDirectorySeparator(Path.GetFullPath(newMirrorsDirectory)), relative);
    }
}
