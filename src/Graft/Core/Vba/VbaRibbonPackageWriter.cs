using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Graft.Core.Vba;

/// <summary>ブックへ書く customUI の XML 1 つ（形式ごとに 1 件まで）。</summary>
/// <param name="Format">形式（ミラーのファイル名で決まる）。</param>
/// <param name="Xml">書く本文（ミラーのファイルの本文そのまま。<see cref="VbaRibbonXmlValidator"/> を通ったもの）。</param>
public sealed record VbaRibbonWriteRequest(VbaRibbonFormat Format, string Xml);

/// <summary>書いた部品 1 つ。</summary>
/// <param name="Format">形式。</param>
/// <param name="PartName">書いた部品名（先頭の <c>/</c> なし）。同期状態の <c>partName</c> はこの値に直す（16.7）。</param>
/// <param name="AddedRelationship">関係を新しく足したか（customUI の無かったブックへの追加）。</param>
public sealed record VbaRibbonWrittenPart(VbaRibbonFormat Format, string PartName, bool AddedRelationship);

/// <summary>書き換えの結果の種類。</summary>
public enum VbaRibbonWriteStatus
{
    /// <summary>書けた（照合まで済んだ）。</summary>
    Ok,

    /// <summary>古い形式（.xls／.xla／.xlt）。対象外。</summary>
    LegacyFormat,

    /// <summary>パスワードで暗号化されている（OLE の複合ファイル）。対象外。</summary>
    Encrypted,

    /// <summary>パッケージに電子署名（<c>_xmlsignatures/</c>）がある。書き換えると署名が無効になるので対象外。</summary>
    Signed,

    /// <summary>zip として読めない・パッケージの構造が不正・同じ名前の部品が重なっている・扱えない圧縮方式や文字コード。</summary>
    Corrupt,

    /// <summary>関係の <c>Target</c> がパッケージの外を指している（<c>..</c>）。</summary>
    InvalidTarget,

    /// <summary>展開後の合計が上限を超えた（zip 爆弾への備え）。</summary>
    TooLarge,

    /// <summary>書いたものを読み直したら、期待と違った（16.5 の段階 10）。元のファイルは変えていない。</summary>
    VerificationFailed,

    /// <summary>
    /// 元のファイルが、読んでから置き換えるまでのあいだに変わった（大きさ・更新日時）。誰かが書いた内容を上書きしないよう止めた。
    /// </summary>
    SourceChanged,

    /// <summary>ファイルの読み書き・置き換えに失敗した（使用中・権限・ディスクの空き）。元のファイルは変えていない。</summary>
    IoFailed,
}

/// <summary>書き換えの結果。</summary>
/// <param name="Status">結果の種類。</param>
/// <param name="Problem">失敗の理由 1 行（成功のときは null）。</param>
/// <param name="Parts">書いた部品（成功のとき）。</param>
/// <param name="RelationshipsChanged"><c>_rels/.rels</c> を書き換えたか（新規の追加のときだけ）。</param>
/// <param name="ContentTypesChanged"><c>[Content_Types].xml</c> を書き換えたか（新規の追加で、<c>xml</c> の既定が無いときだけ）。</param>
public sealed record VbaRibbonWriteResult(
    VbaRibbonWriteStatus Status,
    string? Problem,
    IReadOnlyList<VbaRibbonWrittenPart> Parts,
    bool RelationshipsChanged = false,
    bool ContentTypesChanged = false)
{
    /// <summary>成功したか。</summary>
    public bool Succeeded => Status == VbaRibbonWriteStatus.Ok;

    internal static VbaRibbonWriteResult Fail(VbaRibbonWriteStatus status, string problem)
        => new(status, problem, Array.Empty<VbaRibbonWrittenPart>());
}

