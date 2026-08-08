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
        Func<Task> action, int maxAttempts, TimeSpan delay, CancellationToken ct = default)
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

    private static async Task<T?> TryParseAsync<T>(string path, JsonSerializerOptions options, CancellationToken ct)
        where T : class
    {
        try
        {
            var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
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
        await WriteAsync(path, fallback, options, ct).ConfigureAwait(false);

        var detail = corruptPath is not null
            ? $"{path} をJSONとして解析できなかったため {corruptPath} へ退避し、既定値で再生成しました。"
            : $"{path} をJSONとして解析できなかったため、既定値で再生成しました。";
        var issue = GraftIssue.Of(
            ErrorCode.E404,
            detail: detail,
            path: path,
            severity: Severity.Warning);
        return GraftResult<T>.Ok(fallback, new[] { issue });
    }
}
