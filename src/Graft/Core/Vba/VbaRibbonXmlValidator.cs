using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace Graft.Core.Vba;

/// <summary>リボンの XML の検査で見つかった 1 件（仕様書_ブックの情報と依頼の観点 16.8）。</summary>
/// <param name="Severity">重さ。<see cref="Severity.Error"/> は書き込む前に止める（E818）。<see cref="Severity.Warning"/> は確認に出すだけ。</param>
/// <param name="Check">16.8 の検査の表の番号（1〜14）。テストと、文言を変えても何の検査かを追えるようにするため。</param>
/// <param name="Line">行（1 始まり。位置が無いものは 0）。</param>
/// <param name="Column">列（1 始まり。位置が無いものは 0）。</param>
/// <param name="Message">利用者に見せる文（1 行）。</param>
public sealed record VbaRibbonFinding(Severity Severity, int Check, int Line, int Column, string Message)
{
    /// <summary>一覧に出す 1 行（「12 行 5 列: …」）。位置が無いものは文だけ。</summary>
    public string ToDisplayText()
        => Line > 0 ? $"{Line} 行 {Column} 列: {Message}" : Message;
}

/// <summary>検査の結果。</summary>
/// <param name="Format">検査した形式。</param>
/// <param name="Findings">見つかったもの（エラーが先、同じ重さの中は位置の順）。</param>
public sealed record VbaRibbonValidation(VbaRibbonFormat Format, IReadOnlyList<VbaRibbonFinding> Findings)
{
    /// <summary>エラー（書き込みを止めるもの）があるか。</summary>
    public bool HasErrors => Findings.Any(f => f.Severity == Severity.Error);

    /// <summary>エラーだけ。</summary>
    public IEnumerable<VbaRibbonFinding> Errors => Findings.Where(f => f.Severity == Severity.Error);

    /// <summary>警告だけ。</summary>
    public IEnumerable<VbaRibbonFinding> Warnings => Findings.Where(f => f.Severity == Severity.Warning);
}

/// <summary>検査に添える、XML の外の情報（どれも任意。渡さなければ、その検査を行わない）。</summary>
/// <param name="Modules">
/// コールバックの照合（#12）に使う VBA のミラーのモジュール。null なら照合しない（「照合できていません」の警告も出さない。
/// 照合の可否は 15 章の行が既に伝えているため、検査の一覧を同じ話で埋めない）。
/// </param>
/// <param name="ImageIds">
/// その部品の画像の関係（<c>customUI/_rels/customUI14.xml.rels</c>）の <c>Id</c> の一覧（#13）。null なら調べない。
/// </param>
public sealed record VbaRibbonValidationContext(
    IReadOnlyList<VbaRibbonSourceModule>? Modules = null, IReadOnlyCollection<string>? ImageIds = null);

