using System;
using System.IO;
using System.Threading.Tasks;

namespace Graft.UiTests.TestSupport;

/// <summary>
/// テストの後片付け（一時ディレクトリの再帰削除）で使う共通ヘルパ。
///
/// Windowsでは次の2種類の失敗が起きうる（実機のテスト失敗で確認・不具合5）。
/// <list type="bullet">
/// <item>
/// gitが作成するオブジェクトファイル（<c>.git/objects</c>配下等）には読み取り専用属性が付くため、
/// 削除前に配下の読み取り専用属性を解除しないと<see cref="Directory.Delete(string, bool)"/>が
/// <see cref="UnauthorizedAccessException"/>で失敗する（GitAutoCommitScenarioTests・
/// LiveSettingsPropagationTestsのDispose）。
/// </item>
/// <item>
/// テスト内で起動したgitの子プロセス（例: <see cref="Graft.Editor.GitGutterProvider"/>が保存時に
/// 実行するfire-and-forgetの<c>RefreshAsync</c>）がまだ終了しきっておらず、対象ディレクトリを
/// カレントディレクトリとして掴んでいる場合、Windowsはそのディレクトリの削除を拒否し
/// <see cref="IOException"/>（「別のプロセスが使用中」）になる（EditorSelectionPromptTests）。
/// この場合は少し待てば解放されるため、短い間隔で数回リトライする。
/// </item>
/// </list>
/// 個々のテストで対症療法的に try/catch を書くのではなく、ここへ共通化する。
/// </summary>
public static class TempDirectoryCleanup
{
    private const int MaxAttempts = 5;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// ディレクトリを再帰的に削除する（同期版、<c>Dispose()</c>から呼ぶ用）。
    /// 削除できなかった場合は例外を投げず false を返す（後片付けの失敗でテスト結果を汚さないための
    /// ベストエフォート。ただし読み取り専用属性の解除と短いリトライにより、実際にはほぼ成功する）。
    /// </summary>
    public static bool TryDeleteRecursive(string path)
        => TryDeleteRecursiveAsync(path).GetAwaiter().GetResult();

    /// <summary>非同期版。async なテストメソッドの finally から呼ぶ用。</summary>
    public static async Task<bool> TryDeleteRecursiveAsync(string path)
    {
        if (!Directory.Exists(path)) return true;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                ClearReadOnlyRecursively(path);
                Directory.Delete(path, recursive: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == MaxAttempts) return false;
                await Task.Delay(RetryDelay).ConfigureAwait(false);
            }
        }

        return false;
    }

    /// <summary>
    /// 読み取り専用属性を再帰的に解除する。シンボリックリンク配下（ルート外を指す可能性がある）へは
    /// 決して降りない（TempWorkspace.ClearReadOnlyRecursivelyと同じ方針）。
    /// </summary>
    private static void ClearReadOnlyRecursively(string dir)
    {
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
            {
                var attrs = File.GetAttributes(entry);
                if ((attrs & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                if ((attrs & FileAttributes.Directory) != 0)
                {
                    ClearReadOnlyRecursively(entry);
                    continue;
                }

                if ((attrs & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(entry, attrs & ~FileAttributes.ReadOnly);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 属性解除自体の失敗は上位の削除試行（リトライ込み）に委ねる。
        }
    }
}
