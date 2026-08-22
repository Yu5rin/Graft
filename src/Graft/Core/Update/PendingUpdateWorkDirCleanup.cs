namespace Graft.Core.Update;

/// <summary>
/// 自動更新の一時作業フォルダ（<c>%TEMP%\GraftUpdate\&lt;GUID&gt;\</c>。<see
/// cref="UpdateInstallPipeline.RunAsync"/>がダウンロード・展開に使う）を、次回起動時に掃除する。
///
/// 【背景（利用者からの指摘・穴2）】<see cref="UpdateInstallPipeline.RunAsync"/>はfinallyで
/// 自分の作業フォルダを掃除するが（穴1の修正）、これは「そのプロセスが生きている間」しか
/// 働かない。ダウンロード中にGraftが強制終了・クラッシュすると、ダウンロード中だったZIP
/// （配布物は50MB超）を含む作業フォルダがそのまま残り、次にGraftを起動しても誰も掃除しに
/// 行かないため永久に残り続けてしまう。<see cref="PendingUpdateCleanup"/>（実行ファイルの
/// 隣に残る<c>*.old</c>を次回起動時に掃除する）と同じ考え方で、こちらは<c>%TEMP%\GraftUpdate\</c>
/// 配下を対象に、同じくStartupCoordinator（次回起動時）から呼ぶ。
///
/// 【安全策1: 実行中の更新を消さない】このプロセス自身が今まさに更新中の作業フォルダを
/// 誤って消さないよう、<see cref="DefaultMinimumAge"/>（既定24時間）以上前に作成された
/// 子フォルダだけを対象にする。1回の更新（ダウンロード＋SHA256検証＋ZIP検証＋展開＋
/// 自己置き換え）は通常は数十秒〜数分で終わり、よほど遅い回線でも数時間で終わるはずなので、
/// 24時間という閾値は「更新中のフォルダを誤って消してしまう」実害を実質ゼロにしつつ、
/// 「クラッシュで残った古い残骸をいつまでも放置し続ける」実害も防げる、十分に安全側の値。
/// 判定にはフォルダの作成日時（<see cref="Directory.GetCreationTimeUtc(string)"/>）を使う。
/// 作業フォルダは<c>Path.Combine(..., Guid.NewGuid()...)</c>で毎回新規作成され、既存の
/// フォルダを使い回すことは無いため、作成日時＝「その更新を始めてからの経過時間」と
/// 素直に読める（更新中の書き込みでフォルダの更新日時が変わってもズレない）。
///
/// 【安全策2: 対象を構造的に限定する】<see cref="UpdateFiles.WorkDirectoryRootName"/>
/// （<c>GraftUpdate</c>）という単一の情報源を経由して<c>%TEMP%\GraftUpdate\</c>直下の
/// 子フォルダだけを列挙・削除の対象にし、<c>%TEMP%</c>の他の場所やそれ以外の場所には
/// 一切触れない（<see cref="PendingUpdateCleanup"/>が対象ファイル名を6つに限定しているのと
/// 同じ考え方）。
/// </summary>
public static class PendingUpdateWorkDirCleanup
{
    /// <summary>掃除対象と判定する経過時間の既定値。</summary>
    public static readonly TimeSpan DefaultMinimumAge = TimeSpan.FromHours(24);

    /// <summary>
    /// 掃除を実行する。個々の削除の失敗は起動を妨げないよう握りつぶす（次回起動時に再試行される）。
    /// </summary>
    /// <param name="minimumAge">この経過時間未満のフォルダは「実行中かもしれない」として残す。省略時は<see cref="DefaultMinimumAge"/>。</param>
    /// <param name="now">テスト用の時刻差し替え口。省略時は<see cref="DateTimeOffset.Now"/>。</param>
    /// <param name="rootOverride">テスト用の掃除対象ルート差し替え口。省略時は<c>%TEMP%\GraftUpdate\</c>。</param>
    /// <returns>実際に削除できたフォルダ数。ログ記録用。</returns>
    public static int Run(TimeSpan? minimumAge = null, DateTimeOffset? now = null, string? rootOverride = null)
    {
        var root = rootOverride ?? Path.Combine(Path.GetTempPath(), UpdateFiles.WorkDirectoryRootName);
        if (!Directory.Exists(root)) return 0;

        var threshold = (now ?? DateTimeOffset.Now) - (minimumAge ?? DefaultMinimumAge);

        IReadOnlyList<string> children;
        try
        {
            children = Directory.GetDirectories(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 列挙自体に失敗しても起動は継続する。
            return 0;
        }

        var removed = 0;
        foreach (var dir in children)
        {
            try
            {
                // クラス冒頭のコメント参照: 作成日時で「実行中かもしれないフォルダ」を除外する。
                if (Directory.GetCreationTimeUtc(dir) > threshold.UtcDateTime) continue;

                Directory.Delete(dir, recursive: true);
                removed++;
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
