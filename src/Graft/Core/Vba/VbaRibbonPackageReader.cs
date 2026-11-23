using System.IO.Compression;

namespace Graft.Core.Vba;

/// <summary>
/// 保存済みの .xlsm／.xlam／.xlsb を Open XML パッケージ（zip）として<b>読み取り専用</b>で開き、リボン（customUI）の XML の部品を
/// 取り出す（仕様書_ブックの情報と依頼の観点 15.2）。Excel（COM）には触れないので、Excel が閉じていても、Windows でも Linux でも動く。
/// <para>
/// 【最後に保存した内容を読む】 読むのはディスク上のファイルなので、Excel で開いて編集中の、まだ保存していない変更は含まれない。
/// 呼び出し側は、ファイルの更新日時（<see cref="VbaRibbonReadResult.FileModified"/>）を添えてそのことを利用者に示す。
/// </para>
/// <para>
/// 【Excel が開いていても読める】 <c>FileShare.ReadWrite | FileShare.Delete</c> で開く。Excel はブックを開いている間、他の
/// プログラムからの書き込みを拒むが、読み取りは許す（Explorer のコピーが通るのと同じ）。こちらが「他者の書き込み・削除を許す」と
/// 宣言しておかないと、Excel が既に書き込み権限で開いているときに共有違反になる。<b>この挙動は実機で要確認</b>（付録B）。
/// </para>
/// <para>
/// 【読む部品の探し方】 部品名を決め打ちせず、パッケージの関係の一覧（<c>_rels/.rels</c>）から、関係の種類が
/// <c>http://schemas.microsoft.com/office/2007/relationships/ui/extensibility</c>（新しい形式。通常 <c>customUI/customUI14.xml</c>）または
/// <c>http://schemas.microsoft.com/office/2006/relationships/ui/extensibility</c>（古い形式。通常 <c>customUI/customUI.xml</c>）の
/// ものを探す。両方あれば両方読む。部品名の大文字小文字は区別しない（OPC の規則）。
/// </para>
/// <para>
/// 【zip 爆弾への備え】 展開しながら読み、<see cref="VbaRibbonLimits"/> の上限を超えた時点で読むのをやめる
/// （zip の見出しに書かれた展開後の大きさは、嘘を書けるので信用しない）。読むのは関係の一覧と customUI の部品だけで、
/// それ以外の部品は開かない。
/// </para>
/// </summary>
public static class VbaRibbonPackageReader
{
    /// <summary>新しい形式（Office 2010 以降）の関係の種類。</summary>
    public const string RelationshipTypeNew = "http://schemas.microsoft.com/office/2007/relationships/ui/extensibility";

    /// <summary>古い形式（Office 2007）の関係の種類。</summary>
    public const string RelationshipTypeOld = "http://schemas.microsoft.com/office/2006/relationships/ui/extensibility";

    private const string RelationshipsPart = "_rels/.rels";

