using System.IO;
using System.Security.Cryptography;

namespace Graft.Core;

/// <summary>
/// 安全なファイル置換。6.4/6.7節に対応する。同一ボリューム上の一時ファイルへ書き出したうえで
/// <see cref="File.Replace(string, string, string?)"/>（非Windowsでは <see cref="File.Move(string, string, bool)"/>）
/// で置換し、失敗時は退避方式（別名へ退避してからリネーム）へフォールバックする。退避方式も
/// 失敗した場合は、メモリ上に残っている書き込み内容を対象ファイルへ直接書き戻す最終手段を試みる。
/// <para>
/// 【消失防止の保証】 どの経路を通っても、処理終了時に対象ファイルが「存在しない」状態のまま
/// 終わることは無い（対象が元々存在しなかった＝新規作成対象で、最終手段まですべて失敗した
/// 場合を除く）。この保証のため、対象が「処理開始前に存在していたか」は最初に一度だけ判定し、
/// 以降はその値を使い続ける。失敗の後に存在確認をやり直すと、失敗そのものが対象を消して
/// しまった場合に「元から無かった」と誤認し、退避方式による復旧を素通りしてしまう
/// （実機で観測されたファイル消失不具合の原因の一つ）。
/// </para>
/// <para>
/// 【成功の検証】 実機で「適用は成功したと記録されたのに対象ファイルが消えていた」という
/// 報告を受け、書き込みが成功と判定された直後に実際にディスク上へ意図した内容で書き込めて
/// いるかを検証する（<see cref="VerifyWrittenAsync"/>）。File.Replace 等が例外を投げずに
/// 完了しても、ウイルス対策ソフト等プロセス外の要因で直後にファイルが変化・消失する余地は
/// 否定できないため、「例外が出なかった＝成功」と過信しない。検証に失敗した場合は書き込みを
/// 最初からやり直し（自己修復）、それでも一致しなければ失敗として報告する（無音のデータ
/// 消失を避ける）。
/// </para>
/// <para>
/// ウイルス対策ソフト等による直後アクセス失敗に備え、書き込み・置換とも100ms間隔で最大3回
/// リトライする。ただし <see cref="File.Replace(string, string, string?)"/> のように部分的に
/// 成功しうる操作は、直前の試行から一時ファイル・対象ファイルの状態が変化していないことを
/// 確認できた場合に限りリトライする（状態が変化していれば、部分的に壊れた可能性があるため
/// 即座に退避方式へ委ねる）。
/// </para>
/// </summary>
public static class SafeFileWriter
{
    private const int RetryCount = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// 書き込み後の検証でファイル内容のハッシュ再計算まで行う上限サイズ。これを超える場合は
    /// 存在確認とサイズ照合のみ行う（巨大ファイルで毎回全体を読み直すコストを避けるため）。
    /// Graftの適用対象は主に設定で許可されたテキスト/ソースファイルであり、8MBはその典型的な
    /// 上限を十分に超える値として選んだ。これを超えるファイルでも「存在するか」「サイズが
    /// 一致するか」は必ず確認するため、消失や大幅な欠落は引き続き検出できる。
    /// </summary>
    private const long HashVerificationThresholdBytes = 8 * 1024 * 1024;

    /// <summary>
    /// 検証に失敗した場合に書き込み全体をやり直す最大回数（初回を含む）。1回の自己修復機会を
    /// 与えつつ、外部要因が持続的にファイルへ干渉しているケースでは無音にリトライを重ねず
    /// 速やかに失敗として報告するため、小さい値にとどめている。
    /// </summary>
    private const int MaxVerifyAttempts = 2;

    /// <summary>ファイルの内容をバイト列で安全に置換する。書き込み先が存在しない場合は新規作成する。</summary>
    public static Task<GraftResult<bool>> ReplaceAsync(string fullPath, byte[] content, CancellationToken ct = default)
        => ReplaceAsync(fullPath, content, RealPrimaryReplaceOp.Instance, RealMoveOp.Instance, ct);

