using System.IO;

namespace Graft.Core;

/// <summary>
/// クリップボードの内容が「パッチらしいテキスト」かどうかを判定する（仕様書9章・10章）。
///
/// 判定はブロックヘッダの行頭一致だけで行い、内容はどこにも保持しない。
/// OSごとのクリップボード監視実装（<c>Platform/Windows</c>・<c>Platform/Linux</c>）が
/// 共通して使えるよう、UI・OSのいずれにも依存しないCore層に置く。
/// </summary>
public static class PatchTextDetector
{
    private static readonly string[] HeaderPrefixes =
    {
        "<<<< FILE:",
        "<<<< PATCH",
        "<<<< DELETE:",
        "<<<< RENAME:",
        "<<<< MKDIR:",
        "<<<< APPEND:",
        "<<<< PREPEND:",
        "<<<<<<< SEARCH",
    };

    /// <summary>
    /// テキストがブロックヘッダのパターンを行頭に含むかどうかを判定する。
    /// パッチらしいと判定できない通常のコピー内容はここで弾かれ、以降一切処理しない。
    /// </summary>
    public static bool LooksLikePatch(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            foreach (var prefix in HeaderPrefixes)
            {
                if (line.StartsWith(prefix, StringComparison.Ordinal)) return true;
            }
        }

        return false;
    }
}
