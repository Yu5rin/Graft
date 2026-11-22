using System.Text.RegularExpressions;

namespace Graft.Core.Vba;

/// <summary>
/// リボン（customUI）のコールバック名を、VBA のミラー（モジュールの本文）にある <c>Sub</c>／<c>Function</c> と照合する純ロジック
/// （仕様書_ブックの情報と依頼の観点 15.4）。COM にも UI にもファイルにも触れない（本文は呼び出し側が渡す）。
/// <para>
/// 【何を照合するか】 <c>onAction="OnBtn"</c> の <c>OnBtn</c> が、VBA に<b>呼べる形で</b>あるか。AI が「リボンの XML は直したが、
/// 呼ばれる側のマクロが無い（名前が違う・Private になっている）」という半端な回答をしないよう、ミラーにあるかを添える。
/// </para>
/// <list type="bullet">
/// <item><b>大文字小文字は区別しない</b>（VBA の名前は区別しない）。</item>
/// <item><b><c>Public</c> と修飾なしは呼べる</b>（修飾子を省いた宣言は Public）。<b><c>Private</c>・<c>Friend</c> は呼べない</b>
/// （リボンのコールバックは Excel 本体から呼ばれるので、モジュールの外に公開されていなければならない）。
/// 同名の Public が 1 つでもあれば「見つかった」。</item>
/// <item><b>モジュール名つきの指定</b>（<c>Module1.OnBtn</c>）は、そのモジュールの中だけを探す。<c>プロジェクト名.Module1.OnBtn</c> のように
/// 3 つ以上つながっていても、最後の 2 つ（モジュール名と手続き名）を使う。<c>Book.xlsm!Module1.OnBtn</c> の <c>!</c> より前も無視する。
/// モジュールが無ければ「モジュールが無い」と区別して伝える。</item>
/// <item><c>Declare Function</c>（外部 API の宣言）・<c>Property</c>・コメント行は手続きとして数えない。</item>
/// </list>
/// <para>
/// 【解析は行単位の簡易なもの】 完全な VBA の構文解析はしない。行頭の宣言だけを見るので、行継続（<c>_</c>）で宣言を折り返した
/// 手続きは見落とす（この場合は「見つかりません」と出てしまうので、実害は「念のため確認してください」になる側）。
/// </para>
/// </summary>
public static class VbaRibbonCallbackMatcher
{
    // 行頭の手続きの宣言。例: "Public Sub OnBtn(control As IRibbonControl)"、"Function GetLabel(c As IRibbonControl, ByRef r)"。
    // 名前は VBA の識別子（日本語も可）。直後の型宣言文字（$ % & ! # @）は名前に含めない。
    private static readonly Regex Declaration = new(
        @"^\s*(?:(?<scope>Public|Private|Friend)\s+)?(?:Static\s+)?(?<kind>Sub|Function)\s+(?<name>[\p{L}_][\p{L}\p{N}_]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// コールバックの名前ごとに、使われている所を集めて照合する。
    /// </summary>
    /// <param name="references">構成から取り出したコールバック（<see cref="VbaRibbonPart.Nodes"/> の <c>Callbacks</c>）。</param>
    /// <param name="modules">照合に使うモジュール。null は「ミラーを読めなかった」（すべて <see cref="VbaRibbonCallbackStatus.Unchecked"/>）。</param>
    /// <returns>名前（大文字小文字を区別せずにまとめる）ごとの結果。最初に出てきた順。</returns>
    public static IReadOnlyList<VbaRibbonCallbackCheck> Check(
        IEnumerable<VbaRibbonCallbackRef> references, IReadOnlyList<VbaRibbonSourceModule>? modules)
    {
        ArgumentNullException.ThrowIfNull(references);

        // 本文は 1 回だけ走査して、手続きの宣言の一覧にしておく（コールバックごとに全文を舐め直さない）。
        var declarations = modules is null ? null : modules.SelectMany(Scan).ToArray();

        var groups = new List<(string Name, List<string> Usages)>();
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in references)
        {
            var usage = $"{reference.Attribute}（{reference.Owner}）";
            if (index.TryGetValue(reference.Name, out var at))
            {
                if (!groups[at].Usages.Contains(usage)) groups[at].Usages.Add(usage);
            }
            else
            {
                index[reference.Name] = groups.Count;
                groups.Add((reference.Name, new List<string> { usage }));
            }
        }

        return groups
            .Select(g =>
            {
                var (status, detail) = declarations is null || modules is null
                    ? (VbaRibbonCallbackStatus.Unchecked, "VBA のミラーを読めなかったため、照合できていません")
                    : Resolve(g.Name, declarations, modules);
                return new VbaRibbonCallbackCheck(g.Name, g.Usages, status, detail);
            })
            .ToArray();
    }

