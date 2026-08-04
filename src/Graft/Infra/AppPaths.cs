using System.IO;

namespace Graft.Infra;

/// <summary>
/// 2.2章のフォルダ構成に基づく各種パスを提供する。
/// exe と同じ階層（<see cref="BaseDirectory"/>）を基準とし、テストから差し替えられる
/// よう基準ディレクトリをコンストラクタ引数で受け取れるようにしている。
/// </summary>
public sealed class AppPaths
{
    /// <summary>settings.json 等の実体が置かれる基準ディレクトリ。</summary>
    public string BaseDirectory { get; }

    /// <param name="baseDirectory">
    /// 基準ディレクトリ。省略時は <see cref="AppContext.BaseDirectory"/>（exe の場所）を使う。
    /// テストでは一時ディレクトリを渡して差し替える。
    /// </param>
    public AppPaths(string? baseDirectory = null)
    {
        BaseDirectory = baseDirectory ?? AppContext.BaseDirectory;
    }

    /// <summary>settings.json の絶対パス。</summary>
    public string SettingsFilePath => Path.Combine(BaseDirectory, "settings.json");

    /// <summary>projects.json の絶対パス。</summary>
    public string ProjectsFilePath => Path.Combine(BaseDirectory, "projects.json");

    /// <summary>バックアップの起点ディレクトリ（back/）。</summary>
    public string BackupRootDirectory => Path.Combine(BaseDirectory, "back");

    /// <summary>ログの起点ディレクトリ（logs/）。</summary>
    public string LogsDirectory => Path.Combine(BaseDirectory, "logs");

    /// <summary>指定プロジェクトのバックアップディレクトリ（back/&lt;プロジェクトID&gt;/）。</summary>
    public string GetProjectBackupDirectory(string projectId)
        => Path.Combine(BackupRootDirectory, projectId);

    /// <summary>
    /// リビジョンのフォルダ名を組み立てる（例: r24_20260804_143052）。
    /// </summary>
    public static string BuildRevisionFolderName(int revision, DateTimeOffset appliedAt)
        => $"r{revision}_{appliedAt:yyyyMMdd_HHmmss}";

    /// <summary>指定リビジョンのバックアップディレクトリ（リビジョン番号と適用日時から組み立てる）。</summary>
    public string GetRevisionDirectory(string projectId, int revision, DateTimeOffset appliedAt)
        => Path.Combine(GetProjectBackupDirectory(projectId), BuildRevisionFolderName(revision, appliedAt));

    /// <summary>フォルダ名を直接指定してリビジョンのバックアップディレクトリを得る。</summary>
    public string GetRevisionDirectory(string projectId, string revisionFolderName)
        => Path.Combine(GetProjectBackupDirectory(projectId), revisionFolderName);

    /// <summary>指定リビジョンの manifest.json の絶対パス。</summary>
    public string GetManifestFilePath(string projectId, string revisionFolderName)
        => Path.Combine(GetRevisionDirectory(projectId, revisionFolderName), "manifest.json");

    /// <summary>指定日のログファイルの絶対パス（logs/yyyyMMdd.log）。</summary>
    public string GetLogFilePath(DateOnly date)
        => Path.Combine(LogsDirectory, $"{date:yyyyMMdd}.log");

    /// <summary>back/ と logs/ ディレクトリが存在することを保証する。</summary>
    public void EnsureCoreDirectoriesExist()
    {
        Directory.CreateDirectory(BackupRootDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }
}
