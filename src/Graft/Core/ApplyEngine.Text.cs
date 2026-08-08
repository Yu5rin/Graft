using System.IO;
using System.Text;

namespace Graft.Core;

/// <summary>
/// <see cref="ApplyEngine"/> の分割ファイル（1ファイル400行上限のため）。
/// 6.4「改行コードの混在は可能な限り維持する」に関わるテキスト合成を担う。
/// </summary>
public sealed partial class ApplyEngine
{
    /// <summary>
    /// 6.4「改行コードの混在は可能な限り維持する」への対応。未変更行（OriginalIndexが非null）は
    /// 元ファイルの改行文字をそのまま使い、新規生成行（置換後・追記・先頭挿入・FULL全文）は
    /// TextShape.NewLineを使う。末尾改行の有無は行の由来によらずTextShape.EndsWithNewLineに従う。
    /// </summary>
    private static string ComposeFinalText(
        IReadOnlyList<ResolvedLine> lines, IReadOnlyList<(string Text, string Terminator)>? original, TextShape shape)
    {
        if (lines.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            sb.Append(lines[i].Text);
            if (i < lines.Count - 1)
            {
                sb.Append(OriginalTerminatorOrDefault(lines[i], original, shape.NewLine));
            }
            else if (shape.EndsWithNewLine)
            {
                sb.Append(shape.NewLine);
            }
        }
        return sb.ToString();
    }

    private static string OriginalTerminatorOrDefault(
        ResolvedLine line, IReadOnlyList<(string Text, string Terminator)>? original, string fallback)
    {
        if (original is null || line.OriginalIndex is not int idx || idx >= original.Count) return fallback;
        var terminator = original[idx].Terminator;
        return terminator.Length > 0 ? terminator : fallback;
    }

    /// <summary>
    /// <see cref="TextNormalizer.SplitLines"/> と同じ行区切り規則（CRLF/LF/CRいずれも境界として扱う）
    /// で分割しつつ、各行の元の改行文字列も保持する。未変更行の改行コードを書き込み時に維持するために
    /// のみ使う（比較・マッチングには関与しない）。
    /// </summary>
    private static List<(string Text, string Terminator)> SplitLinesWithTerminators(string text)
    {
        var result = new List<(string, string)>();
        var start = 0;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c != '\r' && c != '\n') { i++; continue; }

            var content = text.Substring(start, i - start);
            string terminator;
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') { terminator = "\r\n"; i++; }
            else terminator = c.ToString();
            i++;
            result.Add((content, terminator));
            start = i;
        }

        if (start < text.Length) result.Add((text.Substring(start), string.Empty));
        return result;
    }

    private static bool ClearReadOnlyIfNeeded(string fullPath, ApplyContext ctx)
    {
        if (!ctx.AllowReadOnlyOverride) return false;
        var ioPath = LongPath.Extended(fullPath);
        if (!File.Exists(ioPath)) return false;

        var info = new FileInfo(ioPath);
        if (!info.IsReadOnly) return false;
        info.IsReadOnly = false;
        return true;
    }

    private static void RestoreReadOnlyIfNeeded(string fullPath, bool wasCleared)
    {
        if (!wasCleared) return;
        var ioPath = LongPath.Extended(fullPath);
        if (!File.Exists(ioPath)) return;
        new FileInfo(ioPath).IsReadOnly = true;
    }

    // ------------------------------------------------------------------
    // DELETE（バックアップは BackupTargetsAsync で既に退避済み）
    // ------------------------------------------------------------------

    private static GraftResult<bool> ExecuteDeletes(List<BlockPlan> eligible, ApplyContext ctx, List<RevisionEntry> entries)
    {
        foreach (var p in eligible.Where(p => p.Operation == EntryOperation.Delete))
        {
            var resolved = ctx.Guard.Resolve(p.Path);
            if (!resolved.IsSuccess) return GraftResult<bool>.Fail(resolved.Issues);

            var ioPath = LongPath.Extended(resolved.Value);
            if (!File.Exists(ioPath))
            {
                entries.Add(new RevisionEntry { Path = p.Path, Operation = EntryOperation.Delete, Desc = p.Description });
                continue;
            }

            try
            {
                ClearReadOnlyIfNeeded(resolved.Value, ctx);
                File.Delete(ioPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return GraftResult<bool>.Fail(ErrorCode.E402, ExceptionMessages.Describe(ex), path: p.Path);
            }

            entries.Add(new RevisionEntry { Path = p.Path, Operation = EntryOperation.Delete, Desc = p.Description });
        }
        return GraftResult<bool>.Ok(true);
    }
}
