using System.Linq;
using System.Text;

namespace Graft.Core;

/// <summary>
/// ブロック・差分表示の「unified diff 形式でコピー」（右クリックメニュー）向けの整形。
/// AIとの往復で「この変更のここを直して」と貼り戻せる形にすることが目的のため、
/// 出力は既存の取り込み側である<see cref="UnifiedDiffAdapter"/>がそのまま解析できる形式
/// （<c>--- a/…</c> / <c>+++ b/…</c> ヘッダ対 + <c>@@</c> ハンク）に揃える。
/// <see cref="UnifiedDiffAdapter"/>はハンク見出しの行番号を一切解釈しない
/// （文脈行だけを頼りに位置を特定する）ため、ここでの行番号計算は「人間が読んで違和感が無い」
/// 程度の実用精度で足り、DiffPlexの行対応から素直に積み上げる。
/// </summary>
public static class UnifiedDiffFormatter
{
    /// <summary>
    /// 前後の折りたたみ文脈行数。0にすると変更行だけが並び、周辺の見分けが付きにくくなるため、
    /// 一般的な `git diff` と同じ既定値（3行）に合わせる。
    /// </summary>
    private const int ContextLines = 3;

    /// <summary>
    /// 指定ブロック（または差分表示中の1ファイル分）を unified diff 形式のテキストへ整形する。
    /// </summary>
    /// <param name="relativePath">プロジェクト相対パス（表示用。"a/"・"b/"を前置してヘッダへ出す）。</param>
    /// <param name="beforeText">変更前の全文。新規作成では null。</param>
    /// <param name="afterText">変更後の全文。削除では null。</param>
    public static string Format(string relativePath, string? beforeText, string? afterText)
    {
        var sb = new StringBuilder();
        AppendFileHeader(sb, relativePath, beforeText, afterText);

        var model = DiffBuilder.Build(relativePath, beforeText, afterText, ContextLines);
        var oldPos = 1;
        var newPos = 1;

        foreach (var hunk in model.Hunks)
        {
            if (IsOmittedOnly(hunk, out var omittedCount))
            {
                oldPos += omittedCount;
                newPos += omittedCount;
                continue;
            }

            AppendHunk(sb, hunk, ref oldPos, ref newPos);
        }

        return sb.ToString();
    }

    private static void AppendFileHeader(StringBuilder sb, string relativePath, string? beforeText, string? afterText)
    {
        var oldLabel = beforeText is null ? "/dev/null" : $"a/{relativePath}";
        var newLabel = afterText is null ? "/dev/null" : $"b/{relativePath}";
        sb.Append("--- ").Append(oldLabel).Append('\n');
        sb.Append("+++ ").Append(newLabel).Append('\n');
    }

    private static bool IsOmittedOnly(DiffHunk hunk, out int omittedCount)
    {
        if (hunk.Lines.Count == 1 && hunk.Lines[0].Kind == DiffLineKind.Omitted)
        {
            omittedCount = hunk.Lines[0].OmittedCount;
            return true;
        }
        omittedCount = 0;
        return false;
    }

    private static void AppendHunk(StringBuilder sb, DiffHunk hunk, ref int oldPos, ref int newPos)
    {
        var oldCount = hunk.Lines.Count(l => l.Kind != DiffLineKind.Added);
        var newCount = hunk.Lines.Count(l => l.Kind != DiffLineKind.Removed);
        var oldStart = oldCount > 0 ? oldPos : Math.Max(oldPos - 1, 0);
        var newStart = newCount > 0 ? newPos : Math.Max(newPos - 1, 0);

        sb.Append("@@ -").Append(oldStart).Append(',').Append(oldCount)
          .Append(" +").Append(newStart).Append(',').Append(newCount).Append(" @@\n");

        foreach (var line in hunk.Lines)
        {
            var marker = line.Kind switch
            {
                DiffLineKind.Added => '+',
                DiffLineKind.Removed => '-',
                _ => ' ',
            };
            sb.Append(marker).Append(line.Text).Append('\n');
        }

        oldPos += oldCount;
        newPos += newCount;
    }
}