    /// <summary>
    /// テスト用オーバーロード。一次経路（File.Replace 相当）・退避方式のリネームに使う
    /// 低レベル操作を差し替え可能にする。Windows実機の File.Replace が起こしうる「部分的な
    /// 失敗」や「例外を投げずに完了したのに直後に内容が失われる」状況をLinux上でも再現する
    /// ために使う（本体の判定ロジックにテスト専用の分岐は増やさず、OSプリミティブの
    /// 呼び出し口だけを差し替える）。
    /// </summary>
    internal static async Task<GraftResult<bool>> ReplaceAsync(
        string fullPath, byte[] content, IPrimaryReplaceOp primaryOp, IMoveOp moveOp, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
        {
            return GraftResult<bool>.Fail(ErrorCode.E402, "書き込み先ディレクトリを特定できません", path: fullPath);
        }

        var expectedHash = ComputeHashHex(content);
        var issues = new List<GraftIssue>();

        for (var verifyAttempt = 1; verifyAttempt <= MaxVerifyAttempts; verifyAttempt++)
        {
            var attempt = await ReplaceOnceAsync(fullPath, content, directory, primaryOp, moveOp, ct).ConfigureAwait(false);
            if (!attempt.IsSuccess)
            {
                // 一次経路・退避方式・最終手段のいずれも尽くしたうえでの失敗。これ以上やり直しても
                // 状況が改善する見込みは薄いため、ここで打ち切って利用者へ報告する。
                return attempt;
            }
            issues.AddRange(attempt.Issues);

            if (await VerifyWrittenAsync(fullPath, content, expectedHash, ct).ConfigureAwait(false))
            {
                return GraftResult<bool>.Ok(true, issues);
            }

            issues.Add(GraftIssue.Of(ErrorCode.E402,
                $"書き込みは成功と判定されましたが、直後の確認でディスク上の内容が一致しませんでした（{verifyAttempt}回目）。" +
                "ウイルス対策ソフト等、Graft以外の要因が直後に影響した可能性があります。書き込みをやり直します",
                path: fullPath, severity: Severity.Warning));

            if (verifyAttempt < MaxVerifyAttempts)
            {
                await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
            }
        }

        return GraftResult<bool>.Fail(ErrorCode.E402,
            $"書き込み後の確認を{MaxVerifyAttempts}回試みましたが、ディスク上のファイルが書き込んだ内容と一致しません。" +
            "ウイルス対策ソフトなど、Graft以外の要因が直後にファイルへ影響している可能性があります。ファイルの内容を手動で確認してください",
            path: fullPath);
    }

