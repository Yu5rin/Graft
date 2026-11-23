using System.IO.Compression;
using System.Text;

namespace Graft.Core.Vba;

/// <summary>
/// customUI の部品の画像の関係（<c>customUI/_rels/customUI14.xml.rels</c>）の <c>Id</c> の一覧を読む（仕様書_ブックの情報と依頼の観点
/// 16.8 の #13。XML の <c>image="…"</c> が、ブックに実在する画像を指しているかの検査の材料）。
/// <para>
/// 【読み取り（<see cref="VbaRibbonPackageReader"/>）に足さない理由】 読み取りは依頼文と取込のためのもので、画像の関係は使わない。
/// 書き換えの前の検査だけが要るので、ここで小さく読む。読めないときは null を返し、呼び出し側は #13 の検査を省く
/// （読めないことを「画像が無い」と取り違えて、正しい XML に警告を並べない）。
/// </para>
/// </summary>
public static class VbaRibbonImageRelations
{
    /// <summary>
    /// ブックのファイルから、<paramref name="partName"/>（<c>customUI/customUI14.xml</c>）の画像の関係の <c>Id</c> を読む。
    /// 関係のファイルが無ければ空（画像を 1 つも持たない部品）。ブックや関係のファイルが読めなければ null。
    /// </summary>
    public static IReadOnlyCollection<string>? ReadIds(string workbookPath, string partName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workbookPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(partName);
        try
        {
            // 読み取り（15.2）と同じ共有の指定。Excel が開いていても読める（書き換えは閉じてから行うが、検査は先に済ませる）。
            using var stream = new FileStream(workbookPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            return ReadIds(archive, partName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary><see cref="ReadIds(string, string)"/> の中身（zip を開いたあと）。</summary>
    internal static IReadOnlyCollection<string>? ReadIds(ZipArchive archive, string partName)
    {
        var normalized = partName.Replace('\\', '/').TrimStart('/');
        var slash = normalized.LastIndexOf('/');
        var folder = slash < 0 ? string.Empty : normalized[..(slash + 1)];
        var relsName = folder + "_rels/" + normalized[(slash + 1)..] + ".rels";
        var entry = archive.Entries.FirstOrDefault(e => string.Equals(e.FullName.Replace('\\', '/').TrimStart('/'), relsName, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return Array.Empty<string>();
        if (entry.Length > VbaRibbonLimits.DefaultMaxRelationshipsBytes) return null;

        string text;
        using (var input = entry.Open())
        using (var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            text = reader.ReadToEnd();
        }

        // 関係のファイルも外から来たもの。表示と同じ安全な解析（DTD 禁止・外部実体なし）を通す。
        if (!VbaRibbonXmlAnalyzer.TryParse(text, VbaRibbonLimits.DefaultMaxRelationshipsBytes, out var document, out _) || document?.Root is null)
            return null;
        return document.Root.Elements()
            .Where(e => e.Name.LocalName == "Relationship")
            .Select(e => (string?)e.Attribute("Id"))
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!)
            .ToList();
    }
}
