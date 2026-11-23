using System.Text;

namespace Graft.Core.Vba;

/// <summary>
/// リボン（customUI）の取得結果（<see cref="VbaRibbonSnapshot"/>）を、依頼文に載せる Markdown の文にする純ロジック
/// （仕様書_ブックの情報と依頼の観点 15.5）。COM にも UI にもファイルにも触れない。
/// <para>
/// 【出す内容は 3 状態で決まる】 <see cref="VbaWorkbookInfoLevel.Hidden"/> は空。<see cref="VbaWorkbookInfoLevel.StructureOnly"/> は
/// 構成（タブ・グループ・コントロールの id・label・種類）とコールバックの一覧（VBA との照合つき）だけ。
/// <see cref="VbaWorkbookInfoLevel.Full"/> はそれに加えて XML の全文を、文字数の上限つきで出す。
/// 出すものが何も無いとき（customUI が無い・読めなかった）も空にして、見出しだけが残らないようにする。
/// </para>
/// <para>
/// 【「最後に保存した内容」を明記する】 読んでいるのはディスク上のファイルなので、Excel で編集中の未保存の変更は入らない。
/// AI が「今の画面と同じ」と思い込んで古い XML を前提に書き換えないよう、冒頭に必ず書き、取得日時とファイルの更新日時を添える。
/// </para>
/// <para>
/// 【改行は LF】 差し込み側が自分の改行へそろえるので、ここは LF 固定（<see cref="VbaWorkbookInfoFormatter"/> と同じ作法）。
/// XML の本文の改行も LF にそろえる。
/// </para>
/// </summary>
public static class VbaRibbonFormatter
{
    /// <summary>見出し。</summary>
    public const string Heading = "# リボン（customUI）";

    /// <summary>構成の最大行数（部品 1 つあたり）。実物のリボンは数十〜数百行。それを超える異常に大きいものは切って、その旨を書く。</summary>
    public const int MaxStructureLines = 400;

    /// <summary>コールバックの表の最大行数。</summary>
    public const int MaxCallbackRows = 200;

    /// <summary>ラベルを出すときの最大文字数（超えたら「…」をつけて切る）。</summary>
    public const int MaxLabelChars = 80;

    /// <summary>
    /// 構成の最大文字数（全部品の合計）。超えたら、そこで切って、その旨を書く。
    /// <para>
    /// 【なぜ <c>Vba.RibbonMaxChars</c> を流用せず、別の上限か】 <c>Vba.RibbonMaxChars</c>（既定 30000、2000〜200000）は
    /// 「XML の全文」を載せる量を利用者が決める設定で、仕様 15.5 は「構成とコールバックの一覧は、この上限とは別に常に出す」と定めている。
    /// 流用すると、XML を絞るために 2000 文字へ下げた利用者の「構成だけ」まで切れてしまい、構成の役目（骨格を把握させる）を果たせない。
    /// かといって上限なしだと、1 行ごとの長さを切っても、行数 × 行の長さの積が大きくなりうる
    /// （1 行 400 行 × コールバック 200 個 × 文字列 120 文字なら数 MB）。
    /// そこで、実物で足りる量（大きなリボンでも構成は 200 行 × 約 100 文字 = 2 万文字弱）に余裕を見た定数にする。
    /// </para>
    /// </summary>
    public const int MaxStructureChars = 40000;

    /// <summary>コールバックの照合表の最大文字数。理由は <see cref="MaxStructureChars"/> と同じ（200 行 × 約 150 文字 = 3 万文字弱の実物に余裕を見る）。</summary>
    public const int MaxCallbackChars = 40000;

    /// <summary>構成の 1 行に並べるコールバックの最大数（超えたら「ほか N 件」。全部は照合表に出る）。実物の 1 要素は多くても 10 個ほど。</summary>
    public const int MaxCallbacksPerLine = 12;

    /// <summary>
    /// 依頼文用の文にする。出すものが無いときは空の文字列。
    /// </summary>
    /// <param name="snapshot">取得結果。</param>
    /// <param name="level">出し方の 3 状態。</param>
    /// <param name="maxXmlChars">XML の全文を載せる最大文字数（<c>Vba.RibbonMaxChars</c>）。複数の部品があれば合計で数える。</param>
    public static string Format(VbaRibbonSnapshot snapshot, VbaWorkbookInfoLevel level, int maxXmlChars)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (level == VbaWorkbookInfoLevel.Hidden || !snapshot.HasContent) return string.Empty;

