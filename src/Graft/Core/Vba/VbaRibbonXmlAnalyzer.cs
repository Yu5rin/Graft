using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Graft.Core.Vba;

/// <summary>
/// customUI の XML を安全に読み、リボンの構成（タブ・グループ・コントロールの id・label・種類と、コールバック）を取り出す純ロジック
/// （仕様書_ブックの情報と依頼の観点 15.3）。
/// <para>
/// 【XXE（外部実体の読み込み）対策】 ブックは外から受け取ったファイルなので、中の XML は信用しない。<c>&lt;!DOCTYPE …&gt;</c> に
/// 外部実体（<c>&lt;!ENTITY x SYSTEM "file:///…"&gt;</c>）を書かれると、素朴な読み方では手元のファイルの中身が XML に
/// 展開され、AI へ渡る依頼文に混ざってしまう。そこで次の二重の対策を取る。
/// <list type="number">
/// <item><see cref="DtdProcessing.Prohibit"/>（DTD があれば読み込みが例外になる）と <c>XmlResolver = null</c>（仮に DTD を通しても外部を解決しない）。</item>
/// <item>解析の前に <c>&lt;!DOCTYPE</c> の有無を文字列で見て、あれば理由つきで読まない（例外の文面は言語によって変わるので、
/// 「DTD を含むから読まない」という理由を利用者へ確実に伝えるため）。コメントや CDATA の中に書かれていても読まない側に倒す。</item>
/// </list>
/// customUI には DTD を使う正当な理由が無い（Office が書き出すものにも無い）ので、読めなくなる実害は無い。
/// </para>
/// <para>
/// 【文字数の上限】 <see cref="XmlReaderSettings.MaxCharactersInDocument"/> に上限を掛ける。展開後の大きさは
/// <see cref="VbaRibbonPackageReader"/> が先に切っているが、ここでも読む量の天井を持っておく（この関数を単独で呼んでも安全なように）。
/// </para>
/// </summary>
public static class VbaRibbonXmlAnalyzer
{
    private static readonly Regex DoctypePattern = new("<!DOCTYPE", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DeclaredEncodingPattern = new(
        "^\\s*<\\?xml[^>]*encoding\\s*=\\s*[\"']([^\"']+)[\"']", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// コールバックの属性か。customUI のコールバックは <c>onAction</c>・<c>onChange</c>・<c>onLoad</c> などの <c>on*</c> と、
    /// <c>getLabel</c>・<c>getEnabled</c>・<c>getImage</c> などの <c>get*</c>、それに <c>loadImage</c>。
    /// 接頭辞のあとが大文字のときだけ数える（<c>one</c> のような別の名前を取り違えないため）。
    /// </summary>
    public static bool IsCallbackAttribute(string name)
    {
        if (name == "loadImage") return true;
        var prefix = name.StartsWith("on", StringComparison.Ordinal) ? 2 : name.StartsWith("get", StringComparison.Ordinal) ? 3 : 0;
        return prefix > 0 && name.Length > prefix && char.IsUpper(name[prefix]);
    }

    /// <summary>
    /// XML の文字列を安全に読む。読めなければ理由（1 行）を返す。
    /// </summary>
    /// <param name="xml">XML の本文。</param>
    /// <param name="maxCharacters">読む最大文字数（0 以下なら <see cref="VbaRibbonLimits.DefaultMaxPartBytes"/>）。</param>
    /// <param name="document">読めた文書。</param>
    /// <param name="problem">読めなかった理由。</param>
    public static bool TryParse(string xml, int maxCharacters, out XDocument? document, out string? problem)
    {
        document = null;
        problem = null;
        if (DoctypePattern.IsMatch(xml))
        {
            problem = "DTD（<!DOCTYPE）を含むため、安全のため読みません。";
            return false;
        }

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = maxCharacters > 0 ? maxCharacters : VbaRibbonLimits.DefaultMaxPartBytes,
            MaxCharactersFromEntities = 0,
            CloseInput = true,
        };

        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            return true;
        }
        catch (XmlException ex)
        {
            problem = ex.LineNumber > 0
                ? $"XML として読めません（{ex.LineNumber} 行目付近）。"
                : "XML として読めません。";
            return false;
        }
    }

