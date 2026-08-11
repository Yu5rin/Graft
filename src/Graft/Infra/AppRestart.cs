using System.Diagnostics;

namespace Graft.Infra;

/// <summary>
/// 不具合3（設定画面「データ保存先の移行」完了ダイアログの「再起動」ボタン）: 自分自身を
/// 再起動するための実行ファイルパスの解決と<see cref="ProcessStartInfo"/>の組み立てのみを担う。
///
/// 単一ファイル発行（<c>PublishSingleFile</c>）では<see cref="System.Reflection.Assembly.Location"/>が
/// 空文字列になるため使えず、実行中のプロセスイメージのパスを確実に得るには
/// <see cref="Environment.ProcessPath"/>を使う必要がある
/// （<see cref="Views.AboutView"/>のビルド日時取得と同じ注意点。あちらも同じ理由で
/// <c>Assembly.Location</c>を避けている）。
///
/// 実際に<see cref="Process.Start(ProcessStartInfo)"/>を呼ぶ・アプリを終了させる、といった
/// 副作用そのものはこのクラスの外（<c>Graft.App</c>）で行う。ここはパス解決という純粋な部分だけを
/// 切り出し、実プロセスを起動せずに単体テストできるようにするため。
/// </summary>
public static class AppRestart
{
    /// <summary>
    /// 不具合2（「再起動」ボタンで終了はするが再起動しない）: 自己再起動で起動した新プロセスへ
    /// <see cref="BuildStartInfo"/>が付与する起動引数。新プロセス側
    /// （<see cref="Views.StartupCoordinator.TryAcquireSingleInstanceAsync"/>）は
    /// <see cref="IsRestartLaunch"/>でこの引数の有無を確認し、多重起動防止Mutexの取得に失敗しても
    /// 即座に諦めず短時間リトライする（旧プロセスのMutex解放がOSレベルで間に合っていない場合の
    /// 保険。原因候補A「多重起動防止に新プロセスが弾かれている」への対処）。利用者が手動で
    /// 2つ目のGraftを起動した通常の多重起動検知ではこの引数が付かないため、リトライは働かず
    /// これまでどおり即座に判定される。
    /// </summary>
    public const string RestartLaunchArgument = "--graft-restarted";

    /// <summary>
    /// 起動引数（<see cref="Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime.Args"/>
    /// 等）に<see cref="RestartLaunchArgument"/>が含まれているかどうか。
    /// </summary>
    public static bool IsRestartLaunch(IReadOnlyList<string>? args)
        => args is not null && args.Contains(RestartLaunchArgument, StringComparer.Ordinal);

    /// <summary>
    /// 自動的に再起動できる状態かどうかを事前に確認する。実際のシャットダウン処理を始める前に
    /// 呼び、falseなら「手動で再起動してください」と案内したうえでシャットダウン自体を
    /// 開始しない（後始末を始めてから再起動できないと分かるより、その前に諦められる方が
    /// 利用者にとって被害が小さい）。
    /// </summary>
    /// <param name="processPath">
    /// テスト用に差し替え可能な実行ファイルパス。省略時は<see cref="Environment.ProcessPath"/>。
    /// </param>
    public static bool CanRestart(string? processPath = null)
        => TryResolveExecutablePath(processPath) is not null;

    /// <summary>
    /// 新プロセスを起動するための<see cref="ProcessStartInfo"/>を組み立てる。実行ファイルパスを
    /// 解決できない場合はnullを返す（呼び出し側は<see cref="Process.Start(ProcessStartInfo)"/>を
    /// 呼んではならない）。
    ///
    /// 作業ディレクトリを実行ファイルと同じフォルダに固定するのは、ポータブル運用
    /// （datapath.txt・settings.json等をexeと同じ階層で探す既定動作、<see cref="AppPaths"/>参照）を
    /// 再起動後も壊さないため。<c>UseShellExecute = false</c>で直接プロセスイメージを起動する
    /// （シェル経由の関連付け解決は不要かつコマンドプロンプトのウィンドウ等が余計に出うるため）。
    /// 不具合2: 新プロセスが自己再起動由来であることを伝えるため、<see cref="RestartLaunchArgument"/>を
    /// 起動引数へ必ず付与する（<see cref="BuildStartInfo"/>は自己再起動専用であり、他の用途で
    /// 呼ばれることは無い）。
    /// </summary>
    /// <param name="processPath">テスト用に差し替え可能な実行ファイルパス。省略時は<see cref="Environment.ProcessPath"/>。</param>
    public static ProcessStartInfo? BuildStartInfo(string? processPath = null)
    {
        var path = TryResolveExecutablePath(processPath);
        if (path is null) return null;

        var workingDirectory = Path.GetDirectoryName(path);
        var startInfo = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            WorkingDirectory = string.IsNullOrEmpty(workingDirectory) ? Environment.CurrentDirectory : workingDirectory,
        };
        startInfo.ArgumentList.Add(RestartLaunchArgument);
        return startInfo;
    }

    private static string? TryResolveExecutablePath(string? processPath)
    {
        var path = processPath ?? Environment.ProcessPath;
        return string.IsNullOrEmpty(path) || !File.Exists(path) ? null : path;
    }
}
