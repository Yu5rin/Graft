using System.Collections.Concurrent;
using System.IO;
using Graft.Core;
using Graft.Infra;

namespace Graft.Features;

/// <summary>AI向けプロンプトテンプレート。仕様書4.8。</summary>
public sealed record PromptTemplate
{
    /// <summary>識別子。</summary>
    public required string Id { get; init; }
    /// <summary>表示名。</summary>
    public required string Name { get; init; }
    /// <summary>本文。<c>{{standingContext}}</c> 等の変数を含められる（4.8.2）。</summary>
    public required string Body { get; init; }
    /// <summary>同梱の既定テンプレートかどうか。既定テンプレートは <see cref="PromptTemplateStore.SaveAsync"/> で保存しない。</summary>
    public bool IsBuiltIn { get; init; }
    /// <summary>継続用の短縮版かどうか（4.8.1）。</summary>
    public bool IsContinuation { get; init; }
}

/// <summary>templates.json のルート要素。</summary>
internal sealed record PromptTemplateFile
{
    public IReadOnlyList<PromptTemplate> Templates { get; init; } = Array.Empty<PromptTemplate>();
}

/// <summary>
/// 仕様書4.8のプロンプトテンプレート管理を担う。既定テンプレート（<see cref="BuiltIns"/>）は
/// 仕様書4.8・4.8.1・4.8.3の本文を一字一句そのまま同梱し、ユーザー定義テンプレートは
/// <c>templates.json</c> へ保存する。4.8.1の「直近1時間以内にコピー済みか」の判定はプロセス内
/// メモリのみで保持し、永続化はしない（仕様上も履歴の永続化は不要）。
/// </summary>
public sealed class PromptTemplateStore
{
    private readonly AppPaths _paths;
    private readonly JsonFileStore _jsonStore = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastCopyAt = new();

    public PromptTemplateStore(AppPaths paths)
    {
        _paths = paths;
    }

    /// <summary>既定テンプレートの一覧（4.8「初回用（完全版）」、4.8.1の短縮版、4.8.3の3種類）。</summary>
    public static IReadOnlyList<PromptTemplate> BuiltIns { get; } = BuildBuiltIns();

    /// <summary>templates.json のパス。settings.json 等と同じ階層に置く。</summary>
    public string TemplatesFilePath => _paths.TemplatesFilePath;

    /// <summary>既定テンプレートとユーザー定義テンプレートを合わせて返す。</summary>
    public async Task<GraftResult<IReadOnlyList<PromptTemplate>>> LoadAsync(CancellationToken ct = default)
    {
        var result = await _jsonStore.ReadWithRecoveryAsync(
            TemplatesFilePath,
            () => new PromptTemplateFile(),
            ct: ct).ConfigureAwait(false);
        if (!result.IsSuccess) return GraftResult<IReadOnlyList<PromptTemplate>>.Fail(result.Issues);

        var custom = result.Value.Templates.Where(t => !t.IsBuiltIn).ToList();
        var merged = BuiltIns.Concat(custom).ToList();
        return GraftResult<IReadOnlyList<PromptTemplate>>.Ok(merged, result.Issues);
    }

    /// <summary>ユーザー定義テンプレートを保存する。既定テンプレートは書き出さない（常にコード側で供給する）。</summary>
    public async Task<GraftResult<bool>> SaveAsync(IReadOnlyList<PromptTemplate> templates, CancellationToken ct = default)
    {
        var custom = templates.Where(t => !t.IsBuiltIn).ToArray();
        await _jsonStore.WriteAsync(TemplatesFilePath, new PromptTemplateFile { Templates = custom }, ct: ct).ConfigureAwait(false);
        return GraftResult<bool>.Ok(true);
    }

    /// <summary>指定プロジェクトでテンプレートをコピーした日時を記録する（4.8.1）。</summary>
    public void RecordCopy(string projectId, DateTimeOffset at)
    {
        _lastCopyAt[projectId] = at;
    }

    /// <summary>
    /// 同一プロジェクトで直近1時間以内にコピー済みなら true を返す。true のとき、
    /// 呼び出し側は継続用の短縮版（<see cref="PromptTemplate.IsContinuation"/>）を既定表示にする。
    /// </summary>
    public bool ShouldUseContinuation(string projectId, DateTimeOffset now)
    {
        if (!_lastCopyAt.TryGetValue(projectId, out var last)) return false;
        return now - last <= TimeSpan.FromHours(1);
    }