/// <summary>
/// リボン（customUI）の XML を、ブックへ書く前に検査する純ロジック（仕様書_ブックの情報と依頼の観点 16.8。v0.8）。
/// <para>
/// 【なぜ書く前に止めるか】 Excel は、customUI の XML が定義に合わないと、<b>リボン全体を読み込まず、既定では何も知らせない</b>
/// （「アドイン ユーザー インターフェイスのエラーを表示する」がオフのとき）。利用者には「反映したのにボタンが消えた」としか見えず、
/// 原因の切り分けもできない。ブックを閉じて開き直す重い手順（16.5）の後でそれが分かるより、書く前に止めるほうが安全。
/// </para>
/// <para>
/// 【Office の公開スキーマ（XSD）で検査する】 v0.8 の利用者の決定（16.16-5・6）により、[MS-CUSTOMUI]（2006/01 形式）と
/// [MS-CUSTOMUI2]（2009/07 形式）の付録にある XSD の全文を、埋め込みリソースとして同梱した（出典と再配布の根拠は
/// <c>THIRD-PARTY-NOTICES.md</c> と XSD の先頭のコメント）。要素名・属性名・列挙の値・<c>id</c> の書式（XML の ID 型）と重複・
/// コールバックの値の長さ（1〜1024 文字）は、<b>スキーマに合わなければエラー</b>にする。自前の表で近似すると、表の漏れで
/// 正しい XML を書けなくするか、誤りを見逃すかのどちらかになる。形式は 2010 年以降変わっていないので、同梱した定義が古くなる心配も小さい。
/// 検査は .NET 標準の <see cref="System.Xml.Schema"/> だけで行う（新しい NuGet は使わない）。
/// </para>
/// <para>
/// 【スキーマで表せないもの】 XSD 1.0 では「<c>id</c>・<c>idMso</c>・<c>idQ</c> のどれか 1 つだけ」は書けないので、#6（同時に 2 つ）は
/// 自前のエラー、#11（識別子なし）は自前の警告にした。コールバックの呼び先（#12）・画像の関係（#13）・<c>startFromScratch</c>（#14）は、
/// XML としては正しいので警告にとどめる。<c>idMso</c>・<c>imageMso</c> の値が実在するかは、一覧を同梱しないので調べない。
/// </para>
/// <para>
/// 【安全】 解析は <see cref="VbaRibbonXmlAnalyzer.TryParse"/>（DTD 禁止・外部実体なし・入れ子 128 段まで・文字数の上限）を
/// 通ったものだけに行い、木を作るときも同じ設定で読む。スキーマの読み込みも DTD を禁止し、<c>XmlResolver</c> を持たせない
/// （<c>xsi:schemaLocation</c> などで外部のスキーマを取りに行かない）。
/// </para>
/// </summary>
public static class VbaRibbonXmlValidator
{
    /// <summary>
    /// 一覧に載せる件数の上限。壊れた XML では、1 つの誤りから後続の要素がすべて誤りになり、数千件になりうる。
    /// 利用者が読めるのは先頭の数十件なので、超えた分は「ほか N 件」の 1 行にまとめる。
    /// </summary>
    public const int MaxFindings = 100;

    /// <summary>埋め込みリソースの論理名（<c>Graft.csproj</c> と <c>Graft.Tests.csproj</c> の <c>LogicalName</c> と一致させる）。</summary>
    public const string Office2010SchemaResource = "Graft.Schemas.customUI14.xsd";

    /// <summary>古い形式のスキーマの埋め込みリソースの論理名。</summary>
    public const string Office2007SchemaResource = "Graft.Schemas.customUI.xsd";