        var usable = snapshot.Parts.Where(p => p.IsUsable).ToArray();
        var sb = new StringBuilder();
        sb.Append(Heading).Append('\n').Append('\n');
        sb.Append("- ブック: ").Append(VbaRibbonText.OneLine(snapshot.WorkbookName)).Append('\n');
        sb.Append("- 読んだもの: **最後に保存したときのファイルの内容**です（Excel で開いて編集中の、まだ保存していない変更は含まれません）。").Append('\n');
        sb.Append("- 取得: ").Append(Stamp(snapshot.ReadAt))
          .Append(" ／ ファイルの更新: ").Append(snapshot.FileModified is { } m ? Stamp(m) : "不明").Append('\n');
        sb.Append("- 部品: ").Append(string.Join("、", usable.Select(p => $"{VbaRibbonText.OneLine(p.Raw.PartName)}（{(p.Raw.IsOffice2010Format ? "新しい形式" : "古い形式")}）"))).Append('\n');
        foreach (var bad in snapshot.Parts.Where(p => !p.IsUsable))
        {
            sb.Append("- 読めなかった部品: ").Append(VbaRibbonText.OneLine(bad.Raw.PartName)).Append("（")
              .Append(VbaRibbonText.OneLine((bad.Problem ?? bad.Raw.Problem ?? "読めませんでした。").TrimEnd('。'), 300)).Append("）").Append('\n');
        }

        sb.Append('\n').Append("## 構成").Append('\n').Append('\n');
        var structureStart = sb.Length;
        foreach (var part in usable)
        {
            if (sb.Length - structureStart >= MaxStructureChars)
            {
                sb.Append("（構成が長いため、以降の部品を省略しました）").Append('\n').Append('\n');
                break;
            }

            if (usable.Length > 1) sb.Append("### ").Append(VbaRibbonText.OneLine(part.Raw.PartName)).Append('\n').Append('\n');
            AppendStructure(sb, part, structureStart);
            sb.Append('\n');
        }

        AppendCallbacks(sb, snapshot.Callbacks);

        if (level == VbaWorkbookInfoLevel.Full) AppendXml(sb, usable, maxXmlChars);

