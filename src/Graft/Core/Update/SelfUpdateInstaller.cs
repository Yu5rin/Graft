namespace Graft.Core.Update;

/// <summary>自己置き換えの結果。</summary>
public sealed record SelfUpdateInstallOutcome
{
    public required bool Success { get; init; }

    /// <summary>失敗時の説明（成功時はnull）。</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>失敗した処理対象のファイル名（成功時・特定できない失敗時はnull）。</summary>
    public string? FailedFileName { get; init; }

    public static SelfUpdateInstallOutcome Ok() => new() { Success = true };

    public static SelfUpdateInstallOutcome Fail(string message, string? fileName = null)
        => new() { Success = false, ErrorMessage = message, FailedFileName = fileName };
}

/// <summary>
/// 配布物6ファイル（<see cref="UpdateFiles.RequiredFileNames"/>）を、実行中でも安全に置き換える。
///
/// 【アルゴリズム】各ファイルについて「(1) 既存の.old残骸があれば削除 → (2) 現在のファイルを
/// .oldへリネーム → (3) 展開済みの新ファイルを本来の名前でコピー配置」の3手順を順番に行う。
/// 実行中のexe・ロード済みネイティブDLLは内容の上書きはできないが、リネームはできるという
/// Windowsの性質（指示書参照）を使う。取扱説明書.md・はじめにお読みください.txtはロードされて
/// いないため直接上書きしても問題ないが、全ファイルに同じ手順を適用することでロールバックの
/// ロジックを1本化している。
///
/// 【ロールバック】1ファイルでも(2)または(3)で失敗したら、それまでに成功した分だけを
/// 逆順に正確に巻き戻す。「新配置済み（(3)まで完了）」のファイルは新ファイルを削除してから
/// .oldを元の名前へ戻し、「リネームのみ済み（(2)まで）」のファイルは.oldを元の名前へ戻すだけ。
/// これにより、どの段階で失敗しても最終的にすべてのファイルが更新前の状態へ戻る
/// （指示書「ここを省くとアプリが壊れます」への対応）。
///
/// 実際のファイル操作は<see cref="IUpdateFileSystem"/>越しに行うため、実際にWindows環境で
/// exeのロックを再現しなくても、フェイク実装で「Nファイル目の(2)で失敗」等を指定して
/// ロールバックを単体テストできる。
/// </summary>
public static class SelfUpdateInstaller
{
    /// <param name="installDirectory">Graft.exeが実際に置かれているフォルダ（<see cref="Infra.AppPaths.BaseDirectory"/>相当）。</param>
    /// <param name="stagingDirectory">
    /// ダウンロード・検証済みのZIPを展開済みの一時フォルダ。<see cref="UpdateFiles.RequiredFileNames"/>と
    /// 同名のファイルがフラットに置かれている前提（<see cref="UpdateZipInspector.ExtractTo"/>参照）。
    /// </param>
    /// <param name="fileSystem">ファイル操作の実体。省略時は<see cref="RealUpdateFileSystem"/>。</param>
    /// <param name="fileNames">対象ファイル名一覧。省略時は<see cref="UpdateFiles.RequiredFileNames"/>（テスト用の差し替え口）。</param>
    public static SelfUpdateInstallOutcome Install(
        string installDirectory,
        string stagingDirectory,
        IUpdateFileSystem? fileSystem = null,
        IReadOnlyList<string>? fileNames = null)
    {
        fileSystem ??= new RealUpdateFileSystem();
        fileNames ??= UpdateFiles.RequiredFileNames;

        // 「リネーム済み（.oldが存在する）」ファイル名の一覧。ロールバック時にこの逆順で処理する。
        var renamed = new List<string>();
        // renamedのうち、新ファイルの配置（コピー）まで完了したものの一覧（renamedの部分集合）。
        var placed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var fileName in fileNames)
        {
            var targetPath = Path.Combine(installDirectory, fileName);
            var oldPath = targetPath + UpdateFiles.OldFileSuffix;
            var stagedPath = Path.Combine(stagingDirectory, fileName);

            if (!fileSystem.FileExists(stagedPath))
            {
                return RollBack(fileSystem, installDirectory, renamed, placed,
                    $"更新用の一時フォルダに {fileName} が見つかりませんでした。", fileName);
            }

            try
            {
                // 前回の更新が失敗して.oldが残っている場合に備え、まず消しておく
                // （無ければ何もしない。DeleteFileの契約）。
                fileSystem.DeleteFile(oldPath);
                fileSystem.MoveFile(targetPath, oldPath);
                renamed.Add(fileName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return RollBack(fileSystem, installDirectory, renamed, placed,
                    $"{fileName} を退避できませんでした: {ex.Message}", fileName);
            }

            try
            {
                fileSystem.CopyFile(stagedPath, targetPath, overwrite: false);
                placed.Add(fileName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return RollBack(fileSystem, installDirectory, renamed, placed,
                    $"{fileName} を配置できませんでした: {ex.Message}", fileName);
            }
        }

        return SelfUpdateInstallOutcome.Ok();
    }

    /// <summary>
    /// <paramref name="renamed"/>に記録済みの分だけ、追加した順と逆順に巻き戻す。1件の巻き戻しが
    /// 失敗しても他の巻き戻しは続行し（ベストエフォート）、最終的に元のエラーへ巻き戻し失敗分の
    /// 情報を付け加えて返す。
    /// </summary>
    private static SelfUpdateInstallOutcome RollBack(
        IUpdateFileSystem fileSystem, string installDirectory,
        List<string> renamed, HashSet<string> placed, string errorMessage, string? failedFileName)
    {
        List<string>? rollbackErrors = null;

        for (var i = renamed.Count - 1; i >= 0; i--)
        {
            var fileName = renamed[i];
            var targetPath = Path.Combine(installDirectory, fileName);
            var oldPath = targetPath + UpdateFiles.OldFileSuffix;

            try
            {
                if (placed.Contains(fileName))
                {
                    fileSystem.DeleteFile(targetPath);
                }
                fileSystem.MoveFile(oldPath, targetPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                (rollbackErrors ??= new List<string>()).Add($"{fileName}: {ex.Message}");
            }
        }

        var message = rollbackErrors is null
            ? errorMessage
            : $"{errorMessage}（さらに巻き戻し中にも失敗しました: {string.Join("; ", rollbackErrors)}。手動での復旧が必要な可能性があります）";
        return SelfUpdateInstallOutcome.Fail(message, failedFileName);
    }
}