    private static readonly Regex DeclarationEncoding = new(
        "^\\uFEFF?\\s*<\\?xml[^>]*?encoding\\s*=\\s*[\"']([^\"']*)[\"']",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Lazy<CompiledSchema> Office2010 = new(() => CompiledSchema.Load(VbaRibbonFormat.Office2010));
    private static readonly Lazy<CompiledSchema> Office2007 = new(() => CompiledSchema.Load(VbaRibbonFormat.Office2007));

    /// <summary>
    /// 形式ごとのスキーマを読み込めるか（同梱漏れの検出用。ビルドの設定を誤ると、ここで例外になる）。
    /// </summary>
    public static XmlSchemaSet GetSchemaSet(VbaRibbonFormat format) => Schema(format).Set;

    /// <summary>XML を検査する。例外は投げない（壊れた XML も、理由つきの結果で返す）。</summary>
    /// <param name="xml">XML の本文（ミラーのファイル、またはパッチの適用後の本文）。</param>
    /// <param name="format">書き込む先の形式（ミラーのファイル名で決まる）。</param>
    /// <param name="context">コールバックの照合・画像の関係に使う情報（任意）。</param>
    public static VbaRibbonValidation Validate(string xml, VbaRibbonFormat format, VbaRibbonValidationContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(xml);
        var findings = new List<VbaRibbonFinding>();

        // #2 大きさ。読み取り（15.2）と同じ部品の上限。超えるものを書くと、Graft 自身が次の取込で読めなくなる。
        var bytes = Encoding.UTF8.GetByteCount(xml);
        if (bytes > VbaRibbonLimits.DefaultMaxPartBytes)
        {
            findings.Add(Error(2, 0, 0,
                $"大きすぎます（{bytes.ToString("N0", CultureInfo.InvariantCulture)} バイト。上限 {VbaRibbonLimits.DefaultMaxPartBytes / (1024 * 1024)} MB）。"));
            return Done(format, findings);
        }

        // #1 整形式（DTD 禁止・外部実体なし・入れ子 128 段まで）。読み取りと同じ関数を通し、読めるものの範囲をそろえる。
        if (!VbaRibbonXmlAnalyzer.TryParse(xml, VbaRibbonLimits.DefaultMaxPartBytes, out _, out var parseProblem))
        {
            var dtd = parseProblem?.StartsWith("DTD", StringComparison.Ordinal) == true;
            findings.Add(Error(1, 0, 0, dtd ? "DTD（<!DOCTYPE）を含むため書けません。" : parseProblem ?? "XML として読めません。"));
            return Done(format, findings);
        }

        // #3 XML 宣言の encoding。Graft は UTF-8 で書くので、宣言が別の文字コードを名乗ると、宣言と中身が食い違う部品になる。
        var declared = DeclarationEncoding.Match(xml);
        if (declared.Success && !declared.Groups[1].Value.Equals("utf-8", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(Error(3, 1, 1,
                $"encoding は UTF-8 にしてください（いまは「{VbaRibbonText.OneLine(declared.Groups[1].Value)}」。Graft は UTF-8 で書きます）。"));
        }

        var document = LoadWithLineInfo(xml);
        var root = document.Root!;

        // #4 ルート要素と名前空間。名前空間がファイルと合わないと、Excel はその部品を別の形式として読もうとして無視する。
        var expectedNs = VbaRibbonMirrorLayout.Namespace(format);
        if (root.Name.LocalName != "customUI")
        {
            findings.Add(Error(4, root, $"ルート要素は customUI です（いまは {VbaRibbonText.OneLine(root.Name.LocalName)}）。"));
            return Done(format, findings);
        }

        if (root.Name.NamespaceName != expectedNs)
        {
            var hint = root.Name.NamespaceName == VbaRibbonMirrorLayout.Namespace(Other(format))
                ? $"（この内容は {VbaRibbonMirrorLayout.ToRelativePath(Other(format))} の形式です）"
                : string.Empty;
            var actual = root.Name.NamespaceName.Length == 0 ? "名前空間なし" : VbaRibbonText.OneLine(root.Name.NamespaceName);
            findings.Add(Error(4, root,
                $"{VbaRibbonMirrorLayout.FileName(format)} の名前空間は {expectedNs} です（いまは {actual}）{hint}。"));
            return Done(format, findings);
        }

        // #5・#7〜#10 スキーマ。名前空間が合っているときだけ（合わなければ、全要素が「定義に無い」になって一覧が埋まるだけ）。
        ValidateAgainstSchema(document, format, findings);

        // #6 識別子を 2 つ以上（スキーマでは表せない）。#11 識別子なし（同）。#14 startFromScratch。#13 画像。
        CheckIdentifiers(document, findings);
        CheckStartFromScratch(document, findings);
        if (context?.ImageIds is { } imageIds) CheckImages(document, imageIds, findings);

        // #12 コールバックの呼び先。15 章の照合（VbaRibbonCallbackMatcher.Check）と同じ結果を使い、食い違わせない（16.14）。
        if (context?.Modules is { } modules) CheckCallbacks(xml, format, modules, findings);

        return Done(format, findings);
    }

    private static VbaRibbonFormat Other(VbaRibbonFormat format)
        => format == VbaRibbonFormat.Office2010 ? VbaRibbonFormat.Office2007 : VbaRibbonFormat.Office2010;

    private static CompiledSchema Schema(VbaRibbonFormat format)
        => format == VbaRibbonFormat.Office2010 ? Office2010.Value : Office2007.Value;

    /// <summary>
    /// 行と列を持たせて読み直す。<see cref="VbaRibbonXmlAnalyzer.TryParse"/> を通ったあとなので、同じ設定で必ず読める。
    /// 空白は保つ（行・列が元の文字列と一致するように）。
    /// </summary>
    private static XDocument LoadWithLineInfo(string xml)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = VbaRibbonLimits.DefaultMaxPartBytes,
            MaxCharactersFromEntities = 0,
        };
        using var reader = XmlReader.Create(new StringReader(xml), settings);
        return XDocument.Load(reader, LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace);
    }

