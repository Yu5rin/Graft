using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Graft.Core;

/// <summary>適用の途中経過の1件（<see cref="BackupJournal"/>の1行）。</summary>
/// <param name="Op">種別。<see cref="BackupJournal"/>の <c>Stored</c> / <c>Created</c> / <c>Renamed</c> / <c>Mkdir</c>。</param>
/// <param name="Path">stored・created・mkdir の相対パス。</param>
/// <param name="From">renamed の移動元の相対パス。</param>
/// <param name="To">renamed の移動先の相対パス。</param>
internal sealed record JournalOp(string Op, string? Path = null, string? From = null, string? To = null);

/// <summary>
/// 1回の適用が「何をしたか」を、バックアップフォルダの中の <c>journal.jsonl</c> へ1件ずつ追記して残す記録。
/// 適用の途中でアプリが落ちても、この記録があれば起動時のロールバック（<see cref="RevisionRestorer"/>）が
/// 元の状態へ戻せる。
///
/// 【なぜ必要か】 in_progress の manifest は <c>entries</c> が空のまま残る（entries は適用が終わって確定する
/// ときにしか書かない）。以前の復元は entries を順に戻すだけだったので、途中で止まった適用を戻そうとしても
/// 何も戻さずに「成功」を返し、そのうえで rolled_back の印を付けるため、以後は検出もされず、
/// 書き換わったままのファイルが残った。退避したファイルと新しく作ったファイルは、書き込む前後に
/// その都度ディスクへ追記するこの記録に残す。
///
/// 【manifest に追記せず別ファイルにした理由】 manifest.json は JSON 全体の書き直しで、1件ごとに書くと
/// 途中で落ちたときに壊れた manifest が残りうる。1行ごとの追記なら、落ちた時点で壊れるのは最後の1行だけで、
/// 読み込みは壊れた行を読み飛ばせる。重複番号の修復（<see cref="RevisionDuplicateRepairer"/>）は
/// manifest を JSON の木として書き換えるだけで、フォルダごと移すこの記録には触れないので影響しない。
/// 退避ファイルは <c>files/</c> の下にあり、利用者のファイルとは名前が衝突しない
/// （<see cref="BackupPathUtil.FilesSubfolderName"/>）。
///
/// 【記録の順序】 各記録は、取り返しのつかない操作より前に追記するか、成功した直後に追記する。
/// ・stored: 退避のコピーが済んだ直後（このあとで元のファイルを書き換える）。
/// ・created: 新規作成ファイルを書く前。実在しないファイルの削除は何もしないので、書く前に記録して構わない。
/// ・renamed: 移動が成功した直後。移動の前に記録すると、移動に失敗したとき（移動先に既にファイルがあった等）に
///   利用者の既存のファイルを「自分が作ったもの」と取り違えて消してしまう。移動と記録の間で落ちた場合は
///   移動元のバックアップが書き戻され、移動先が残るだけで、失われるものは無い。
/// ・mkdir: 新しく作ったフォルダだけ。もともとあったフォルダは記録しない。
/// </summary>
internal static class BackupJournal
{
    public const string FileName = "journal.jsonl";
    public const string Stored = "stored";
    public const string Created = "created";
    public const string Renamed = "renamed";
    public const string Mkdir = "mkdir";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static string PathFor(string revisionFolder) => Path.Combine(revisionFolder, FileName);

    /// <summary>1件を追記する。書けなかったとき（ディスクが一杯等）は false。メモリ上の記録は別に残るので、
    /// 同じプロセス内のロールバックには影響しない（落ちたあとの復旧が弱くなるだけ）。</summary>
    public static bool TryAppend(string revisionFolder, JournalOp op)
    {
        try
        {
            File.AppendAllText(LongPath.Extended(PathFor(revisionFolder)), JsonSerializer.Serialize(op, Options) + "\n");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>記録を読む。ファイルが無ければ null（古い版の in_progress フォルダ）。壊れた行は読み飛ばす
    /// （落ちた瞬間に書きかけだった最後の1行）。</summary>
    public static IReadOnlyList<JournalOp>? TryRead(string revisionFolder)
    {
        var path = LongPath.Extended(PathFor(revisionFolder));
        if (!File.Exists(path)) return null;

        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var ops = new List<JournalOp>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var op = JsonSerializer.Deserialize<JournalOp>(line, Options);
                if (op is not null && !string.IsNullOrEmpty(op.Op)) ops.Add(op);
            }
            catch (JsonException)
            {
                // 書きかけの行。読み飛ばす。
            }
        }
        return ops;
    }

