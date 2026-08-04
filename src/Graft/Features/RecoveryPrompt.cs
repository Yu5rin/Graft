using System.Text;
using Graft.Core;

namespace Graft.Features;

/// <summary>
/// 仕様書11章の失敗時リカバリ支援と、4.10章の継続依頼プロンプトを生成する。
/// いずれもAIへ再投入するための日本語プレーンテキストを返す（クリップボードへのコピー自体は
/// 呼び出し側のUIが行う）。
/// </summary>
public static class RecoveryPrompt
{
    private const int ContextLines = 20;

    /// <summary>11章 失敗ブロックの再依頼文を生成する。</summary>
    public static string Build(IReadOnlyList<BlockPlan> failedPlans, Func<string, string?> readCurrentText)
    {
        var sb = new StringBuilder();
        AppendLine(sb, "以下のブロックの適用に失敗しました。現在の実際のコードを示すので、");
        AppendLine(sb, "これに一致するSEARCH部で再出力してください。");

        foreach (var plan in failedPlans)
        {
            AppendLine(sb, string.Empty);
            AppendBlockSection(sb, plan, readCurrentText);
        }

        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>4.10 切断時の継続依頼プロンプトを生成する。末尾3行のみを含める。</summary>
    public static string BuildContinuation(IReadOnlyList<string> tailLines)
    {
        var sb = new StringBuilder();
        AppendLine(sb, "出力が途中で切れています。以下の続きから、同じGraft形式で出力してください。");
        AppendLine(sb, "最後に受け取った行:");

        var tail = tailLines.Count <= 3 ? tailLines : tailLines.Skip(tailLines.Count - 3).ToList();
        foreach (var line in tail)
        {
            AppendLine(sb, line);
        }

        return sb.ToString().TrimEnd('\n');
    }

    private static void AppendBlockSection(StringBuilder sb, BlockPlan plan, Func<string, string?> readCurrentText)
    {
        var issue = plan.Issues.FirstOrDefault(i => i.Severity == Severity.Error) ?? plan.Issues.FirstOrDefault();
        AppendLine(sb, $"■ {plan.Path} — {ReasonText(issue)}");

        var searchLines = ResolveSearchLines(plan.Block, issue);
        var currentText = searchLines.Count > 0 ? readCurrentText(plan.Path) : null;
        if (string.IsNullOrEmpty(currentText))
        {
            AppendLine(sb, "現在のコードを取得できませんでした。");
            return;
        }

        var fileLines = TextNormalizer.SplitLines(currentText);
        var (startLine, endLine, snippet) = ExtractContext(fileLines, searchLines);
        AppendLine(sb, $"現在のコード（{startLine}〜{endLine}行目）:");
        AppendLine(sb, snippet);
    }

    private static string ReasonText(GraftIssue? issue)
    {
        if (issue is null) return "適用に失敗しました";
        // 仕様書11章の例文と一致させる（カタログの言い切り表現とは語尾のみ異なる）。
        return issue.Code == ErrorCode.E101 ? "SEARCH部が見つかりません" : issue.Summary;
    }

    private static IReadOnlyList<string> ResolveSearchLines(PatchBlock block, GraftIssue? issue)
    {
        if (block is not SearchReplaceBlock srBlock || srBlock.Pairs.Count == 0)
        {
            return Array.Empty<string>();
        }

        SearchReplacePair? matched = issue?.LineNumber is int line
            ? srBlock.Pairs.FirstOrDefault(p => p.SourceLine == line)
            : null;
        var pair = matched ?? srBlock.Pairs[0];
        return TextNormalizer.SplitLines(pair.SearchText);
    }

    private static (int StartLine, int EndLine, string Snippet) ExtractContext(
        IReadOnlyList<string> fileLines, IReadOnlyList<string> searchLines)
    {
        if (fileLines.Count == 0)
        {
            return (0, 0, string.Empty);
        }

        // 閾値0で常に「最も類似する箇所」を採用する（要確認扱いにはしない。あくまで文面生成用）。
        var best = SimilarityScorer.FindBestMatch(fileLines, searchLines, threshold: 0.0);
        var startLine = best?.StartLine ?? 0;
        var lineCount = best?.LineCount ?? Math.Min(searchLines.Count, fileLines.Count);

        var contextStart = Math.Max(0, startLine - ContextLines);
        var contextEndExclusive = Math.Min(fileLines.Count, startLine + lineCount + ContextLines);
        var snippet = string.Join("\n", fileLines.Skip(contextStart).Take(contextEndExclusive - contextStart));
        return (contextStart + 1, contextEndExclusive, snippet);
    }

    private static void AppendLine(StringBuilder sb, string text) => sb.Append(text).Append('\n');
}
