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
    /// <see cref="RenderAsync"/>が{{files}}を持たないテンプレートの末尾に足すファイル部分の見出し。
    /// 組み込みの「修正依頼」「調査依頼」テンプレートが{{files}}の直前に置いている見出しと同じ表記にし、
    /// 付けた場合と本文に{{files}}がある場合で、AIが受け取る形が変わらないようにしてある。
    /// </summary>
    internal const string AppendedFilesHeading = "# 対象ファイル";

    /// <summary>
    /// テンプレート本文中の {{standingContext}} {{tree}} {{files}} {{projectName}} {{lastRevision}}
    /// を実際の値へ展開する。テンプレートが使わない変数の収集処理は呼び出さない
    /// （調査依頼テンプレートで {{tree}} を使わない場合にツリー走査をしない等）。
    ///
    /// 【appendFilesIfAbsent: 指示文とファイルを1回のコピーで渡す】既定の「初回用（完全版）」のように
    /// {{files}} を含まないテンプレートでは、指示文のコピーとファイルのコピーを別々に行って
    /// 2回貼る必要があった。true のときは、本文に {{files}} が**無い**場合に限り、展開後の末尾へ
    /// 「# 対象ファイル」の見出しとコンテキスト収集の選択（{{files}} と同じ内容）を足す。
    /// 本文に {{files}} がある場合は何もしない（二重に付かない）。**テンプレートの本文そのもの
    /// （組み込み・利用者が編集したもの）は書き換えず**、展開結果にだけ足す。
    /// 選択ファイルが1つも無い・収集モードがファイルを出さない設定（ツリーのみ）のように
    /// 足す中身が空のときは、見出しだけが残らないよう何も足さない。
    /// </summary>
    public async Task<GraftResult<string>> RenderAsync(
        PromptTemplate template, ContextRequest request, string? lastRevisionSummary, CancellationToken ct = default,
        bool appendFilesIfAbsent = false)
    {
        var body = template.Body;
        var issues = new List<GraftIssue>();
        var hasFilesVariable = body.Contains("{{files}}", StringComparison.Ordinal);

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

        string? appendedFiles = null;
        if (appendFilesIfAbsent && !hasFilesVariable)
        {
            var files = await _collector.BuildFilesTextAsync(request, ct).ConfigureAwait(false);
            if (!files.IsSuccess) return GraftResult<string>.Fail(files.Issues);
            issues.AddRange(files.Issues);
            if (!string.IsNullOrWhiteSpace(files.Value)) appendedFiles = files.Value;
        }

        body = body
            .Replace("{{standingContext}}", request.Project.StandingContext ?? string.Empty, StringComparison.Ordinal)
            .Replace("{{projectName}}", request.Project.Name, StringComparison.Ordinal)
            .Replace("{{lastRevision}}", lastRevisionSummary ?? string.Empty, StringComparison.Ordinal);

        // 足すのは変数の展開が済んだあと。ファイルの中身に "{{standingContext}}" のような文字列が
        // 含まれていても、置換の対象にならないようにするため。
        if (appendedFiles is not null)
        {
            body = body.TrimEnd('\r', '\n') + "\n\n" + AppendedFilesHeading + "\n" + appendedFiles;
        }

        return GraftResult<string>.Ok(body, issues);
    }
}
