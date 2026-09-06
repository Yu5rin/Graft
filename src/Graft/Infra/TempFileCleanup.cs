namespace Graft.Infra;

/// <summary>
/// 異常系点検「低」6件目の対応: 一時ファイル・退避ファイルの起動時掃除。
///
/// <see cref="JsonFileStore.WriteAsync{T}"/>（<c>&lt;path&gt;.tmp.&lt;guid&gt;</c>）・
/// <see cref="Graft.Core.SafeFileWriter"/>（<c>&lt;対象&gt;.graft-bak-&lt;guid&gt;</c>）は
/// いずれも「一時ファイルへ書いてからrename」という設計のため、途中で電源断・強制終了が
/// 起きると書きかけの一時ファイル・退避ファイルがそのまま残る。特に<c>.graft-bak-*</c>は
/// 利用者のプロジェクトフォルダ内（適用対象ファイルの隣）に残るため、ファイルツリーや
/// <c>git status</c>に映り込んで目に付く。これらを掃除するコードがリポジトリ内に存在
/// しなかった（grepで確認済み）ため、<see cref="Views.PendingUpdateWorkDirCleanup"/>等、
/// 既存の「次回起動時に後始末する」流儀に揃えてここへ実装する。
///
/// 【安全策: 実行中の書き込みを誤って消さない】<see cref="Core.Update.PendingUpdateWorkDirCleanup"/>
/// と同じ考え方で、<see cref="DefaultMinimumAge"/>（既定24時間）以上前に最終更新された
/// ファイルだけを対象にする。JsonFileStore.WriteAsync・SafeFileWriterはいずれも一時ファイルを
/// 数百ms〜数秒程度で消費し切る設計のため、24時間残っているものは「進行中の書き込み」では
/// なく「異常終了で取り残された残骸」と判断してよい。
/// </summary>
public static class TempFileCleanup
{
    /// <summary>掃除対象と判定する経過時間の既定値。<see cref="Core.Update.PendingUpdateWorkDirCleanup"/>と同じ値を踏襲する。</summary>
    public static readonly TimeSpan DefaultMinimumAge = TimeSpan.FromHours(24);

    /// <summary>
    /// プロジェクトルート配下の走査で辿る最大の深さ。
    ///
    /// 【上限を設ける理由】<c>.graft-bak-*</c>はSafeFileWriterが書き込み中の一瞬だけ作り、
    /// 成功すれば即座に自分で削除する一時的なファイルであり、そもそも通常運用では長時間
    /// 残らない。にも関わらず起動のたびに無制限に再帰走査してしまうと、数十万ファイル規模の
    /// モノレポ（<c>node_modules</c>を含むJS系リポジトリ等）では起動が数秒〜場合によっては
    /// 分単位に悪化しかねない（18章「起動から操作可能まで1秒以内」に反する）。6階層は
    /// 一般的なプロジェクト構成（例: <c>src/機能領域/サブモジュール/実装/テスト/ファイル</c>）を
    /// 実用上十分にカバーする値として選んだ。
    /// </summary>
    public const int DefaultMaxDepth = 6;

    /// <summary>
    /// プロジェクトルート配下の走査で調べる最大エントリ数（ファイル＋ディレクトリの合計）。
    /// 一般的なディスクでこの件数の<c>Directory.GetFiles</c>/<c>GetDirectories</c>呼び出しは
    /// 数十〜百数十ms程度に収まる規模として選んだ。これを超える巨大なプロジェクトでは
    /// 「今回の起動では見つけきれない」ことを許容する（掃除自体は次回以降の起動でも
    /// 繰り返し試みられるため、いずれは掃除される。起動速度の安定を優先する）。
    /// </summary>
    public const int DefaultMaxEntriesScanned = 20000;

    /// <summary>
    /// 走査中に降りない（<c>.graft-bak-*</c>が生まれることが想定されない）ディレクトリ名。
    /// <see cref="Features.ContextCollector.DefaultExcludePatterns"/>と同じ発想の抜粋だが、
    /// こちらは.gitignoreやプロジェクト設定までは読まない軽量な決め打ちに留める
    /// （起動時の掃除という補助的な処理のために、毎回.gitignoreを読みに行くI/Oを増やしたく
    /// ないため）。依存関係フォルダ（<c>node_modules</c>等）はファイル数が非常に多く、
    /// ここを除外するだけで<see cref="DefaultMaxEntriesScanned"/>の予算を大きく節約できる。
    /// </summary>
    private static readonly HashSet<string> SkipDirectoryNames =
        new(StringComparer.OrdinalIgnoreCase) { "node_modules", ".git", "bin", "obj", ".venv", "dist" };

