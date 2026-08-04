using System.Globalization;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace Graft.Infra;

/// <summary>ログの重大度。15章の logLevel 設定と対応する。</summary>
public enum LogLevel
{
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warn = 3,
    Error = 4,
}

/// <summary>
/// 1件のログ記録。15章のとおりタイムスタンプ・リビジョン・イベント種別・対象パス・
/// 結果・所要ミリ秒のみを保持する。ファイルの中身そのものは含めない。
/// </summary>
public sealed record LogEntry
{
    public required DateTimeOffset Timestamp { get; init; }
    public required LogLevel Level { get; init; }
    public required string EventType { get; init; }
    public int? Revision { get; init; }
    public string? TargetPath { get; init; }
    public required string Result { get; init; }
    public long? DurationMs { get; init; }
}

/// <summary>
/// logs/yyyyMMdd.log へJSON Lines形式で追記するロガー。
/// 書き込みはチャネル（キュー）+ 単一の書き込みタスクにより非同期・スレッドセーフに行い、
/// ログ書き込みの失敗でアプリを落とさないことを最優先とする。
/// </summary>
public sealed class Logger : IAsyncDisposable
{
    private static readonly JsonSerializerOptions LineOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly AppPaths _paths;
    private readonly Channel<LogEntry> _channel;
    private readonly Task _writerTask;

    public Logger(AppPaths paths, LogLevel minLevel = LogLevel.Info, bool autoCleanupOnStart = true)
    {
        _paths = paths;
        MinLevel = minLevel;
        _channel = Channel.CreateUnbounded<LogEntry>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        _writerTask = Task.Run(WriteLoopAsync);

        if (autoCleanupOnStart)
        {
            _ = SafeCleanupAsync();
        }
    }

    /// <summary>現在の最低ログレベル。これ未満のイベントは記録しない。</summary>
    public LogLevel MinLevel { get; set; }

    public void Trace(string eventType, string result, string? targetPath = null, int? revision = null, long? durationMs = null)
        => Write(LogLevel.Trace, eventType, result, targetPath, revision, durationMs);

    public void Debug(string eventType, string result, string? targetPath = null, int? revision = null, long? durationMs = null)
        => Write(LogLevel.Debug, eventType, result, targetPath, revision, durationMs);

    public void Info(string eventType, string result, string? targetPath = null, int? revision = null, long? durationMs = null)
        => Write(LogLevel.Info, eventType, result, targetPath, revision, durationMs);

    public void Warn(string eventType, string result, string? targetPath = null, int? revision = null, long? durationMs = null)
        => Write(LogLevel.Warn, eventType, result, targetPath, revision, durationMs);

    public void Error(string eventType, string result, string? targetPath = null, int? revision = null, long? durationMs = null)
        => Write(LogLevel.Error, eventType, result, targetPath, revision, durationMs);

    /// <summary>90日を超えたログファイルを削除する。起動時に呼び出す想定（15章）。</summary>
    public async Task CleanupOldLogsAsync(int retentionDays = 90, CancellationToken ct = default)
        => await Task.Run(() => CleanupOldLogsCore(retentionDays), ct).ConfigureAwait(false);

    /// <summary>キューに残った書き込みを完了させてから終了する。</summary>
    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        try
        {
            await _writerTask.ConfigureAwait(false);
        }
        catch
        {
            // 終了処理中の失敗でアプリを落とさない
        }
    }

    private void Write(LogLevel level, string eventType, string result, string? targetPath, int? revision, long? durationMs)
    {
        if (level < MinLevel)
        {
            return;
        }

        var entry = new LogEntry
        {
            Timestamp = DateTimeOffset.Now,
            Level = level,
            EventType = eventType,
            Result = result,
            TargetPath = targetPath,
            Revision = revision,
            DurationMs = durationMs,
        };

        // Unbounded チャネルへの書き込みは基本的に失敗しないが、
        // ログ書き込みの失敗でアプリを落とさないという要件上、念のため握りつぶす。
        try
        {
            _channel.Writer.TryWrite(entry);
        }
        catch
        {
            // 無視する
        }
    }

    private async Task WriteLoopAsync()
    {
        StreamWriter? writer = null;
        string? currentDate = null;
        try
        {
            await foreach (var entry in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                writer = EnsureWriter(writer, entry.Timestamp, ref currentDate);
                await WriteEntrySafeAsync(writer, entry).ConfigureAwait(false);
            }
        }
        finally
        {
            await DisposeWriterSafeAsync(writer).ConfigureAwait(false);
        }
    }

    private StreamWriter? EnsureWriter(StreamWriter? writer, DateTimeOffset timestamp, ref string? currentDate)
    {
        var date = timestamp.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        if (writer is not null && date == currentDate)
        {
            return writer;
        }

        _ = DisposeWriterSafeAsync(writer);
        currentDate = date;
        return OpenWriterSafe(date);
    }

    private StreamWriter? OpenWriterSafe(string date)
    {
        try
        {
            Directory.CreateDirectory(_paths.LogsDirectory);
            var path = Path.Combine(_paths.LogsDirectory, $"{date}.log");
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, useAsync: true);
            return new StreamWriter(stream) { AutoFlush = false };
        }
        catch
        {
            // ログファイルを開けなくてもアプリは継続する
            return null;
        }
    }

    private static async Task WriteEntrySafeAsync(StreamWriter? writer, LogEntry entry)
    {
        if (writer is null)
        {
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(entry, LineOptions);
            await writer.WriteLineAsync(json).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }
        catch
        {
            // ログ書き込みの失敗でアプリを落とさない
        }
    }

    private static async Task DisposeWriterSafeAsync(StreamWriter? writer)
    {
        if (writer is null)
        {
            return;
        }

        try
        {
            await writer.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // 無視する
        }
    }

    private void CleanupOldLogsCore(int retentionDays)
    {
        if (!Directory.Exists(_paths.LogsDirectory))
        {
            return;
        }

        var threshold = DateTime.Today.AddDays(-retentionDays);
        foreach (var file in Directory.EnumerateFiles(_paths.LogsDirectory, "????????.log"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var isOld = DateTime.TryParseExact(name, "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var fileDate) && fileDate < threshold;
            if (isOld)
            {
                TryDeleteFile(file);
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // 削除できなくても起動は継続する
        }
    }

    private async Task SafeCleanupAsync()
    {
        try
        {
            await CleanupOldLogsAsync().ConfigureAwait(false);
        }
        catch
        {
            // 起動時クリーンアップの失敗でアプリを落とさない
        }
    }
}
