using System.IO;
using Graft.Core;
using Graft.Infra;

namespace Graft.Features;

/// <summary>
/// エクスプローラから削除した1件（ファイルまたはフォルダ）を元に戻せるようにするための
/// 退避エントリ。<see cref="StagingPath"/> は <see cref="AppPaths.TrashStagingDirectory"/>
/// 配下の一意なフォルダに置いた複製で、フォルダ削除の場合はフォルダ名ごと丸ごと退避する。
/// </summary>
internal sealed record DeleteUndoEntry(string OriginalFullPath, string StagingPath, bool IsDirectory, DateTimeOffset DeletedAt);

/// <summary>
/// 削除の取り消し（Ctrl+Z）が1件成功したときの結果。呼び出し側（ExplorerViewModel）が
/// ツリーの再読込や監視の一時抑止に使う。
/// </summary>
public sealed record DeleteUndoOutcome(string OriginalFullPath, bool IsDirectory);

/// <summary>
/// 課題2: ごみ箱削除のアプリ内復元（Ctrl+Z）。エクスプローラから削除する直前に、Graft自身が
/// <see cref="AppPaths.TrashStagingDirectory"/>（back/trash/）へ退避コピーを取っておき、
/// アプリ内から元の場所へ書き戻せるようにする。OSのごみ箱（<see cref="Graft.Platform.ITrashService"/>）
/// には依存せず独立して動作するため、ごみ箱にも従来どおり入る（二重の安全）。
/// <para>
/// 【保持期間】 セッション内のみ保持する。無制限に溜め続けると、削除するたびにディスクを
/// 消費し続けてしまい（特に大きなフォルダの削除を繰り返した場合）、利用者が気付かないまま
/// back/trash/ が肥大化する恐れがあるため。OSのごみ箱には従来どおり入っているので、
/// セッションをまたぐ長期の救済はそちら（Windowsのごみ箱・Linuxの~/.local/share/Trash）に
/// 任せる。アプリ終了時（<see cref="Cleanup"/>）に必ず空にする。
/// </para>
/// <para>
/// 【取り消しの単位】 直近の削除から順に戻すスタック構造。複数回 Ctrl+Z すれば古い削除まで
/// 順に遡れる。復元に成功した項目はスタックから取り除き、退避コピーも直ちに削除する
/// （セッション内で無制限に溜めないため）。
/// </para>
/// <para>
/// 【スレッド境界】 UIスレッド（ExplorerViewModel）からのみ呼び出す前提で、内部の
/// <see cref="Stack{T}"/> はロックしていない。
/// </para>
/// </summary>
public sealed class DeleteUndoStore
{
    private readonly string _stagingRoot;
    private readonly Stack<DeleteUndoEntry> _stack = new();

    public DeleteUndoStore(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _stagingRoot = paths.TrashStagingDirectory;
    }

    /// <summary>元に戻せる削除が1件以上あるかどうか。</summary>
    public bool CanUndo => _stack.Count > 0;