    /// <summary>古い形式（OLE の複合ファイル）の先頭 8 バイト。.xls はこれで始まる。</summary>
    private static readonly byte[] OleSignature = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };

    /// <summary>
    /// ブックのファイルからリボンの部品を読む。例外は投げず、結果の種類と理由で返す（収集を止めないため）。
    /// </summary>
    /// <param name="workbookPath">ブックのフルパス。</param>
    /// <param name="limits">読む大きさの上限（null なら既定）。</param>
    public static VbaRibbonReadResult Read(string workbookPath, VbaRibbonLimits? limits = null)
    {
        limits ??= new VbaRibbonLimits();
        if (string.IsNullOrWhiteSpace(workbookPath))
            return Fail(VbaRibbonReadStatus.FileNotFound, "ブックのファイルが指定されていません。");

        var extension = Path.GetExtension(workbookPath);
        if (extension.Equals(".xls", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".xla", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".xlt", StringComparison.OrdinalIgnoreCase))
        {
            return Fail(VbaRibbonReadStatus.LegacyFormat, "古い形式（.xls）のブックは、リボン（customUI）の読み取りの対象外です。");
        }

        FileStream stream;
        try
        {
            stream = new FileStream(
                workbookPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096, FileOptions.None);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException
                                       or ArgumentException or NotSupportedException or PathTooLongException)
        {
            // 【パスそのものが不正なものも「見つかりません」に丸める】 NUL 文字を含むパス（ArgumentException）、
            // 「C:foo:bar」のような書式の不正なパス（NotSupportedException）は、ファイルとして存在しえない。
            // 「例外は投げず結果の種類で返す」という約束を守るため、想定外の問題として外へ漏らさない。
            return Fail(VbaRibbonReadStatus.FileNotFound, "ブックのファイルが見つかりません（移動・削除・名前の変更をしていないか確認してください）。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(VbaRibbonReadStatus.Locked, "ブックのファイルを開けません（他のプログラムが使用中か、読む権限がありません）。");
        }

        using (stream)
        {
            try
            {
                var modified = new DateTimeOffset(File.GetLastWriteTime(workbookPath));
                return ReadFromStream(stream, limits, modified, extension);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Fail(VbaRibbonReadStatus.Locked, "ブックのファイルを読めません（他のプログラムが使用中か、読む権限がありません）。");
            }
        }
    }

    /// <summary>
    /// 開いたストリーム（先頭から読める・シークできるもの）からリボンの部品を読む。テストはメモリ上の zip をここへ渡す。
    /// </summary>
    /// <param name="stream">ブックの内容。</param>
    /// <param name="limits">読む大きさの上限（null なら既定）。</param>
    /// <param name="modified">ファイルの最終更新日時。</param>
    /// <param name="extension">
    /// ファイルの拡張子（<c>.xlsm</c>）。OLE の複合ファイルだったときの文言を分けるために使う（15.2）。
    /// 分からない（null）ときは、従来どおり古い形式（.xls）として扱う。
    /// </param>
    public static VbaRibbonReadResult ReadFromStream(
        Stream stream, VbaRibbonLimits? limits = null, DateTimeOffset? modified = null, string? extension = null)
    {
        limits ??= new VbaRibbonLimits();

        var signature = new byte[OleSignature.Length];
        var read = stream.Read(signature, 0, signature.Length);
        if (read == signature.Length && signature.AsSpan().SequenceEqual(OleSignature))
        {
            // 【拡張子が .xls／.xla／.xlt 以外の OLE は、暗号化されたブック】 パスワードで暗号化した .xlsx／.xlsm／.xlsb は、
            // 中身の zip ごと OLE の複合ファイルに包まれる。拡張子が .xlsm なのに「.xls は対象外」と出ると原因が伝わらないので、
            // 暗号化（または古い形式を別の拡張子にしたもの）と分けて伝える。Read は .xls 系の拡張子をここへ来る前に返すので、
            // 拡張子が分かっていてここへ来るのは、必ず .xls 系以外。
            return extension is not null
                ? Fail(VbaRibbonReadStatus.Encrypted, "パスワードで暗号化されている（または古い形式の）ブックのため読めません。")
                : Fail(VbaRibbonReadStatus.LegacyFormat, "古い形式（.xls）のブックは、リボン（customUI）の読み取りの対象外です。");
        }

        stream.Seek(0, SeekOrigin.Begin);
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException)
        {
            return Fail(VbaRibbonReadStatus.Corrupt, "ブックのファイルを zip として読めません（壊れているか、Excel のブックではありません）。");
        }

        using (archive)
        {
            try
            {
                return ReadArchive(archive, limits, modified);
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException)
            {
                // zip の中身の読み出しで出うる例外（壊れた圧縮・未対応の圧縮方式・不正な部品名）をまとめて「壊れている」にする。
                return Fail(VbaRibbonReadStatus.Corrupt, "ブックのファイルが壊れているため、読めません。");
            }
        }
    }

    private static VbaRibbonReadResult ReadArchive(ZipArchive archive, VbaRibbonLimits limits, DateTimeOffset? modified)
    {
        // 部品名は大文字小文字を区別しない（OPC の規則）。同じ名前（大文字小文字違い）が重なっても例外にせず、先のものを使う。
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            entries.TryAdd(entry.FullName.Replace('\\', '/').TrimStart('/'), entry);
        }

        if (!entries.TryGetValue(RelationshipsPart, out var relsEntry))
        {
            return Fail(VbaRibbonReadStatus.Corrupt, "Excel のブックの構造ではありません（_rels/.rels がありません）。");
        }

        var relsBytes = ReadBounded(relsEntry, limits.MaxRelationshipsBytes, out var relsProblem);
        if (relsBytes is null)
        {
            return Fail(VbaRibbonReadStatus.Corrupt, $"パッケージの関係の一覧（_rels/.rels）を読めません。{relsProblem}");
        }

        var targets = FindRibbonTargets(VbaRibbonXmlAnalyzer.DecodeXml(relsBytes), limits.MaxRelationshipsBytes, out var relsError);
        if (targets is null)
        {
            return Fail(VbaRibbonReadStatus.Corrupt, $"パッケージの関係の一覧（_rels/.rels）を読めません。{relsError}");
        }

        if (targets.Count == 0)
        {
            return new VbaRibbonReadResult(VbaRibbonReadStatus.NoRibbon, null, Array.Empty<VbaRibbonRawPart>(), modified);
        }

        var parts = new List<VbaRibbonRawPart>();
        foreach (var (partName, isNew) in targets)
        {
            if (!entries.TryGetValue(partName, out var entry))
            {
                parts.Add(new VbaRibbonRawPart(partName, isNew, null, "関係はありますが、部品がパッケージの中にありません。"));
                continue;
            }

            var bytes = ReadBounded(entry, limits.MaxPartBytes, out var problem);
            parts.Add(bytes is null
                ? new VbaRibbonRawPart(partName, isNew, null, problem)
                : new VbaRibbonRawPart(partName, isNew, VbaRibbonXmlAnalyzer.DecodeXml(bytes), null));
        }

        if (parts.Any(p => p.IsReadable))
        {
            return new VbaRibbonReadResult(VbaRibbonReadStatus.Ok, null, parts, modified);
        }

        var reasons = string.Join("、", parts.Select(p => $"{p.PartName}: {(p.Problem ?? string.Empty).TrimEnd('。')}"));
        return new VbaRibbonReadResult(VbaRibbonReadStatus.PartsUnreadable, $"部品を読めません（{reasons}）", parts, modified);
    }

    /// <summary>
    /// 関係の一覧（<c>_rels/.rels</c>）から、customUI を指す関係の部品名を集める。XML として読めなければ null（<paramref name="error"/> に理由）。
    /// 外部を指す関係（<c>TargetMode="External"</c>）は、パッケージの中の部品ではないので除く。同じ部品を指す関係は 1 つにまとめる。
    /// </summary>
    private static List<(string PartName, bool IsNewFormat)>? FindRibbonTargets(string relsXml, int maxCharacters, out string? error)
    {
        error = null;
        if (!VbaRibbonXmlAnalyzer.TryParse(relsXml, maxCharacters, out var document, out var problem))
        {
            error = problem;
            return null;
        }

        var result = new List<(string, bool)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in document!.Descendants().Where(e => e.Name.LocalName == "Relationship"))
        {
            var type = (string?)element.Attribute("Type");
            var target = (string?)element.Attribute("Target");
            if (string.IsNullOrWhiteSpace(target)) continue;
            if (string.Equals((string?)element.Attribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase)) continue;

            bool isNew;
            if (string.Equals(type, RelationshipTypeNew, StringComparison.OrdinalIgnoreCase)) isNew = true;
            else if (string.Equals(type, RelationshipTypeOld, StringComparison.OrdinalIgnoreCase)) isNew = false;
            else continue;

            var name = NormalizePartName(target);
            if (name.Length > 0 && seen.Add(name)) result.Add((name, isNew));
        }

        return result;
    }

    /// <summary>
    /// 関係の <c>Target</c> を部品名にする。<c>_rels/.rels</c> はパッケージの根にあるので、相対パスは根からのパス。
    /// 先頭の <c>/</c>・<c>./</c> を除き、<c>%20</c> のような URI のエスケープを戻し、<c>\</c> は <c>/</c> にそろえる。
    /// パッケージの外へ出る <c>..</c> を含むものは空（使わない）にする。
    /// </summary>
    internal static string NormalizePartName(string target)
    {
        string unescaped;
        try
        {
            unescaped = Uri.UnescapeDataString(target.Trim());
        }
        catch (UriFormatException)
        {
            unescaped = target.Trim();
        }

        var segments = unescaped.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => s != ".").ToArray();
        return segments.Contains("..") ? string.Empty : string.Join('/', segments);
    }

    /// <summary>
    /// 部品を、展開しながら最大 <paramref name="maxBytes"/> バイトまで読む。超えたら null（<paramref name="problem"/> に理由）。
    /// 見出しの展開後サイズは先に見るが（明らかに大きいものを開かない早道）、嘘を書けるので、最終的な上限は実際に読んだ量で決める。
    /// </summary>
    private static byte[]? ReadBounded(ZipArchiveEntry entry, int maxBytes, out string? problem)
    {
        problem = null;
        try
        {
            if (entry.Length > maxBytes)
            {
                problem = $"大きすぎるため読みません（上限 {FormatSize(maxBytes)}）。";
                return null;
            }

            using var input = entry.Open();
            using var output = new MemoryStream();
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var n = input.Read(buffer, 0, buffer.Length);
                if (n <= 0) break;
                if (output.Length + n > maxBytes)
                {
                    problem = $"大きすぎるため読みません（上限 {FormatSize(maxBytes)}）。";
                    return null;
                }

                output.Write(buffer, 0, n);
            }

            return output.ToArray();
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException)
        {
            problem = "部品が壊れているため読めません。";
            return null;
        }
    }

    private static string FormatSize(int bytes)
        => bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024.0):0.#} MB" : $"{bytes / 1024.0:0.#} KB";

    private static VbaRibbonReadResult Fail(VbaRibbonReadStatus status, string reason)
        => new(status, reason, Array.Empty<VbaRibbonRawPart>());
}
