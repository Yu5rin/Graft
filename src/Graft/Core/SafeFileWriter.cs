using System.IO;

namespace Graft.Core;

/// <summary>
/// 安全なファイル置換。6.4/6.7節に対応する。同一ボリューム上の一時ファイルへ書き出したうえで
/// <see cref="File.Replace(string, string, string?)"/>（非Windowsでは <see cref="File.Move(string, string, bool)"/>）
/// で置換し、失敗時は退避方式へフォールバックする。ウイルス対策ソフト等による直後アクセス失敗に
/// 備え、書き込み・置換とも100ms間隔で最大3回リトライする。
/// </summary>
public static class SafeFileWriter
{
    private const int RetryCount = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>ファイルの内容をバイト列で安全に置換する。書き込み先が存在しない場合は新規作成する。</summary>
    public static async Task<GraftResult<bool>> ReplaceAsync(string fullPath, byte[] content, CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
        {
            return GraftResult<bool>.Fail(ErrorCode.E402, "書き込み先ディレクトリを特定できません", path: fullPath);
        }

        var tempPath = Path.Combine(directory, MakeTempFileName(fullPath));

        try
        {
            await RetryIoAsync(() => WriteTempFileAsync(tempPath, content, ct), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(tempPath);
            return GraftResult<bool>.Fail(ErrorCode.E402, $"一時ファイルの作成に失敗しました: {ExceptionMessages.Describe(ex)}", path: fullPath);
        }

        try
        {
            await RetryIoAsync(() => ReplacePrimaryAsync(fullPath, tempPath), ct).ConfigureAwait(false);
            return GraftResult<bool>.Ok(true);
        }
        catch (Exception primaryEx) when (primaryEx is IOException or UnauthorizedAccessException)
        {
            return await FallbackReplaceAsync(fullPath, tempPath, primaryEx, ct).ConfigureAwait(false);
        }
    }

    private static async Task WriteTempFileAsync(string tempPath, byte[] content, CancellationToken ct)
    {
        var ioPath = LongPath.Extended(tempPath);
        await using var stream = new FileStream(ioPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        await stream.WriteAsync(content, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static Task ReplacePrimaryAsync(string fullPath, string tempPath)
    {
        var target = LongPath.Extended(fullPath);
        var temp = LongPath.Extended(tempPath);
        var targetExists = File.Exists(target);

        if (OperatingSystem.IsWindows())
        {
            if (targetExists)
            {
                File.Replace(temp, target, null);
            }
            else
            {
                File.Move(temp, target);
            }
        }
        else
        {
            // Linux/macOS（主にテスト実行環境）では File.Replace の代わりに上書きMoveを用いる
            File.Move(temp, target, overwrite: true);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// File.Replace失敗時のフォールバック。元ファイルを別名へ退避してから一時ファイルを
    /// リネームし、成功後に退避ファイルを削除する。ネットワークドライブやクラウド同期
    /// フォルダで異なるボリューム扱いとなり File.Replace が失敗するケースを想定する。
    /// </summary>
    private static async Task<GraftResult<bool>> FallbackReplaceAsync(string fullPath, string tempPath, Exception cause, CancellationToken ct)
    {
        var backupPath = fullPath + $".graft-bak-{Guid.NewGuid():N}";
        var targetExists = File.Exists(LongPath.Extended(fullPath));

        try
        {
            if (targetExists)
            {
                await RetryIoAsync(() => MoveAsync(fullPath, backupPath), ct).ConfigureAwait(false);
            }

            await RetryIoAsync(() => MoveAsync(tempPath, fullPath), ct).ConfigureAwait(false);

            if (targetExists)
            {
                TryDelete(backupPath);
            }

            var issue = GraftIssue.Of(ErrorCode.E402, "代替手順（退避方式）で書き込みました", severity: Severity.Info);
            return GraftResult<bool>.Ok(true, new[] { issue });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RestoreFromBackupIfPossible(fullPath, backupPath, targetExists);
            TryDelete(tempPath);
            return GraftResult<bool>.Fail(ErrorCode.E402,
                $"代替手順でも書き込みに失敗しました: {ExceptionMessages.Describe(ex)}（一次要因: {ExceptionMessages.Describe(cause)}）",
                path: fullPath);
        }
    }

    private static Task MoveAsync(string sourcePath, string destinationPath)
    {
        File.Move(LongPath.Extended(sourcePath), LongPath.Extended(destinationPath), overwrite: true);
        return Task.CompletedTask;
    }

    private static void RestoreFromBackupIfPossible(string fullPath, string backupPath, bool targetExisted)
    {
        if (!targetExisted) return;
        if (File.Exists(LongPath.Extended(fullPath))) return;
        if (!File.Exists(LongPath.Extended(backupPath))) return;

        try
        {
            File.Move(LongPath.Extended(backupPath), LongPath.Extended(fullPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 復旧できない場合も上位へは元の失敗を返す。退避ファイルはそのまま残す
        }
    }

    private static string MakeTempFileName(string fullPath)
    {
        var fileName = Path.GetFileName(fullPath);
        return $".{fileName}.graft-tmp-{Guid.NewGuid():N}";
    }

    private static void TryDelete(string path)
    {
        try
        {
            var ioPath = LongPath.Extended(path);
            if (File.Exists(ioPath))
            {
                File.Delete(ioPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 後始末の失敗は主結果に影響させない
        }
    }

    /// <summary>IOException/UnauthorizedAccessExceptionを100ms間隔で最大3回までリトライする。</summary>
    private static async Task RetryIoAsync(Func<Task> action, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= RetryCount; attempt++)
        {
            try
            {
                await action().ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < RetryCount)
            {
                await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
            }
        }
    }
}