    /// <summary>
    /// エディタの選択範囲から修正依頼プロンプトを組み立てる（右クリックメニュー
    /// 「選択範囲の修正依頼プロンプトをコピー」）。「修正依頼」テンプレートの形式指示
    /// （<see cref="FixRequestFormatInstruction"/>）に続けて、対象ファイルのプロジェクト相対パス・
    /// 行範囲・選択コード（``` フェンス。拡張子から言語名を付けられれば付ける）を並べ、末尾に
    /// 依頼内容を書き足すための誘導行を置く。10章のコンテキスト収集を介さない単発の依頼のため、
    /// <c>{{standingContext}}</c>・<c>{{files}}</c> のようなプレースホルダは使わない。
    /// UIに依存しない純粋メソッドとして置き、単体テストで検証できるようにする。
    /// </summary>
    /// <param name="relativePath">対象ファイルのプロジェクト相対パス。</param>
    /// <param name="startLine">選択範囲の開始行（1始まり）。</param>
    /// <param name="endLine">選択範囲の終了行（1始まり）。</param>
    /// <param name="selectedCode">選択されたコード本文。</param>
    /// <param name="fileExtension">対象ファイルの拡張子（先頭の <c>.</c> の有無は問わない）。</param>
    public static string BuildSelectionFixRequestPrompt(
        string relativePath, int startLine, int endLine, string selectedCode, string fileExtension)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentNullException.ThrowIfNull(selectedCode);
        ArgumentNullException.ThrowIfNull(fileExtension);

        var fenceLanguage = FenceLanguageForExtension(fileExtension);
        var code = selectedCode.EndsWith('\n') ? selectedCode : selectedCode + "\n";