    private static void ValidateAgainstSchema(XDocument document, VbaRibbonFormat format, List<VbaRibbonFinding> findings)
    {
        var schema = Schema(format);
        var other = Schema(Other(format));
        var results = new List<VbaRibbonFinding>();

        void OnEvent(object? sender, ValidationEventArgs e)
        {
            // 警告（スキーマが見つからない、など）は、名前空間を先に確かめているので通常は出ない。出ても止める理由にはしない。
            if (e.Severity != XmlSeverityType.Error) return;
            results.Add(Describe(sender, e.Exception, schema, other, format));
        }

        // 【XmlSchemaSet を共有するときの錠】 コンパイル済みの XmlSchemaSet を検証だけに使うのは実際には安全とされるが、
        // ドキュメント上はスレッドセーフを保証していない。検査は 1 回数ミリ秒で、同時に走ることもまず無いので、錠で確実にする。
        lock (schema.Set)
        {
            document.Validate(schema.Set, OnEvent, addSchemaInfo: true);
        }

        findings.AddRange(results);

        // #11 識別子の無いコントロール。スキーマ上 id・idMso・idQ を持てる要素（＝コントロール）で、どれも書いていないもの。
        // XSD 1.0 では「どれか 1 つ必須」を書けないので、スキーマは通る。Excel が拒むかは実機で要確認（付録B）なので警告にする。
        foreach (var element in document.Root!.DescendantsAndSelf())
        {
            if (element.GetSchemaInfo()?.SchemaType is not XmlSchemaComplexType type) continue;
            var declaresId = false;
            foreach (XmlQualifiedName name in type.AttributeUses.Names)
            {
                if (name.Name is "id" or "idMso" or "idQ") { declaresId = true; break; }
            }

            if (!declaresId) continue;
            if (element.Attributes().Any(a => a.Name.NamespaceName.Length == 0 && a.Name.LocalName is "id" or "idMso" or "idQ")) continue;
            findings.Add(Warning(11, element, $"{Describe(element)} に識別子（id・idMso・idQ）がありません。"));
        }
    }

