using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Features;
using Graft.Infra;
using Graft.Platform.Null;
using Graft.ViewModels;
using Xunit;

namespace Graft.UiTests;

/// <summary>
/// 仕様書6.5・3.1 適用後フック編集UI（<see cref="HookSettingsViewModel"/>）の単体テスト。
/// フックの追加・編集・保存が <see cref="ProjectStore"/> 経由でprojects.jsonへ正しく
/// 永続化されることを検証する。画面は開かないが、AsyncRelayCommand.RaiseCanExecuteChangedが
/// Avaloniaのコマンド再評価機構（CommandRequery）を経由するため、AvaloniaFactでUIスレッドの
/// コンテキストを用意する必要がある（プロセス内の他テストが登録したButtonの購読が残っているため、
/// 素のFactではUIスレッド外からのDispatcherアクセスとして例外になる）。
/// </summary>
public class HookSettingsViewModelTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-hooksettings", Guid.NewGuid().ToString("N"));

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

    [AvaloniaFact(DisplayName = "フックを追加して保存すると、ProjectStoreから読み直しても永続化されている")]
    public async Task フック追加保存が永続化される()
    {
        var appPaths = new AppPaths(Path.Combine(_root, "app"));
        appPaths.EnsureCoreDirectoriesExist();
        var projectDir = Path.Combine(_root, "project");
        Directory.CreateDirectory(projectDir);

        var projectStore = new ProjectStore(appPaths);
        var registered = await projectStore.RegisterAsync(projectDir, "テスト対象プロジェクト");
        registered.IsSuccess.Should().BeTrue();

        var vm = new HookSettingsViewModel(projectStore, new NullDialogService());
        await vm.InitializeAsync();
        vm.SelectedProject.Should().NotBeNull();
        vm.SelectedProject!.Id.Should().Be(registered.Value.Id);

        await ExecuteAsync(vm.AddCommand);
        vm.Hooks.Should().ContainSingle();
        vm.SelectedHook.Should().NotBeNull();

        vm.SelectedHook!.Name = "ビルド確認";
        vm.SelectedHook.Command = "echo build";
        vm.SelectedHook.OnFailure = HookFailureAction.AutoRollback;

        await ExecuteAsync(vm.SaveCommand);

        // 別インスタンスのProjectStoreで読み直し、保存が実プロセス間でも通用する形（ファイルI/O）で
        // 永続化されていることを確認する。
        var reloadStore = new ProjectStore(appPaths);
        var reloaded = await reloadStore.LoadAsync();
        var project = reloaded.Value.Should().ContainSingle().Subject;

        project.PostApplyHooks.Should().ContainSingle();
        var hook = project.PostApplyHooks[0];
        hook.Name.Should().Be("ビルド確認");
        hook.Command.Should().Be("echo build");
        hook.OnFailure.Should().Be(HookFailureAction.AutoRollback);
    }

    [AvaloniaFact(DisplayName = "フックを削除して確認ダイアログで承諾すると、永続化から取り除かれる")]
    public async Task フック削除が永続化される()
    {
        var appPaths = new AppPaths(Path.Combine(_root, "app2"));
        appPaths.EnsureCoreDirectoriesExist();
        var projectDir = Path.Combine(_root, "project2");
        Directory.CreateDirectory(projectDir);

        var projectStore = new ProjectStore(appPaths);
        var registered = await projectStore.RegisterAsync(projectDir, "削除確認用プロジェクト");
        var withHook = (await projectStore.LoadAsync()).Value.ToList();
        var index = withHook.FindIndex(p => p.Id == registered.Value.Id);
        withHook[index] = withHook[index] with
        {
            PostApplyHooks = new[] { new PostApplyHook { Name = "削除対象", Command = "echo x", OnFailure = HookFailureAction.Warn } },
        };
        await projectStore.SaveAsync(withHook);

        var vm = new HookSettingsViewModel(projectStore, new AutoConfirmDialogService());
        await vm.InitializeAsync();
        vm.Hooks.Should().ContainSingle();

        await ExecuteAsync(vm.DeleteCommand);

        vm.Hooks.Should().BeEmpty();
        var reloaded = await new ProjectStore(appPaths).LoadAsync();
        reloaded.Value.Should().ContainSingle().Subject.PostApplyHooks.Should().BeEmpty();
    }

    /// <summary>非同期コマンドを実行し、完了するまで待つ（ScenarioTestsと同じ手法）。</summary>
    private static async Task ExecuteAsync(System.Windows.Input.ICommand command)
    {
        command.Execute(null);
        if (command is AsyncRelayCommand async)
        {
            while (async.IsExecuting)
            {
                await Task.Delay(10);
            }
        }
    }

    private sealed class AutoConfirmDialogService : Graft.Platform.IDialogService
    {
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);

        public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel)
            => Task.FromResult<bool?>(true);

        public Task<string?> PromptAsync(string title, string message, string? initial = null)
            => Task.FromResult<string?>(initial ?? "テスト");

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
    }
}