    /// <summary>一次経路→退避方式→最終手段の1サイクル分。成功時も検証はまだ行っていない。</summary>
    private static async Task<GraftResult<bool>> ReplaceOnceAsync(
        string fullPath, byte[] content, string directory, IPrimaryReplaceOp primaryOp, IMoveOp moveOp, CancellationToken ct)
    {
        // 対象が「処理開始前に存在していたか」は、ここで一度だけ判定し以降使い続ける。
        var targetExisted = File.Exists(LongPath.Extended(fullPath));
        var tempPath = Path.Combine(directory, MakeTempFileName(fullPath));

        try
        {
            await RetryIoAsync(() => WriteFileAsync(tempPath, content, ct), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(tempPath);
            // 対象ファイルにはまだ一切手を付けていないため安全（元の内容のまま）。
            return GraftResult<bool>.Fail(ErrorCode.E402, $"一時ファイルの作成に失敗しました: {ExceptionMessages.Describe(ex)}", path: fullPath);
        }

        try
        {
            await ReplacePrimaryWithRetryAsync(fullPath, tempPath, targetExisted, primaryOp, ct).ConfigureAwait(false);
            return GraftResult<bool>.Ok(true);
        }
        catch (Exception primaryEx) when (primaryEx is IOException or UnauthorizedAccessException)
        {
            return await FallbackReplaceAsync(fullPath, tempPath, targetExisted, content, moveOp, primaryEx, ct).ConfigureAwait(false);
        }
    }

    private static async Task WriteFileAsync(string path, byte[] content, CancellationToken ct)
    {
        var ioPath = LongPath.Extended(path);
        await using var stream = new FileStream(ioPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        await stream.WriteAsync(content, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 対象への一次的な置換。File.Replace（Windows）は内部で複数手順を踏むため部分的に
    /// 失敗しうる。そのため、リトライ前に一時ファイル・対象ファイルの状態が直前の試行から
    /// 変化していないかを確認し、変化していれば（＝部分的に壊れた可能性があれば）即座に
    /// 諦めてフォールバックへ委ねる。状態が変化していない場合のみ、ウイルス対策ソフト等に
    /// よる直後アクセス失敗を見込んで100ms間隔で最大3回リトライする。
    /// </summary>
    private static async Task ReplacePrimaryWithRetryAsync(
        string fullPath, string tempPath, bool targetExisted, IPrimaryReplaceOp primaryOp, CancellationToken ct)
    {
        var target = LongPath.Extended(fullPath);
        var temp = LongPath.Extended(tempPath);

        for (var attempt = 1; attempt <= RetryCount; attempt++)
        {
            try
            {
                primaryOp.Execute(target, temp, targetExisted);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < RetryCount)
            {
                var stateUnchanged = File.Exists(temp) && (!targetExisted || File.Exists(target));
                if (!stateUnchanged)
                {
                    throw;
                }
                await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// 一次経路失敗時のフォールバック。元ファイルを別名へ退避してから一時ファイルをリネームし、
    /// 成功後に退避ファイルを削除する。ネットワークドライブやクラウド同期フォルダで異なる
    /// ボリューム扱いとなり File.Replace が失敗するケースを想定する。これも失敗した場合は
    /// メモリ上に残っている書き込み内容 <paramref name="content"/> を対象ファイルへ直接
    /// 書き戻す最終手段を試みる（<see cref="LastResortWriteBackAsync"/>）。
    /// </summary>
    private static async Task<GraftResult<bool>> FallbackReplaceAsync(
        string fullPath, string tempPath, bool targetExisted, byte[] content, IMoveOp moveOp, Exception primaryCause, CancellationToken ct)
    {
        var backupPath = fullPath + $".graft-bak-{Guid.NewGuid():N}";
        var backupCreated = false;

        try
        {
            // 元々存在していた場合のみ退避する。一次経路の失敗で既に対象が消えている場合は
            // 退避不要（無いものは退避できない）。ここでの存在確認は「復旧を諦める」判断には
            // 使わない点に注意（このあと temp が残っていれば必ず復旧を試み、残っていなくても
            // 最終手段へ進む）。
            if (targetExisted && File.Exists(LongPath.Extended(fullPath)))
            {
                await RetryIoAsync(() => MoveAsync(moveOp, fullPath, backupPath), ct).ConfigureAwait(false);
                backupCreated = true;
            }

            if (File.Exists(LongPath.Extended(tempPath)))
            {
                await RetryIoAsync(() => MoveAsync(moveOp, tempPath, fullPath), ct).ConfigureAwait(false);
                if (backupCreated)
                {
                    TryDelete(backupPath);
                }
                var issue = GraftIssue.Of(ErrorCode.E402, "代替手順（退避方式）で書き込みました", severity: Severity.Info);
                return GraftResult<bool>.Ok(true, new[] { issue });
            }

            // 一時ファイルが失われている（一次経路の失敗で消費された等）。最終手段へ進む。
            return await LastResortWriteBackAsync(fullPath, tempPath, content, backupPath, backupCreated, primaryCause, null, ct).ConfigureAwait(false);
        }
        catch (Exception fallbackEx) when (fallbackEx is IOException or UnauthorizedAccessException)
        {
            return await LastResortWriteBackAsync(fullPath, tempPath, content, backupPath, backupCreated, primaryCause, fallbackEx, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 最終手段。一次経路・退避方式のいずれも失敗した場合に、メモリ上に残っている書き込み
    /// 内容を対象ファイルへ直接書き戻す。書き込む内容は <see cref="ReplaceAsync"/> の呼び出し
    /// から最後まで保持されているため、これが失敗するのは書き込み権限そのものが無い場合くらい
    /// で、その場合は元ファイルも消せていないはず（6.4/6.7節）。
    /// </summary>
    private static async Task<GraftResult<bool>> LastResortWriteBackAsync(
        string fullPath, string tempPath, byte[] content, string backupPath, bool backupCreated,
        Exception primaryCause, Exception? fallbackCause, CancellationToken ct)
    {
        // ここまで来た時点で一時ファイルはもう使わない（退避方式のMoveが失敗した等でまだ
        // 残っている可能性があるため、後始末として削除しておく）。
        TryDelete(tempPath);

        try
        {
            await RetryIoAsync(() => WriteFileAsync(fullPath, content, ct), ct).ConfigureAwait(false);

            var message = backupCreated
                ? $"想定外の状況が発生したため緊急の書き戻しで復旧しました。元の内容の退避ファイルが残っています（不要であれば削除してください）: {backupPath}"
                : "想定外の状況が発生したため緊急の書き戻しで復旧しました";
            var issue = GraftIssue.Of(ErrorCode.E402, message, severity: Severity.Warning);
            return GraftResult<bool>.Ok(true, new[] { issue });
        }
        catch (Exception lastEx) when (lastEx is IOException or UnauthorizedAccessException)
        {
            // 最終手段も失敗。退避してあった元ファイルは可能な限り復元する。
            if (backupCreated)
            {
                RestoreFromBackup(fullPath, backupPath);
            }

            var remaining = File.Exists(LongPath.Extended(fullPath))
                ? "元のファイルは復元できています"
                : backupCreated && File.Exists(LongPath.Extended(backupPath))
                    ? $"退避ファイルにのみ元の内容が残っています。手動で戻してください: {backupPath}"
                    : "対象ファイルの内容が失われた可能性があります";

            var detail = fallbackCause is null
                ? $"代替手順でも書き込みに失敗しました: {ExceptionMessages.Describe(lastEx)}（一次要因: {ExceptionMessages.Describe(primaryCause)}）"
                : $"代替手順でも書き込みに失敗しました: {ExceptionMessages.Describe(fallbackCause)}（一次要因: {ExceptionMessages.Describe(primaryCause)}、緊急の書き戻しも失敗: {ExceptionMessages.Describe(lastEx)}）";

            return GraftResult<bool>.Fail(ErrorCode.E402, $"{detail}。{remaining}", path: fullPath);
        }
    }

    /// <summary>
    /// 書き込みが成功と判定された直後に、実際にディスク上へ意図した内容で書き込めているかを
    /// 検証する。存在確認とサイズ照合は常に行い、<see cref="HashVerificationThresholdBytes"/>
    /// 以下のファイルはハッシュ再計算まで行って厳密に照合する。
    /// </summary>
    private static async Task<bool> VerifyWrittenAsync(string fullPath, byte[] content, string expectedHashHex, CancellationToken ct)
    {
        var ioPath = LongPath.Extended(fullPath);
        if (!File.Exists(ioPath)) return false;

        long length;
        try
        {
            length = new FileInfo(ioPath).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        if (length != content.LongLength) return false;

        if (content.LongLength > HashVerificationThresholdBytes)
        {
            // 巨大ファイルは毎回全体を読み直すコストを避け、存在確認とサイズ照合のみで妥当とみなす。
            return true;
        }

        byte[] actual;
        try
        {
            actual = await File.ReadAllBytesAsync(ioPath, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return string.Equals(ComputeHashHex(actual), expectedHashHex, StringComparison.OrdinalIgnoreCase);
    }

    private static string ComputeHashHex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static Task MoveAsync(IMoveOp moveOp, string sourcePath, string destinationPath)
    {
        moveOp.Move(LongPath.Extended(sourcePath), LongPath.Extended(destinationPath));
        return Task.CompletedTask;
    }

    private static void RestoreFromBackup(string fullPath, string backupPath)
    {
        try
        {
            var target = LongPath.Extended(fullPath);
            var backup = LongPath.Extended(backupPath);
            if (!File.Exists(target) && File.Exists(backup))
            {
                File.Move(backup, target);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 復旧できない場合も上位へは元の失敗を返す。退避ファイルはそのまま残す。
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

/// <summary>
/// <see cref="SafeFileWriter"/> の一次経路（File.Replace 相当）を表す抽象。既定実装
/// （<see cref="RealPrimaryReplaceOp"/>）は本番と同じ挙動。テストはこれを差し替えたフェイクを
/// 注入し、Windows の File.Replace が起こしうる部分的な失敗・「例外なく完了したのに直後に
/// 内容が失われる」状況をLinux上でも再現する。
/// </summary>
internal interface IPrimaryReplaceOp
{
    /// <summary><paramref name="extendedTarget"/>・<paramref name="extendedTemp"/>はいずれも
    /// <see cref="LongPath.Extended(string)"/>適用済みの絶対パス。</summary>
    void Execute(string extendedTarget, string extendedTemp, bool targetExisted);
}

internal sealed class RealPrimaryReplaceOp : IPrimaryReplaceOp
{
    public static readonly RealPrimaryReplaceOp Instance = new();

    public void Execute(string extendedTarget, string extendedTemp, bool targetExisted)
    {
        if (OperatingSystem.IsWindows())
        {
            if (targetExisted)
            {
                File.Replace(extendedTemp, extendedTarget, null);
            }
            else
            {
                File.Move(extendedTemp, extendedTarget);
            }
        }
        else
        {
            // Linux/macOS（主にテスト実行環境）では File.Replace の代わりに上書きMoveを用いる
            File.Move(extendedTemp, extendedTarget, overwrite: true);
        }
    }
}

/// <summary>
/// <see cref="SafeFileWriter"/> の退避方式で使うリネーム操作の抽象。既定実装は
/// <see cref="File.Move(string, string, bool)"/>（上書きあり）そのもの。
/// </summary>
internal interface IMoveOp
{
    void Move(string extendedSource, string extendedDestination);
}

internal sealed class RealMoveOp : IMoveOp
{
    public static readonly RealMoveOp Instance = new();

    public void Move(string extendedSource, string extendedDestination)
        => File.Move(extendedSource, extendedDestination, overwrite: true);
}
