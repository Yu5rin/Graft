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
    ///
    /// 【入出力の失敗は破損と区別する】 ウイルス対策・同期ソフトなどが一瞬ファイルを掴んでいる
    /// だけで読めないことがある。以前はこれも「解析できなかった」と同じ扱いにして退避・既定値での
    /// 上書きまで進めており、settings.json などが既定値に戻ってしまった。今は読み取りの
    /// IOException／UnauthorizedAccessException を短い待ちつつ計約1秒まで再試行し、それでも
    /// 読めなければ、ファイルには一切触れず（退避も書き戻しもしない）、既定値に
    /// 「一時的に読めなかった」問題（E402・警告）を付けて返す。さらに、その既定値を呼び出し側が
    /// そのまま保存して実体を潰さないよう、このパスへの書き込みを次に正しく読めるまで止める
    /// （<see cref="UnreadablePaths"/>）。
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
            UnreadablePaths.TryRemove(NormalizeKey(path), out _);
            return GraftResult<T>.Ok(createDefault());
        }

        var (parsed, outcome, error) = await TryParseAsync<T>(path, options, ct).ConfigureAwait(false);
        if (outcome == ParseOutcome.Parsed)
        {
            // 正しく読めたので、過去の読み取り失敗による書き込み保護は解除する。
            UnreadablePaths.TryRemove(NormalizeKey(path), out _);
            return GraftResult<T>.Ok(parsed!);
        }

        if (outcome == ParseOutcome.Unreadable)
        {
            UnreadablePaths[NormalizeKey(path)] = 0;
            var issue = GraftIssue.Of(
                ErrorCode.E402,
                detail: $"{path} を一時的に読み取れませんでした（他のアプリが開いている可能性があります）。ファイルは変更せず、今回は既定値で動作します。この状態では上書き保存しません。{ExceptionMessages.Describe(error!)}",
                path: path,
                severity: Severity.Warning);
            return GraftResult<T>.Ok(createDefault(), new[] { issue });
        }

        return await RecoverFromCorruptionAsync(path, createDefault, options, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 読み取りに失敗して既定値を返したパス（正規化済み）の集合。ここに載っている間は
    /// <see cref="WriteAsync{T}"/> が何も書かない。インスタンスをまたぐ（各所が別々に
    /// JsonFileStore を持つ）ため静的にしている。読めなかった実体は「中身が不明」であり、
    /// 呼び出し側が既定値をそのまま保存すると本物の設定・履歴を失うため。
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> UnreadablePaths =
        new(StringComparer.Ordinal);

    /// <summary>
    /// このパスへの書き込みが、読み取り失敗の保護（<see cref="UnreadablePaths"/>）で止められているか。
    /// <see cref="WriteAsync{T}"/> は止められているとき<b>黙って何も書かずに戻る</b>ので、呼び出し側が
    /// 「保存できた」と利用者に伝えてよいかは、この問い合わせで知る（観点の追加で、書かれていないのに
    /// 「追加しました」と出てしまうのを防ぐため）。
    /// </summary>
    public bool IsWriteProtected(string path) => UnreadablePaths.ContainsKey(NormalizeKey(path));

    private static string NormalizeKey(string path)
    {
        var full = Path.GetFullPath(path);
        return OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
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

        var (parsed, _, _) = await TryParseAsync<T>(path, options, ct).ConfigureAwait(false);
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

        // 読めなかった実体を、読めなかった結果の既定値で潰さない（ReadWithRecoveryAsync参照）。
        // 例外にすると保存のたびに呼び出し元が落ちるため、何も書かずに戻る。利用者へは
        // 読み込み時に返した E402 の問題で「今回は保存されない」ことが伝わっている。
        if (UnreadablePaths.ContainsKey(NormalizeKey(path)))
        {
            return;
        }

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
    // 50ms x 20回で待ちの合計は約1秒（最後の失敗の後は待たないので実際は19回分）。ウイルス対策や
    // 同期ソフトの一瞬の掴みを越えられ、かつ起動を長く止めない長さとして選んだ。
    private const int ReadShareViolationRetryCount = 20;

    /// <summary>TryParseAsyncでの読み取り共有違反のリトライ間隔。</summary>
    private static readonly TimeSpan ReadShareViolationRetryDelay = TimeSpan.FromMilliseconds(50);

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

    /// <summary>TryParseAsyncの結果の種別。</summary>
    private enum ParseOutcome
    {
        /// <summary>JSONとして解析できた。</summary>
        Parsed,
        /// <summary>内容がJSONとして解析できない、または読み取り直前に消えた（破損扱い）。</summary>
        Corrupt,
        /// <summary>入出力の失敗で、再試行しても内容を読めなかった（中身は不明。破損とは限らない）。</summary>
        Unreadable,
    }

    private static async Task<(T? Value, ParseOutcome Outcome, Exception? Error)> TryParseAsync<T>(
        string path, JsonSerializerOptions options, CancellationToken ct)
        where T : class
    {
        string text;
        try
        {
            var read = string.Empty;
            // Windowsは読み取り中のファイルへ強制的な共有ロックを掛けるため、別タスクの
            // QuarantineAsyncのFile.Moveや他プロセスがこのファイルを掴んでいる瞬間に
            // File.ReadAllTextAsyncが共有違反のIOExceptionで失敗することがある。
            // 短い間隔で計約1秒まで読み直しを試みる。
            await RetryOnIoOrAccessDeniedAsync(
                async () => read = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false),
                ReadShareViolationRetryCount, ReadShareViolationRetryDelay, ct,
                onRetry: attempt => OnReadShareViolationRetry?.Invoke(path, attempt)).ConfigureAwait(false);
            text = read;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // ReadWithRecoveryAsync冒頭のFile.Existsから、ここへ到達するまでの間に、同じ破損
            // ファイルを同時に読んでいた別タスクが先にQuarantineAsyncで退避（移動）してしまった
            // 場合のTOCTOU。「解析できなかった」扱いで返し、呼び出し側の復旧フロー
            // （QuarantineAsyncは対象が既に無ければnullを返して「退避済み扱い」で続行する）へ委ねる。
            return (null, ParseOutcome.Corrupt, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 再試行の上限まで読めなかった。内容が壊れているのではなく「読めなかった」だけなので、
            // 破損とは区別して返す（退避・上書きの対象にしない）。
            return (null, ParseOutcome.Unreadable, ex);
        }

        try
        {
            var value = JsonSerializer.Deserialize<T>(text, options);
            return value is null ? (null, ParseOutcome.Corrupt, null) : (value, ParseOutcome.Parsed, null);
        }
        catch (JsonException)
        {
            return (null, ParseOutcome.Corrupt, null);
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
