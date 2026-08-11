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
/// 機能3: データ保存先の切り替え（「ポータブル」⇔「ユーザーフォルダ」）で、既存データ一式を
/// 新しい場所へコピーし、検証したうえでポインタファイルを切り替える。
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
    private static IEnumerable<(string Src, string Dst)> BuildFilePairs(AppPaths source, AppPaths target)
    {
        yield return (source.SettingsFilePath, target.SettingsFilePath);
        yield return (source.ProjectsFilePath, target.ProjectsFilePath);
        yield return (source.TemplatesFilePath, target.TemplatesFilePath);
        yield return (source.QueueFilePath, target.QueueFilePath);
        yield return (source.OnboardingMarkerFilePath, target.OnboardingMarkerFilePath);
        yield return (source.WindowLayoutFilePath, target.WindowLayoutFilePath);
    }

    // 移行対象のディレクトリ（バックアップ一式・ログ一式）。
    private static IEnumerable<(string Src, string Dst)> BuildDirectoryPairs(AppPaths source, AppPaths target)
    {
        yield return (source.BackupRootDirectory, target.BackupRootDirectory);
        yield return (source.LogsDirectory, target.LogsDirectory);
    }

    /// <summary>
    /// <paramref name="sourceDirectory"/> の既存データ一式を <paramref name="targetDirectory"/> へ
    /// コピーし、検証する。成功時の <see cref="GraftResult{T}.Value"/> は
    /// <paramref name="targetDirectory"/>。ポインタファイルの書き換えはここでは行わない
    /// （呼び出し側が成功を確認してから<see cref="DataDirectoryPointer"/>を使う）。
    /// </summary>
    public static GraftResult<string> Migrate(string sourceDirectory, string targetDirectory)
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
            TryCleanupIncompleteTarget(targetDirectory, sourceDirectory);
            return GraftResult<string>.Fail(ErrorCode.E407, ExceptionMessages.Describe(ex));
        }

        if (!Verify(filePairs, directoryPairs))
        {
            TryCleanupIncompleteTarget(targetDirectory, sourceDirectory);
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

        return migrated;
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
