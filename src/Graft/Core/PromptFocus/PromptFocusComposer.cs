using System.Text;

namespace Graft.Core.PromptFocus;

/// <summary>
/// 選ばれた依頼の観点を、AIへ渡す1つの文にまとめる純ロジック（COM にも UI にも触れない）。
/// 仕様書_ブックの情報と依頼の観点 6.4。
///
/// 出力の形:
/// <code>
/// # 依頼の観点
/// ## VBA の前提        ← VBA プロジェクトのときだけ・1回
/// ## &lt;チップの指示文&gt;   ← 画面の並び順。VBA のときは各チップの末尾に VBA 追記を続ける
/// ## 進め方の決まり    ← 1回だけ
/// </code>
/// <b>1つも選ばれていなければ空の文字列を返す</b>。これが「何も選ばなければ依頼文が従来とバイト単位で同一」
/// の土台で、呼び出し側（<c>PromptTemplateRenderer</c>・<c>ContextCollector</c>）は空なら何も足さない。
/// </summary>
public static class PromptFocusComposer
{
    /// <summary>観点の文の先頭に置く大見出し。</summary>
    internal const string Heading = "# 依頼の観点";

    /// <summary>
    /// 進め方の決まり（全プロジェクト共通。仕様書6.4の全文）。観点は「きれいに」が「正しく」より
    /// 上に並ぶため、並びの優先順位だけだと見た目の整形が正しさより先に効いてしまう。
    /// この文で「動作を変えないことと正しさを優先」と明示して補う（仕様書付録A-2）。
    /// </summary>
    internal const string RulesSection =
        "## 進め方の決まり\n"
        + "- 観点は、書かれている順に優先する。動作を変えないことと正しさを、見た目より優先する。\n"
        + "- 渡したファイルの中だけを変える。確かめられないものは推測で書かず、質問する。\n"
        + "- コードの前に、変更点と理由を 5 行以内で書く。";

    /// <summary>VBA の前提の最後の行。ブックの情報が依頼文に入っているとき（仕様書6.4）。</summary>
    internal const string VbaPremiseLastLineWithWorkbook =
        "- シート名・テーブル名・列は、「ブックの情報」に載っているものだけを使ってください。";

    /// <summary>
    /// VBA の前提の最後の行。ブックの情報が入っていないとき。存在しない「ブックの情報」を
    /// 指さないよう、推測せず質問させる文に差し替える（仕様書6.4）。
    /// </summary>
    internal const string VbaPremiseLastLineWithoutWorkbook =
        "- シート名・テーブル名・列は、確かめられないものを推測で使わず、質問してください。";

    private const string VbaPremiseHead =
        "## VBA の前提\n"
        + "- これは Excel VBA です。各ファイルは VBE のモジュール本文で、Attribute 行やエクスポート用のヘッダは含みません。\n"
        + "- Shift_JIS（CP932）で表せない文字（絵文字、波ダッシュ「〜」、数学のマイナス「−」など）は使わないでください。\n"
        + "- ボタン・図形に割り当てられたマクロ名（Public な Sub）、シートやブックのイベントプロシージャ（Worksheet_Change など）、ほかのブックや数式から呼ばれる関数の名前と引数は変えないでください。\n";

    /// <summary>
    /// カタログから、実際に使う観点を<b>画面の並び順</b>で取り出す。選んだ順（クリック順）には依らない。
    /// 同じ選択なら必ず同じ依頼文になるようにするため（仕様書6.3）。
    /// <list type="bullet">
    /// <item>カタログに無い Id（削除された自作の観点）は黙って捨てる（4.1）。</item>
    /// <item>VBA 専用の観点は、VBA でないプロジェクトでは落とす。</item>
    /// </list>
    /// 並びは「見出しの順（<see cref="PromptFocusGroups.All"/>）→ カタログ内の順」。画面（チップの並び）も
    /// 同じ <see cref="OrderForDisplay"/> を使うので、画面の並びと依頼文の並びは構造的に一致する。
    /// </summary>
    public static IReadOnlyList<PromptFocus> ResolveSelected(
        IEnumerable<PromptFocus> catalog, IEnumerable<string> selectedIds, bool isVba)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(selectedIds);
        var ids = new HashSet<string>(selectedIds, StringComparer.Ordinal);
        if (ids.Count == 0) return Array.Empty<PromptFocus>();

        return OrderForDisplay(catalog)
            .Where(f => ids.Contains(f.Id) && (isVba || !f.VbaOnly))
            .ToArray();
    }

    /// <summary>
    /// 画面と依頼文に共通の並び順。見出しの順に、同じ見出しの中ではカタログの順（組み込み→自作の追加順）。
    /// <see cref="Enumerable.OrderBy{TSource, TKey}(IEnumerable{TSource}, Func{TSource, TKey})"/> は安定ソートなので、
    /// 同じ見出し内の順序は保たれる。
    /// </summary>
    public static IReadOnlyList<PromptFocus> OrderForDisplay(IEnumerable<PromptFocus> catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.OrderBy(f => PromptFocusGroups.IndexOf(f.Group)).ToArray();
    }

    /// <summary>
    /// 選ばれた観点（<b>画面の並び順に並べ済み</b>であること。<see cref="ResolveSelected"/> が返す形）を、
    /// AIへ渡す文にまとめる。1つも無ければ空の文字列。
    /// </summary>
    /// <param name="selected">画面の並び順の、選ばれた観点。</param>
    /// <param name="isVba">VBA プロジェクトか。true のとき VBA の前提を1回と、各観点の VBA 追記を足す。</param>
    /// <param name="hasWorkbookInfo">ブックの情報が依頼文に入っているか（VBA の前提の最終行が変わる）。</param>
    public static string Compose(IReadOnlyList<PromptFocus> selected, bool isVba, bool hasWorkbookInfo)
    {
        ArgumentNullException.ThrowIfNull(selected);
        if (selected.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.Append(Heading).Append("\n\n");

        if (isVba)
        {
            sb.Append(VbaPremiseHead);
            sb.Append(hasWorkbookInfo ? VbaPremiseLastLineWithWorkbook : VbaPremiseLastLineWithoutWorkbook);
            sb.Append("\n\n");
        }

        foreach (var focus in selected)
        {
            sb.Append(focus.Body.TrimEnd('\r', '\n'));
            // VBA 追記は「見出しなしの箇条書きとして」本文の末尾へそのまま続ける（仕様書6.5.2）。
            // 空行を挟むと別のまとまりに見えるため、改行1つで続ける。
            if (isVba && !string.IsNullOrWhiteSpace(focus.VbaAddendum))
            {
                sb.Append('\n').Append(focus.VbaAddendum.TrimEnd('\r', '\n'));
            }
            sb.Append("\n\n");
        }

        sb.Append(RulesSection).Append('\n');
        return sb.ToString();
    }
}
