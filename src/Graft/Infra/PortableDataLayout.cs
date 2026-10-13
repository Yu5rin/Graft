using System.IO;
using Graft.Core;

namespace Graft.Infra;

/// <summary><see cref="PortableDataLayout.Resolve"/>の結果種別。</summary>
public enum PortableDataResult
{
    /// <summary>data フォルダを使う。移すものは無かった（新規インストール、または移行済み）。</summary>
    PortableReady,

    /// <summary>datapath.txt が利用者の明示した場所（または空・存在しない場所）を指しているので、何もせずその場所を使う。</summary>
    CustomLocation,

    /// <summary>旧配置（exe の横）から data フォルダへ移した。</summary>
    MigratedFromLegacy,

    /// <summary>ユーザーフォルダ（1.0.25 以前から使っているもの）から data フォルダへ移した。</summary>
    MigratedFromUserFolder,

    /// <summary>exe の横と data の両方に既知のデータがある。どちらも上書きせず data を使う（警告）。</summary>
    BothHaveData,

    /// <summary>ユーザーフォルダと data の両方に既知のデータがある。どちらも上書きせずユーザーフォルダのまま使う（警告）。</summary>
    UserFolderAndDataConflict,

    /// <summary>旧配置から移そうとして失敗した。旧配置のまま起動する。</summary>
    FailedKeptLegacy,

    /// <summary>ユーザーフォルダから移そうとして失敗した。ユーザーフォルダのまま起動する。</summary>
    FailedKeptUserFolder,

    /// <summary>exe のフォルダに書き込めず data を作れない。旧配置のまま起動する。</summary>
    NotWritableKeptLegacy,

    /// <summary>exe のフォルダに書き込めず data を作れない（datapath.txt も消せない）。ユーザーフォルダのまま起動する。</summary>
    NotWritableKeptUserFolder,
}

/// <summary>起動時のデータ保存先の確定結果。</summary>
/// <param name="ExeDirectory">exe のフォルダ。</param>
/// <param name="BaseDirectory">この起動で実際に使うデータ保存先（<see cref="AppPaths"/> に渡す）。</param>
/// <param name="SourceDirectory">移行の元（旧配置の exe フォルダ、またはユーザーフォルダ）。無ければ null。</param>
/// <param name="Detail">失敗の理由、または一部の後始末に失敗した旨。無ければ null。</param>
public sealed record PortableDataOutcome(
    PortableDataResult Result, string ExeDirectory, string BaseDirectory, string? SourceDirectory, string? Detail)
{
    /// <summary>移行に失敗した、または移せない状態で起動を続けるか（ログは Warn、利用者へも知らせる）。</summary>
    public bool IsProblem => Result is PortableDataResult.FailedKeptLegacy or PortableDataResult.FailedKeptUserFolder
        or PortableDataResult.NotWritableKeptLegacy or PortableDataResult.NotWritableKeptUserFolder
        or PortableDataResult.BothHaveData or PortableDataResult.UserFolderAndDataConflict;

    /// <summary>
    /// 利用者向けのダイアログを出す対象か。<see cref="PortableDataResult.NotWritableKeptLegacy"/> は
    /// 既存の「書き込めない」警告（StartupCoordinator.WriteCheck）が毎回出るので、重ねて出さない。
    /// </summary>
    public bool ShouldNotifyUser => IsProblem && Result != PortableDataResult.NotWritableKeptLegacy;

    /// <summary>テストや既存の経路が、移行を伴わずに基準ディレクトリだけ決めたいとき用。</summary>
    public static PortableDataOutcome Plain(string exeDirectory, string baseDirectory)
        => new(PortableDataResult.CustomLocation, exeDirectory, baseDirectory, null, null);
}

