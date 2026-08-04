using Graft.Core;

namespace Graft.Features;

/// <summary>
/// 仕様書4.8.2の変数展開を行う。<c>{{files}}</c>・<c>{{tree}}</c> の展開は
/// <see cref="ContextCollector"/> の <c>BuildFilesTextAsync</c>・<c>BuildTreeTextAsync</c> を
/// そのまま呼び出すことで、4.8.4「コンテキスト収集とは同一の出力パイプラインを共有する」を満たす。
/// </summary>
public sealed class PromptTemplateRenderer
{
    private readonly ContextCollector _collector;

    public PromptTemplateRenderer(ContextCollector collector)
    {
        _collector = collector;
    }

    /// <summary>
    /// テンプレート本文中の {{standingContext}} {{tree}} {{files}} {{projectName}} {{lastRevision}}
    /// を実際の値へ展開する。テンプレートが使わない変数の収集処理は呼び出さない
    /// （調査依頼テンプレートで {{tree}} を使わない場合にツリー走査をしない等）。
    /// </summary>
    public async Task<GraftResult<string>> RenderAsync(
        PromptTemplate template, ContextRequest request, string? lastRevisionSummary, CancellationToken ct = default)
    {
        var body = template.Body;
        var issues = new List<GraftIssue>();

        if (body.Contains("{{tree}}", StringComparison.Ordinal))
        {
            var tree = await _collector.BuildTreeTextAsync(request.Project, request.Settings, ct).ConfigureAwait(false);
            if (!tree.IsSuccess) return GraftResult<string>.Fail(tree.Issues);
            body = body.Replace("{{tree}}", tree.Value, StringComparison.Ordinal);
            issues.AddRange(tree.Issues);
        }

        if (body.Contains("{{files}}", StringComparison.Ordinal))
        {
            var files = await _collector.BuildFilesTextAsync(request, ct).ConfigureAwait(false);
            if (!files.IsSuccess) return GraftResult<string>.Fail(files.Issues);
            body = body.Replace("{{files}}", files.Value, StringComparison.Ordinal);
            issues.AddRange(files.Issues);
        }

        body = body
            .Replace("{{standingContext}}", request.Project.StandingContext ?? string.Empty, StringComparison.Ordinal)
            .Replace("{{projectName}}", request.Project.Name, StringComparison.Ordinal)
            .Replace("{{lastRevision}}", lastRevisionSummary ?? string.Empty, StringComparison.Ordinal);

        return GraftResult<string>.Ok(body, issues);
    }
}
