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

    /// <summary>
    /// 削除の取り消し（Ctrl+Z、エクスプローラ）用の退避ディレクトリ（back/trash/）。
    /// OSのごみ箱（<see cref="Graft.Platform.ITrashService"/>）とは別に、Graftが自前で
    /// 退避コピーを保持する場所。back/ 配下に置くのはリビジョンのバックアップと同じ
    /// 「アプリのデータフォルダ配下」の流儀に合わせるため。セッション内のみ保持し、
    /// アプリ終了時に空にする（<see cref="Graft.Features.DeleteUndoStore.Cleanup"/>）。
    /// </summary>
    public string TrashStagingDirectory => Path.Combine(BackupRootDirectory, "trash");

    /// <summary>プロンプトテンプレート定義（仕様書4.8）の保存先。</summary>
    public string TemplatesFilePath => Path.Combine(BaseDirectory, "templates.json");

    /// <summary>パッチキュー（仕様書4.10）の保存先。終了時に保持し次回起動時に復元する。</summary>
    public string QueueFilePath => Path.Combine(BaseDirectory, "queue.json");

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

    /// <summary>
    /// 課題1（バグ）: 書き込み権限の無いフォルダ（例: Windowsの Program Files 配下）に
    /// 置かれても何の警告も出ずに起動してしまい、settings.json / projects.json / back/ / logs/ の
    /// いずれも保存できないまま利用者の変更が次回起動時に静かに消えていた不具合への対応。
    ///
    /// <see cref="BaseDirectory"/> へ実際に書き込めるかを、空の一時ファイルを1つ作成して
    /// 即座に削除するだけの最小限の方法で確認する。back/・logs/ 配下ではなく
    /// <see cref="BaseDirectory"/> 直下で確認するのは、settings.json・projects.json は
    /// そこへ直接書くため（back/・logs/ が存在しなくても、そちらが書けなければ
    /// どのみち保存できない）。ディレクトリ作成やファイルI/Oを何段も行わないため、
    /// 起動を遅延させない（仕様書18章「起動から操作可能まで1秒以内」）。
    ///
    /// 例外は握りつぶし、判定結果のみをboolで返す。呼び出し側（StartupCoordinator）が
    /// falseの場合に日本語の警告を表示する責務を持つ。
    /// </summary>
    public bool CanWriteToBaseDirectory()
    {
        var probePath = Path.Combine(BaseDirectory, $".graft_write_check_{Guid.NewGuid():N}.tmp");
        try
        {
            using (File.Create(probePath))
            {
                // 存在確認のみが目的で、中身は書かない。
            }

            File.Delete(probePath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
