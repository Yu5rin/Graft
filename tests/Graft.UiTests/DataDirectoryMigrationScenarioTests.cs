using System.Windows.Input;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Infra;
using Graft.Platform;
using Graft.ViewModels;

namespace Graft.UiTests;

/// <summary>
/// 機能3（データ保存先の選択）・機能2（ログの参照手段）を、実際の<see cref="SettingsViewModel"/>を
/// 通して確認するシナリオテスト。<see cref="DataDirectoryMigratorTests"/>（Graft.Tests）が
/// コピー・検証・ポインタ切り替えのロジック単体を検証するのに対し、ここでは
/// SettingsViewModelの確認ダイアログ・IsBusy・IsDataDirectoryMigrationPendingまで含めた
/// 一連の流れを検証する。
/// </summary>
public class DataDirectoryMigrationScenarioTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-data-dir-scenario", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 後始末の失敗は検証結果に影響しない。
        }

        GC.SuppressFinalize(this);
    }

    [AvaloniaFact(DisplayName = "既定はポータブル（exeDirectoryを省略するとAppPaths.BaseDirectoryと同じ扱いになる）")]
    public async Task 既定はポータブル表示になる()
    {
        var exeDir = Path.Combine(_root, "exe");
        var appPaths = new AppPaths(exeDir);
        appPaths.EnsureCoreDirectoriesExist();

        var vm = new SettingsViewModel(
            appPaths, new Graft.Platform.Null.NullDialogService(), new AvaloniaUiServices(), exeDirectory: exeDir);
        await vm.InitializeAsync();

        vm.IsPortableDataDirectory.Should().BeTrue();
        vm.DataDirectoryModeLabel.Should().Contain("ポータブル");
        vm.DataDirectoryPath.Should().Be(exeDir);
    }

    [AvaloniaFact(DisplayName = "「ユーザーフォルダへ移動」を確認すると、データをコピーしポインタファイルを書いてから再起動待ちになる")]
    public async Task ユーザーフォルダへ移動すると再起動待ちになる()
    {
        var exeDir = Path.Combine(_root, "exe2");
        var appPaths = new AppPaths(exeDir);
        appPaths.EnsureCoreDirectoriesExist();
        await File.WriteAllTextAsync(appPaths.SettingsFilePath, "{}");

        var dialogs = new ConfirmingDialogService();
        var vm = new SettingsViewModel(appPaths, dialogs, new AvaloniaUiServices(), exeDirectory: exeDir);
        await vm.InitializeAsync();

        vm.IsDataDirectoryMigrationPending.Should().BeFalse();
        vm.MigrateDataDirectoryCommand.CanExecute(null).Should().BeTrue();

        await ExecuteAsync(vm.MigrateDataDirectoryCommand);

        dialogs.ConfirmCallCount.Should().Be(1, "破壊的操作（コピー）なので必ず確認ダイアログを経る必要がある");
        vm.IsDataDirectoryMigrationPending.Should().BeTrue("移行成功後は再起動を促す表示のため保留状態になる");
        vm.MigrateDataDirectoryCommand.CanExecute(null).Should().BeFalse("再起動待ちの間はもう一度実行できないようにする");
        dialogs.LastMessage.Should().Contain("再起動", "利用者へ再起動が必要なことを案内する必要がある");

        var pointer = DataDirectoryPointer.TryRead(exeDir);
        pointer.Should().NotBeNull();
        File.Exists(Path.Combine(pointer!, "settings.json")).Should().BeTrue("設定ファイルが新しい場所へコピーされている必要がある");
        File.Exists(appPaths.SettingsFilePath).Should().BeTrue(
            "元の場所のデータは移行操作の場では削除しない。削除は次回起動時（RunPendingCleanup）にのみ行う");
    }

    [AvaloniaFact(DisplayName = "確認ダイアログでキャンセルすると何もコピーされずポインタファイルも書かれない")]
    public async Task キャンセルすると何も変更されない()
    {
        var exeDir = Path.Combine(_root, "exe3");
        var appPaths = new AppPaths(exeDir);
        appPaths.EnsureCoreDirectoriesExist();
        await File.WriteAllTextAsync(appPaths.SettingsFilePath, "{}");

        var dialogs = new ConfirmingDialogService { ConfirmResponse = false };
        var vm = new SettingsViewModel(appPaths, dialogs, new AvaloniaUiServices(), exeDirectory: exeDir);
        await vm.InitializeAsync();

        await ExecuteAsync(vm.MigrateDataDirectoryCommand);

        vm.IsDataDirectoryMigrationPending.Should().BeFalse();
        File.Exists(DataDirectoryPointer.PointerFilePath(exeDir)).Should().BeFalse();
    }

    [AvaloniaFact(DisplayName = "「最新のログを表示」はログが無ければ案内ダイアログを出し、あればLogViewerRequestedを発火する")]
    public async Task 最新のログを表示のイベント発火を確認する()
    {
        var exeDir = Path.Combine(_root, "exe4");
        var appPaths = new AppPaths(exeDir);
        appPaths.EnsureCoreDirectoriesExist();

        var dialogs = new ConfirmingDialogService();
        var vm = new SettingsViewModel(appPaths, dialogs, new AvaloniaUiServices(), exeDirectory: exeDir);
        await vm.InitializeAsync();

        // ログが1件も無い状態: 案内ダイアログのみでイベントは発火しない。
        LogViewerRequestEventArgs? received = null;
        vm.LogViewerRequested += (_, e) => received = e;
        await ExecuteAsync(vm.ShowLatestLogCommand);
        received.Should().BeNull();
        dialogs.LastMessage.Should().Contain("ログファイルがまだありません");

        // ログファイルを1件用意すると、末尾を含んだイベントが発火する。
        Directory.CreateDirectory(appPaths.LogsDirectory);
        var logPath = Path.Combine(appPaths.LogsDirectory, "20260101.log");
        await File.WriteAllTextAsync(logPath, "{\"eventType\":\"startup\"}");

        await ExecuteAsync(vm.ShowLatestLogCommand);

        received.Should().NotBeNull();
        received!.FilePath.Should().Be(logPath);
        received.TailText.Should().Contain("startup");
    }

    private static async Task ExecuteAsync(ICommand command)
    {
        command.Execute(null);
        if (command is Graft.ViewModels.AsyncRelayCommand async)
        {
            while (async.IsExecuting)
            {
                await Task.Delay(10);
            }
        }
    }

    /// <summary>確認ダイアログに常に既定の応答を返す簡易実装。表示したメッセージも記録する。</summary>
    private sealed class ConfirmingDialogService : IDialogService
    {
        public bool ConfirmResponse { get; set; } = true;
        public int ConfirmCallCount { get; private set; }
        public string? LastMessage { get; private set; }

        public Task<bool> ConfirmAsync(string title, string message)
        {
            ConfirmCallCount++;
            return Task.FromResult(ConfirmResponse);
        }

        public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel)
            => Task.FromResult<bool?>(true);

        public Task<string?> PromptAsync(string title, string message, string? initial = null)
            => Task.FromResult(initial);

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync(string title, IReadOnlyList<string>? extensions = null) => Task.FromResult<string?>(null);

        public Task<string?> SaveFileAsync(string title, string suggestedFileName, IReadOnlyList<string>? extensions = null)
            => Task.FromResult<string?>(null);

        public Task ShowMessageAsync(string title, string message)
        {
            LastMessage = message;
            return Task.CompletedTask;
        }
    }
}
