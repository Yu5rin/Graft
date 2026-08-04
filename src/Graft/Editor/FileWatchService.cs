using System.IO;
using System.Windows.Threading;
using Graft.Core;

namespace Graft.Editor;

/// <summary>
/// プロジェクトルート配下を <see cref="FileSystemWatcher"/> で監視する（仕様書4.2・4.6）。
/// イベントが短時間に多発するため200〜300msデバウンスしてまとめ、更新が必要なディレクトリの
/// 一覧（<see cref="DirectoriesChanged"/>）と、変更のあった個別ファイルのフルパス
/// （<see cref="FileContentChanged"/>）の2種類の通知に振り分ける。監視開始に失敗しても例外は
/// 投げず、呼び出し側は<see cref="Start"/>が返す<see cref="GraftResult{T}"/>のE704警告を見て
/// 手動更新（更新ボタン）で継続できるようにする。UI層（Editor/）に置くのは指示（附録A）どおり。
/// </summary>
public sealed class FileWatchService : IDisposable
{
    private static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan SuppressDuration = TimeSpan.FromSeconds(2);

    private readonly DispatcherTimer _debounceTimer;
    private readonly HashSet<string> _pendingPaths = new(PathComparer);
    private readonly Dictionary<string, DateTimeOffset> _suppressed = new(PathComparer);
    private FileSystemWatcher? _watcher;
    private string? _projectRoot;

    public FileWatchService()
    {
        _debounceTimer = new DispatcherTimer { Interval = DebounceInterval };
        _debounceTimer.Tick += OnDebounceTick;
    }

    /// <summary>再列挙が必要なディレクトリ（プロジェクトルートからの相対パス。ルート自身は空文字列）。</summary>
    public event EventHandler<IReadOnlyList<string>>? DirectoriesChanged;

    /// <summary>ディスク上で変更のあったファイルの絶対パス（仕様書4.6の外部変更検知用）。</summary>
    public event EventHandler<string>? FileContentChanged;

    /// <summary>監視を開始する。失敗してもアプリを落とさずE704警告として返す。</summary>
    public GraftResult<bool> Start(string projectRoot)
    {
        Stop();
        _projectRoot = projectRoot;
        try
        {
            var watcher = new FileSystemWatcher(projectRoot)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                    | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            watcher.Created += OnFileSystemEvent;
            watcher.Deleted += OnFileSystemEvent;
            watcher.Changed += OnFileSystemEvent;
            watcher.Renamed += OnRenamed;
            watcher.Error += OnWatcherError;
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
            return GraftResult<bool>.Ok(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _watcher = null;
            return GraftResult<bool>.Fail(GraftIssue.Of(ErrorCode.E704, ex.Message, path: projectRoot, severity: Severity.Warning));
        }
    }

    /// <summary>監視を停止する。プロジェクト切替・破棄時に呼ぶ。</summary>
    public void Stop()
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= OnFileSystemEvent;
            _watcher.Deleted -= OnFileSystemEvent;
            _watcher.Changed -= OnFileSystemEvent;
            _watcher.Renamed -= OnRenamed;
            _watcher.Error -= OnWatcherError;
            _watcher.Dispose();
            _watcher = null;
        }
        _debounceTimer.Stop();
        lock (_pendingPaths) _pendingPaths.Clear();
    }

    /// <summary>
    /// 自分自身（エクスプローラの操作やエディタの保存）による書き込みで発火するイベントを
    /// 無限ループさせないよう、一定時間そのパスの通知を抑制する（仕様書4.2・4.6）。
    /// </summary>
    public void SuppressPath(string fullPath)
    {
        lock (_suppressed) _suppressed[fullPath] = DateTimeOffset.UtcNow.Add(SuppressDuration);
    }

    public void Dispose() => Stop();

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        QueuePath(e.OldFullPath);
        QueuePath(e.FullPath);
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e) => QueuePath(e.FullPath);

    /// <summary>NTFSの内部バッファ溢れ等。個々のパスが分からないためルート全体の再列挙を促す。</summary>
    private void OnWatcherError(object sender, ErrorEventArgs e) => QueuePath(_projectRoot ?? string.Empty);

    private void QueuePath(string fullPath)
    {
        if (IsSuppressed(fullPath)) return;
        lock (_pendingPaths) _pendingPaths.Add(fullPath);

        // FileSystemWatcherのコールバックはスレッドプール上で実行されるため、
        // DispatcherTimer（UIスレッド専用）の開始はDispatcher経由で行う。
        _debounceTimer.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_debounceTimer.IsEnabled) _debounceTimer.Start();
        }));
    }

    private bool IsSuppressed(string fullPath)
    {
        lock (_suppressed)
        {
            if (!_suppressed.TryGetValue(fullPath, out var until)) return false;
            if (until < DateTimeOffset.UtcNow)
            {
                _suppressed.Remove(fullPath);
                return false;
            }
            return true;
        }
    }

    private void OnDebounceTick(object? sender, EventArgs e)
    {
        _debounceTimer.Stop();
        string[] paths;
        lock (_pendingPaths)
        {
            paths = _pendingPaths.ToArray();
            _pendingPaths.Clear();
        }
        if (paths.Length == 0) return;

        var dirs = paths
            .Select(p => ToRelativeDirectory(_projectRoot, p))
            .Where(d => d is not null)
            .Select(d => d!)
            .Distinct(PathComparer)
            .ToList();
        if (dirs.Count > 0) DirectoriesChanged?.Invoke(this, dirs);
        foreach (var path in paths) FileContentChanged?.Invoke(this, path);
    }

    /// <summary>変更されたパスの「親ディレクトリ」をプロジェクトルート相対で返す（ルート自身は空文字列）。</summary>
    private static string? ToRelativeDirectory(string? root, string fullPath)
    {
        if (string.IsNullOrEmpty(root)) return null;
        var trimmedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var trimmedPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (PathComparer.Equals(trimmedPath, trimmedRoot)) return string.Empty;

        var dir = Path.GetDirectoryName(trimmedPath);
        if (string.IsNullOrEmpty(dir)) return null;
        try
        {
            var rel = Path.GetRelativePath(trimmedRoot, dir).Replace('\\', '/');
            if (rel.StartsWith("..", StringComparison.Ordinal)) return null;
            return rel == "." ? string.Empty : rel;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static StringComparer PathComparer
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
