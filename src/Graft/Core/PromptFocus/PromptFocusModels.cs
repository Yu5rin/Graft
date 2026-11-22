namespace Graft.Core.PromptFocus;

/// <summary>
/// 依頼の観点の見出し（チップをまとめる単位）。仕様書_ブックの情報と依頼の観点 6.1。
///
/// 【文字列の定数にする理由】観点は <c>focus.json</c> にも保存され、設定画面の選択肢にもなる。
/// 文字列をあちこちに直書きすると、見出しの表記ゆれ（「きれいに」と「綺麗に」など）が
/// 保存データに紛れ込むため、唯一の定義をここに置く。並び順（<see cref="All"/>）は
/// 画面の並びであり、そのまま依頼文に連結するときの優先順位でもある（6.4）。
/// </summary>
public static class PromptFocusGroups
{
    /// <summary>きれいに（読みやすさ・保守性・差分の小ささ）。</summary>
    public const string Clean = "きれいに";
    /// <summary>正しく（不具合・エラー処理・型と警告）。</summary>
    public const string Correct = "正しく";
    /// <summary>品質（速さ・安全・テスト・説明）。</summary>
    public const string Quality = "品質";
    /// <summary>デザイン（見た目・使いやすさ・画面幅・アクセシビリティ）。</summary>
    public const string Design = "デザイン";
    /// <summary>VBA（VBA プロジェクトのときだけ出す専用の観点）。</summary>
    public const string Vba = "VBA";
    /// <summary>自分用（利用者が足した観点の既定の置き場）。</summary>
    public const string Mine = "自分用";

    /// <summary>画面に並べる順。先に書いたものほど優先される。</summary>
    public static IReadOnlyList<string> All { get; } = new[] { Clean, Correct, Quality, Design, Vba, Mine };

    /// <summary>見出しの並び順での位置。未知の見出しは「自分用」の後ろ（末尾）に回す。</summary>
    public static int IndexOf(string group)
    {
        for (var i = 0; i < All.Count; i++)
        {
            if (string.Equals(All[i], group, StringComparison.Ordinal)) return i;
        }
        return All.Count;
    }
}

/// <summary>
/// 依頼の観点1件（チップ1つ）。画面の名前（短い）・ツールチップ（一言）・AIへ渡す指示文（裏側に持つ）の3つ組。
/// 仕様書_ブックの情報と依頼の観点 4.2。
/// </summary>
public sealed record PromptFocus
{
    /// <summary>識別子。組み込みは <c>focus-readable</c> など、自作は <c>custom-&lt;Guid&gt;</c>。</summary>
    public required string Id { get; init; }

    /// <summary><see cref="PromptFocusGroups"/> のいずれか。</summary>
    public required string Group { get; init; }

    /// <summary>チップに出す短い名前。</summary>
    public required string Name { get; init; }

    /// <summary>マウスを乗せたときに出す一言の説明。</summary>
    public string Tooltip { get; init; } = string.Empty;

    /// <summary>AIへ渡す指示文。<c>## 名前</c> の見出しから始める。画面には出さない。</summary>
    public required string Body { get; init; }

    /// <summary>VBA プロジェクトのときだけ、<see cref="Body"/> の後ろへ見出しなしの箇条書きで続ける追記。</summary>
    public string? VbaAddendum { get; init; }

    /// <summary>VBA プロジェクトのときだけチップに出すか。</summary>
    public bool VbaOnly { get; init; }

    /// <summary>同梱の観点か。組み込みは <c>focus.json</c> へ書き出さず、編集・削除もできない。</summary>
    public bool IsBuiltIn { get; init; }
}
