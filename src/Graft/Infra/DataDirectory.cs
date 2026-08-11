using System.IO;
using Graft.Core;

namespace Graft.Infra;

/// <summary>
/// 機能3（データ保存先の選択）: exeと同じ階層に置く小さなポインタファイル
/// （既定名 <see cref="FileName"/>）の読み書きを担う。
///
/// 【設計上の罠への対応】保存先の設定をsettings.jsonへ書くと、「settings.json自体の場所が
/// settings.jsonの中身に依存する」循環に陥る（保存先を読むにはまずsettings.jsonを読む必要が
/// あるが、settings.jsonの場所自体が読めていない）。これを避けるため、settings.jsonより
/// 手前・かつ場所が固定（exeと同じ階層）のこの1行だけのファイルにデータ保存先を書く。
/// <see cref="AppPaths.ResolveBaseDirectory"/> はこのファイルだけを読んで
/// <see cref="AppPaths.BaseDirectory"/> を決める。
/// </summary>
public static class DataDirectoryPointer
{
    /// <summary>ポインタファイル名。中身はデータ保存先の絶対パス1行のみ。</summary>
    public const string FileName = "datapath.txt";

    /// <summary>exeディレクトリからポインタファイルの絶対パスを組み立てる。</summary>
    public static string PointerFilePath(string exeDirectory) => Path.Combine(exeDirectory, FileName);

