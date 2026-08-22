namespace Graft.Core.Update;

/// <summary>
/// 前回の自動更新が成功した場合に残る<c>*.old</c>ファイル（<see cref="SelfUpdateInstaller"/>が
/// リネームした旧ファイル）を、次回起動時に掃除する。指示書の設計どおり
/// 「次回起動時に.oldを削除する」を実装したもの。
///
/// <see cref="UpdateFiles.RequiredFileNames"/>に列挙された6ファイル＋<see
/// cref="UpdateFiles.OldFileSuffix"/>という組み合わせの名前だけを対象にし、それ以外の
/// ファイルには一切触れない（settings.json等の利用者データを誤って消さないため）。
/// </summary>
public static class PendingUpdateCleanup
{
    /// <summary>
    /// 掃除を実行する。個々の削除の失敗は起動を妨げないよう握りつぶす（次回起動時に再試行される）。
    /// </summary>
    /// <returns>実際に削除できたファイル名（.old無し）の一覧。ログ記録用。</returns>
    public static IReadOnlyList<string> Run(string baseDirectory)
    {
        var removed = new List<string>();
        foreach (var fileName in UpdateFiles.RequiredFileNames)
        {
            var oldPath = Path.Combine(baseDirectory, fileName + UpdateFiles.OldFileSuffix);
            try
            {
                if (File.Exists(oldPath))
                {
                    File.Delete(oldPath);
                    removed.Add(fileName);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 削除できなくても起動は継続する。
                _ = ex;
            }
        }
        return removed;
    }
}
