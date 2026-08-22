using System.IO;
using Graft.Core;

namespace Graft.Infra;

/// <summary>
/// v1.0.7 実機不具合対応: 起動時・プロジェクト切替時に「原因究明に要る環境の要約」を
/// 1行のログ（eventType="environment"）へ記録する。
/// <para>
/// 【背景】 半月わからなかった不具合（ネットワーク上のプロジェクトで取り込みが全滅する）の
/// 原因は、最終的にはdry-run-file-probeの1行で確定できたが、それでも「壊れたパスがどこで
/// 生まれたのか」までは追い切れなかった。理由は単純で、それまでの起動ログに
/// プロジェクトルートの絶対パスが一切記録されていなかったため。今回はここを埋める。
/// </para>
/// <para>
/// 【記録する内容】（変更履歴1.0.7・依頼書参照）
///   - プロジェクトルートの絶対パス。<see cref="PathGuard"/>が実際に使う正規化後の値
///     （<see cref="PathGuard.NormalizeRoot"/>）をそのまま使う。未選択なら「未選択」と記録する
///     （起動直後はプロジェクトが選ばれていないことがあるため、例外にはしない）。
///   - そのルートの種別（ローカル／UNC共有／マップ済みネットワークドライブ／
///     クラウド同期フォルダ。<see cref="LongPath.ClassifyLocation"/>を流用する）。
///   - データ保存先（ポータブル＝exeと同じフォルダ、またはユーザーフォルダ）とその絶対パス。
///   - 取り込み結果を左右する設定の値（適用モード・概要必須・マッチング・安全機構）。
/// </para>
/// <para>
/// 【呼び出し箇所】 起動時（<see cref="Views.StartupCoordinator.StartAsync"/>、設定読み込み直後・
/// プロジェクト自動選択より前なのでプロジェクトは「未選択」として記録される）と、
/// プロジェクト切替時（<see cref="ViewModels.ShellViewModel.OnProjectSelected"/>、起動直後の
/// 自動選択を含む）の両方から呼ぶ。ファイルの中身やAIの応答内容は一切含めない
/// （既存ログ方針どおり）。
/// </para>
/// </summary>
public static class EnvironmentSummaryLogger
{
    /// <summary>このクラスが記録するログのeventType。</summary>
    public const string EventType = "environment";

    /// <summary>
    /// 環境の要約を1行記録する。
    /// </summary>
    /// <param name="logger">記録先。nullなら何もしない（Logger生成前の異常系向け）。</param>
    /// <param name="appPaths">データ保存先の解決に使う。</param>
    /// <param name="exeDirectory">
    /// 実行ファイルのあるフォルダ。<paramref name="appPaths"/>のBaseDirectoryと比較し、
    /// ポータブル（同じ）かユーザーフォルダ（異なる）かを判定する。
    /// </param>
    /// <param name="settings">取り込み結果を左右する設定の読み取り元。</param>
    /// <param name="projectRoot">
    /// 現在のプロジェクトルート（Project.Rootそのまま。未正規化でよい）。未選択ならnullまたは空。
    /// </param>
    public static void Log(Logger? logger, AppPaths appPaths, string exeDirectory, Settings settings, string? projectRoot)
    {
        if (logger is null) return;

        ArgumentNullException.ThrowIfNull(appPaths);
        ArgumentNullException.ThrowIfNull(exeDirectory);
        ArgumentNullException.ThrowIfNull(settings);

        var message =
            $"プロジェクト: {DescribeProjectRoot(projectRoot)} / " +
            $"データ保存先: {DescribeDataDirectory(appPaths, exeDirectory)} / " +
            $"設定: {DescribeSettings(settings)}";

        logger.Info(EventType, message, targetPath: TryNormalize(projectRoot));
    }

    private static string? TryNormalize(string? projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot)) return null;
        try
        {
            return PathGuard.NormalizeRoot(projectRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return null;
        }
    }

    private static string DescribeProjectRoot(string? projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            return "未選択";
        }

        string normalized;
        try
        {
            normalized = PathGuard.NormalizeRoot(projectRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            // 正規化そのものに失敗するような壊れたパスも、例外にせず原因調査の手がかりとして
            // そのまま（未正規化で）記録する。
            return $"{projectRoot}（正規化に失敗: {ex.GetType().Name}: {ex.Message}）";
        }

        return $"{normalized}（{DescribeKind(LongPath.ClassifyLocation(normalized))}）";
    }

    private static string DescribeKind(LongPath.PathLocationKind kind) => kind switch
    {
        LongPath.PathLocationKind.UncShare => "UNC共有",
        LongPath.PathLocationKind.NetworkDrive => "ネットワークドライブ",
        LongPath.PathLocationKind.CloudSyncFolder => "クラウド同期フォルダ",
        _ => "ローカル",
    };

    private static string DescribeDataDirectory(AppPaths appPaths, string exeDirectory)
    {
        var isPortable = PathsEqual(appPaths.BaseDirectory, exeDirectory);
        var mode = isPortable ? "ポータブル" : "ユーザーフォルダ";
        return $"{mode}（{appPaths.BaseDirectory}）";
    }

    /// <summary>
    /// 設定画面のデータ保存先切り替え（SettingsViewModel.DataDirectory.cs の私有メソッド
    /// <c>PathsEqual</c>）と同じ判定基準（フルパス化・末尾区切り除去のうえ、Windowsのみ
    /// 大文字小文字を無視）。
    /// </summary>
    private static bool PathsEqual(string a, string b)
    {
        try
        {
            var fullA = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a));
            var fullB = Path.TrimEndingDirectorySeparator(Path.GetFullPath(b));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return string.Equals(fullA, fullB, comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// 取り込み結果を左右する設定（依頼書のとおり、少なくとも適用モードは含める）。
    /// パッチ適用の可否・マッチングの挙動・安全機構の上限に関わる値をまとめる。
    /// </summary>
    private static string DescribeSettings(Settings settings)
    {
        var matching = settings.Matching;
        var safety = settings.Safety;
        return $"applyMode={settings.ApplyMode}, requireSummary={settings.RequireSummary}, " +
               $"similarityThreshold={matching.SimilarityThreshold}, allowSimilarityMatch={matching.AllowSimilarityMatch}, " +
               $"rangeWarningLines={matching.RangeWarningLines}, " +
               $"maxFileSizeMB={safety.MaxFileSizeMB}, maxFilesPerRevision={safety.MaxFilesPerRevision}";
    }
}
