using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Graft.Core;

namespace Graft.Infra;

/// <summary>
/// JSON設定・履歴ファイル（settings.json / projects.json / manifest.json）の
/// 汎用的な読み書きを行う。13.1章の破損時復旧（<c>.corrupt.&lt;日時&gt;</c> への退避と
/// 既定値からの再生成）をここに共通実装し、各ファイル種別から利用できるようにする。
/// </summary>
public sealed class JsonFileStore
{
    /// <summary>
    /// 既定のシリアライズ設定。camelCase・インデント付き・日本語をエスケープしない。
    /// </summary>
    public static readonly JsonSerializerOptions DefaultOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
        // NaN/Infinity等の名前付き浮動小数点リテラルを読み書き両方で受け付ける。
        // WindowLayoutState.Left/Top のような「未保存」を表す値でJSON直列化が
        // 例外にならないようにするための設定（バグ1対応）。旧形式（"NaN"文字列）の
        // layout.jsonも読めるよう、書き込みだけでなく読み込み側にも対称に効かせる。
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    /// <summary>
    /// ファイルを読み込み、JSONとして解析できない場合は破損として退避したうえで
    /// 既定値を書き戻す。ファイルが存在しない場合は単に既定値を返す（破損扱いにしない）。
    /// </summary>
    public async Task<GraftResult<T>> ReadWithRecoveryAsync<T>(
        string path,
        Func<T> createDefault,
        JsonSerializerOptions? options = null,
        CancellationToken ct = default)
        where T : class
    {
        options ??= DefaultOptions;
        if (!File.Exists(path))
        {
            return GraftResult<T>.Ok(createDefault());
        }

        var parsed = await TryParseAsync<T>(path, options, ct).ConfigureAwait(false);
        if (parsed is not null)
        {
            return GraftResult<T>.Ok(parsed);
        }

        return await RecoverFromCorruptionAsync(path, createDefault, options, ct).ConfigureAwait(false);
    }

    /// <summary>ファイルを解析するだけで、破損時の退避は行わない検証用API。</summary>
    public async Task<GraftResult<T>> ValidateJsonAsync<T>(
        string path,
        JsonSerializerOptions? options = null,
        CancellationToken ct = default)
        where T : class
    {
        options ??= DefaultOptions;
        if (!File.Exists(path))
        {
            return GraftResult<T>.Fail(ErrorCode.E404, detail: "ファイルが存在しません。", path: path);
        }

        var parsed = await TryParseAsync<T>(path, options, ct).ConfigureAwait(false);
        return parsed is not null
            ? GraftResult<T>.Ok(parsed)
            : GraftResult<T>.Fail(ErrorCode.E404, detail: "JSONとして解析できませんでした。", path: path);
    }

    /// <summary>
    /// 値をJSONへ直列化し、同一ボリューム上の一時ファイル経由でアトミックに書き込む。
    /// </summary>
    public async Task WriteAsync<T>(
        string path,
        T value,
        JsonSerializerOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= DefaultOptions;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = $"{path}.tmp.{Guid.NewGuid():N}";
        var json = JsonSerializer.Serialize(value, options);
        await File.WriteAllTextAsync(tempPath, json, ct).ConfigureAwait(false);

        try
        {
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 異なるボリューム間などで File.Move が失敗する場合の代替手順（6.7章準拠）。
            // Windows では、複数タスクがほぼ同時に同じ対象ファイルへ上書きしようとした場合や、
            // ウイルス対策ソフト・インデクサが書き込み直後のファイルを一時的に掴んでいる場合に
            // File.Move / File.Copy が UnauthorizedAccessException を投げることがある
            // （IOExceptionの派生ではないため、この捕捉が無いと素通りしてしまう）。
            // Copy+Delete も同じ理由で一時的に失敗しうるため、短い間隔で数回だけリトライする。
            await RetryOnIoOrAccessDeniedAsync(
                () =>
                {
                    File.Copy(tempPath, path, overwrite: true);
                    File.Delete(tempPath);
                    return Task.CompletedTask;
                },
                WriteFallbackRetryCount, WriteFallbackRetryDelay, ct).ConfigureAwait(false);
        }
    }

