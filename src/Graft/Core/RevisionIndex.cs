using System.IO;
using System.Runtime.InteropServices;
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
                ErrorCode.E404, $"history.jsonl を読み取れませんでした: {ExceptionMessages.Describe(ex)}", path: path, severity: Severity.Warning);
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

/// <summary>
/// Windowsのごみ箱へファイル・フォルダを送るための最小限のP/Invokeラッパー。仕様書7.4の
/// 世代管理で使う。<c>Microsoft.VisualBasic</c> への参照を追加しないため、shell32.dll の
/// <c>SHFileOperationW</c> を直接呼び出す（A.3の依存関係制約に準拠）。
/// </summary>
internal static class RecycleBin
{
    private const uint FoDelete = 0x0003;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofSilent = 0x0004;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHFileOperationW(ref ShFileOpStruct fileOp);

    /// <summary>指定パス（ファイルまたはフォルダ）をごみ箱へ送る。成功時 true。</summary>
    public static bool Send(string path)
    {
        // pFrom はNULL文字2つで終端された複数文字列形式が必要。マーシャラが末尾にもう1つ
        // NULLを付与するため、ここで明示的に付けておくことで確実に二重終端となる。
        var op = new ShFileOpStruct
        {
            wFunc = FoDelete,
            pFrom = path + '\0' + '\0',
            fFlags = (ushort)(FofAllowUndo | FofNoConfirmation | FofSilent),
        };
        var result = SHFileOperationW(ref op);
        return result == 0 && !op.fAnyOperationsAborted;
    }
}