/// <summary>
/// 1.0.26: ポータブル運用のデータ保存先を「exe の横」から「exe のフォルダ直下の data」へ変え、
/// 起動時に旧配置・ユーザーフォルダ（1.0.25 以前から使っているもの）から自動で移す。
///
/// 【不変条件（データを失わないための核心）】移行の元は、移行が完全に終わるまで唯一の正本である。
///   1. data に「移行中」の印（pending-cleanup.txt が元を指す。<see cref="DataDirectoryPendingCleanupMarker"/>）を
///      コピーより先に書く。
///   2. 既存の <see cref="DataDirectoryMigrator.RunPendingCleanup"/> で、元から data へコピー・検証
///      （大きさまで）し、成功してはじめて元の既知の対象を消して印を消す。
///   3. 印が残っている間（途中で落ちた、失敗した）は、起動は元のまま続ける。data は半端なコピーで
///      ありうるので使わない。次の起動でも印が元を指していれば、元から取り直す（上書き）。
///      元が正本のままなので、取り直しで新しい変更を古い内容で潰すことは無い。
///   4. ユーザーフォルダの場合の datapath.txt は、移行が済んでから消す。落ちても、次の起動で
///      「元が空になったポインタ」として片付く。
/// 実行中のアプリの保存先を差し替えることはしない（起動のごく初期、各ストアがファイルを開く前に
/// 終える。App.axaml.cs から Logger の生成よりも前に呼ぶ）。
///
/// 【ユーザーフォルダを自動で移す範囲】datapath.txt に <see cref="DataDirectoryPointer.ExplicitMarker"/> が
/// 無いものだけ。1.0.26 以降に設定画面の「ユーザーフォルダへ移動」で選んだ場所には印が付くので、
/// 次の起動で勝手に data へ戻ることは無い。印の無いものは 1.0.25 以前の版、または復帰確認で
/// 書かれたもので、初回の 1.0.26 起動で一度だけ移される（移した後は datapath.txt が消えるので、
/// 二度目は無い）。
/// </summary>
public static class PortableDataLayout
{
    /// <summary>旧通知の識別ファイル。同じ理由の通知を毎回の起動で繰り返さないために使う。</summary>
    public const string NoticeFileName = "data-migration-notice.txt";

    public static PortableDataOutcome Resolve(string exeDirectory)
    {
        var data = AppPaths.PortableDataDirectory(exeDirectory);
        var pointer = DataDirectoryPointer.TryRead(exeDirectory);
        var pointerIsOther = pointer is not null
            && !AppPaths.PathsEqual(pointer, exeDirectory) && !AppPaths.PathsEqual(pointer, data);

        string source;
        if (pointerIsOther)
        {
            // 利用者が明示的に選んだ場所、実在しない場所（外付けドライブが外れている等）、
            // 既知のデータが無い場所には触れない。ポインタも消さない。
            if (DataDirectoryPointer.IsExplicitChoice(exeDirectory))
            {
                return new(PortableDataResult.CustomLocation, exeDirectory, pointer!, null, null);
            }

            if (!DataDirectoryMigrator.HasKnownContents(pointer!))
            {
                // 前回、移行の後始末で元を消したあと、datapath.txt を消す前に落ちた状態を見分ける:
                // data に本物のデータがあり、元の親フォルダは存在する（フォルダ自体が空で消えただけ）。
                // 外付けドライブが外れているだけなら親も無いので、こちらには入らず触れない。
                var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(pointer!));
                if (parent is not null && Directory.Exists(parent) && DataDirectoryMigrator.HasKnownContents(data))
                {
                    DataDirectoryPointer.TryClear(exeDirectory);
                    return new(PortableDataResult.PortableReady, exeDirectory, data, null, null);
                }

                return new(PortableDataResult.CustomLocation, exeDirectory, pointer!, null, null);
            }

            source = pointer!;
        }
        else
        {
            source = exeDirectory;
        }

        var marker = DataDirectoryPendingCleanupMarker.TryRead(data);
        var markerPointsAtSource = marker is not null && AppPaths.PathsEqual(marker, source);
        var dataHasContents = DataDirectoryMigrator.HasKnownContents(data);

        if (!DataDirectoryMigrator.HasKnownContents(source))
        {
            // 移すものが無い（新規インストール、または移行済み）。
            TryClearStalePointer(exeDirectory);
            try
            {
                Directory.CreateDirectory(data);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // data を作れない（書き込めない場所）。旧配置のまま起動し、既存の書き込み警告に任せる。
                return new(PortableDataResult.NotWritableKeptLegacy, exeDirectory, exeDirectory, null,
                    $"{AppPaths.PortableDataDirectoryName} フォルダを作れませんでした: {ExceptionMessages.Describe(ex)}");
            }

            return new(PortableDataResult.PortableReady, exeDirectory, data, null, null);
        }

