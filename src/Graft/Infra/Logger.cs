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
    private readonly int _processId;
    private readonly Channel<LogEntry> _channel;
    private readonly Task _writerTask;

    /// <param name="paths">ログファイルの置き場所（logs/）を含む各種パスの解決元。</param>
    /// <param name="minLevel">この重大度未満のイベントは記録しない。</param>
    /// <param name="autoCleanupOnStart">起動時に90日超の古いログを自動削除するか。</param>
    /// <param name="processIdOverride">
    /// ログファイル名に使うプロセスID（<see cref="AppPaths.GetLogFilePath"/>参照）。
    /// 省略時は<see cref="Environment.ProcessId"/>（本番の挙動）。
    /// 単体テストで「同一プロセス内に、別プロセストして振る舞う複数のLoggerが同時に
    /// 存在する」状況（多重起動・自己再起動の再現）を作るためだけに用意した引数で、
    /// 本番コードから渡すことは想定していない。
    /// </param>
    public Logger(AppPaths paths, LogLevel minLevel = LogLevel.Info, bool autoCleanupOnStart = true, int? processIdOverride = null)
    {
        _paths = paths;
        _processId = processIdOverride ?? Environment.ProcessId;
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
            // 不具合1の修正: ファイル名にプロセスIDを含める（AppPaths.GetLogFilePathのコメント参照）ことで、
            // 多重起動や自己再起動で複数のGraftプロセスが同時に存在しても、物理的に別ファイルへ
            // 書き込むようにする。日付の解釈はここではなく呼び出し元（EnsureWriter）で
            // DateOnlyへ変換済みなので、date引数はそのまま渡す。
            var dateOnly = DateOnly.ParseExact(date, "yyyyMMdd", CultureInfo.InvariantCulture);
            var path = _paths.GetLogFilePath(dateOnly, _processId);
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
        // 不具合1の修正でファイル名が yyyyMMdd.log（旧）と yyyyMMdd-<pid>.log（新）の
        // 2形態になった。"????????.log" では新形式（8文字ちょうどではない）を拾えず
        // 掃除されないまま残ってしまうため、"*.log" 全件から先頭8文字だけを日付として
        // 解釈する（新形式・旧形式のどちらでも先頭8文字が yyyyMMdd になっている）。
        foreach (var file in Directory.EnumerateFiles(_paths.LogsDirectory, "*.log"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name.Length < 8)
            {
                continue;
            }

            var isOld = DateTime.TryParseExact(name[..8], "yyyyMMdd", CultureInfo.InvariantCulture,
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