    /// <summary>
    /// スキーマの誤り 1 件を、利用者の言葉の 1 行にする。.NET の文面（英語のことが多い）は「詳細」として添えるだけにし、
    /// 先頭には何が悪いかを日本語で書く。要素・属性の名前がスキーマに無ければ綴りの誤り、もう一方の形式にだけあれば形式の取り違え、
    /// と見分けて伝える（利用者が直す手がかりになるのはここ）。
    /// </summary>
    private static VbaRibbonFinding Describe(
        object? sender, XmlSchemaException exception, CompiledSchema schema, CompiledSchema other, VbaRibbonFormat format)
    {
        // .NET の文面は要素名と名前空間と「置けた要素の一覧」を含み、120 文字では肝心の所が切れる（実測: group の子の誤りで約 300 文字）。
        // 誤りの文面そのものは XML の属性値を引用しうるので、1 行にそろえて長さの天井（400 文字）だけは掛ける。
        var detail = VbaRibbonText.OneLine(exception.Message, 400);
        var fileName = VbaRibbonMirrorLayout.FileName(format);
        var otherFile = VbaRibbonMirrorLayout.FileName(Other(format));
        switch (sender)
        {
            case XElement element:
            {
                var name = VbaRibbonText.OneLine(element.Name.LocalName);
                string lead;
                if (!schema.ElementNames.Contains(element.Name.LocalName))
                {
                    lead = other.ElementNames.Contains(element.Name.LocalName)
                        ? $"『{name}』は {otherFile} の形式にだけある要素で、{fileName} には書けません。"
                        : $"『{name}』は customUI の定義に無い要素です。綴りを確かめてください（Excel は、定義に合わない要素が 1 つでもあると、リボン全体を読み込みません）。";
                }
                else
                {
                    lead = $"{Describe(element)} が customUI の定義に合いません（置ける場所・中身・必須の属性を確かめてください）。";
                }

                return Error(9, element, $"{lead} 詳細: {detail}");
            }

            case XAttribute attribute:
            {
                var name = VbaRibbonText.OneLine(attribute.Name.LocalName);
                var owner = attribute.Parent is { } parent ? Describe(parent) : "要素";
                string lead;
                int check;
                if (attribute.Name.NamespaceName.Length == 0 && !schema.AttributeNames.Contains(attribute.Name.LocalName))
                {
                    check = 9;
                    lead = other.AttributeNames.Contains(attribute.Name.LocalName)
                        ? $"属性『{name}』は {otherFile} の形式にだけあり、{fileName} には書けません（{owner}）。"
                        : $"属性『{name}』は customUI の定義に無い属性です。綴りを確かめてください（{owner}）。";
                }
                else if (attribute.Name.NamespaceName.Length == 0 && attribute.Name.LocalName is "id")
                {
                    check = 5;
                    lead = $"id『{VbaRibbonText.OneLine(attribute.Value)}』は使えないか、ほかの要素と重なっています"
                           + "（id は英字か _ で始まり、空白を含まず、文書の中で重ならない名前にしてください）"
                           + $"（{owner}）。";
                }
                else if (attribute.Name.NamespaceName.Length == 0 && VbaRibbonXmlAnalyzer.IsCallbackAttribute(attribute.Name.LocalName))
                {
                    check = 8;
                    lead = $"{name} の値が定義に合いません（空にせず、1024 文字以内にしてください）（{owner}）。";
                }
                else
                {
                    check = 10;
                    lead = $"属性『{name}』の値『{VbaRibbonText.OneLine(attribute.Value)}』が customUI の定義に合いません（{owner}）。";
                }

                return Error(check, attribute, $"{lead} 詳細: {detail}");
            }

            default:
                return Error(9, exception.LineNumber, exception.LinePosition, $"customUI の定義に合いません。詳細: {detail}");
        }
    }

    /// <summary>#6: <c>id</c>・<c>idMso</c>・<c>idQ</c> を 1 つの要素に 2 つ以上書いていない（スキーマでは表せない）。</summary>
    private static void CheckIdentifiers(XDocument document, List<VbaRibbonFinding> findings)
    {
        foreach (var element in document.Root!.DescendantsAndSelf())
        {
            var names = element.Attributes()
                .Where(a => a.Name.NamespaceName.Length == 0 && a.Name.LocalName is "id" or "idMso" or "idQ")
                .Select(a => a.Name.LocalName)
                .ToArray();
            if (names.Length < 2) continue;
            findings.Add(Error(6, element, $"{string.Join(" と ", names)} は同時に書けません（{Describe(element)}）。"));
        }
    }