        if ((dataHasContents && !markerPointsAtSource) || (marker is not null && !markerPointsAtSource))
        {
            // 移行の途中ではない data に、すでに本物のデータがある。どちらも上書きしない。
            return pointerIsOther
                ? new(PortableDataResult.UserFolderAndDataConflict, exeDirectory, source, source,
                    $"{data} にもデータがあるため、ユーザーフォルダのデータは移しませんでした。")
                : new(PortableDataResult.BothHaveData, exeDirectory, data, source,
                    $"{source} にも古いデータが残っています。{data} を使い、どちらも上書き・削除していません。");
        }

        // ここから移行。元の側（ユーザーフォルダ / exe）が失敗時の継続先。
        var failedResult = pointerIsOther ? PortableDataResult.FailedKeptUserFolder : PortableDataResult.FailedKeptLegacy;
        var notWritableResult = pointerIsOther ? PortableDataResult.NotWritableKeptUserFolder : PortableDataResult.NotWritableKeptLegacy;

        if (!AppPaths.CanWriteToDirectory(exeDirectory))
        {
            return new(notWritableResult, exeDirectory, source, source,
                $"実行ファイルのフォルダ（{exeDirectory}）へ書き込めないため、{AppPaths.PortableDataDirectoryName} フォルダを作れません。");
        }

        try
        {
            Directory.CreateDirectory(data);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(notWritableResult, exeDirectory, source, source,
                $"{AppPaths.PortableDataDirectoryName} フォルダを作れませんでした: {ExceptionMessages.Describe(ex)}");
        }

        if (!markerPointsAtSource && !DataDirectoryPendingCleanupMarker.TryWrite(data, source))
        {
            return new(failedResult, exeDirectory, source, source, "移行中の印を書き込めませんでした。");
        }

        var cleanup = DataDirectoryMigrator.RunPendingCleanup(data);
        if (cleanup.Result is not (DataDirectoryMigrator.PendingCleanupResult.Completed
            or DataDirectoryMigrator.PendingCleanupResult.NothingToDo))
        {
            // 印は残る。次の起動で元から取り直す。
            return new(failedResult, exeDirectory, source, source, cleanup.Detail ?? "コピーに失敗しました。");
        }

        var detail = cleanup.Detail;
        if (!DataDirectoryPointer.TryClear(exeDirectory))
        {
            detail = (detail is null ? "" : detail + " ") +
                $"{DataDirectoryPointer.FileName} を消せませんでした（次回起動時に片付きます）。";
        }

        TryDeleteNotice(source);
        TryDeleteNotice(data);
        return new(pointerIsOther ? PortableDataResult.MigratedFromUserFolder : PortableDataResult.MigratedFromLegacy,
            exeDirectory, data, source, detail);
    }

    // 旧版の「明示的にポータブル」（datapath.txt が exe 自身を指す）は意味を失ったので消す。
    private static void TryClearStalePointer(string exeDirectory)
    {
        var pointer = DataDirectoryPointer.TryRead(exeDirectory);
        if (pointer is not null && AppPaths.PathsEqual(pointer, exeDirectory)) DataDirectoryPointer.TryClear(exeDirectory);
    }

    /// <summary>
    /// 同じ内容の通知を既に出したか。出していなければ（または内容が変わっていれば）
    /// 記録して true を返す。記録先は継続して使う保存先（<see cref="PortableDataOutcome.BaseDirectory"/>）。
    /// 書き込めない場合は「出していない」扱いで true を返す（見落とすより繰り返す方がまし）。
    /// </summary>
    public static bool TryMarkNoticeShown(string baseDirectory, string key)
    {
        var path = Path.Combine(baseDirectory, NoticeFileName);
        try
        {
            if (File.Exists(path) && File.ReadAllText(path).Trim() == key) return false;
            File.WriteAllText(path, key + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 記録できなくても通知は出す。
        }

        return true;
    }

    private static void TryDeleteNotice(string directory)
    {
        try
        {
            var path = Path.Combine(directory, NoticeFileName);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 残っても無害。
        }
    }
}
