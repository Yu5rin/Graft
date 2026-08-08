namespace Graft.Features;

/// <summary>
/// プロジェクト名を表示用に正規化する。実機検証で発見した不具合2への対応
/// （projects.jsonが破損・改竄等で異常な値を持っていても、一覧表示が壊れないようにする）。
/// <para>
/// 正規化はここ（表示用の <see cref="Project.DisplayName"/> を通す時点）でのみ行い、
/// projects.json 上の生の <see cref="Project.Name"/> は書き換えない。理由は2つ:
/// (1) 起動のたびに壊れたファイルを読み直す運用のため、保存済みデータを一度だけ直す方式では
///     手動編集や外部ツールによる再破損に追随できない。都度算出する方式なら常に安全側になる。
/// (2) Name はプロジェクトの識別・比較（フックのプロジェクト選択・プロンプトの{{projectName}}
///     置換等）にも使われており、表示専用の正規化を混ぜると意味が変わってしまう。
/// </para>
/// </summary>
public static class ProjectNameFormatter
{
    /// <summary>名前が空・空白のみ・フォルダ名も取れない場合に使う既定表示。</summary>
    public const string Placeholder = "(名前なし)";

    /// <summary>
    /// 表示用に名前を正規化する。改行・タブは空白へ置き換え、前後の空白を除去する。
    /// 結果が空になる場合はフォルダ名（<paramref name="root"/> の末尾要素）で代替し、
    /// それも取れない場合は <see cref="Placeholder"/> を返す。
    /// 長さの上限はここでは設けない（表示側のUI（ComboBox/一覧）の最大幅＋省略記号で対応する）。
    /// </summary>
    public static string Normalize(string? rawName, string? root)
    {
        var normalized = NormalizeWhitespace(rawName);
        if (normalized.Length > 0)
        {
            return normalized;
        }

        var folderName = TryGetFolderName(root);
        return string.IsNullOrEmpty(folderName) ? Placeholder : folderName;
    }

    private static string NormalizeWhitespace(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        // 改行・タブは空白1つに置き換える。一覧・ドロップダウンが2行以上に崩れる不具合への対応。
        var replaced = raw.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        return replaced.Trim();
    }

    /// <summary>ルートパスの末尾要素（フォルダ名）を取り出す。区切り文字はOSを問わず両方認識する。</summary>
    private static string? TryGetFolderName(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        var trimmed = root.TrimEnd('/', '\\');
        if (trimmed.Length == 0)
        {
            return null;
        }

        var lastSeparator = trimmed.LastIndexOfAny(new[] { '/', '\\' });
        var name = lastSeparator >= 0 ? trimmed[(lastSeparator + 1)..] : trimmed;
        name = NormalizeWhitespace(name);
        return name.Length == 0 ? null : name;
    }
}