    /// <summary>
    /// 削除の直前に呼ぶ。指定パス（ファイルまたはフォルダ、フォルダは中身ごと再帰的に）を
    /// 退避ディレクトリへ複製し、取り消しスタックへ積む。退避自体に失敗した場合
    /// （ディスク容量不足など）は失敗を返すのみで、呼び出し側は「取り消しは提供できないが
    /// 削除自体は続行する」判断を行ってよい（OSのごみ箱という二重の安全網が別途あるため）。
    /// </summary>
    public async Task<GraftResult<bool>> StageAsync(string originalFullPath, bool isDirectory, CancellationToken ct = default)
    {
        var exists = isDirectory
            ? Directory.Exists(LongPath.Extended(originalFullPath))
            : File.Exists(LongPath.Extended(originalFullPath));
        if (!exists)
        {
            return GraftResult<bool>.Fail(ErrorCode.E405, "退避対象が見つかりません", path: originalFullPath);
        }

        var ticketDirectory = Path.Combine(_stagingRoot, Guid.NewGuid().ToString("N"));
        var name = Path.GetFileName(originalFullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var stagingPath = Path.Combine(ticketDirectory, name);

        try
        {
            await Task.Run(() =>
            {
                Directory.CreateDirectory(LongPath.Extended(ticketDirectory));
                if (isDirectory)
                {
                    CopyDirectoryRecursively(originalFullPath, stagingPath);
                }
                else
                {
                    File.Copy(LongPath.Extended(originalFullPath), LongPath.Extended(stagingPath));
                }
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDeleteTree(ticketDirectory);
            return GraftResult<bool>.Fail(ErrorCode.E402, $"削除の取り消し用に退避できませんでした: {ExceptionMessages.Describe(ex)}", path: originalFullPath);
        }

        _stack.Push(new DeleteUndoEntry(originalFullPath, stagingPath, isDirectory, DateTimeOffset.Now));
        return GraftResult<bool>.Ok(true);
    }

    /// <summary>
    /// <see cref="StageAsync"/>で退避した直後に、実際の削除（<see cref="FileTreeService.DeleteAsync"/>）
    /// 自体が失敗した場合に呼ぶ。まだ元のファイルは消えていないため、取り消しスタックには
    /// 積まず、退避コピーだけを後始末する。
    /// </summary>
    public void DiscardLast()
    {
        if (_stack.Count == 0) return;
        var entry = _stack.Pop();
        TryDeleteTree(GetTicketDirectory(entry));
    }

    /// <summary>
    /// 直近の削除を1件、元の場所へ書き戻す。復元先に同名の項目が既にある場合は上書きせず、
    /// E201として失敗を返す（スタックからは取り除かない。同名の項目をどけてから再度Ctrl+Zで
    /// 再試行できるようにするため）。何も取り消せるものが無い場合は成功のうえ null を返す。
    /// </summary>
    public async Task<GraftResult<DeleteUndoOutcome?>> UndoAsync(CancellationToken ct = default)
    {
        if (_stack.Count == 0)
        {
            return GraftResult<DeleteUndoOutcome?>.Ok(null);
        }

        var entry = _stack.Peek();
        var collides = entry.IsDirectory
            ? Directory.Exists(LongPath.Extended(entry.OriginalFullPath))
            : File.Exists(LongPath.Extended(entry.OriginalFullPath));
        if (collides)
        {
            var kind = entry.IsDirectory ? "フォルダ" : "ファイル";
            return GraftResult<DeleteUndoOutcome?>.Fail(
                ErrorCode.E201, $"復元先に同名の{kind}が既に存在するため、上書きせずに元に戻すのを取りやめました", path: entry.OriginalFullPath);
        }

        var restored = entry.IsDirectory
            ? await RestoreDirectoryAsync(entry.StagingPath, entry.OriginalFullPath, ct).ConfigureAwait(false)
            : await RestoreFileAsync(entry.StagingPath, entry.OriginalFullPath, ct).ConfigureAwait(false);
        if (!restored.IsSuccess)
        {
            return GraftResult<DeleteUndoOutcome?>.Fail(restored.Issues);
        }

        _stack.Pop();
        TryDeleteTree(GetTicketDirectory(entry));
        return GraftResult<DeleteUndoOutcome?>.Ok(new DeleteUndoOutcome(entry.OriginalFullPath, entry.IsDirectory), restored.Issues);
    }

    /// <summary>
    /// アプリ終了時に呼ぶ。退避ディレクトリを丸ごと空にする（セッション内のみ保持する方針、
    /// クラス本体のコメント参照）。取り消せなくなったスタックの中身も併せて捨てる。
    /// </summary>
    public void Cleanup()
    {
        _stack.Clear();
        TryDeleteTree(_stagingRoot);
    }

    private static string GetTicketDirectory(DeleteUndoEntry entry)
        => Path.GetDirectoryName(entry.StagingPath) ?? entry.StagingPath;

    /// <summary>MODIFY相当の1ファイル復元。<see cref="SafeFileWriter"/>を再利用する（並行実装を作らない）。</summary>
    private static async Task<GraftResult<bool>> RestoreFileAsync(string stagingPath, string originalFullPath, CancellationToken ct)
    {
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(LongPath.Extended(stagingPath), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return GraftResult<bool>.Fail(ErrorCode.E402, $"退避内容の読み取りに失敗しました: {ExceptionMessages.Describe(ex)}", path: originalFullPath);
        }

        return await SafeFileWriter.ReplaceAsync(originalFullPath, bytes, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// フォルダ丸ごとの復元。フォルダ本体・中間フォルダ（空フォルダを含む）をすべて作り直し、
    /// 各ファイルは<see cref="SafeFileWriter"/>で書き戻す。
    /// </summary>
    private static async Task<GraftResult<bool>> RestoreDirectoryAsync(string stagingPath, string originalFullPath, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(LongPath.Extended(originalFullPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return GraftResult<bool>.Fail(ErrorCode.E402, $"フォルダを復元できませんでした: {ExceptionMessages.Describe(ex)}", path: originalFullPath);
        }

        var issues = new List<GraftIssue>();

        // 空フォルダも含めて先にディレクトリ構造を作る。
        foreach (var sourceDir in Directory.EnumerateDirectories(stagingPath, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(stagingPath, sourceDir);
            var targetDir = Path.Combine(originalFullPath, relative);
            try
            {
                Directory.CreateDirectory(LongPath.Extended(targetDir));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return GraftResult<bool>.Fail(ErrorCode.E402, $"フォルダを復元できませんでした: {ExceptionMessages.Describe(ex)}", path: targetDir);
            }
        }

        foreach (var sourceFile in Directory.EnumerateFiles(stagingPath, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(stagingPath, sourceFile);
            var targetFile = Path.Combine(originalFullPath, relative);

            var written = await RestoreFileAsync(sourceFile, targetFile, ct).ConfigureAwait(false);
            if (!written.IsSuccess)
            {
                return GraftResult<bool>.Fail(written.Issues);
            }
            issues.AddRange(written.Issues);
        }

        return GraftResult<bool>.Ok(true, issues);
    }

    private static void CopyDirectoryRecursively(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(LongPath.Extended(destDir));
        foreach (var dir in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, dir);
            Directory.CreateDirectory(LongPath.Extended(Path.Combine(destDir, relative)));
        }
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file);
            var target = Path.Combine(destDir, relative);
            var targetDir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(targetDir))
            {
                Directory.CreateDirectory(LongPath.Extended(targetDir));
            }
            File.Copy(LongPath.Extended(file), LongPath.Extended(target));
        }
    }

    private static void TryDeleteTree(string path)
    {
        try
        {
            var ioPath = LongPath.Extended(path);
            if (Directory.Exists(ioPath))
            {
                Directory.Delete(ioPath, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 後始末の失敗は主結果に影響させない。OSのごみ箱側に実体が残っているため実害は軽微。
        }
    }
}