        return FixRequestFormatInstruction +
            $"\n\n対象: {relativePath}（{startLine}〜{endLine}行目）\n\n" +
            $"```{fenceLanguage}\n{code}```\n\n" +
            "このコードの修正を依頼します。修正内容: ";
    }

    /// <summary>
    /// コードフェンスに付ける言語識別子。<see cref="Graft.Core.LanguageRule"/>の
    /// シンタックスハイライト対応拡張子と合わせるが、フェンス表記はMarkdownで一般的な言語名
    /// （<c>csharp</c>等）を使う。対応外の拡張子は言語名なしのフェンス（<c>```</c>のみ）にする。
    /// </summary>
    private static string FenceLanguageForExtension(string extension)
    {
        var normalized = (extension.StartsWith('.') ? extension[1..] : extension).Trim().ToLowerInvariant();
        return normalized switch
        {
            "cs" => "csharp",
            "py" => "python",
            "js" or "jsx" => "javascript",
            "ts" or "tsx" => "typescript",
            "json" => "json",
            "html" or "htm" => "html",
            "xml" or "axaml" or "xaml" => "xml",
            "css" => "css",
            "md" => "markdown",
            "sh" or "bash" => "bash",
            "sql" => "sql",
            "yaml" or "yml" => "yaml",
            _ => string.Empty,
        };
    }

    private static IReadOnlyList<PromptTemplate> BuildBuiltIns() => new[]
    {
        new PromptTemplate { Id = "builtin-full", Name = "初回用（完全版）", Body = FullBody, IsBuiltIn = true },
        new PromptTemplate { Id = "builtin-continuation", Name = "継続用（短縮版）", Body = ContinuationBody, IsBuiltIn = true, IsContinuation = true },
        new PromptTemplate { Id = "builtin-fix-request", Name = "修正依頼", Body = FixRequestBody, IsBuiltIn = true },
        new PromptTemplate { Id = "builtin-new-file", Name = "新規実装", Body = NewFileBody, IsBuiltIn = true },
        new PromptTemplate { Id = "builtin-investigate", Name = "調査依頼", Body = InvestigateBody, IsBuiltIn = true },
    };

    /// <summary>仕様書4.9末尾「プロンプトテンプレートにもこの規則を記載し、AIがエスケープ済みの形で出力するよう指示する。」に対応する追記。</summary>
    private const string EscapeRuleNote =
        "【エスケープ規則】ブロック本文の行頭に <<<<、>>>>、======= が現れる場合は、直前に \\ を置いて" +
        "エスケープしてください（例: \\<<<< FILE: 相対パス）。パーサ適用時に先頭の \\ を1つ取り除きます。" +
        "多数のエスケープが必要な場合は、ヘッダに FENCE=<任意文字列> を指定し、終了マーカーを " +
        ">>>> END:<任意文字列> に変更できます。";

    /// <summary>仕様書4.8「初回用（完全版）」の本文（一字一句そのまま）。</summary>
    private const string FullBody =
        "コードの修正を提案する際は、必ず以下の形式で出力してください。\n" +
        "\n" +
        "まずパッチ全体の要約を書きます。\n" +
        "<<<< PATCH\n" +
        "summary: （変更内容を1行で）\n" +
        "type: feat|fix|refactor|docs|test|chore\n" +
        ">>>>\n" +
        "\n" +
        "【原則】既存ファイルの修正はSEARCH/REPLACE形式を使い、\n" +
        "ファイル全文の再出力はしないでください。\n" +
        "\n" +
        "<<<< FILE: 相対パス\n" +
        "<<<<<<< SEARCH  # このペアの変更内容を1行で\n" +
        "（修正前のコード。一意に特定できる最小限の行数）\n" +
        "=======\n" +
        "（修正後のコード）\n" +
        ">>>>>>> REPLACE\n" +
        "\n" +
        "新規ファイルのみ以下を使用してください。\n" +
        "<<<< FILE: 相対パス MODE=FULL\n" +
        "（全文）\n" +
        ">>>> END\n" +
        "\n" +
        "説明文はブロックの外に書いてください。";

    /// <summary>仕様書4.8.1 継続用の短縮テンプレートの本文（一字一句そのまま）。</summary>
    private const string ContinuationBody = "先ほどと同じGraft形式（PATCHメタ + SEARCH/REPLACE）で出力してください。";

    /// <summary>
    /// 4.8.3「修正依頼」の形式指示部分（standingContext/filesを含まない）。単体でも、
    /// 選択範囲からの修正依頼プロンプト（<see cref="BuildSelectionFixRequestPrompt"/>）でも使う。
    /// </summary>
    private const string FixRequestFormatInstruction =
        "コードの修正を提案する際は、必ず以下の形式で出力してください。\n" +
        "\n" +
        "まずパッチ全体の要約を書きます。\n" +
        "<<<< PATCH\n" +
        "summary: （変更内容を1行で）\n" +
        "type: feat|fix|refactor|docs|test|chore\n" +
        ">>>>\n" +
        "\n" +
        "【原則】既存ファイルの修正はSEARCH/REPLACE形式を使い、\n" +
        "ファイル全文の再出力はしないでください。\n" +
        "\n" +
        "<<<< FILE: 相対パス\n" +
        "<<<<<<< SEARCH  # このペアの変更内容を1行で\n" +
        "（修正前のコード。一意に特定できる最小限の行数）\n" +
        "=======\n" +
        "（修正後のコード）\n" +
        ">>>>>>> REPLACE\n" +
        "\n" +
        "説明文はブロックの外に書いてください。\n" +
        "\n" +
        EscapeRuleNote;

    /// <summary>4.8.3「修正依頼」: 形式指示（SR優先）＋standingContext＋files。</summary>
    private const string FixRequestBody =
        FixRequestFormatInstruction + "\n\n# 前提\n{{standingContext}}\n\n# 対象ファイル\n{{files}}";

    /// <summary>4.8.3「新規実装」: 形式指示（FULL許可）＋standingContext＋tree。</summary>
    private const string NewFileBody =
        "コードの新規作成を提案する際は、必ず以下の形式で出力してください。\n" +
        "\n" +
        "まずパッチ全体の要約を書きます。\n" +
        "<<<< PATCH\n" +
        "summary: （変更内容を1行で）\n" +
        "type: feat|fix|refactor|docs|test|chore\n" +
        ">>>>\n" +
        "\n" +
        "新規ファイルは以下の形式を使用してください。\n" +
        "<<<< FILE: 相対パス MODE=FULL\n" +
        "（全文）\n" +
        ">>>> END\n" +
        "\n" +
        "説明文はブロックの外に書いてください。\n" +
        "\n" +
        EscapeRuleNote +
        "\n\n# 前提\n{{standingContext}}\n\n# プロジェクト構成\n{{tree}}";

    /// <summary>4.8.3「調査依頼」: 「まず原因を説明し、修正案の合意が取れてからコードを出力してください」＋files。</summary>
    private const string InvestigateBody =
        "まず原因を説明し、修正案の合意が取れてからコードを出力してください。\n\n# 対象ファイル\n{{files}}";
}
