using System.IO;
using Graft.Core;
using Graft.Infra;
using Graft.Platform;

namespace Graft.ViewModels;

/// <summary>
/// <see cref="SettingsViewModel"/> の分割ファイル（1ファイル400行上限のため）。
///
/// 機能3（データ保存先の選択。「一般」タブ）: 「ポータブル（実行ファイルと同じフォルダ）」と
/// 「ユーザーフォルダ（%APPDATA%\Graft 等）」を切り替える。利用者からは「移動」として見える
/// 操作だが、実処理は<see cref="DataDirectoryMigrator"/>（純粋なコピー・検証・ポインタ切り替え
/// ロジック。単体テスト対象）に委ね、ここではダイアログでの確認・実行中表示・結果の通知だけを
/// 担う。元の場所の削除は次回起動時（<see cref="DataDirectoryMigrator.RunPendingCleanup"/>）に
/// 別途行われる（下記【あえて「実行中の切り替え」をしない理由】、および
/// <see cref="DataDirectoryMigrator"/>クラスドキュメントの【なぜ即時削除ではなく次回起動時なのか】
/// 参照）。
///
/// 【あえて「実行中の切り替え」をしない理由】
/// 移行後もこのプロセスは古い<see cref="AppPaths"/>（コンストラクタで受け取ったインスタンス）を
/// 使い続ける前提で動いている（SettingsStore・ProjectStore・Logger等、起動時に一度だけ
/// 生成されたインスタンスがあちこちに配られているため、実行中に差し替えるには
/// それらすべてを再生成し直す大掛かりな変更が要る）。加えて、コピー中にファイルが
/// 開かれたまま書き換えられると整合性が壊れる恐れもある。そのため「コピー＋ポインタ切り替え」
/// までをこの場で行い、実際にそのデータ保存先を使い始めるのは次回起動からとする
/// （利用者には「再起動してください」と案内する）。元の場所の削除も同じ理由で今すぐは行わない
/// （このプロセスがまだ元の場所を使って動作中のため。削除する前に一旦停止するには
/// アプリごと再起動する必要があり、その再起動のタイミングで初めて安全に削除できる）。
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
    /// 「ユーザーフォルダへ移動」／「ポータブルへ戻す」の実処理。実態は「移動」で、元の場所の
    /// データは次回起動時に削除される破壊的操作のため必ず確認し、実行中はIsBusyを立てる。
    /// </summary>
    private async Task MigrateDataDirectoryAsync()
    {
        var switchingToPortable = !IsPortableDataDirectory;
        var target = switchingToPortable ? _exeDirectory : AppPaths.DefaultUserDataDirectory();

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

        // 不具合3: 「OK」だけだった通知ボタンを「再起動」に変え、押されたら実際にアプリを
        // 再起動する。実際のプロセス起動・多重起動防止Mutexとの競合回避はViewModelの責務外
        // （ViewModelをAvaloniaのApplication型に依存させない方針）のため、押されたことだけを
        // RestartRequestedイベントで通知し、実処理はView側（SettingsWindow.axaml.cs経由でApp）へ
        // 委譲する（LogViewerRequestedと同じ役割分担）。
        var restartConfirmed = await _dialogService.ShowActionMessageAsync("移行しました",
            $"データを次の場所へコピーしました。{Environment.NewLine}{target}{Environment.NewLine}{Environment.NewLine}" +
            $"元の場所（{_appPaths.BaseDirectory}）のデータは、次回起動時に削除されます。" +
            $"再起動するまでの間は引き続き元の場所が使われるため、それまでに行った変更も" +
            $"次回起動時にきちんと引き継がれます。" +
            $"{Environment.NewLine}{Environment.NewLine}この変更を使い始めるには、Graftの再起動が必要です。",
            "再起動")
            .ConfigureAwait(true);
        if (!restartConfirmed) return;

        // 後始末を始めてから「実は再起動できない」と分かるより、始める前に諦められる方が
        // 実害が小さい（RestartSequencer.RunAsyncのコメント参照）ため、ここで事前に確認する。
        if (!AppRestart.CanRestart())
        {
            await _dialogService.ShowMessageAsync("再起動できません",
                "実行ファイルの場所を特定できなかったため、自動的に再起動できませんでした。" +
                "手動でGraftを再起動してください。")
                .ConfigureAwait(true);
            return;
        }

        RestartRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 不具合3: 移行完了ダイアログの「再起動」ボタンが押され、かつ再起動が可能と確認できたときに
    /// 発火する。ViewModelはAvaloniaのApplication/Window型に依存させない方針のため、実際の
    /// 再起動処理（後始末→新プロセス起動→旧プロセス終了。多重起動防止Mutexとの競合回避を含む、
    /// <see cref="Core.RestartSequencer"/>参照）はView側（<see cref="Views.SettingsWindow"/>経由で
    /// <see cref="Graft.App"/>）が担う。
    /// </summary>
    public event EventHandler? RestartRequested;

    /// <summary>
    /// 移行前の確認文言。実態は「移動」であり元の場所のデータは削除されること・
    /// 削除は次回起動時に行われること・再起動が必要なことを明記する（破壊的操作のため、
    /// 何がどこへ移り元がどうなるかを読んで分かるようにする）。
    /// </summary>
    private string BuildMigrationConfirmText(string target) =>
        $"データ（設定・プロジェクト定義・バックアップ・ログ）を次の場所へ移動します。" +
        $"{Environment.NewLine}{target}{Environment.NewLine}{Environment.NewLine}" +
        $"元の場所（{_appPaths.BaseDirectory}）のデータは、Graftの再起動後に削除されます。" +
        Environment.NewLine +
        "再起動するまでの間は引き続き元の場所が使われるため、それまでに行った変更も" +
        "次回起動時に新しい場所へ引き継がれたうえで、元の場所は削除されます。" +
        Environment.NewLine +
        "この変更を使い始めるにはGraftの再起動が必要です（実行中の切り替えは行いません）。";

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
