using System.IO;
using Graft.Core;
using Graft.Infra;
using Graft.Platform;

namespace Graft.ViewModels;

/// <summary>
/// <see cref="SettingsViewModel"/> の分割ファイル（1ファイル400行上限のため）。
///
/// 機能3（データ保存先の選択。「一般」タブ）: 「ポータブル（実行ファイルと同じフォルダ）」と
/// 「ユーザーフォルダ（%APPDATA%\Graft 等）」を切り替える。実処理は<see cref="DataDirectoryMigrator"/>
/// （純粋なコピー・検証・ポインタ切り替えロジック。単体テスト対象）に委ね、ここではダイアログでの
/// 確認・実行中表示・結果の通知だけを担う。
///
/// 【あえて「実行中の切り替え」をしない理由】
/// 移行後もこのプロセスは古い<see cref="AppPaths"/>（コンストラクタで受け取ったインスタンス）を
/// 使い続ける前提で動いている（SettingsStore・ProjectStore・Logger等、起動時に一度だけ
/// 生成されたインスタンスがあちこちに配られているため、実行中に差し替えるには
/// それらすべてを再生成し直す大掛かりな変更が要る）。加えて、コピー中にファイルが
/// 開かれたまま書き換えられると整合性が壊れる恐れもある。そのため「コピー＋ポインタ切り替え」
/// までをこの場で行い、実際にそのデータ保存先を使い始めるのは次回起動からとする
/// （利用者には「再起動してください」と案内する）。
///
/// 機能2（ログの参照手段。「バージョン情報」タブ）: 「ログフォルダを開く」「最新のログを表示」。
/// ウィンドウの生成自体はコードビハインド（<see cref="Views.AboutView"/>）側が担い、ここでは
/// 「ログフォルダを開く」の実処理のみを持つ（<see cref="Views.AboutView"/>のコメント参照）。
/// </summary>
public sealed partial class SettingsViewModel
{
    // 機能3: exeと同じ階層（datapath.txtの置き場所）。コンストラクタで受け取る
    // （StartupCoordinator.csのコメント参照）。
    private readonly string _exeDirectory;

    private bool _dataDirectoryMigrationPending;

    /// <summary>
    /// 現在のデータ保存先が「ポータブル」（exeと同じ階層）かどうか。falseなら「ユーザーフォルダ」。
    /// <see cref="_appPaths"/>のBaseDirectoryと<see cref="_exeDirectory"/>を実際に比較して求める
    /// （設定値としては持たない。設定に保存先を書くと循環になる問題は<see cref="DataDirectoryPointer"/>
    /// のコメントを参照）。
    /// </summary>
    public bool IsPortableDataDirectory => PathsEqual(_appPaths.BaseDirectory, _exeDirectory);

    /// <summary>現在のデータ保存先（絶対パス）。画面に「どこに保存されているか」を示すために使う。</summary>
    public string DataDirectoryPath => _appPaths.BaseDirectory;

    /// <summary>現在のモードの表示名。</summary>
    public string DataDirectoryModeLabel => IsPortableDataDirectory
        ? "ポータブル（実行ファイルと同じフォルダ）"
        : "ユーザーフォルダ";

    /// <summary>移行ボタンのラベル。現在のモードに応じて「移動」か「戻す」かが変わる。</summary>
    public string DataDirectoryActionLabel => IsPortableDataDirectory
        ? "ユーザーフォルダへ移動"
        : "ポータブルへ戻す";