/// <summary>
/// ブックのファイル（Open XML パッケージ＝zip）の customUI の部品を差し替える・足す純ロジック（仕様書_ブックの情報と依頼の観点 16.7）。
/// COM には触れない（Excel を閉じてから呼ぶのは呼び出し側の役目）。ストリームどうしの <see cref="Write"/> と、照合の
/// <see cref="Verify"/> はファイルにも触れず、<c>Graft.Tests</c> で網羅する。ファイルを原子的に置き換える <see cref="RewriteFile"/> も
/// ここに置く（一時ファイル → 照合 → 置き換え）。
/// <para>
/// 【新しい zip を作って 1 項目ずつ写す】 <see cref="ZipArchiveMode.Update"/> は元のファイルをその場で書き換える作りで、途中で落ちると
/// ブックが壊れる。元の zip を読み取り専用で開き、新しい zip（<see cref="ZipArchiveMode.Create"/>）へ、元の順序のまま 1 項目ずつ
/// 展開して書き直す。保つのは項目の名前（大文字小文字も）・順序・更新日時・<b>展開した中身（1 バイトも変えない）</b>。圧縮後のバイト列と
/// zip の細かな見出しは保たない（.NET の Deflate で圧縮し直す）。Excel は中身しか見ない見込み（<b>実機で要確認</b>。付録B・実機検証 1）。
/// 触っていない項目が同じ中身であることは、<see cref="Verify"/> が全項目の大きさと CRC-32 で確かめる。
/// </para>
/// <para>
/// 【<c>_rels/.rels</c> と <c>[Content_Types].xml</c> は、新規に足すときだけ触る】 既に関係がある形式は、関係の指す部品の中身だけを
/// 差し替え、この 2 つは 1 バイトも変えない。足すときも、ファイル全体を書き直さず、ルート要素の閉じタグの直前に 1 行を差し込む
/// （既存の関係の順序・属性・空白をそのまま残すため。XML として組み直すと、宣言や属性の引用符などの見た目が変わる）。
/// </para>
/// </summary>
public static class VbaRibbonPackageWriter
{
    /// <summary>展開後の合計の上限の既定（2 GB）。大きなブックでも数百 MB なので余裕があり、zip 爆弾は止められる。</summary>
    public const long DefaultMaxTotalExpandedBytes = 2L * 1024 * 1024 * 1024;

    private const string RelationshipsPart = "_rels/.rels";
    private const string ContentTypesPart = "[Content_Types].xml";
    private const string SignaturesFolder = "_xmlsignatures/";
    private const string XmlContentType = "application/xml";