    /// <summary>
    /// ポインタファイルを読む。存在しない・空・読み取れない（exeフォルダへのアクセス自体が
    /// 拒否されている等）場合はnullを返し、呼び出し側は従来どおりexeと同じ階層
    /// （ポータブル）を使う。
    /// </summary>
    public static string? TryRead(string exeDirectory)
    {
        var path = PointerFilePath(exeDirectory);
        try
        {
            if (!File.Exists(path)) return null;

            foreach (var line in File.ReadAllLines(path))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0) return trimmed;
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// ポインタファイルへデータ保存先を書く。exeフォルダへ書き込めない場合（Program Files配下等）は
    /// falseを返す。呼び出し側はこの戻り値がfalseの間は「移行できなかった」ものとして扱うこと
    /// （コピーしたデータ自体は残るが、次回起動時には従来の場所が使われ続ける）。
    /// </summary>
    public static bool TryWrite(string exeDirectory, string dataDirectory)
    {
        try
        {
            File.WriteAllText(PointerFilePath(exeDirectory), dataDirectory + Environment.NewLine);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// ポインタファイルを消す（ポータブルへ戻す＝「無ければexeと同じ階層」の既定動作に戻す）。
    /// 元々存在しない場合も成功として扱う。
    /// </summary>
    public static bool TryClear(string exeDirectory)
    {
        var path = PointerFilePath(exeDirectory);
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>
/// 機能3: データ保存先の切り替え後、「元の場所（旧保存先）はまだ削除していない・次回起動時に
/// 削除してよい」ことを示す後始末待ちマーカー。<see cref="DataDirectoryPointer"/>と同じ作法
/// （1行だけのテキストファイル）で、新しい保存先（<see cref="DataDirectoryMigrator.
/// MigrateAndSwitchPointer"/>のtargetDirectory）に置く点だけが異なる（ポインタファイルは常に
/// exeフォルダに置くが、こちらは「今回取り込むべき旧保存先はどこか」を新しい保存先自身が
/// 知っている必要があるため）。
///
/// なぜ移行のその場で削除せずマーカーだけ書くのかは<see cref="DataDirectoryMigrator"/>クラス
/// ドキュメントの【なぜ即時削除ではなく次回起動時なのか】を参照。実際の取り込み直し・削除は
/// <see cref="DataDirectoryMigrator.RunPendingCleanup"/>が次回起動時に行う。
/// </summary>
public static class DataDirectoryPendingCleanupMarker
{
    /// <summary>マーカーファイル名。中身は削除対象となる旧保存先の絶対パス1行のみ。</summary>
    public const string FileName = "pending-cleanup.txt";

    /// <summary>データ保存先からマーカーファイルの絶対パスを組み立てる。</summary>
    public static string MarkerFilePath(string directory) => Path.Combine(directory, FileName);

    /// <summary>
    /// マーカーファイルを読む。存在しない・空・読み取れない場合はnullを返す
    /// （呼び出し側は「後始末は不要」として扱う）。
    /// </summary>
    public static string? TryRead(string directory)
    {
        var path = MarkerFilePath(directory);
        try
        {
            if (!File.Exists(path)) return null;

            foreach (var line in File.ReadAllLines(path))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0) return trimmed;
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// マーカーファイルへ、削除対象となる旧保存先の絶対パスを書く。書き込めなくても
    /// （新しい保存先への書き込み権限が無い等）コピー・ポインタ切り替え自体は既に成功しており
    /// 実害は無い（旧保存先が次回起動時に自動削除されないだけ）ため、falseを返すのみで
    /// 呼び出し側が全体を失敗として扱う必要は無い。
    /// </summary>
    public static bool TryWrite(string directory, string oldDirectory)
    {
        try
        {
            File.WriteAllText(MarkerFilePath(directory), oldDirectory + Environment.NewLine);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// マーカーファイルを消す（後始末の完了、または不要と判断したとき）。
    /// 元々存在しない場合も成功として扱う。
    /// </summary>
    public static bool TryClear(string directory)
    {
        var path = MarkerFilePath(directory);
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>
/// 機能3: データ保存先の切り替え（「ポータブル」⇔「ユーザーフォルダ」）で、既存データ一式を
/// 新しい場所へコピーし、検証したうえでポインタファイルを切り替える。実態は「移動」だが、
/// 元の場所の削除はこのクラスの<see cref="Migrate"/>・<see cref="MigrateAndSwitchPointer"/>では
/// 一切行わない。削除は<see cref="RunPendingCleanup"/>が次回起動時に別途行う
/// （下記【なぜ即時削除ではなく次回起動時なのか】参照）。
///
/// 【安全性の方針】
/// - コピー先へは書くが、コピー元（<see cref="Migrate"/>のsourceDirectory）へは一切書き込まない。
///   ディスク不足等でコピーが途中失敗しても、元の場所のデータは常に無傷のまま残る。
/// - コピーが不完全（主要ファイル・フォルダの存在確認に失敗）な場合は、コピー先が
///   中途半端に残っていてもポインタファイルは書き換えない（<see cref="DataDirectoryPointer.TryWrite"/>
///   を呼ばない）。次回起動時も従来の場所がそのまま使われ続けるため、利用者から見て
///   「起動できなくなる」「データが消える」事故にはならない。
/// - 実行中のプロセスがデータを開いたまま移行するとファイルの整合性が壊れる恐れがあるため、
///   移行はコピー＋ポインタ切り替えのみに留め、実行中のアプリの参照先をその場で切り替える
///   ことはしない（設定画面から「再起動してください」と案内するだけ。<see cref="ViewModels.SettingsViewModel"/>
///   のデータ保存先まわりのコメント参照）。
///
/// 【なぜ即時削除ではなく次回起動時なのか】ユーザーからは「移動」として見えるべき操作だが、
/// 移行操作を行っているその瞬間、アプリ自身はまだ元の場所（<see cref="Migrate"/>の
/// sourceDirectory）を使って動作し続けている（Loggerがログファイルを開いたまま、設定は
/// 変更のたびに即時保存、WindowLayoutStoreは終了時に書き込む）。この場でいきなり元データを
/// 消すと、次の2つの実害が起こりうる。
///   (1) 開いたままのファイルの削除に失敗する。あるいは削除直後にアプリ側の保存処理が
///       同じ場所へ書き戻してしまい、消したはずのファイルが復活する。
///   (2) 移行完了後、案内どおりすぐに再起動せず使い続けた場合、その間の変更（設定変更・
///       パッチ適用によるバックアップ追加等）は引き続き元の場所へ書かれる。ここで元データを
///       消してしまうと、再起動前に行った変更がそのまま失われる。
/// そこで削除は行わず、代わりに新しい保存先へ「後始末待ち」マーカー
/// （<see cref="DataDirectoryPendingCleanupMarker"/>）を書くだけに留める。実際の削除は、
/// 次回起動時、<see cref="RunPendingCleanup"/>が「元の場所→新しい場所へもう一度取り込み直して
/// から削除する」ことで安全に行う（再起動を後回しにした間の変更もこの再取り込みで拾える）。
/// </summary>
public static class DataDirectoryMigrator
{
    // 移行対象のファイル。AppPathsが定義する各パスをそのまま使うことで、対象ファイルが
    // 将来増減してもここを個別に追随させる必要がないようにする。
    //
    // 不具合2の修正: OnboardingMarkerFilePath（初回起動ガイドの完了マーカー、
    // <see cref="Views.OnboardingWindow.GetMarkerFilePath"/>が参照）とWindowLayoutFilePath
    // （ウィンドウレイアウト、<see cref="ViewModels.WindowLayoutStore"/>が参照）は、
    // 以前はAppPaths自体にプロパティが無く各利用側がPath.Combineで自前に組み立てていたため、
    // このコピー対象一覧に挙がっておらず、移行後にオンボーディングが再表示される・
    // レイアウトが既定に戻る、という不具合の原因になっていた。AppPaths側にプロパティとして
    // 追加し（そちらのコメント参照）、ここでも同じプロパティを参照することで、
    // 「AppPathsのプロパティを網羅すれば移行対象も網羅できる」という当初の設計意図どおりに戻す。
    //
    // 移動方式への変更に伴い、<see cref="RunPendingCleanup"/>（次回起動時、元の場所の既知の
    // 対象だけを削除する）も同じ一覧を使う必要がある。コピー対象と削除対象を別々に書くと、
    // 将来ファイルが増減したときに片方だけ更新し忘れる（不具合2と同種の食い違い）ため、
    // KnownFilePaths/KnownDirectoryPathsを単一の情報源とし、コピー用のペア列挙・削除用の
    // 一覧のどちらもここから導く。
    private static IEnumerable<string> KnownFilePaths(AppPaths paths)
    {
        yield return paths.SettingsFilePath;
        yield return paths.ProjectsFilePath;
        yield return paths.TemplatesFilePath;
        yield return paths.QueueFilePath;
        yield return paths.OnboardingMarkerFilePath;
        yield return paths.WindowLayoutFilePath;
    }

    // 移行対象のディレクトリ（バックアップ一式・ログ一式）。
    private static IEnumerable<string> KnownDirectoryPaths(AppPaths paths)
    {
        yield return paths.BackupRootDirectory;
        yield return paths.LogsDirectory;
    }

    private static IEnumerable<(string Src, string Dst)> BuildFilePairs(AppPaths source, AppPaths target)
        => KnownFilePaths(source).Zip(KnownFilePaths(target));

    private static IEnumerable<(string Src, string Dst)> BuildDirectoryPairs(AppPaths source, AppPaths target)
        => KnownDirectoryPaths(source).Zip(KnownDirectoryPaths(target));

    /// <summary>
    /// <paramref name="sourceDirectory"/> の既存データ一式を <paramref name="targetDirectory"/> へ
    /// コピーし、検証する。成功時の <see cref="GraftResult{T}.Value"/> は
    /// <paramref name="targetDirectory"/>。ポインタファイルの書き換えはここでは行わない
    /// （呼び出し側が成功を確認してから<see cref="DataDirectoryPointer"/>を使う）。
    /// </summary>
    /// <param name="cleanupIncompleteTargetOnFailure">
    /// コピー失敗・検証失敗時に、中途半端に残った<paramref name="targetDirectory"/>を
    /// 丸ごと削除する（<see cref="TryCleanupIncompleteTarget"/>）かどうか。既定はtrueで、
    /// 設定画面からの移行（<see cref="MigrateAndSwitchPointer"/>）はこのまま使う
    /// （targetDirectoryはこれから切り替える先の新しい場所で、失敗時は片付けても実害が無い）。
    /// <see cref="RunPendingCleanup"/>は明示的にfalseを渡すこと。そちらのtargetDirectoryは
    /// 「現在まさに使用中のデータ保存先」であり、丸ごと削除すると生きている設定・履歴・
    /// バックアップを巻き添えで失う致命的な事故になる（同関数のコメント参照）。
    /// </param>
    public static GraftResult<string> Migrate(
        string sourceDirectory, string targetDirectory, bool cleanupIncompleteTargetOnFailure = true)
    {
        if (PathsEqual(sourceDirectory, targetDirectory))
        {
            // 同じ場所への「移行」は何もしなくてよい（既にその場所を使っている）。
            return GraftResult<string>.Ok(targetDirectory);
        }

        var source = new AppPaths(sourceDirectory);
        var target = new AppPaths(targetDirectory);

        try
        {
            Directory.CreateDirectory(targetDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return GraftResult<string>.Fail(ErrorCode.E407, ExceptionMessages.Describe(ex));
        }

        var filePairs = BuildFilePairs(source, target).ToArray();
        var directoryPairs = BuildDirectoryPairs(source, target).ToArray();

        try
        {
            foreach (var (src, dst) in filePairs)
            {
                if (!File.Exists(src)) continue;
                File.Copy(src, dst, overwrite: true);
            }

            foreach (var (src, dst) in directoryPairs)
            {
                if (!Directory.Exists(src)) continue;
                CopyDirectoryRecursive(src, dst);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (cleanupIncompleteTargetOnFailure) TryCleanupIncompleteTarget(targetDirectory, sourceDirectory);
            return GraftResult<string>.Fail(ErrorCode.E407, ExceptionMessages.Describe(ex));
        }

        if (!Verify(filePairs, directoryPairs))
        {
            if (cleanupIncompleteTargetOnFailure) TryCleanupIncompleteTarget(targetDirectory, sourceDirectory);
            return GraftResult<string>.Fail(ErrorCode.E407, "コピーした内容の検証に失敗しました。");
        }

        return GraftResult<string>.Ok(targetDirectory);
    }

    /// <summary>
    /// <see cref="Migrate"/> でコピー・検証したうえで、成功時のみポインタファイルを切り替える
    /// 一連の操作。<see cref="ViewModels.SettingsViewModel"/> はこちらを呼ぶ。
    /// </summary>
    /// <param name="exeDirectory">ポインタファイルを置く場所（exeと同じ階層）。</param>
    /// <param name="sourceDirectory">現在のデータ保存先。</param>
    /// <param name="targetDirectory">移行先のデータ保存先。</param>
    /// <param name="switchToPortable">
    /// trueの場合、コピー成功後にポインタファイルを「削除」してポータブル（exeと同じ階層）へ
    /// 戻す。falseの場合は<paramref name="targetDirectory"/>を指すポインタファイルを書く。
    /// </param>
    public static GraftResult<string> MigrateAndSwitchPointer(
        string exeDirectory, string sourceDirectory, string targetDirectory, bool switchToPortable)
    {
        var migrated = Migrate(sourceDirectory, targetDirectory);
        if (!migrated.IsSuccess)
        {
            // コピー・検証に失敗した時点でポインタファイルには一切触れない
            // （クラスドキュメントの安全性の方針を参照）。
            return migrated;
        }

        var pointerOk = switchToPortable
            ? DataDirectoryPointer.TryClear(exeDirectory)
            : DataDirectoryPointer.TryWrite(exeDirectory, targetDirectory);

        if (!pointerOk)
        {
            // データそのものはtargetDirectoryへコピー済みだが、切り替え用ファイルを
            // 書き込めなかった（exeフォルダへの書き込み権限が無い等）。次回起動時も
            // 従来の場所（sourceDirectory）が使われ続けるため実害は無いが、
            // その旨を利用者へ伝える必要がある。
            return GraftResult<string>.Fail(ErrorCode.E407,
                "データのコピーは完了しましたが、切り替え用ファイル（" + DataDirectoryPointer.FileName +
                "）を書き込めませんでした。exeと同じフォルダへの書き込み権限を確認してください。" +
                "コピーしたデータは " + targetDirectory + " に残っていますが、次回起動時も引き続き " +
                sourceDirectory + " が使われます。");
        }

        // 移動方式への変更: 元の場所（sourceDirectory）はここでは一切消さず、
        // 「次回起動時に取り込み直してから削除してよい」ことを示すマーカーを新しい保存先へ
        // 書くだけに留める（クラスドキュメントの【なぜ即時削除ではなく次回起動時なのか】参照）。
        // 同じ場所への「移行」（sourceDirectoryとtargetDirectoryが同じ）ではマーカーを書かない。
        // 書いてしまうと、次回起動時に「今まさに使っている場所」を削除対象と誤認してしまう。
        if (!PathsEqual(sourceDirectory, targetDirectory))
        {
            // マーカーを書けなくても、コピー・ポインタ切り替え自体は成功しているため全体としては
            // 成功のまま扱う（実害は「元の場所が次回起動時に自動削除されない」だけで、
            // データの整合性やアプリの起動には影響しない）。
            DataDirectoryPendingCleanupMarker.TryWrite(targetDirectory, sourceDirectory);
        }

        return migrated;
    }

    /// <summary>
    /// 起動時の後始末（<see cref="RunPendingCleanup"/>）の結果種別。
    /// </summary>
    public enum PendingCleanupResult
    {
        /// <summary>マーカーが無かった（前回、保存先の切り替えは行われていない）。何もしていない。</summary>
        NoMarker,

        /// <summary>マーカーはあったが、指す先が既に無い・現在地と同じだった。マーカーだけ消した。</summary>
        NothingToDo,

        /// <summary>取り込み直し・削除まで完了した（一部ファイルの削除に失敗していても、全体としては完了扱い）。</summary>
        Completed,

        /// <summary>元の場所から現在地への取り込み直しに失敗した。削除は行わず、マーカーも残した（次回再試行）。</summary>
        MigrateFailed,
    }

    /// <summary>
    /// <see cref="RunPendingCleanup"/>の結果。<see cref="Views.StartupCoordinator"/>がLoggerへ
    /// 記録するための情報を保持する（後始末そのものはLogger生成前に完了させる必要があるため、
    /// ログ記録は戻り値を受け取った呼び出し側がLogger生成後にまとめて行う）。
    /// </summary>
    public sealed record PendingCleanupOutcome(PendingCleanupResult Result, string? OldDirectory, string? Detail);

    /// <summary>
    /// 起動時の後始末。<see cref="currentBaseDirectory"/>（今回起動時に実際に使うデータ保存先）に
    /// 「後始末待ち」マーカー（<see cref="DataDirectoryPendingCleanupMarker"/>）があれば、
    /// マーカーが指す旧保存先から<paramref name="currentBaseDirectory"/>へもう一度
    /// <see cref="Migrate"/>を実行（再起動を後回しにして使い続けた間の変更を取り込むため）し、
    /// 成功すれば旧保存先の既知の対象だけを削除してマーカーを消す。
    ///
    /// 取り込み直しに失敗した場合は削除を行わずマーカーも残す（次回起動時にもう一度試みられる）。
    /// 呼び出し側（<see cref="Views.StartupCoordinator"/>）は、各ストアがファイルを開く前の
    /// 早い段階でこれを呼ぶこと（<see cref="ViewModels.SettingsViewModel"/>のデータ保存先まわりの
    /// コメント、および<see cref="DataDirectoryMigrator"/>クラスドキュメントの
    /// 【なぜ即時削除ではなく次回起動時なのか】参照）。
    /// </summary>
    public static PendingCleanupOutcome RunPendingCleanup(string currentBaseDirectory)
    {
        var oldDirectory = DataDirectoryPendingCleanupMarker.TryRead(currentBaseDirectory);
        if (oldDirectory is null)
        {
            return new PendingCleanupOutcome(PendingCleanupResult.NoMarker, null, null);
        }

        if (!Directory.Exists(oldDirectory) || PathsEqual(oldDirectory, currentBaseDirectory))
        {
            // 旧保存先が既に無い（利用者が手動で消した等）、または現在地と同じ（通常は
            // 起こらないはずだが念のため）。削除する対象が無いため、マーカーだけ消して終える。
            DataDirectoryPendingCleanupMarker.TryClear(currentBaseDirectory);
            return new PendingCleanupOutcome(PendingCleanupResult.NothingToDo, oldDirectory, null);
        }

        // 再起動を後回しにして使い続けた間の変更（設定変更・パッチ適用によるバックアップ追加等）
        // は旧保存先へ書かれ続けているため、削除の前にもう一度取り込み直す。既存のMigrateを
        // 再利用する（上書きコピー＋検証）が、cleanupIncompleteTargetOnFailure: falseを必ず渡す。
        // ここでのcurrentBaseDirectory（Migrateから見たtargetDirectory）は「これから切り替える先」
        // ではなく「今まさに使用中の生きているデータ保存先」そのもの。既定のtrueのままだと、
        // 万一この再取り込みが失敗したときにMigrateが中途半端な内容を片付けようとして
        // targetDirectory（＝currentBaseDirectory）を丸ごと削除してしまい、今使っている設定・
        // 履歴・バックアップまで巻き添えで失う致命的な事故になる（Migrateのコメント参照）。
        var migrated = Migrate(oldDirectory, currentBaseDirectory, cleanupIncompleteTargetOnFailure: false);
        if (!migrated.IsSuccess)
        {
            // 取り込みに失敗した場合は削除を行わない・マーカーも残す。次回起動でもう一度
            // 試みられる（失敗を握りつぶさないよう、呼び出し側でログに残すこと）。
            var detail = string.Join(" / ", migrated.Errors.Select(i => i.ToDisplayText()));
            return new PendingCleanupOutcome(PendingCleanupResult.MigrateFailed, oldDirectory, detail);
        }

        var deleteFailures = DeleteKnownContents(oldDirectory);
        DataDirectoryPendingCleanupMarker.TryClear(currentBaseDirectory);

        var resultDetail = deleteFailures.Count == 0
            ? null
            : "一部の削除に失敗しました: " + string.Join(", ", deleteFailures);
        return new PendingCleanupOutcome(PendingCleanupResult.Completed, oldDirectory, resultDetail);
    }

    /// <summary>
    /// 旧保存先（<paramref name="oldDirectory"/>）から、コピー対象一覧と同じ情報源
    /// （<see cref="KnownFilePaths"/>・<see cref="KnownDirectoryPaths"/>）に挙がっている
    /// Graft自身のファイル・ディレクトリだけを削除する。<see cref="DataDirectoryPointer.FileName"/>
    /// （datapath.txt）はこの一覧に含まれないため削除しない（保存先を指すポインタとして
    /// exeフォルダに残す必要がある）。
    ///
    /// 個別の削除に失敗しても（ロック等）中断せず残りを試み、失敗した対象のパスを返す
    /// （呼び出し側がログへ記録する）。既知の対象を消した結果、<paramref name="oldDirectory"/>自体が
    /// 空になっていれば、そのフォルダも削除する（<see cref="TryDeleteDirectoryIfEmpty"/>参照。
    /// exeフォルダ側はGraft.exe等が残るため通常は空にならず削除されない。ユーザーフォルダ側
    /// （%APPDATA%\Graft等）は既知の対象しか置かれていなければここで消える）。
    /// </summary>
    private static List<string> DeleteKnownContents(string oldDirectory)
    {
        var failures = new List<string>();
        var oldPaths = new AppPaths(oldDirectory);

        foreach (var file in KnownFilePaths(oldPaths))
        {
            if (!File.Exists(file)) continue;
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{file}（{ExceptionMessages.Describe(ex)}）");
            }
        }

        foreach (var dir in KnownDirectoryPaths(oldPaths))
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                // back/・logs/自体はGraftが管理するディレクトリ一式のため、配下も含めて丸ごと
                // 削除してよい（禁止しているのはoldDirectory自体の丸ごと削除。下記コメント参照）。
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{dir}（{ExceptionMessages.Describe(ex)}）");
            }
        }

        TryDeleteDirectoryIfEmpty(oldDirectory);

        return failures;
    }

    /// <summary>
    /// <paramref name="directory"/>が既に空（ファイル・サブディレクトリが1つも無い）の場合に限り、
    /// そのフォルダ自体を削除する。空でなければ何もしない（exeフォルダにはGraft.exeや利用者が
    /// 置いたファイルがあり得るため、既知の対象を消しただけで中身が残っていれば丸ごと削除しては
    /// いけない。<see cref="DataDirectoryMigrator"/>クラスドキュメント・呼び出し元の
    /// <see cref="DeleteKnownContents"/>コメント参照）。
    /// </summary>
    private static void TryDeleteDirectoryIfEmpty(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            if (Directory.EnumerateFileSystemEntries(directory).Any()) return;
            Directory.Delete(directory, recursive: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 消せなくても実害は無い（旧データは既に空でも、フォルダの入れ物が1つ残るだけ）。
        }
    }

    /// <summary>
    /// <paramref name="directory"/>に、コピー・削除対象と同じ情報源
    /// （<see cref="KnownFilePaths"/>・<see cref="KnownDirectoryPaths"/>）に挙がっている
    /// Graft自身のデータが1つでも存在するかどうか。<see cref="DataDirectoryRecovery"/>が
    /// 「exeフォルダは真にポータブル運用されていないか（＝既に何か保存済みでないか）」
    /// 「ユーザーフォルダに復帰すべきデータがあるか」の両方の判定に使う単一の情報源。
    ///
    /// 意図的にGraft.exeやランタイムのDLL等、配布物に最初から含まれるファイルは一切見ない
    /// （<see cref="KnownFilePaths"/>・<see cref="KnownDirectoryPaths"/>に含まれていないため、
    /// 自然にそうなる）。それらを含めてしまうと、どのexeフォルダでも常にtrueになり判定が
    /// 意味を成さなくなる。
    /// </summary>
    public static bool HasKnownContents(string directory)
    {
        var paths = new AppPaths(directory);
        return KnownFilePaths(paths).Any(File.Exists) || KnownDirectoryPaths(paths).Any(Directory.Exists);
    }

    private static bool Verify(
        IReadOnlyList<(string Src, string Dst)> filePairs, IReadOnlyList<(string Src, string Dst)> directoryPairs)
    {
        foreach (var (src, dst) in filePairs)
        {
            if (File.Exists(src) && !File.Exists(dst)) return false;
        }

        foreach (var (src, dst) in directoryPairs)
        {
            if (Directory.Exists(src) && !Directory.Exists(dst)) return false;
        }

        return true;
    }

    private static void CopyDirectoryRecursive(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var dir in Directory.EnumerateDirectories(sourceDir))
        {
            CopyDirectoryRecursive(dir, Path.Combine(targetDir, Path.GetFileName(dir)));
        }
    }

    /// <summary>
    /// コピー失敗時、中途半端に残ったコピー先だけをベストエフォートで片付ける
    /// （消せなくても実害は無い。sourceDirectoryには一切触れない安全確認のため引数で受け取る）。
    /// </summary>
    private static void TryCleanupIncompleteTarget(string targetDirectory, string sourceDirectory)
    {
        if (PathsEqual(targetDirectory, sourceDirectory)) return;

        try
        {
            if (Directory.Exists(targetDirectory)) Directory.Delete(targetDirectory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 片付けられなくても、元データ（sourceDirectory）は無傷なので実害は無い。
        }
    }

    private static bool PathsEqual(string a, string b)
    {
        var fullA = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a));
        var fullB = Path.TrimEndingDirectorySeparator(Path.GetFullPath(b));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(fullA, fullB, comparison);
    }
}

/// <summary>
/// 機能3の追加: インストーラを持たない配布物特有の穴への対応。
///
/// <see cref="DataDirectoryPointer"/>（datapath.txt）はexeと同じ階層にしか置けない。ところが
/// Graftはインストーラを持たない配布のため、次の2つが現実に起こりうる。
///   (1) 新しい版へ更新するためexeフォルダのファイル一式を丸ごと差し替える → datapath.txtが失われる。
///   (2) 新しくGraftをダウンロードして別の場所に展開する → 最初からdatapath.txtが無い。
/// どちらの場合もGraftはポータブルと誤認し、%APPDATA%\Graft等に残っている過去のデータ
/// （設定・登録済みプロジェクト一覧・適用履歴とバックアップ）が実在するのに一切使われないまま、
/// 初回起動ガイドが再表示されてしまう。適用履歴・バックアップは「元に戻す」ための命綱であり、
/// これが届かなくなるのは軽視できない。
///
/// この穴を塞ぐため、起動のごく初期に次の3条件がすべて成り立つときだけ「以前ユーザーフォルダに
/// 保存したデータを使いますか？」と尋ねる（<see cref="Views.StartupCoordinator.
/// ResolveDataDirectoryRecoveryAsync"/>が実際の確認・ポインタ書き込みを行う）。
///   1. exeと同じ階層にdatapath.txtが無い（<see cref="DataDirectoryPointer.TryRead"/>がnull）。
///   2. exeフォルダ自体にGraftの既知のデータが1つも無い（<see cref="DataDirectoryMigrator.
///      HasKnownContents"/>がfalse）。
///   3. 既定のユーザーフォルダ（<see cref="AppPaths.DefaultUserDataDirectory"/>）に既知のデータが
///      1つ以上ある。
///
/// 条件2が最も重要な安全弁。exeフォルダをUSB等で別のPCへ持ち運んだ先に、たまたま
/// %APPDATA%\Graftが残っていても、持ち運んだ側（exeフォルダ）に既にデータがあるため条件2で
/// 不成立となり、他人（そのPCの以前の利用者）のデータへ乗っ取られることはない
/// （ポータブル運用の安全性を壊さないための条件）。
///
/// 【実行順序が極めて重要】この判定は、<see cref="AppPaths.EnsureCoreDirectoriesExist"/>
/// （起動直後にback/・logs/を作る）や<see cref="Logger"/>の初期化より必ず前に行うこと。
/// 条件2「exeフォルダに既知のデータが1つも無い」は、判定の実行タイミングに依存して壊れる
/// 性質を持つ。Graft自身が起動のたびにexeフォルダ直下へback/・logs/を作ってしまえば、
/// 2回目以降の起動では常にexeフォルダへ「既知のデータ」が存在することになり、条件2が
/// 永久に不成立になってこの機能自体が死ぬ（一度でも普通に起動していれば、以後は復帰確認の
/// 対象に一切ならない、というのは仕様として正しい。しかし「AppPathsを作る前に」行うべき判定を
/// 誤って後回しにすると、単なる実装のバグとしてこれが起きてしまう）。呼び出し元
/// （<see cref="Views.StartupCoordinator"/>）は、<see cref="AppPaths"/>のインスタンスを作る
/// よりも前にこれを完了させること。
/// </summary>
public static class DataDirectoryRecovery
{
    /// <summary>
    /// 上記3条件がすべて成り立つときだけtrueを返す、副作用の無い純粋関数（単体テスト対象）。
    /// 呼び出し側が守るべき実行順序の注意は本クラスドキュメント参照。
    /// </summary>
    public static bool ShouldPromptForRecovery(string exeDirectory, string userDataDirectory)
    {
        // 条件1: exeと同じ階層のポインタファイルが無い（＝保存先が未確定）。
        if (DataDirectoryPointer.TryRead(exeDirectory) is not null) return false;

        // 条件2（最重要の安全弁）: exeフォルダ自体に既知のデータが1つも無い。
        if (DataDirectoryMigrator.HasKnownContents(exeDirectory)) return false;

        // 条件3: 既定のユーザーフォルダに既知のデータが1つ以上ある。
        return DataDirectoryMigrator.HasKnownContents(userDataDirectory);
    }
}

/// <summary>
/// <see cref="Views.StartupCoordinator.ResolveDataDirectoryRecoveryAsync"/>の結果種別。
/// </summary>
public enum DataDirectoryRecoveryResult
{
    /// <summary><see cref="DataDirectoryRecovery.ShouldPromptForRecovery"/>の3条件を満たさなかった。何もしていない。</summary>
    NotApplicable,

    /// <summary>「はい」を選び、ユーザーフォルダを指すポインタファイルを書いた。</summary>
    Recovered,

    /// <summary>「はい」を選んだが、ポインタファイルを書けなかった（exeフォルダへの書き込み権限が無い等）。</summary>
    RecoveredPointerWriteFailed,

    /// <summary>「いいえ」を選び、以後尋ねないようexeフォルダ自身を指すポインタファイル（＝明示的なポータブル）を書いた。</summary>
    DeclinedAndMarkedPortable,

    /// <summary>「いいえ」を選んだが、ポータブルを明示するポインタファイルを書けなかった（次回起動でまた尋ねる）。</summary>
    DeclinedPointerWriteFailed,

    /// <summary>
    /// キャンセル（「キャンセル」ボタン、またはタイトルバーの×）で閉じた。ポインタは一切書かず、
    /// 呼び出し元（<see cref="Views.App"/>）はGraftそのものをその場で終了させる。
    ///
    /// 【なぜ「保留して起動を続ける」ではなく「終了」なのか】以前はここで起動をそのまま続けていたが、
    /// それだと利用者が「今は決めない」を選んだつもりでも、続く起動処理
    /// （<see cref="AppPaths.EnsureCoreDirectoriesExist"/>がexeフォルダ直下にback/・logs/を作り、
    /// 続けてsettings.json等も書く）によってexeフォルダ自身に既知のGraftデータができてしまう。
    /// すると次回起動時には<see cref="DataDirectoryRecovery.ShouldPromptForRecovery"/>の条件2
    /// （exeフォルダに既知データが1つも無い）が永久に不成立となり、二度とこの確認が出せなくなる
    /// （実機のWindows環境で「キャンセルしても再度尋ねられない」として報告された不具合の真因）。
    /// 起動を継続しない限りexeフォルダには何も書かれないため、終了させれば次回起動時にまた
    /// 同じ確認が出せる。これが利用者の期待する「決めなかった」の実際の意味になる。
    /// </summary>
    Cancelled,
}

/// <summary>
/// 復帰確認の結果。<see cref="Views.StartupCoordinator"/>がLoggerへ記録するための情報を保持する
/// （<see cref="DataDirectoryMigrator.PendingCleanupOutcome"/>と同じ考え方。確認自体はLogger生成前
/// ＝AppPaths確定前に完了させる必要があるため、ログ記録は戻り値を受け取った呼び出し側が
/// Logger生成後にまとめて行う）。
/// </summary>
public sealed record DataDirectoryRecoveryOutcome(DataDirectoryRecoveryResult Result, string? UserDataDirectory)
{
    /// <summary>3条件を満たさず、確認自体を行わなかった場合の既定値。</summary>
    public static readonly DataDirectoryRecoveryOutcome NotApplicable =
        new(DataDirectoryRecoveryResult.NotApplicable, null);

    /// <summary>
    /// 呼び出し元（<see cref="Views.App.OnFrameworkInitializationCompleted"/>）が
    /// Graftそのものを終了させるべきかどうかの判定（副作用の無い純粋なプロパティ）。
    /// 実際に終了させる処理（<see cref="Environment.Exit(int)"/>等）自体はここでは行わない
    /// （判定と副作用を分けることで、この判定だけを単体テストできるようにする。
    /// <see cref="DataDirectoryRecoveryResult.Cancelled"/>のコメント参照）。
    /// </summary>
    public bool ShouldExitProcess => Result == DataDirectoryRecoveryResult.Cancelled;
}