    /// <summary>
    /// バックアップフォルダの中身から「退避済みのファイル」を数える。journal.jsonl が無い古い版の
    /// in_progress フォルダを戻すときの代わりの手がかり。新しいレイアウトは <c>files/</c> の下、
    /// 古いレイアウト（v1.0.15以前）はフォルダ直下に平置きされているが、直下の manifest.json は
    /// リビジョンのメタデータと区別できないので数えない（書き戻すと利用者のファイルを壊しうる）。
    /// </summary>
    public static IReadOnlyList<JournalOp> InferStoredFromFolder(string revisionFolder)
    {
        var ops = new List<JournalOp>();
        var filesDir = Path.Combine(revisionFolder, BackupPathUtil.FilesSubfolderName);
        if (Directory.Exists(LongPath.Extended(filesDir)))
        {
            foreach (var file in Directory.EnumerateFiles(LongPath.Extended(filesDir), "*", SearchOption.AllDirectories))
            {
                ops.Add(new JournalOp(Stored, Path.GetRelativePath(LongPath.Extended(filesDir), file)));
            }
            return ops;
        }

        foreach (var file in Directory.EnumerateFiles(LongPath.Extended(revisionFolder), "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(LongPath.Extended(revisionFolder), file);
            if (string.Equals(relative, "manifest.json", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(relative, FileName, StringComparison.OrdinalIgnoreCase)) continue;
            ops.Add(new JournalOp(Stored, relative));
        }
        return ops;
    }

    /// <summary>
    /// 記録を新しい順にたどって、適用を元の状態へ戻す。順序は次のとおり。
    /// <list type="number">
    /// <item><description>新規作成したファイルを削除する。</description></item>
    /// <item><description>改名を戻す（移動先を移動元へ戻す）。このあとの退避の書き戻しが移動元の内容を上書きするので、
    /// 移動先を編集していても元の内容に戻る。</description></item>
    /// <item><description>退避したファイルを書き戻す。</description></item>
    /// <item><description>新しく作ったフォルダを、空のものだけ新しい順に削除する。利用者が後から何かを入れたフォルダは残す。</description></item>
    /// </list>
    /// 1件失敗しても残りは続ける（戻せるものは戻す）。失敗は Error の issue として返す。
    /// </summary>
    public static async Task<List<GraftIssue>> RollbackAsync(
        string revisionFolder, string projectRoot, IReadOnlyList<JournalOp> ops, CancellationToken ct)
    {
        var issues = new List<GraftIssue>();

        foreach (var op in ops.Where(o => o.Op == Created && o.Path is not null).Reverse())
        {
            DeleteCreatedFile(projectRoot, op.Path!, issues);
        }

        foreach (var op in ops.Where(o => o.Op == Renamed && o.From is not null && o.To is not null).Reverse())
        {
            UndoRename(projectRoot, op.From!, op.To!, issues);
        }

        foreach (var path in ops.Where(o => o.Op == Stored && o.Path is not null).Select(o => o.Path!).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var restored = await RestoreStoredAsync(revisionFolder, projectRoot, path, ct).ConfigureAwait(false);
            issues.AddRange(restored.Issues);
        }

        foreach (var op in ops.Where(o => o.Op == Mkdir && o.Path is not null).Reverse())
        {
            DeleteCreatedDirectoryIfEmpty(projectRoot, op.Path!, issues);
        }

        return issues;
    }

    private static string FullPathOf(string projectRoot, string relativePath)
        => Path.GetFullPath(Path.Combine(projectRoot, relativePath));

    private static void DeleteCreatedFile(string projectRoot, string relativePath, List<GraftIssue> issues)
    {
        var ioPath = LongPath.Extended(FullPathOf(projectRoot, relativePath));
        try
        {
            if (File.Exists(ioPath)) File.Delete(ioPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            issues.Add(GraftIssue.Of(ErrorCode.E402, $"新規作成ファイルの削除に失敗しました: {ExceptionMessages.Describe(ex)}", path: relativePath));
        }
    }

    /// <summary>
    /// 改名を戻す。移動元が空いていれば移動先を移動元へ移す（内容はこのあと退避から上書きされる）。
    /// 移動元に既に何かがあれば、移動先は改名で作られた（適用前には無かった。ドライランと本適用が
    /// 上書きを許さない）ものなので削除する。大文字小文字だけが違う改名は同じファイルの名前の付け替えなので、
    /// 削除せず元の綴りへ戻す。
    /// </summary>
    private static void UndoRename(string projectRoot, string fromRelative, string toRelative, List<GraftIssue> issues)
    {
        var fromFull = FullPathOf(projectRoot, fromRelative);
        var toFull = FullPathOf(projectRoot, toRelative);
        var fromIo = LongPath.Extended(fromFull);
        var toIo = LongPath.Extended(toFull);

        try
        {
            if (!File.Exists(toIo)) return; // 既に無い。移動元は退避の書き戻しで復元される。
            if (string.Equals(fromFull, toFull, StringComparison.Ordinal)) return;

            var caseOnly = string.Equals(fromFull, toFull, StringComparison.OrdinalIgnoreCase);
            if (!File.Exists(fromIo) || (caseOnly && !RenameTarget.IsOccupied(toFull, fromFull)))
            {
                var fromDir = Path.GetDirectoryName(fromFull);
                if (!string.IsNullOrEmpty(fromDir)) Directory.CreateDirectory(LongPath.Extended(fromDir));
                File.Move(toIo, fromIo, overwrite: caseOnly);
                return;
            }

            File.Delete(toIo);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            issues.Add(GraftIssue.Of(ErrorCode.E402, $"改名を元に戻せませんでした: {ExceptionMessages.Describe(ex)}", path: toRelative));
        }
    }

    private static async Task<GraftResult<bool>> RestoreStoredAsync(
        string revisionFolder, string projectRoot, string relativePath, CancellationToken ct)
    {
        var normalized = BackupPathUtil.NormalizeRelativePath(relativePath);
        if (!normalized.IsSuccess) return GraftResult<bool>.Fail(normalized.Issues);

        // 新旧レイアウトの解決・存在確認・旧レイアウト特有の破損検出（E216）まで含めて
        // BackupPathUtil.ReadBackupFileAsyncへ委譲する。
        var read = await BackupPathUtil.ReadBackupFileAsync(revisionFolder, normalized.Value, ct).ConfigureAwait(false);
        if (!read.IsSuccess) return GraftResult<bool>.Fail(read.Issues);

        return await SafeFileWriter.ReplaceAsync(FullPathOf(projectRoot, normalized.Value), read.Value, ct).ConfigureAwait(false);
    }

    private static void DeleteCreatedDirectoryIfEmpty(string projectRoot, string relativePath, List<GraftIssue> issues)
    {
        var ioPath = LongPath.Extended(FullPathOf(projectRoot, relativePath));
        try
        {
            if (Directory.Exists(ioPath) && !Directory.EnumerateFileSystemEntries(ioPath).Any())
            {
                Directory.Delete(ioPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 空のフォルダが1つ残るだけでデータは失われない。失敗にはしない。
            issues.Add(GraftIssue.Of(ErrorCode.E402, $"作成したフォルダを削除できませんでした: {ExceptionMessages.Describe(ex)}",
                path: relativePath, severity: Severity.Warning));
        }
    }
}
