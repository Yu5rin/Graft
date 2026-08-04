using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using Graft.Infra;

namespace Graft.Core;

/// <summary>
/// history.jsonl の1行に対応する最小限のリビジョン情報。仕様書13.1「バックアップフォルダが
/// 外部から削除・移動された場合も履歴の記録自体は残す」を満たすため、リビジョンフォルダの
/// 外側（<c>back/&lt;プロジェクトID&gt;/history.jsonl</c>）に保持する。ファイルの中身（entries）は
/// 記録しない（15章の「ファイルの中身そのものは記録しない」方針に合わせる）。
/// </summary>
public sealed record RevisionIndexEntry
{
    /// <summary>リビジョン番号。</summary>
    public int Revision { get; init; }

    /// <summary>変更の要約。</summary>
    public string? Summary { get; init; }

    /// <summary>変更の種別。</summary>
    public string? Type { get; init; }

    /// <summary>適用日時。</summary>
    public DateTimeOffset AppliedAt { get; init; }

    /// <summary>状態。<see cref="RevisionStatus"/> の値。</summary>
    public string Status { get; init; } = RevisionStatus.InProgress;

    /// <summary>パッチ本文の正規化ハッシュ。</summary>
    public string? PatchHash { get; init; }

    /// <summary>統計。</summary>
    public RevisionStats Stats { get; init; } = new();

    /// <summary>リビジョンフォルダ名（例: r24_20260804_143052）。</summary>
    public required string FolderName { get; init; }
}

/// <summary>
/// <c>back/&lt;プロジェクトID&gt;/history.jsonl</c> の読み書きを行う。JSON Lines形式で
/// 1リビジョン1行を追記する。仕様書13.1を満たすための補助的な索引であり、manifest.json の
/// 代替ではない（ファイル内容の正本は各リビジョンフォルダの manifest.json のまま）。
/// </summary>
public sealed class RevisionIndex
{
    private static readonly JsonSerializerOptions LineOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
    };

    private readonly AppPaths _paths;

    public RevisionIndex(AppPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    /// <summary>history.jsonl の絶対パスを返す。</summary>
    public string GetIndexPath(string projectId)
        => Path.Combine(_paths.GetProjectBackupDirectory(projectId), "history.jsonl");

    /// <summary>
    /// history.jsonl を全行読み込む。ファイルが存在しない場合は空一覧を返す（エラーにしない）。
    /// 一部の行が解析できない場合はその行だけ読み飛ばし、Warningのissueとして通知する。
    /// ファイル自体が読み取れない場合も空一覧とWarningを返し、失敗（Fail）にはしない。
    /// E405（実体が見つからない）はここでは使わない。実体フォルダ側の判定に用いる。
    /// </summary>
    public async Task<GraftResult<IReadOnlyList<RevisionIndexEntry>>> ReadAllAsync(
        string projectId, CancellationToken ct = default)
    {
        var path = GetIndexPath(projectId);
        if (!File.Exists(path))
        {
            return GraftResult<IReadOnlyList<RevisionIndexEntry>>.Ok(Array.Empty<RevisionIndexEntry>());
        }

        string[] lines;
        try
        {
            lines = await File.ReadAllLinesAsync(LongPath.Extended(path), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var warning = GraftIssue.Of(
                ErrorCode.E404, $"history.jsonl を読み取れませんでした: {ex.Message}", path: path, severity: Severity.Warning);
            return GraftResult<IReadOnlyList<RevisionIndexEntry>>.Ok(Array.Empty<RevisionIndexEntry>(), new[] { warning });
        }

        return ParseLines(path, lines);
    }

    /// <summary>1件のリビジョンをhistory.jsonlへ追記する。呼び出し側でディレクトリの存在を保証する必要はない。</summary>
    public async Task AppendAsync(string projectId, RevisionIndexEntry entry, CancellationToken ct = default)
    {
        var path = GetIndexPath(projectId);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(entry, LineOptions);
        await File.AppendAllTextAsync(LongPath.Extended(path), json + Environment.NewLine, ct).ConfigureAwait(false);
    }

    private static GraftResult<IReadOnlyList<RevisionIndexEntry>> ParseLines(string path, string[] lines)
    {
        var entries = new List<RevisionIndexEntry>();
        var brokenLines = 0;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var entry = JsonSerializer.Deserialize<RevisionIndexEntry>(line, LineOptions);
                if (entry is not null)
                {
                    entries.Add(entry);
                }
            }
            catch (JsonException)
            {
                brokenLines++;
            }
        }

        var issues = brokenLines > 0
            ? new[]
            {
                GraftIssue.Of(
                    ErrorCode.E404, $"history.jsonl の一部の行を解析できませんでした（{brokenLines}行）",
                    path: path, severity: Severity.Warning),
            }
            : Array.Empty<GraftIssue>();

        return GraftResult<IReadOnlyList<RevisionIndexEntry>>.Ok(entries, issues);
    }
}