    /// <summary>WriteAsyncの代替手順（Copy+Delete）のリトライ上限回数。</summary>
    private const int WriteFallbackRetryCount = 5;

    /// <summary>WriteAsyncの代替手順のリトライ間隔。</summary>
    private static readonly TimeSpan WriteFallbackRetryDelay = TimeSpan.FromMilliseconds(30);

    /// <summary>
    /// IOException/UnauthorizedAccessExceptionを短い間隔で最大<paramref name="maxAttempts"/>回まで
    /// リトライする共通ヘルパ。上限まで試しても解決しない場合は最後の例外をそのまま投げ、
    /// 呼び出し側（ReadWithRecoveryAsync等）へ従来どおりエラーとして伝える（無限に粘らない）。
    /// internalなのは、実機（Windows）でしか自然には起きないUnauthorizedAccessExceptionからの
    /// 回復を、例外を投げるフェイクの<paramref name="action"/>を使ってLinux上でも検証できるように
    /// するため（不具合2の回帰防止）。
    /// </summary>
    internal static async Task RetryOnIoOrAccessDeniedAsync(
        Func<Task> action, int maxAttempts, TimeSpan delay, CancellationToken ct = default, Action<int>? onRetry = null)
    {
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await action().ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < maxAttempts)
            {
                // 上限に達していない間だけ捕捉して再試行する。上限到達時はこのwhen節がfalseになり、
                // 例外がそのまま呼び出し側へ伝わり従来どおりエラーになる。
                // onRetryは実際にリトライへ入る（＝共有違反が実際に起きた）ことをテストへ通知する
                // ためのシーム。既定はnullで、その場合は何もしない（本番の挙動は変わらない）。
                onRetry?.Invoke(attempt);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>同時に同じファイルの退避を試みるタスクが競合し続けた場合に諦めるまでの最大試行回数。</summary>
    private const int MaxQuarantineAttempts = 50;

    /// <summary>
    /// 破損ファイルを <c>&lt;path&gt;.corrupt.&lt;日時&gt;[_連番]</c> へ退避する。
    ///
    /// 複数タスクが同じ破損ファイルを同時に読み込んだ場合、それぞれがここへ到達しうる。
    /// 移動先candidateの存在確認（File.Exists）とFile.Moveの実行の間には隙間があるため、
    /// 確認時点では空いていた名前へ別タスクが先に移動してしまい、自分のFile.MoveがIOException
    /// （移動先が既に存在する）で失敗することがある（TOCTOU）。File.Existsによる事前確認は
    /// 衝突を減らす軽い最適化として残しつつ、実際の衝突検出と再試行はFile.Move自体が投げる
    /// IOExceptionの捕捉で行う。連番を進めながら<see cref="MaxQuarantineAttempts"/>回まで再試行し、
    /// それでも解決しない場合は退避を諦めてnullを返す（呼び出し側は「退避済み扱い」で続行する。
    /// <see cref="ReadWithRecoveryAsync{T}"/>参照）。
    /// </summary>
    public async Task<string?> QuarantineAsync(string path, CancellationToken ct = default)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

        for (var attempt = 0; attempt < MaxQuarantineAttempts; attempt++)
        {
            var candidate = attempt == 0 ? $"{path}.corrupt.{stamp}" : $"{path}.corrupt.{stamp}_{attempt}";
            if (File.Exists(candidate)) continue;

            try
            {
                await Task.Run(() => File.Move(path, candidate), ct).ConfigureAwait(false);
                return candidate;
            }
            catch (FileNotFoundException)
            {
                // 元ファイルが既に無い（別タスクが退避・削除済み）。退避済み扱いへ倒す。
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
            catch (IOException)
            {
                // 移動先が競合して既に存在する（他タスクが同名で先に退避した）。連番を進めて再試行する。
            }
            catch (UnauthorizedAccessException)
            {
                // Windows でウイルス対策ソフト・インデクサ等が元ファイルを一時的に掴んでいる場合
                // （推測）。候補名を変えても解決しないため、短く待ってから同じ流れで再試行する。
                await Task.Delay(WriteFallbackRetryDelay, ct).ConfigureAwait(false);
            }
        }

        // 上限まで衝突が続いた場合は退避を諦める。呼び出し側の「退避済み扱い」へ倒す。
        return null;
    }

    /// <summary>
    /// ファイルをコピーする。ファイル定義の実体コピーが目的（設定エクスポート等）で、
    /// 移動先ディレクトリが無ければ作成する。コピー元が存在しない場合は false を返す。
    /// </summary>
    public async Task<bool> CopyAsync(string sourcePath, string destinationPath, CancellationToken ct = default)
    {
        if (!File.Exists(sourcePath))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        await using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        await source.CopyToAsync(destination, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>TryParseAsyncでの読み取り共有違反(IOException)のリトライ上限回数。</summary>
    private const int ReadShareViolationRetryCount = 5;

    /// <summary>TryParseAsyncでの読み取り共有違反のリトライ間隔。</summary>
    private static readonly TimeSpan ReadShareViolationRetryDelay = TimeSpan.FromMilliseconds(30);

    /// <summary>
    /// テスト専用の内部シーム。読み取り中の共有違反(IOException)によりTryParseAsyncがリトライへ
    /// 入る直前に、対象パスと試行回数(1始まり)とともに呼び出される。既定はnullで、その場合は
    /// 何もしない（本番の挙動は一切変えない）。
    ///
    /// 目的: 不具合1（Windows実機の共有違反リトライ）の回帰テストは、Windowsの強制共有ロックを
    /// FileShare.Noneの排他ハンドルでLinux上でも再現しているが、「排他ハンドルをいつ解放するか」を
    /// Task.Delayのような固定時間で決めると、CI負荷でスレッドプールが枯渇した際にリトライの猶予
    /// （既定で 5回×30ms ≒ 150ms）を固定時間の待ちが超えてしまい、解放前にリトライを使い切って
    /// 不定期に失敗する（壁時計依存のフレーク）。このシームで「実際にリトライへ入った瞬間」を
    /// テストへ通知することで、テストは時間ではなく製品側の実際の挙動を合図にハンドルを解放できる。
    ///
    /// 【テスト以外から使用禁止】 本番コードから呼び出したり値を設定したりしてはならない。
    /// 【並行実行への注意】 これは静的プロパティであり、xUnitは同一アセンブリ内の別テストクラスを
    /// 並行実行しうる。他クラスがこのシームを設定することは無い前提だが、念のためコールバック側で
    /// 対象パス（TempWorkspaceが払い出す一意なパス）を照合し、無関係な呼び出しには反応しない設計に
    /// することを強く推奨する（JsonFileStoreTests参照）。
    /// </summary>
    internal static Action<string, int>? OnReadShareViolationRetry { get; set; }

    private static async Task<T?> TryParseAsync<T>(string path, JsonSerializerOptions options, CancellationToken ct)
        where T : class
    {
        try
        {
            var text = string.Empty;
            // 不具合1対応: Windowsは読み取り中のファイルへ強制的な共有ロックを掛けるため、
            // 別タスクがQuarantineAsyncのFile.Move等でこのファイルを掴んでいる瞬間に
            // File.ReadAllTextAsyncが共有違反のIOExceptionで失敗することがある
            // （Linuxには強制共有ロックが無いため通常は表面化しない）。既存のRetryOnIoOrAccessDeniedAsync
            // を再利用し、短い間隔で数回だけ読み直しを試みる。
            await RetryOnIoOrAccessDeniedAsync(
                async () => text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false),
                ReadShareViolationRetryCount, ReadShareViolationRetryDelay, ct,
                onRetry: attempt => OnReadShareViolationRetry?.Invoke(path, attempt)).ConfigureAwait(false);
            return JsonSerializer.Deserialize<T>(text, options);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // ReadWithRecoveryAsync冒頭のFile.Existsから、ここへ到達するまでの間に、同じ破損
            // ファイルを同時に読んでいた別タスクが先にQuarantineAsyncで退避（移動）してしまった
            // 場合のTOCTOU（File.Exists後の隙間で対象が無くなる）。JsonExceptionと同様に
            // 「解析できなかった」扱いで返し、呼び出し側の復旧フロー（QuarantineAsyncは対象が
            // 既に無ければnullを返して「退避済み扱い」で続行する）へ委ねる。
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // RetryOnIoOrAccessDeniedAsyncの上限まで共有違反が解消しなかった場合。
            // 「別タスクが処理中」とみなし、FileNotFoundException等と同様に「解析できなかった」
            // 扱いで返すことで、呼び出し側の復旧フロー（既定値での再生成）へ穏当に委ねる。
            // ここで例外をそのまま投げてしまうと、破損ファイルの並行復旧テストが意図する
            // 「最終的に全タスクが既定値で成功する」という前提が崩れてしまう。
            return null;
        }
    }

    private async Task<GraftResult<T>> RecoverFromCorruptionAsync<T>(
        string path,
        Func<T> createDefault,
        JsonSerializerOptions options,
        CancellationToken ct)
        where T : class
    {
        var corruptPath = await QuarantineAsync(path, ct).ConfigureAwait(false);
        var fallback = createDefault();

        var detail = corruptPath is not null
            ? $"{path} をJSONとして解析できなかったため {corruptPath} へ退避し、既定値で再生成しました。"
            : $"{path} をJSONとして解析できなかったため、既定値で再生成しました。";
        var issues = new List<GraftIssue>
        {
            GraftIssue.Of(ErrorCode.E404, detail: detail, path: path, severity: Severity.Warning),
        };

        // 不具合1対応: 「既定値を返す」ことと「既定値を永続化する」ことを分離する。
        // 対象ファイルが他プロセス（ウイルス対策ソフト・同期ソフト・別のGraftインスタンス等）に
        // 掴まれたまま共有違反が続くと、WriteAsyncのフォールバック（Copy+Delete）が
        // RetryOnIoOrAccessDeniedAsyncの上限まで解消せず例外を投げることがある（Windows実機）。
        // TryPersistDefaultAsyncでその例外を吸収し、書き戻しに失敗しても既定値そのものは
        // 必ず呼び出し元へ返す。読み取り（起動）が書き込みの失敗に巻き込まれて例外で落ちてはならない。
        var persistIssue = await TryPersistDefaultAsync(
            () => WriteAsync(path, fallback, options, ct), path).ConfigureAwait(false);
        if (persistIssue is not null)
        {
            issues.Add(persistIssue);
        }

        return GraftResult<T>.Ok(fallback, issues);
    }

    /// <summary>
    /// 既定値の永続化アクションを実行し、失敗しても例外を投げず警告のGraftIssueへ変換する
    /// （不具合1対応）。internalなのは、実機（Windows）でしか自然には再現しない共有違反からの
    /// 回復を、例外を投げるフェイクの<paramref name="persistAction"/>を使ってLinux上でも
    /// 検証できるようにするため。
    /// </summary>
    internal static async Task<GraftIssue?> TryPersistDefaultAsync(Func<Task> persistAction, string path)
    {
        try
        {
            await persistAction().ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return GraftIssue.Of(
                ErrorCode.E402,
                detail: $"既定値の書き戻しに失敗しました。ファイルの内容は既定値のまま次回の保存時まで更新されません。{ExceptionMessages.Describe(ex)}",
                path: path,
                severity: Severity.Warning);
        }
    }
}