    /// <summary>
    /// 直前の移行操作が成功し、再起動待ちの状態かどうか。trueの間は
    /// <see cref="MigrateDataDirectoryCommand"/>を実行不可にする（再起動前にもう一度実行して
    /// 「新しい場所」から「さらに新しい場所」へ、のような分かりにくい多重切り替えを避けるため）。
    /// </summary>
    public bool IsDataDirectoryMigrationPending
    {
        get => _dataDirectoryMigrationPending;
        private set
        {
            if (!SetProperty(ref _dataDirectoryMigrationPending, value)) return;
            MigrateDataDirectoryCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>データ保存先の切り替え（「ユーザーフォルダへ移動」／「ポータブルへ戻す」）。</summary>
    public AsyncRelayCommand MigrateDataDirectoryCommand { get; private set; } = null!;

    /// <summary>機能2: logs/フォルダをファイルマネージャで開く。</summary>
    public AsyncRelayCommand OpenLogsFolderCommand { get; private set; } = null!;

    /// <summary>機能2: 最新のログファイルの末尾を表示するウィンドウを開く。</summary>
    public AsyncRelayCommand ShowLatestLogCommand { get; private set; } = null!;

    /// <summary>
    /// 「ユーザーフォルダへ移動」／「ポータブルへ戻す」の実処理。破壊的操作（ファイルコピーを伴う）
    /// なので必ず確認し、実行中はIsBusyを立てる。既存データは常に残す方針（安全側）のため、
    /// 「上書き確認」のような文言は出さない。
    /// </summary>
    private async Task MigrateDataDirectoryAsync()
    {
        var switchingToPortable = !IsPortableDataDirectory;
        var target = switchingToPortable ? _exeDirectory : BuildUserDataDirectory();

        if (PathsEqual(_appPaths.BaseDirectory, target))
        {
            // 既にその場所を使っている（例: ユーザーフォルダの解決結果がたまたまexeと同じ等、
            // 通常は起こらないが念のため）。何もせず案内だけ出す。
            await _dialogService.ShowMessageAsync("データの保存先", "既にその場所を使っています。").ConfigureAwait(true);
            return;
        }

        var confirmTitle = switchingToPortable ? "ポータブルへ戻す" : "ユーザーフォルダへ移動";
        var confirmed = await _dialogService.ConfirmAsync(confirmTitle, BuildMigrationConfirmText(target))
            .ConfigureAwait(true);
        if (!confirmed) return;

        IsBusy = true;
        GraftResult<string>? result = null;
        try
        {
            result = await Task.Run(() => DataDirectoryMigrator.MigrateAndSwitchPointer(
                _exeDirectory, _appPaths.BaseDirectory, target, switchToPortable: switchingToPortable)).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }

        if (!result.IsSuccess)
        {
            await _dialogService.ShowMessageAsync("移行に失敗しました",
                string.Join(Environment.NewLine, result.Errors.Select(i => i.ToDisplayText()))).ConfigureAwait(true);
            return;
        }

        IsDataDirectoryMigrationPending = true;
        await _dialogService.ShowMessageAsync("移行しました",
            $"データを次の場所へコピーしました。{Environment.NewLine}{target}{Environment.NewLine}{Environment.NewLine}" +
            $"元の場所（{_appPaths.BaseDirectory}）のデータはそのまま残しています。" +
            $"{Environment.NewLine}{Environment.NewLine}この変更を使い始めるには、Graftを再起動してください。")
            .ConfigureAwait(true);
    }

    /// <summary>移行前の確認文言。コピーであって移動ではないこと・再起動が必要なことを明記する。</summary>
    private string BuildMigrationConfirmText(string target) =>
        $"データ（設定・プロジェクト定義・バックアップ・ログ）を次の場所へコピーします。" +
        $"{Environment.NewLine}{target}{Environment.NewLine}{Environment.NewLine}" +
        "元の場所のデータは削除せずそのまま残します。" + Environment.NewLine +
        "コピー完了後、この変更を使い始めるにはGraftの再起動が必要です（実行中の切り替えは行いません）。";

    /// <summary>「ユーザーフォルダ」の既定パス（%APPDATA%\Graft 相当）。</summary>
    private static string BuildUserDataDirectory()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Graft");

    private static bool PathsEqual(string a, string b)
    {
        var fullA = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a));
        var fullB = Path.TrimEndingDirectorySeparator(Path.GetFullPath(b));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(fullA, fullB, comparison);
    }

    /// <summary>
    /// 機能2: logs/フォルダを<see cref="IFileManagerLauncher"/>で開く。無ければ先に作る
    /// （空でも「フォルダ自体はある」状態にしてから開いたほうが、利用者が「開けない」と
    /// 誤解しにくいため）。<see cref="IFileManagerLauncher.Reveal"/>はプロセス起動を伴い
    /// 数秒ブロックしうるため、UIスレッドを塞がないようTask.Runへ逃がす。
    /// </summary>
    private Task OpenLogsFolderAsync() => Task.Run(() =>
    {
        try
        {
            Directory.CreateDirectory(_appPaths.LogsDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // フォルダを事前に作れなくても、Reveal側がフォールバック（親フォルダを開く等）を
            // 試みるため、ここでは握りつぶして続行する。
        }

        PlatformServices.Current.FileManager.Reveal(_appPaths.LogsDirectory);
    });

    /// <summary>
    /// 機能2: 最新のログファイルの末尾（<see cref="LogTailReader.DefaultMaxLines"/>行）を
    /// 表示するウィンドウを開く。探索・読み取りは<see cref="LogTailReader"/>（純粋関数・
    /// 単体テスト対象）に委ね、ここではファイルが1つも無い場合の案内と、
    /// ウィンドウの起動（コードビハインド側のイベントへ委譲）のみを担う。
    /// </summary>
    private async Task ShowLatestLogAsync()
    {
        var (path, tail) = await Task.Run(() =>
        {
            var latest = LogTailReader.FindLatestLogFile(_appPaths.LogsDirectory);
            return latest is null
                ? (Path: (string?)null, Tail: (string?)null)
                : (Path: latest, Tail: LogTailReader.ReadTail(latest, LogTailReader.DefaultMaxLines));
        }).ConfigureAwait(true);

        if (path is null)
        {
            await _dialogService.ShowMessageAsync("最新のログ", "ログファイルがまだありません。").ConfigureAwait(true);
            return;
        }

        LogViewerRequested?.Invoke(this, new LogViewerRequestEventArgs(path, tail ?? string.Empty));
    }

    /// <summary>
    /// 機能2: 「最新のログを表示」で末尾の切り出しが完了したときに発火する。ViewModelは
    /// Avaloniaの<c>Window</c>型に依存させない方針のため、実際のウィンドウ表示は
    /// <see cref="Views.AboutView"/>（コードビハインド）がこのイベントを購読して行う。
    /// </summary>
    public event EventHandler<LogViewerRequestEventArgs>? LogViewerRequested;
}

/// <summary>「最新のログを表示」で切り出した内容。<see cref="SettingsViewModel.LogViewerRequested"/>で渡す。</summary>
public sealed class LogViewerRequestEventArgs : EventArgs
{
    public LogViewerRequestEventArgs(string filePath, string tailText)
    {
        FilePath = filePath;
        TailText = tailText;
    }

    /// <summary>表示対象のログファイルの絶対パス。</summary>
    public string FilePath { get; }

    /// <summary>切り出した末尾のテキスト（1行1レコード。整形はしない）。</summary>
    public string TailText { get; }
}