    private static readonly byte[] OleSignature = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };

    private static readonly Regex DeclaredEncoding = new(
        "^\\s*<\\?xml[^>]*?encoding\\s*=\\s*[\"']([^\"']+)[\"']",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 書き換えの前の予行（16.5 の段階 3）。zip の項目を列挙でき、<c>_rels/.rels</c> と <c>[Content_Types].xml</c> を解釈でき、
    /// 暗号化されておらず、電子署名が無く、すべての項目を開ける（扱えない圧縮方式が無い）ことを確かめる。中身は各項目の先頭しか読まない。
    /// </summary>
    public static VbaRibbonWriteResult Inspect(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        try
        {
            if (IsOle(source))
                return VbaRibbonWriteResult.Fail(VbaRibbonWriteStatus.Encrypted, "パスワードで暗号化されている（または古い形式の）ブックのため、書き換えられません。");
            using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
            var problem = CheckPackage(archive, out var status, out _);
            if (problem is not null) return VbaRibbonWriteResult.Fail(status, problem);

            var buffer = new byte[1];
            foreach (var entry in archive.Entries)
            {
                if (IsDirectory(entry)) continue;
                using var s = entry.Open();
                _ = s.Read(buffer, 0, 1);
            }

            return new VbaRibbonWriteResult(VbaRibbonWriteStatus.Ok, null, Array.Empty<VbaRibbonWrittenPart>());
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException)
        {
            return VbaRibbonWriteResult.Fail(VbaRibbonWriteStatus.Corrupt, "ブックのファイルを zip として読めません（壊れているか、扱えない圧縮方式の部品があります）。");
        }
    }

    /// <summary>
    /// <paramref name="source"/>（元のブック）を読み、customUI の部品を差し替えた（または足した）新しいブックを
    /// <paramref name="destination"/> へ書く。<paramref name="source"/> には一切書かない。失敗したとき <paramref name="destination"/> の
    /// 中身は不定（呼び出し側が捨てる）。例外は投げず、結果の種類で返す（ストリームそのものの入出力の例外は除く）。
    /// </summary>
    /// <param name="source">元のブック（先頭から読める・シークできるもの）。</param>
    /// <param name="destination">
    /// 書き先（空で、シークできるもの）。シークできないと、zip の各項目の後ろにデータ記述子が付き、Office の受け入れが変わりうるので受け付けない。
    /// </param>
    /// <param name="requests">形式ごとの本文（1〜2 件。同じ形式を 2 つ渡さない）。</param>
    /// <param name="maxTotalExpandedBytes">展開後の合計の上限（0 以下なら既定）。</param>
    public static VbaRibbonWriteResult Write(
        Stream source, Stream destination, IReadOnlyList<VbaRibbonWriteRequest> requests, long maxTotalExpandedBytes = 0)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(requests);
        if (!destination.CanSeek || !destination.CanWrite)
            throw new ArgumentException("書き先はシークできる書き込み可能なストリームにしてください。", nameof(destination));
        if (requests.Count == 0) throw new ArgumentException("書く本文がありません。", nameof(requests));
        if (requests.Select(r => r.Format).Distinct().Count() != requests.Count)
            throw new ArgumentException("同じ形式の本文が 2 つあります。", nameof(requests));
        var limit = maxTotalExpandedBytes > 0 ? maxTotalExpandedBytes : DefaultMaxTotalExpandedBytes;

        if (IsOle(source))
            return VbaRibbonWriteResult.Fail(VbaRibbonWriteStatus.Encrypted, "パスワードで暗号化されている（または古い形式の）ブックのため、書き換えられません。");

        ZipArchive input;
        try
        {
            input = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException)
        {
            return VbaRibbonWriteResult.Fail(VbaRibbonWriteStatus.Corrupt, "ブックのファイルを zip として読めません（壊れているか、Excel のブックではありません）。");
        }

        using (input)
        {
            try
            {
                return WriteArchive(input, destination, requests, limit);
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
            {
                return VbaRibbonWriteResult.Fail(VbaRibbonWriteStatus.Corrupt, "ブックのファイルが壊れているか、扱えない圧縮方式の部品があるため、書き換えられません。");
            }
            catch (TooLargeException)
            {
                return VbaRibbonWriteResult.Fail(VbaRibbonWriteStatus.TooLarge,
                    $"ブックの中身が大きすぎるため、書き換えません（展開後の合計の上限 {limit / (1024 * 1024)} MB）。");
            }
        }
    }

    private static VbaRibbonWriteResult WriteArchive(
        ZipArchive input, Stream destination, IReadOnlyList<VbaRibbonWriteRequest> requests, long limit)
    {
        var problem = CheckPackage(input, out var status, out var byName);
        if (problem is not null) return VbaRibbonWriteResult.Fail(status, problem);

        var relsEntry = byName[RelationshipsPart];
        var relsBytes = ReadAll(relsEntry, VbaRibbonLimits.DefaultMaxRelationshipsBytes);
        if (relsBytes is null)
            return VbaRibbonWriteResult.Fail(VbaRibbonWriteStatus.Corrupt, "パッケージの関係の一覧（_rels/.rels）が大きすぎるか、読めません。");
        if (!TryDecode(relsBytes, out var relsText, out var relsEncoding)
            || !VbaRibbonXmlAnalyzer.TryParse(relsText, VbaRibbonLimits.DefaultMaxRelationshipsBytes, out var relsDoc, out _)
            || relsDoc!.Root is not { } relsRoot || relsRoot.Name.LocalName != "Relationships")
        {
            return VbaRibbonWriteResult.Fail(VbaRibbonWriteStatus.Corrupt, "パッケージの関係の一覧（_rels/.rels）を読めません。");
        }

        // 1. 形式ごとに、書く部品名を決める。関係があればその Target が指す部品（中身だけ差し替える）、無ければ既定の名前で足す。
        var targets = new List<(VbaRibbonWriteRequest Request, string PartName, bool AddRelationship)>();
        var newRelationships = new List<(string Type, string Target)>();
        foreach (var request in requests)
        {
            var type = VbaRibbonMirrorLayout.RelationshipType(request.Format);
            var existing = relsRoot.Elements()
                .Where(e => e.Name.LocalName == "Relationship")
                .FirstOrDefault(e => string.Equals((string?)e.Attribute("Type"), type, StringComparison.OrdinalIgnoreCase)
                                     && !string.Equals((string?)e.Attribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase)
                                     && !string.IsNullOrWhiteSpace((string?)e.Attribute("Target")));
            if (existing is not null)
            {
                // 読み取り（15.2）と同じ直し方で部品名にする。パッケージの外を指すものは、書く先として使わない（16.12）。
                var partName = VbaRibbonPackageReader.NormalizePartName((string)existing.Attribute("Target")!);
                if (partName.Length == 0)
                {
                    return VbaRibbonWriteResult.Fail(VbaRibbonWriteStatus.InvalidTarget,
                        "リボンの関係が、パッケージの外を指しています。安全のため書き換えません。");
                }

                targets.Add((request, partName, false));
            }
            else
            {
                var partName = VbaRibbonMirrorLayout.DefaultPartName(request.Format);
                targets.Add((request, partName, true));
                newRelationships.Add((type, partName));
            }
        }

        if (targets.Select(t => t.PartName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != targets.Count)
        {
            return VbaRibbonWriteResult.Fail(VbaRibbonWriteStatus.Corrupt,
                "新旧 2 つのリボンの関係が、同じ部品を指しています。どちらへ書くか決められないため、書き換えません。");
        }

        // 2. 新規に足すときだけ、_rels/.rels と [Content_Types].xml を直す（差し込むだけ）。
        byte[]? newRels = null;
        byte[]? newContentTypes = null;
        if (newRelationships.Count > 0)
        {
            var usedIds = new HashSet<string>(
                relsRoot.Elements().Select(e => (string?)e.Attribute("Id")).Where(id => id is not null)!, StringComparer.OrdinalIgnoreCase);
            var prefix = relsRoot.GetPrefixOfNamespace(relsRoot.Name.Namespace);
            var tag = prefix is null ? "Relationship" : prefix + ":Relationship";
            var added = new StringBuilder();
            var n = 1;
            foreach (var (type, target) in newRelationships)
            {
                while (usedIds.Contains("rIdGraft" + n)) n++;
                var id = "rIdGraft" + n;
                usedIds.Add(id);
                added.Append('<').Append(tag).Append(" Id=\"").Append(id).Append("\" Type=\"").Append(type)
                    .Append("\" Target=\"").Append(target).Append("\"/>");
            }

            var relsInserted = InsertBeforeRootEnd(relsText, "Relationships", relsRoot, added.ToString());
            if (relsInserted is null)
                return VbaRibbonWriteResult.Fail(VbaRibbonWriteStatus.Corrupt, "パッケージの関係の一覧（_rels/.rels）に関係を足せません。");
            newRels = Encode(relsInserted, relsEncoding);

            var ctEntry = byName[ContentTypesPart];
            var ctBytes = ReadAll(ctEntry, VbaRibbonLimits.DefaultMaxRelationshipsBytes);
            if (ctBytes is null || !TryDecode(ctBytes, out var ctText, out var ctEncoding)
                || !VbaRibbonXmlAnalyzer.TryParse(ctText, VbaRibbonLimits.DefaultMaxRelationshipsBytes, out var ctDoc, out _)
                || ctDoc!.Root is not { } ctRoot || ctRoot.Name.LocalName != "Types")
            {
                return VbaRibbonWriteResult.Fail(VbaRibbonWriteStatus.Corrupt, "パッケージのコンテンツの種類（[Content_Types].xml）を読めません。");
            }

            var overrides = new StringBuilder();
            var ctPrefix = ctRoot.GetPrefixOfNamespace(ctRoot.Name.Namespace);
            var overrideTag = ctPrefix is null ? "Override" : ctPrefix + ":Override";
            var xmlDefault = ctRoot.Elements().FirstOrDefault(e => e.Name.LocalName == "Default"
                && string.Equals((string?)e.Attribute("Extension"), "xml", StringComparison.OrdinalIgnoreCase));
            var defaultIsXml = string.Equals((string?)xmlDefault?.Attribute("ContentType"), XmlContentType, StringComparison.OrdinalIgnoreCase);
            foreach (var (_, partName, add) in targets)
            {
                if (!add) continue;
                var existingOverride = ctRoot.Elements().FirstOrDefault(e => e.Name.LocalName == "Override"
                    && string.Equals(((string?)e.Attribute("PartName"))?.TrimStart('/'), partName, StringComparison.OrdinalIgnoreCase));
                if (existingOverride is not null)
                {
                    // 関係の無い残骸に Override だけが残っている。種類が application/xml なら、そのまま使える。違う種類を書き換える
                    // 手当ては作らない（Excel が書くブックでは起きない。直すと既存の行を書き換えることになり、16.7 の「差し込むだけ」を崩す）。
                    if (string.Equals((string?)existingOverride.Attribute("ContentType"), XmlContentType, StringComparison.OrdinalIgnoreCase)) continue;
                    return VbaRibbonWriteResult.Fail(VbaRibbonWriteStatus.Corrupt,
                        $"[Content_Types].xml に /{partName} の種類が別の値で書かれているため、書き換えません。");
                }

                // Excel が書くブックには <Default Extension="xml" ContentType="application/xml"/> が通常あり、そのときは足さない。
                // 無いか別の種類のときだけ、その部品の Override を足す（.xlsb でも同じでよいかは実機で要確認。付録B）。
                if (defaultIsXml) continue;
                overrides.Append('<').Append(overrideTag).Append(" PartName=\"/").Append(partName)
                    .Append("\" ContentType=\"").Append(XmlContentType).Append("\"/>");
            }

            if (overrides.Length > 0)
            {
                var ctInserted = InsertBeforeRootEnd(ctText, "Types", ctRoot, overrides.ToString());
                if (ctInserted is null)
                    return VbaRibbonWriteResult.Fail(VbaRibbonWriteStatus.Corrupt, "パッケージのコンテンツの種類（[Content_Types].xml）に種類を足せません。");
                newContentTypes = Encode(ctInserted, ctEncoding);
            }
        }

        // 3. 写す。元の順序のまま 1 項目ずつ。差し替える項目は中身だけを入れ替え、足す部品は最後に置く。
        var replacements = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (request, partName, _) in targets) replacements[partName] = BodyBytes(request.Xml);
        if (newRels is not null) replacements[RelationshipsPart] = newRels;
        if (newContentTypes is not null) replacements[ContentTypesPart] = newContentTypes;

        long total = 0;
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var output = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in input.Entries)
            {
                var name = entry.FullName;
                var key = Key(name);
                // 元が無圧縮（展開前後の大きさが同じ）の項目は無圧縮のまま。Excel が無圧縮で置く部品（画像など）を、
                // わざわざ圧縮し直して性質を変えない。
                var level = entry.CompressedLength == entry.Length ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
                var copy = output.CreateEntry(name, level);
                copy.LastWriteTime = entry.LastWriteTime;
                copy.ExternalAttributes = entry.ExternalAttributes;
                if (IsDirectory(entry)) continue;

                using var target = copy.Open();
                if (replacements.TryGetValue(key, out var body))
                {
                    target.Write(body, 0, body.Length);
                    total += body.Length;
                    written.Add(key);
                }
                else
                {
                    using var from = entry.Open();
                    total = CopyBounded(from, target, total, limit);
                }

                if (total > limit) throw new TooLargeException();
            }

            foreach (var (request, partName, _) in targets)
            {
                if (written.Contains(partName)) continue;
                // 関係はあるのに部品が無かった（壊れかけのブック）・新規の追加。どちらも最後に足す。
                var copy = output.CreateEntry(partName, CompressionLevel.Optimal);
                copy.LastWriteTime = DateTimeOffset.Now;
                using var target = copy.Open();
                var body = replacements[partName];
                target.Write(body, 0, body.Length);
            }
        }

        destination.Flush();
        var parts = targets.Select(t => new VbaRibbonWrittenPart(t.Request.Format, t.PartName, t.AddRelationship)).ToArray();
        return new VbaRibbonWriteResult(VbaRibbonWriteStatus.Ok, null, parts, newRels is not null, newContentTypes is not null);
    }

    /// <summary>
    /// 書いたものを読み直して照合する（16.5 の段階 10）。(a) 触っていない項目の名前・順序・大きさ・CRC-32 が元と同じ、
    /// (b) customUI の部品の本文が書いた本文と同じ、(c) <c>[Content_Types].xml</c> と <c>_rels/.rels</c> が読め、
    /// 読み取り（<see cref="VbaRibbonPackageReader"/>）で同じ本文が読める（往復）。合わなければ理由を返す（合えば null）。
    /// </summary>
    public static string? Verify(Stream source, Stream written, IReadOnlyList<VbaRibbonWriteRequest> requests, VbaRibbonWriteResult result)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(written);
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Succeeded) return "書き換えが成功していません。";

        try
        {
            source.Seek(0, SeekOrigin.Begin);
            written.Seek(0, SeekOrigin.Begin);
            using var a = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
            using var b = new ZipArchive(written, ZipArchiveMode.Read, leaveOpen: true);

            var touched = new HashSet<string>(result.Parts.Select(p => p.PartName), StringComparer.OrdinalIgnoreCase);
            if (result.RelationshipsChanged) touched.Add(RelationshipsPart);
            if (result.ContentTypesChanged) touched.Add(ContentTypesPart);

            var before = a.Entries.ToList();
            var after = b.Entries.ToList();
            if (after.Count < before.Count) return "書いたブックの部品が、元より少なくなっています。";
            for (var i = 0; i < before.Count; i++)
            {
                if (!string.Equals(before[i].FullName, after[i].FullName, StringComparison.Ordinal))
                    return $"部品の名前か順序が元と違います（{i + 1} 番目: {VbaRibbonText.OneLine(before[i].FullName)}）。";
                if (touched.Contains(Key(before[i].FullName))) continue;
                if (before[i].Length != after[i].Length || before[i].Crc32 != after[i].Crc32)
                    return $"触っていない部品の中身が元と違います（{VbaRibbonText.OneLine(before[i].FullName)}）。";
            }

            // 元より多いのは、足した customUI の部品だけのはず。
            foreach (var extra in after.Skip(before.Count))
            {
                if (!result.Parts.Any(p => string.Equals(p.PartName, extra.FullName, StringComparison.OrdinalIgnoreCase)))
                    return $"予定に無い部品が増えています（{VbaRibbonText.OneLine(extra.FullName)}）。";
            }

            var byName = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in after) byName.TryAdd(Key(e.FullName), e);
            foreach (var part in result.Parts)
            {
                var request = requests.First(r => r.Format == part.Format);
                if (!byName.TryGetValue(part.PartName, out var entry)) return $"書いたはずの部品がありません（{part.PartName}）。";
                var bytes = ReadAll(entry, int.MaxValue);
                if (bytes is null || !bytes.AsSpan().SequenceEqual(BodyBytes(request.Xml)))
                    return $"書いた部品の中身が、ミラーの本文と違います（{part.PartName}）。";
            }

            if (!byName.TryGetValue(ContentTypesPart, out var ct) || ReadAll(ct, VbaRibbonLimits.DefaultMaxRelationshipsBytes) is not { } ctBytes
                || !TryDecode(ctBytes, out var ctText, out _)
                || !VbaRibbonXmlAnalyzer.TryParse(ctText, VbaRibbonLimits.DefaultMaxRelationshipsBytes, out _, out _))
            {
                return "書いたブックの [Content_Types].xml を読めません。";
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException or ArgumentException)
        {
            return "書いたブックを zip として読み直せません。";
        }

        // 往復: 読み取りの部品が、書いた本文と同じに読めること（_rels/.rels が解釈でき、関係が正しい部品を指している証拠）。
        written.Seek(0, SeekOrigin.Begin);
        var read = VbaRibbonPackageReader.ReadFromStream(written);
        if (read.Status != VbaRibbonReadStatus.Ok) return $"書いたブックのリボンを読み直せません（{read.Reason}）。";
        foreach (var part in result.Parts)
        {
            var request = requests.First(r => r.Format == part.Format);
            var found = read.Parts.FirstOrDefault(p => VbaRibbonMirrorLayout.FormatOf(p) == part.Format
                                                      && string.Equals(p.PartName, part.PartName, StringComparison.OrdinalIgnoreCase));
            if (found?.Xml is null || found.Xml != StripBom(request.Xml))
                return $"書いたブックから、{VbaRibbonMirrorLayout.FileName(part.Format)} を同じ本文で読み直せません。";
        }

        return null;
    }

    /// <summary>
    /// ブックのファイルを原子的に書き換える（16.7「原子的な置き換え」）。ブックと同じフォルダの一時ファイル
    /// （<c>~graft-&lt;ブック名&gt;-&lt;乱数&gt;.tmp</c>）へ書き、<see cref="Verify"/> で照合し、元のファイルが読んだときから変わっていない
    /// ことを確かめてから、<see cref="File.Replace(string, string, string?)"/> で置き換える。どの段階で失敗しても一時ファイルを消し、
    /// 置き換えの前なら元のファイルは 1 バイトも変わらない。
    /// <para>
    /// Excel を閉じること・排他で開けるかの確認・丸ごとの控え（16.5 の段階 6〜8）は呼び出し側（段階 F3 の <c>VbaRibbonSyncService</c>）の役目。
    /// ここは「開いていないブックのファイルを、壊さずに書き換える」ことだけを受け持つ。
    /// </para>
    /// </summary>
    /// <param name="workbookPath">ブックのフルパス。</param>
    /// <param name="requests">形式ごとの本文。</param>
    /// <param name="maxTotalExpandedBytes">展開後の合計の上限（0 以下なら既定）。</param>
    /// <param name="beforeReplace">
    /// テスト用: 照合のあと・置き換えの直前に呼ぶ（その間に元のファイルが変わる、という状況を作るため）。本番では渡さない。
    /// </param>
    public static VbaRibbonWriteResult RewriteFile(
        string workbookPath, IReadOnlyList<VbaRibbonWriteRequest> requests, long maxTotalExpandedBytes = 0, Action? beforeReplace = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workbookPath);
        ArgumentNullException.ThrowIfNull(requests);

        var extension = Path.GetExtension(workbookPath);
        if (extension.Equals(".xls", StringComparison.OrdinalIgnoreCase) || extension.Equals(".xla", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".xlt", StringComparison.OrdinalIgnoreCase))
        {
            return VbaRibbonWriteResult.Fail(VbaRibbonWriteStatus.LegacyFormat, "古い形式（.xls）のブックは、リボンを書き換えられません（.xlsm で保存し直してください）。");
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(workbookPath))!;
        var tempPath = Path.Combine(directory, $"~graft-{Path.GetFileNameWithoutExtension(workbookPath)}-{RandomToken()}.tmp");
        try
        {
            VbaRibbonWriteResult result;
            long sizeBefore;
            DateTime modifiedBefore;
            // 元のファイルは読み取りだけ。ほかのプログラムの書き込みは拒む（読んでいる最中に中身が変わらないように）。
            using (var source = new FileStream(workbookPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                sizeBefore = source.Length;
                modifiedBefore = File.GetLastWriteTimeUtc(workbookPath);
                // 一時ファイルは新規作成だけ（同名があれば失敗させ、他人のファイルを上書きしない）。隠し属性は付けない（同期ソフトの扱いを変えない）。
                using (var temp = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                {
                    result = Write(source, temp, requests, maxTotalExpandedBytes);
                    if (!result.Succeeded) return result;
                    temp.Flush(flushToDisk: true);

                    var mismatch = Verify(source, temp, requests, result);
                    if (mismatch is not null)
                        return VbaRibbonWriteResult.Fail(VbaRibbonWriteStatus.VerificationFailed, $"書いたブックの照合が合いません。元のブックは変えていません（{mismatch}）。");
                }
            }

            beforeReplace?.Invoke();
            var info = new FileInfo(workbookPath);
            if (!info.Exists || info.Length != sizeBefore || info.LastWriteTimeUtc != modifiedBefore)
            {
                return VbaRibbonWriteResult.Fail(VbaRibbonWriteStatus.SourceChanged,
                    "書き換えの途中で、ブックのファイルがほかのプログラムに変えられました。上書きしないよう止めました（元のブックは変えていません）。");
            }

            try
            {
                // Win32 の ReplaceFile（元のファイルの属性と ACL が残る）。成功か失敗のどちらかで、半端な状態にならない。
                File.Replace(tempPath, workbookPath, destinationBackupFileName: null);
            }
            catch (Exception ex) when (ex is PlatformNotSupportedException or IOException)
            {
                // File.Replace が使えない保存先（一部のネットワーク共有など。実機で要確認）。同じボリュームの上書きの移動に切り替える。
                File.Move(tempPath, workbookPath, overwrite: true);
            }

            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return VbaRibbonWriteResult.Fail(VbaRibbonWriteStatus.IoFailed,
                $"ブックのファイルを書き換えられませんでした。元のブックは変えていません（{VbaRibbonText.OneLine(ex.GetType().Name)}）。");
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 消せない一時ファイルは残る（次の作業の記録の後始末で消す。16.11）。元のブックには影響しない。
            }
        }
    }

    /// <summary>
    /// 書く本文のバイト列。UTF-8（BOM なし）で、改行はミラーのまま。本文の先頭に BOM の文字（U+FEFF）があれば除く
    /// （そのまま書くと、読み取り（BOM を除いて読む）との往復で本文が 1 文字ずれ、「ブック側が変わった」と誤って判定されるため）。
    /// </summary>
    internal static byte[] BodyBytes(string xml) => new UTF8Encoding(false).GetBytes(StripBom(xml));

    private static string StripBom(string xml) => xml.Length > 0 && xml[0] == '\uFEFF' ? xml[1..] : xml;

    /// <summary>
    /// 予行と書き込みに共通の、パッケージの構造の検査。項目の名前の重なり（大文字小文字違い）・必須の 2 部品・電子署名を見る。
    /// </summary>
    private static string? CheckPackage(ZipArchive archive, out VbaRibbonWriteStatus status, out Dictionary<string, ZipArchiveEntry> byName)
    {
        status = VbaRibbonWriteStatus.Corrupt;
        byName = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var key = Key(entry.FullName);
            if (key.StartsWith(SignaturesFolder, StringComparison.OrdinalIgnoreCase))
            {
                status = VbaRibbonWriteStatus.Signed;
                return "ブックに電子署名があるため、書き換えません（書き換えると署名が無効になります）。";
            }

            // 部品名は大文字小文字を区別しない（OPC の規則）。重なっていると、どちらを差し替えるか決められない。
            if (!byName.TryAdd(key, entry) && !IsDirectory(entry))
                return $"同じ名前の部品が重なっているため、書き換えません（{VbaRibbonText.OneLine(entry.FullName)}）。";
        }

        if (!byName.ContainsKey(RelationshipsPart))
            return "Excel のブックの構造ではありません（_rels/.rels がありません）。";
        if (!byName.ContainsKey(ContentTypesPart))
            return "Excel のブックの構造ではありません（[Content_Types].xml がありません）。";
        return null;
    }

    /// <summary>
    /// ルート要素の閉じタグの直前に <paramref name="fragment"/> を差し込む。<c>&lt;Relationships …/&gt;</c> のように子の無い自己終了の
    /// ルートなら、開始タグを開いて閉じタグを足す。どちらの形も見つからなければ null。
    /// </summary>
    private static string? InsertBeforeRootEnd(string text, string localName, XElement root, string fragment)
    {
        var prefix = root.GetPrefixOfNamespace(root.Name.Namespace);
        var qualified = prefix is null ? localName : prefix + ":" + localName;
        var close = new Regex("</" + Regex.Escape(qualified) + "\\s*>", RegexOptions.CultureInvariant | RegexOptions.RightToLeft);
        var match = close.Match(text);
        if (match.Success) return text[..match.Index] + fragment + text[match.Index..];

        var selfClosing = new Regex("<" + Regex.Escape(qualified) + "(\\s[^>]*?)?/>", RegexOptions.CultureInvariant);
        match = selfClosing.Match(text);
        if (!match.Success) return null;
        var opened = match.Value[..^2].TrimEnd() + ">" + fragment + "</" + qualified + ">";
        return text[..match.Index] + opened + text[(match.Index + match.Length)..];
    }

    /// <summary>
    /// 関係の一覧などのバイト列を文字列にし、書き戻すときの文字コードを決める。BOM があればその文字コード（BOM も保つ）、
    /// 無ければ宣言の <c>encoding</c>。UTF-8 と UTF-16 以外を名乗るものは扱わない（宣言と中身を食い違わせないため）。
    /// </summary>
    private static bool TryDecode(byte[] bytes, out string text, out Encoding encoding)
    {
        text = string.Empty;
        encoding = new UTF8Encoding(false);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            encoding = new UTF8Encoding(true);
            text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            return true;
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
            text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            return true;
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            encoding = new UnicodeEncoding(bigEndian: true, byteOrderMark: true);
            text = Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            return true;
        }

        var head = Encoding.Latin1.GetString(bytes, 0, Math.Min(bytes.Length, 200));
        var declared = DeclaredEncoding.Match(head);
        if (declared.Success && !declared.Groups[1].Value.Equals("utf-8", StringComparison.OrdinalIgnoreCase))
            return false;
        text = Encoding.UTF8.GetString(bytes);
        return true;
    }

    private static byte[] Encode(string text, Encoding encoding)
    {
        var preamble = encoding.GetPreamble();
        var body = encoding.GetBytes(text);
        if (preamble.Length == 0) return body;
        var all = new byte[preamble.Length + body.Length];
        preamble.CopyTo(all, 0);
        body.CopyTo(all, preamble.Length);
        return all;
    }

    private static long CopyBounded(Stream from, Stream to, long total, long limit)
    {
        var buffer = new byte[81920];
        while (true)
        {
            var n = from.Read(buffer, 0, buffer.Length);
            if (n <= 0) return total;
            total += n;
            // 上限を超えた時点で止める（見出しの展開後の大きさは嘘を書けるので、実際に展開した量で数える）。
            if (total > limit) throw new TooLargeException();
            to.Write(buffer, 0, n);
        }
    }

    private static byte[]? ReadAll(ZipArchiveEntry entry, int maxBytes)
    {
        if (entry.Length > maxBytes) return null;
        using var input = entry.Open();
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var n = input.Read(buffer, 0, buffer.Length);
            if (n <= 0) break;
            if (output.Length + n > maxBytes) return null;
            output.Write(buffer, 0, n);
        }

        return output.ToArray();
    }

    private static bool IsOle(Stream source)
    {
        source.Seek(0, SeekOrigin.Begin);
        var head = new byte[OleSignature.Length];
        var read = source.Read(head, 0, head.Length);
        source.Seek(0, SeekOrigin.Begin);
        return read == head.Length && head.AsSpan().SequenceEqual(OleSignature);
    }

    private static bool IsDirectory(ZipArchiveEntry entry) => entry.FullName.EndsWith('/') && entry.Length == 0;

    private static string Key(string fullName) => fullName.Replace('\\', '/').TrimStart('/');

    private static string RandomToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();

    /// <summary>展開後の合計の上限を超えた（写す途中で止めるための内部の合図）。</summary>
    private sealed class TooLargeException : Exception
    {
    }
}