    /// <summary>
    /// <paramref name="baseDirectory"/>（<see cref="AppPaths.BaseDirectory"/>）直下の
    /// <c>*.tmp.*</c>（<see cref="JsonFileStore.WriteAsync{T}"/>が作る一時ファイル）のうち、
    /// 一定時間以上古いものを削除する。settings.json等はすべてこの階層に平置きされ
    /// （<see cref="JsonFileStore.WriteAsync{T}"/>参照）、サブディレクトリ（back/・logs/）には
    /// 同名パターンのファイルが作られないため、深く再帰する必要はない
    /// （<see cref="SearchOption.TopDirectoryOnly"/>で十分）。
    /// </summary>
    /// <returns>実際に削除できたファイル数。ログ記録用。</returns>
    public static int CleanupBaseDirectoryTempFiles(
        string baseDirectory, TimeSpan? minimumAge = null, DateTimeOffset? now = null)
    {
        if (!Directory.Exists(baseDirectory)) return 0;

        var threshold = (now ?? DateTimeOffset.Now) - (minimumAge ?? DefaultMinimumAge);

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(baseDirectory, "*.tmp.*", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 列挙自体に失敗しても起動は継続する。
            return 0;
        }

        var removed = 0;
        foreach (var file in files)
        {
            if (TryDeleteIfOld(file, threshold)) removed++;
        }
        return removed;
    }

    /// <summary>
    /// <paramref name="projectRoot"/>配下の<c>*.graft-bak-*</c>（<see cref="Core.SafeFileWriter"/>が
    /// 作る退避ファイル）のうち、一定時間以上古いものを削除する。深さ・走査件数の上限は
    /// クラスコメント・<see cref="DefaultMaxDepth"/>・<see cref="DefaultMaxEntriesScanned"/>参照。
    /// <see cref="Directory.EnumerateFiles(string, string, SearchOption)"/>の
    /// <see cref="SearchOption.AllDirectories"/>は深さを制御できないため使わず、スタックによる
    /// 手動の幅優先寄りの走査（実際には深さ優先だが、上限判定にdepthを持ち歩ければ十分なため
    /// 順序自体は問わない）で1階層ずつ深さを数えながら辿る。
    /// </summary>
    /// <returns>実際に削除できたファイル数。ログ記録用。</returns>
    public static int CleanupProjectBackupFiles(
        string projectRoot,
        TimeSpan? minimumAge = null,
        DateTimeOffset? now = null,
        int maxDepth = DefaultMaxDepth,
        int maxEntriesScanned = DefaultMaxEntriesScanned)
    {
        if (!Directory.Exists(projectRoot)) return 0;

        var threshold = (now ?? DateTimeOffset.Now) - (minimumAge ?? DefaultMinimumAge);
        var removed = 0;
        var entriesScanned = 0;

        var pending = new Stack<(string Dir, int Depth)>();
        pending.Push((projectRoot, 0));

        while (pending.Count > 0 && entriesScanned < maxEntriesScanned)
        {
            var (dir, depth) = pending.Pop();

            string[] backupFiles;
            string[] subDirectories = Array.Empty<string>();
            try
            {
                backupFiles = Directory.GetFiles(dir, "*.graft-bak-*");
                if (depth < maxDepth)
                {
                    subDirectories = Directory.GetDirectories(dir);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // このディレクトリだけ読めなくても、他のディレクトリの掃除は継続する。
                continue;
            }

            entriesScanned += backupFiles.Length + subDirectories.Length;

            foreach (var file in backupFiles)
            {
                if (TryDeleteIfOld(file, threshold)) removed++;
            }

            foreach (var sub in subDirectories)
            {
                if (SkipDirectoryNames.Contains(Path.GetFileName(sub))) continue;
                pending.Push((sub, depth + 1));
            }
        }

        return removed;
    }

    private static bool TryDeleteIfOld(string file, DateTimeOffset threshold)
    {
        try
        {
            // 安全策: 作成日時ではなく最終更新日時で判定する。JsonFileStore.WriteAsyncの
            // 一時ファイルは書き込み直後にrenameされるのが通常のため、万一rename前に
            // 検査が行われても「たった今書き込まれた＝進行中」であることを最終更新日時が
            // 素直に反映する（作成日時だとファイルシステムによっては更新されない場合がある）。
            if (File.GetLastWriteTimeUtc(file) > threshold.UtcDateTime) return false;

            File.Delete(file);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 削除できなくても起動は継続する（次回起動時に再試行される）。
            return false;
        }
    }
}