    /// <summary>
    /// 名前（<c>OnBtn</c>・<c>Module1.OnBtn</c>）を 1 つ照合する。
    /// </summary>
    public static (VbaRibbonCallbackStatus Status, string Detail) Resolve(
        string callbackName, IReadOnlyList<VbaRibbonSourceModule> modules)
        => Resolve(callbackName, modules.SelectMany(Scan).ToArray(), modules);

    private static (VbaRibbonCallbackStatus Status, string Detail) Resolve(
        string callbackName, IReadOnlyList<Declared> declarations, IReadOnlyList<VbaRibbonSourceModule> modules)
    {
        var (moduleName, procedure) = Split(callbackName);
        if (procedure.Length == 0) return (VbaRibbonCallbackStatus.NotFound, "VBA に見つかりません");

        if (moduleName is not null && !modules.Any(m => m.Name.Equals(moduleName, StringComparison.OrdinalIgnoreCase)))
        {
            return (VbaRibbonCallbackStatus.ModuleNotFound, $"VBA に見つかりません（モジュール「{moduleName}」がありません）");
        }

        var matches = declarations
            .Where(d => d.Procedure.Equals(procedure, StringComparison.OrdinalIgnoreCase)
                        && (moduleName is null || d.Module.Name.Equals(moduleName, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        var callable = matches.Where(d => d.IsPublic).ToArray();
        if (callable.Length > 0)
        {
            var places = string.Join("、", callable.Select(d => Describe(d.Module)).Distinct());
            var note = callable.Select(d => d.Module.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1
                ? "（同名が複数のモジュールにあります）"
                : string.Empty;
            return (VbaRibbonCallbackStatus.Found, $"あります: {places}{note}");
        }

        if (matches.Length > 0)
        {
            var places = string.Join("、", matches.Select(d => $"{Describe(d.Module)} の {d.Scope}"));
            return (VbaRibbonCallbackStatus.NotPublic, $"Public ではないため、リボンから呼べません（{places}）");
        }

        return (VbaRibbonCallbackStatus.NotFound, moduleName is null
            ? "VBA に見つかりません"
            : $"VBA に見つかりません（モジュール「{moduleName}」に {procedure} がありません）");
    }

    /// <summary>
    /// <c>Book.xlsm!Module1.OnBtn</c>・<c>Project.Module1.OnBtn</c>・<c>Module1.OnBtn</c>・<c>OnBtn</c> を、
    /// （モジュール名 or null, 手続き名）に分ける。
    /// </summary>
    internal static (string? Module, string Procedure) Split(string callbackName)
    {
        var name = callbackName.Trim();
        var bang = name.LastIndexOf('!');
        if (bang >= 0) name = name[(bang + 1)..];

        var parts = name.Split('.', StringSplitOptions.TrimEntries);
        if (parts.Length == 1) return (null, parts[0]);
        var procedure = parts[^1];
        var module = parts[^2];
        return (module.Length == 0 ? null : module, procedure);
    }

    private static string Describe(VbaRibbonSourceModule module)
        => module.Kind == VbaModuleKind.Standard ? module.Name : $"{VbaModuleKinds.Label(module.Kind)} {module.Name}";

    private static IEnumerable<Declared> Scan(VbaRibbonSourceModule module)
    {
        foreach (var rawLine in module.Text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').TrimStart();
            if (line.Length == 0 || line[0] == '\'' || IsRemark(line)) continue;

            var match = Declaration.Match(line);
            if (!match.Success) continue;

            var scope = match.Groups["scope"].Success ? match.Groups["scope"].Value : "Public";
            // 「Public」「Private」の綴りは宣言のままの大文字小文字で出さず、正規の綴りにそろえる（画面の表記を安定させる）。
            var normalized = scope.Equals("Private", StringComparison.OrdinalIgnoreCase) ? "Private"
                : scope.Equals("Friend", StringComparison.OrdinalIgnoreCase) ? "Friend" : "Public";
            yield return new Declared(module, match.Groups["name"].Value, normalized);
        }
    }

    private static bool IsRemark(string line)
        => line.StartsWith("Rem", StringComparison.OrdinalIgnoreCase) && (line.Length == 3 || char.IsWhiteSpace(line[3]));

    private sealed record Declared(VbaRibbonSourceModule Module, string Procedure, string Scope)
    {
        public bool IsPublic => Scope == "Public";
    }
}
