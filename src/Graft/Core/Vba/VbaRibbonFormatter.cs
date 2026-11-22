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
        sb.Append("- ブック: ").Append(snapshot.WorkbookName).Append('\n');
        sb.Append("- 読んだもの: **最後に保存したときのファイルの内容**です（Excel で開いて編集中の、まだ保存していない変更は含まれません）。").Append('\n');
        sb.Append("- 取得: ").Append(Stamp(snapshot.ReadAt))
          .Append(" ／ ファイルの更新: ").Append(snapshot.FileModified is { } m ? Stamp(m) : "不明").Append('\n');
        sb.Append("- 部品: ").Append(string.Join("、", usable.Select(p => $"{p.Raw.PartName}（{(p.Raw.IsOffice2010Format ? "新しい形式" : "古い形式")}）"))).Append('\n');
        foreach (var bad in snapshot.Parts.Where(p => !p.IsUsable))
        {
            sb.Append("- 読めなかった部品: ").Append(bad.Raw.PartName).Append("（").Append((bad.Problem ?? bad.Raw.Problem ?? "読めませんでした。").TrimEnd('。')).Append("）").Append('\n');
        }

        sb.Append('\n').Append("## 構成").Append('\n').Append('\n');
        foreach (var part in usable)
        {
            if (usable.Length > 1) sb.Append("### ").Append(part.Raw.PartName).Append('\n').Append('\n');
            AppendStructure(sb, part);
            sb.Append('\n');
        }

        AppendCallbacks(sb, snapshot.Callbacks);

        if (level == VbaWorkbookInfoLevel.Full) AppendXml(sb, usable, maxXmlChars);

        return sb.ToString().TrimEnd('\n') + "\n";
    }

    private static string Stamp(DateTimeOffset value)
        => value.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    private static void AppendStructure(StringBuilder sb, VbaRibbonPart part)
    {
        if (part.Nodes.Count == 0)
        {
            sb.Append("（タブ・グループ・コントロールの定義はありません）").Append('\n');
            return;
        }

        var shown = 0;
        foreach (var node in part.Nodes)
        {
            if (shown >= MaxStructureLines)
            {
                sb.Append("（構成が長いため、以降の ").Append(part.Nodes.Count - shown).Append(" 要素を省略しました）").Append('\n');
                break;
            }

            sb.Append(' ', node.Depth * 2).Append("- ").Append(node.Display);
            if (node.Label is not null) sb.Append(" label=\"").Append(OneLine(node.Label)).Append('"');
            foreach (var callback in node.Callbacks) sb.Append(' ').Append(callback.Attribute).Append('=').Append(callback.Name);
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
        foreach (var check in callbacks.Take(MaxCallbackRows))
        {
            var usages = check.Usages.Count <= 4
                ? string.Join("、", check.Usages)
                : string.Join("、", check.Usages.Take(4)) + $" ほか {check.Usages.Count - 4} 件";
            sb.Append("| ").Append(Cell(check.Name)).Append(" | ").Append(Cell(usages)).Append(" | ").Append(Cell(check.Detail)).Append(" |").Append('\n');
        }

        if (callbacks.Count > MaxCallbackRows)
            sb.Append('\n').Append("（コールバックが多いため、以降の ").Append(callbacks.Count - MaxCallbackRows).Append(" 件を省略しました）").Append('\n');
        sb.Append('\n');
    }

    private static void AppendXml(StringBuilder sb, IReadOnlyList<VbaRibbonPart> parts, int maxChars)
    {
        sb.Append("## XML").Append('\n').Append('\n');
        var remaining = maxChars <= 0 ? int.MaxValue : maxChars;
        foreach (var part in parts.Where(p => p.Raw.Xml is not null))
        {
            var xml = (part.Raw.Xml ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');
            sb.Append("### ").Append(part.Raw.PartName).Append('\n').Append('\n');
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

    /// <summary>ラベルを 1 行・最大 <see cref="MaxLabelChars"/> 文字にする。</summary>
    private static string OneLine(string text)
    {
        var flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Replace("\"", "'");
        return flat.Length <= MaxLabelChars ? flat : flat[..MaxLabelChars] + "…";
    }

    /// <summary>表のセル用。改行を空白にし、縦棒を逃がす。</summary>
    private static string Cell(string text)
        => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Replace("|", "\\|");
}