        return sb.ToString().TrimEnd('\n') + "\n";
    }

    private static string Stamp(DateTimeOffset value)
        => value.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    private static void AppendStructure(StringBuilder sb, VbaRibbonPart part, int structureStart)
    {
        if (part.Nodes.Count == 0)
        {
            sb.Append("（タブ・グループ・コントロールの定義はありません）").Append('\n');
            return;
        }

        var shown = 0;
        foreach (var node in part.Nodes)
        {
            if (shown >= MaxStructureLines || sb.Length - structureStart >= MaxStructureChars)
            {
                sb.Append("（構成が長いため、以降の ").Append(part.Nodes.Count - shown).Append(" 要素を省略しました）").Append('\n');
                break;
            }

            // 深さ（字下げ）も、細工した値で巨大にならないよう上限を掛ける（実物は十数段）。
            // id・コールバック名などの 1 行化と長さの上限は <see cref="VbaRibbonText"/>。ここでも掛けるのは、取得結果を経由せず
            // 直接組み立てた値（テスト・将来の呼び出し）が来ても、依頼文の行が偽装・肥大化しないようにするため（掛けても結果は同じ）。
            sb.Append(' ', Math.Min(node.Depth, 32) * 2).Append("- ").Append(VbaRibbonText.OneLine(node.Display, VbaRibbonText.MaxFieldChars * 2));
            if (node.Label is not null) sb.Append(" label=\"").Append(OneLine(node.Label)).Append('"');
            foreach (var callback in node.Callbacks.Take(MaxCallbacksPerLine))
                sb.Append(' ').Append(VbaRibbonText.OneLine(callback.Attribute)).Append('=').Append(VbaRibbonText.OneLine(callback.Name));
            if (node.Callbacks.Count > MaxCallbacksPerLine)
                sb.Append(" ほか ").Append(node.Callbacks.Count - MaxCallbacksPerLine).Append(" 件のコールバック");
            sb.Append('\n');
            shown++;
        }
    }

    private static void AppendCallbacks(StringBuilder sb, IReadOnlyList<VbaRibbonCallbackCheck> callbacks)
    {
        if (callbacks.Count == 0) return;

        sb.Append("## コールバックと VBA の照合").Append('\n').Append('\n');
        sb.Append("リボンから呼ばれる VBA の手続きが、ミラー（このプロジェクトの VBA）にあるかを調べた結果です。").Append('\n').Append('\n');
        sb.Append("| コールバック | 使われている所 | VBA |").Append('\n');
        sb.Append("|---|---|---|").Append('\n');
        var tableStart = sb.Length;
        var written = 0;
        foreach (var check in callbacks.Take(MaxCallbackRows))
        {
            if (sb.Length - tableStart >= MaxCallbackChars) break;
            written++;
            var usages = check.Usages.Count <= 4
                ? string.Join("、", check.Usages)
                : string.Join("、", check.Usages.Take(4)) + $" ほか {check.Usages.Count - 4} 件";
            sb.Append("| ").Append(Cell(check.Name, VbaRibbonText.MaxFieldChars)).Append(" | ").Append(Cell(usages, 400)).Append(" | ").Append(Cell(check.Detail, 400)).Append(" |").Append('\n');
        }

        if (written < callbacks.Count)
            sb.Append('\n').Append("（コールバックが多いため、以降の ").Append(callbacks.Count - written).Append(" 件を省略しました）").Append('\n');
        sb.Append('\n');
    }

    private static void AppendXml(StringBuilder sb, IReadOnlyList<VbaRibbonPart> parts, int maxChars)
    {
        sb.Append("## XML").Append('\n').Append('\n');
        var remaining = maxChars <= 0 ? int.MaxValue : maxChars;
        foreach (var part in parts.Where(p => p.Raw.Xml is not null))
        {
            var xml = (part.Raw.Xml ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');
            sb.Append("### ").Append(VbaRibbonText.OneLine(part.Raw.PartName)).Append('\n').Append('\n');
            if (remaining <= 0)
            {
                sb.Append("（長いため、この部品の XML は省略しました。全体は ").Append(xml.Length).Append(" 文字です）").Append('\n').Append('\n');
                continue;
            }

            var cut = xml.Length > remaining ? CutPoint(xml, remaining) : xml.Length;
            var body = xml[..cut];
            var fence = new string('`', Math.Max(3, LongestBacktickRun(body) + 1));
            sb.Append(fence).Append("xml").Append('\n').Append(body).Append('\n').Append(fence).Append('\n').Append('\n');
            if (cut < xml.Length)
            {
                sb.Append("（長いため、先頭の ").Append(cut).Append(" 文字だけを載せています。全体は ").Append(xml.Length).Append(" 文字です）").Append('\n').Append('\n');
            }

            remaining -= cut;
        }
    }

    /// <summary>
    /// 切る位置。できれば行の切れ目（改行の直前）で切る（行の途中で終わる XML は読みづらい）。ただし、上限の半分より手前に
    /// しか改行が無いときは、行を切るより多く載せるほうを優先して上限ちょうどで切る。サロゲートペアの途中では切らない。
    /// </summary>
    private static int CutPoint(string text, int limit)
    {
        var newline = text.LastIndexOf('\n', Math.Min(limit, text.Length - 1));
        if (newline >= limit / 2) return newline;
        return char.IsHighSurrogate(text[limit - 1]) ? limit - 1 : limit;
    }

    private static int LongestBacktickRun(string text)
    {
        int longest = 0, run = 0;
        foreach (var c in text)
        {
            run = c == '`' ? run + 1 : 0;
            if (run > longest) longest = run;
        }

        return longest;
    }

    /// <summary>ラベルを 1 行・最大 <see cref="MaxLabelChars"/> 文字にする（ラベルは引用符で囲むので、中の <c>"</c> は <c>'</c> にする）。</summary>
    private static string OneLine(string text)
        => VbaRibbonText.OneLine(text.Replace("\"", "'"), MaxLabelChars);

    /// <summary>表のセル用。改行を空白にし、縦棒を逃がす。</summary>
    private static string Cell(string text, int max)
        => VbaRibbonText.OneLine(text, max).Replace("|", "\\|");
}
