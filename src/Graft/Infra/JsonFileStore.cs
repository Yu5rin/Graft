using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
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
        catch (IOException)
        {
            // 異なるボリューム間などで File.Move が失敗する場合の代替手順（6.7章準拠）。
            File.Copy(tempPath, path, overwrite: true);
            File.Delete(tempPath);
        }
    }

    /// <summary>
    /// ファイルを <c>.corrupt.&lt;yyyyMMdd_HHmmss&gt;</c> へ退避する（上書きしない）。
    /// 退避先の絶対パスを返す。
    /// </summary>
    public async Task<string> QuarantineAsync(string path, CancellationToken ct = default)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var candidate = $"{path}.corrupt.{stamp}";
        var suffix = 1;
        while (File.Exists(candidate))
        {
            candidate = $"{path}.corrupt.{stamp}_{suffix}";
            suffix++;
        }

        await Task.Run(() => File.Move(path, candidate), ct).ConfigureAwait(false);
        return candidate;
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

        var issue = GraftIssue.Of(
            ErrorCode.E404,
            detail: $"{path} をJSONとして解析できなかったため {corruptPath} へ退避し、既定値で再生成しました。",
            path: path,
            severity: Severity.Warning);
        return GraftResult<T>.Ok(fallback, new[] { issue });
    }
}