    /// <summary>
    /// バイト列を XML の文字列にする。BOM（UTF-8・UTF-16）があればそれに従い、無ければ宣言の <c>encoding</c>、それも無ければ UTF-8。
    /// 先頭の BOM は取り除く（依頼文に不可視の文字を混ぜない）。
    /// </summary>
    public static string DecodeXml(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        var head = Encoding.Latin1.GetString(bytes, 0, Math.Min(bytes.Length, 200));
        var declared = DeclaredEncodingPattern.Match(head);
        if (declared.Success)
        {
            var name = declared.Groups[1].Value;
            if (!name.Equals("utf-8", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    return Encoding.GetEncoding(name).GetString(bytes);
                }
                catch (ArgumentException)
                {
                    // 知らない名前や、この環境で使えないコードページ。UTF-8 として読む（文字化けしうるが、読めないよりよい）。
                }
            }
        }

        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// 読んだ部品の XML を解析して、構成とコールバックを取り出す。解析できなければ <see cref="VbaRibbonPart.Problem"/> に理由を入れる。
    /// </summary>
    public static VbaRibbonPart Analyze(VbaRibbonRawPart raw, int maxCharacters = 0)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (raw.Xml is null) return new VbaRibbonPart(raw, Array.Empty<VbaRibbonNode>(), raw.Problem ?? "読めませんでした。");
        if (!TryParse(raw.Xml, maxCharacters, out var document, out var problem))
            return new VbaRibbonPart(raw, Array.Empty<VbaRibbonNode>(), problem);

        var nodes = new List<VbaRibbonNode>();
        if (document!.Root is { } root) Walk(root, depth: 0, nodes);
        return new VbaRibbonPart(raw, nodes, null);
    }

    private static void Walk(XElement element, int depth, List<VbaRibbonNode> nodes)
    {
        var node = ToNode(element, depth);
        var childDepth = depth;
        if (node is not null)
        {
            nodes.Add(node);
            childDepth = depth + 1;
        }

        foreach (var child in element.Elements()) Walk(child, childDepth, nodes);
    }

    /// <summary>
    /// 要素を構成の 1 行にする。出すのは、id・idMso・idQ・label・コールバックのどれかを持つ要素と、タブ・グループ・リボン・
    /// バックステージ（属性が無くても骨格として意味がある）。<c>tabs</c>・<c>items</c> のような入れ物だけの要素は出さず、
    /// 深さにも数えない（余計な字下げで読みにくくならないように）。
    /// </summary>
    private static VbaRibbonNode? ToNode(XElement element, int depth)
    {
        var kind = element.Name.LocalName;
        string? id = null, idq = null, idMso = null, label = null;
        var attributes = element.Attributes().Where(a => !a.IsNamespaceDeclaration && a.Name.NamespaceName.Length == 0).ToArray();
        foreach (var a in attributes)
        {
            switch (a.Name.LocalName)
            {
                case "id": id = a.Value; break;
                case "idQ": idq = a.Value; break;
                case "idMso": idMso = a.Value; break;
                case "label": label = a.Value; break;
            }
        }

        var shownId = !string.IsNullOrWhiteSpace(id) ? id : !string.IsNullOrWhiteSpace(idq) ? idq : null;
        var shownMso = !string.IsNullOrWhiteSpace(idMso) ? idMso : null;
        var owner = shownId is not null ? $"{kind} {shownId}" : shownMso is not null ? $"{kind} idMso={shownMso}" : kind;
        var callbacks = attributes
            .Where(a => IsCallbackAttribute(a.Name.LocalName) && !string.IsNullOrWhiteSpace(a.Value))
            .Select(a => new VbaRibbonCallbackRef(a.Name.LocalName, a.Value.Trim(), owner))
            .ToArray();

        var skeleton = kind is "tab" or "group" or "ribbon" or "backstage" or "qat";
        var interesting = shownId is not null || shownMso is not null || !string.IsNullOrWhiteSpace(label) || callbacks.Length > 0;
        if (!skeleton && !interesting) return null;

        return new VbaRibbonNode(
            depth, kind, shownId, shownMso, string.IsNullOrWhiteSpace(label) ? null : label, callbacks);
    }
}