    /// <summary>#14: <c>startFromScratch="true"</c> は Excel の標準のタブをすべて隠す。正しい指定だが、知らずに書くと驚くので知らせる。</summary>
    private static void CheckStartFromScratch(XDocument document, List<VbaRibbonFinding> findings)
    {
        foreach (var element in document.Root!.DescendantsAndSelf().Where(e => e.Name.LocalName == "ribbon"))
        {
            var value = (string?)element.Attribute("startFromScratch");
            if (value is not null && (value.Trim() == "1" || value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)))
                findings.Add(Warning(14, element, "startFromScratch が true のため、Excel の標準のタブがすべて隠れます。"));
        }
    }

    /// <summary>
    /// #13: <c>image="…"</c> が、その部品の画像の関係に無い。Graft は画像を扱わないので、AI が存在しない画像を指すと、
    /// ボタンの絵が出ないだけで済まず、Excel によってはそのコントロールが出ない。<c>loadImage</c> があれば、画像は VBA が
    /// 渡すので調べない。
    /// </summary>
    private static void CheckImages(XDocument document, IReadOnlyCollection<string> imageIds, List<VbaRibbonFinding> findings)
    {
        if (document.Root!.Attribute("loadImage") is { } load && !string.IsNullOrWhiteSpace(load.Value)) return;
        var known = new HashSet<string>(imageIds, StringComparer.Ordinal);
        foreach (var element in document.Root.DescendantsAndSelf())
        {
            var image = (string?)element.Attribute("image");
            if (string.IsNullOrWhiteSpace(image) || known.Contains(image)) continue;
            findings.Add(Warning(13, element,
                $"画像『{VbaRibbonText.OneLine(image)}』はブックにありません（Graft は画像を扱いません）（{Describe(element)}）。"));
        }
    }

    /// <summary>#12: コールバックの呼び先（15.4 の照合の結果）。呼べる見込みのもの（<c>Found</c>）と、照合できていないものは出さない。</summary>
    private static void CheckCallbacks(
        string xml, VbaRibbonFormat format, IReadOnlyList<VbaRibbonSourceModule> modules, List<VbaRibbonFinding> findings)
    {
        var part = VbaRibbonXmlAnalyzer.Analyze(new VbaRibbonRawPart(
            VbaRibbonMirrorLayout.DefaultPartName(format), format == VbaRibbonFormat.Office2010, xml, null));
        if (!part.IsUsable) return;
        var checks = VbaRibbonCallbackMatcher.Check(part.Nodes.SelectMany(n => n.Callbacks), modules);
        foreach (var check in checks)
        {
            if (check.Status is VbaRibbonCallbackStatus.Found or VbaRibbonCallbackStatus.Unchecked) continue;
            var usages = string.Join("、", check.Usages.Take(3)) + (check.Usages.Count > 3 ? $" ほか {check.Usages.Count - 3} か所" : string.Empty);
            findings.Add(Warning(12, 0, 0, $"コールバック「{check.Name}」: {check.Detail}（{usages}）。"));
        }
    }

    /// <summary>画面・文に出す要素の呼び名（<c>button btnRun</c>・<c>button idMso=Copy</c>・<c>group</c>）。1 行・長さの上限つき。</summary>
    private static string Describe(XElement element)
    {
        var kind = element.Name.LocalName;
        var id = (string?)element.Attribute("id") ?? (string?)element.Attribute("idQ");
        var mso = (string?)element.Attribute("idMso");
        return VbaRibbonText.OneLine(
            !string.IsNullOrWhiteSpace(id) ? $"{kind} {id}" : !string.IsNullOrWhiteSpace(mso) ? $"{kind} idMso={mso}" : kind);
    }

    private static VbaRibbonValidation Done(VbaRibbonFormat format, List<VbaRibbonFinding> findings)
    {
        var ordered = findings
            .OrderBy(f => f.Severity == Severity.Error ? 0 : 1)
            .ThenBy(f => f.Line == 0 ? int.MaxValue : f.Line)
            .ThenBy(f => f.Column)
            .ToList();
        if (ordered.Count > MaxFindings)
        {
            var rest = ordered.Count - MaxFindings;
            var restHasError = ordered.Skip(MaxFindings).Any(f => f.Severity == Severity.Error);
            ordered = ordered.Take(MaxFindings).ToList();
            ordered.Add(new VbaRibbonFinding(restHasError ? Severity.Error : Severity.Warning, 0, 0, 0, $"ほか {rest} 件（先頭の {MaxFindings} 件だけを載せています）。"));
        }

        return new VbaRibbonValidation(format, ordered);
    }

    private static VbaRibbonFinding Error(int check, int line, int column, string message)
        => new(Severity.Error, check, line, column, message);

    private static VbaRibbonFinding Error(int check, XObject at, string message)
    {
        var info = (IXmlLineInfo)at;
        return new VbaRibbonFinding(Severity.Error, check, info.HasLineInfo() ? info.LineNumber : 0, info.HasLineInfo() ? info.LinePosition : 0, message);
    }

    private static VbaRibbonFinding Warning(int check, int line, int column, string message)
        => new(Severity.Warning, check, line, column, message);

    private static VbaRibbonFinding Warning(int check, XObject at, string message)
    {
        var info = (IXmlLineInfo)at;
        return new VbaRibbonFinding(Severity.Warning, check, info.HasLineInfo() ? info.LineNumber : 0, info.HasLineInfo() ? info.LinePosition : 0, message);
    }

    /// <summary>
    /// コンパイル済みのスキーマと、そこに出てくる要素名・属性名の一覧（綴りの誤りか、置き場所の誤りかを見分けるため）。
    /// </summary>
    private sealed class CompiledSchema
    {
        private CompiledSchema(XmlSchemaSet set, HashSet<string> elementNames, HashSet<string> attributeNames)
        {
            Set = set;
            ElementNames = elementNames;
            AttributeNames = attributeNames;
        }

        public XmlSchemaSet Set { get; }

        public HashSet<string> ElementNames { get; }

        public HashSet<string> AttributeNames { get; }

        public static CompiledSchema Load(VbaRibbonFormat format)
        {
            var resource = format == VbaRibbonFormat.Office2010 ? Office2010SchemaResource : Office2007SchemaResource;
            using var stream = typeof(VbaRibbonXmlValidator).Assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"埋め込みリソース {resource} がありません（csproj の EmbeddedResource を確かめてください）。");
            var readerSettings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(stream, readerSettings);
            var schema = XmlSchema.Read(reader, null)
                ?? throw new InvalidOperationException($"{resource} をスキーマとして読めません。");
            // XmlResolver を null にして、import・include などで外部を取りに行かせない（同梱の XSD には無いが、念のため）。
            var set = new XmlSchemaSet { XmlResolver = null };
            set.Add(schema);
            set.Compile();

            var elements = new HashSet<string>(StringComparer.Ordinal);
            var attributes = new HashSet<string>(StringComparer.Ordinal);
            var visited = new HashSet<XmlSchemaType>();
            var pending = new Stack<XmlSchemaType>();
            foreach (XmlSchemaElement e in set.GlobalElements.Values)
            {
                elements.Add(e.QualifiedName.Name);
                if (e.ElementSchemaType is { } t) pending.Push(t);
            }

            foreach (XmlSchemaType t in set.GlobalTypes.Values) pending.Push(t);

            // 型をたどって、子要素の名前と属性名を集める。再帰はせず明示的なスタックで（スキーマは自分を入れ子に含む型を持つ）。
            while (pending.Count > 0)
            {
                var type = pending.Pop();
                if (!visited.Add(type) || type is not XmlSchemaComplexType complex) continue;
                foreach (XmlQualifiedName name in complex.AttributeUses.Names) attributes.Add(name.Name);
                var particles = new Stack<XmlSchemaParticle>();
                if (complex.ContentTypeParticle is { } root) particles.Push(root);
                while (particles.Count > 0)
                {
                    switch (particles.Pop())
                    {
                        case XmlSchemaElement element:
                            elements.Add(element.QualifiedName.Name);
                            if (element.ElementSchemaType is { } child) pending.Push(child);
                            break;
                        case XmlSchemaGroupBase group:
                            foreach (XmlSchemaObject item in group.Items)
                                if (item is XmlSchemaParticle p) particles.Push(p);
                            break;
                    }
                }
            }

            return new CompiledSchema(set, elements, attributes);
        }
    }

    /// <summary>テスト用: 検査に使う要素名の一覧の件数（スキーマを読めていることの確認）。</summary>
    internal static int KnownElementCount(VbaRibbonFormat format) => Schema(format).ElementNames.Count;

    /// <summary>テスト用: 要素名がスキーマにあるか。</summary>
    internal static bool IsKnownElement(VbaRibbonFormat format, string localName) => Schema(format).ElementNames.Contains(localName);
}
