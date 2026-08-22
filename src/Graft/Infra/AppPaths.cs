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
    /// 基準ディレクトリ。省略時は <see cref="ResolveBaseDirectory"/>（データ保存先の選択機能。
    /// 通常はexeの場所だが、ポインタファイルがあればそちらを使う）で決める。
    /// テストでは一時ディレクトリを渡して差し替える（この場合ポインタファイルは一切参照しない）。
    /// </param>
    public AppPaths(string? baseDirectory = null)
    {
        BaseDirectory = baseDirectory ?? ResolveBaseDirectory();
    }

    /// <summary>
    /// 機能3（データ保存先の選択）: 実際のデータ保存先を決める。
    ///
    /// settings.json自体に保存先を書くと「settings.jsonの場所がsettings.jsonの中身に
    /// 依存する」循環に陥るため、代わりにexeと同じ階層に置く小さなポインタファイル
    /// （<see cref="DataDirectoryPointer"/>、既定名 datapath.txt）だけを読んで決める。
    /// ポインタファイルが無い・空・読み取れない場合は、従来どおりexeと同じ階層
    /// （ポータブル）を使う。
    /// </summary>
    /// <param name="exeDirectory">
    /// exeのあるフォルダ。省略時は <see cref="AppContext.BaseDirectory"/>。
    /// テストからポインタファイルの解決だけを検証できるよう差し替え可能にしてある。
    /// </param>
    public static string ResolveBaseDirectory(string? exeDirectory = null)
    {
        var exeDir = exeDirectory ?? AppContext.BaseDirectory;
        return DataDirectoryPointer.TryRead(exeDir) ?? exeDir;
    }

    /// <summary>
    /// 「ユーザーフォルダ」の既定パス（%APPDATA%\Graft 相当）。設定画面からの明示的な移行
    /// （<see cref="ViewModels.SettingsViewModel"/>のデータ保存先まわり）と、孤立したユーザー
    /// フォルダの復帰確認（<see cref="DataDirectoryRecovery"/>）の両方が同じ定義を参照する
    /// 単一の情報源。以前は<c>SettingsViewModel.DataDirectory.cs</c>にprivate staticで
    /// 個別に定義されていたが、復帰確認機能を追加するにあたり2箇所で食い違わないようここへ
    /// 集約した。
    /// </summary>
    public static string DefaultUserDataDirectory()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Graft");

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

    /// <summary>
    /// 自動更新（機能追加）: 前回の更新確認日時（<see cref="Graft.Core.Update.UpdateCheckState"/>）の
    /// 保存先。settings.json（利用者が編集しうる設定本体）とは別ファイルにしているのは、
    /// 「JSON直接編集タブ」やエクスポート/インポートの対象を増やしたくないため
    /// （settings.jsonはUpdateSettings.CheckOnStartup/CheckUrlのみを持ち、こちらは内部状態）。
    /// 他の内部状態（queue.json・layout.json）と同じくexeと同じ階層に平置きする。
    /// </summary>
    public string UpdateCheckStateFilePath => Path.Combine(BaseDirectory, "update-check.json");

    /// <summary>
    /// 初回起動ガイド（<see cref="Views.OnboardingWindow"/>）の完了マーカーファイルの絶対パス。
    /// 不具合2の修正: 以前は<see cref="Views.OnboardingWindow"/>側でファイル名を直書きしており、
    /// <see cref="DataDirectoryMigrator"/>のコピー対象一覧（<see cref="AppPaths"/>の各プロパティを
    /// そのまま使う設計）に載っていなかった。ここへプロパティとして持たせることで、
    /// 両者が同じファイル名を単一の情報源から参照するようにする。
    /// </summary>
    public string OnboardingMarkerFilePath => Path.Combine(BaseDirectory, "onboarding.done");

    /// <summary>
    /// ウィンドウレイアウト（<see cref="ViewModels.WindowLayoutStore"/>）の絶対パス。
    /// <see cref="OnboardingMarkerFilePath"/>と同じ理由（不具合2）でここへプロパティとして持たせる。
    /// </summary>
    public string WindowLayoutFilePath => Path.Combine(BaseDirectory, "layout.json");

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
