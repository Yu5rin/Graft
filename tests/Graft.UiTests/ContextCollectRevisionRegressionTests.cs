using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;
using Graft.Platform.Null;
using Graft.ViewModels;
using Xunit;

namespace Graft.UiTests;

/// <summary>
/// 実機ログで確認した「履歴番号が巻き戻り、同じ番号のバックアップが2つできる」不具合の回帰テスト。
///
/// 【再現した経緯】EPSEnhanceでr35〜r42を適用したあと、コンテキスト収集の画面でファイルの
/// チェック状態を変えると、画面を作った時点で受け取った古いプロジェクトのスナップショット
/// （古いNextRevision・LastAppliedAt）でprojects.jsonを丸ごと上書きしていた。次の適用は
/// その巻き戻った番号（r36）を使い、r36〜r42が2つずつできた。
/// </summary>
public class ContextCollectRevisionRegressionTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-ccrev", Guid.NewGuid().ToString("N"));

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

    [AvaloniaFact(DisplayName = "ファイルのチェック状態を保存しても、画面を開いたあとに進んだNextRevisionとLastAppliedAtは巻き戻らない")]
    public async Task チェック状態の保存でNextRevisionが巻き戻らない()
    {
        var (vm, store, project) = await BuildAsync("states", ws =>
        {
            ws.WriteText("a.py", "x");
            ws.WriteText("b.py", "x");
        });

        // 画面を開いたあとに、別の操作（適用）が番号を進める。画面が持つ project は古いまま。
        for (var i = 0; i < 7; i++) await store.ConsumeNextRevisionAsync(project.Id);
        var appliedAt = new DateTimeOffset(2026, 10, 9, 15, 3, 0, TimeSpan.FromHours(9));
        await store.MarkAppliedAsync(project.Id, appliedAt);
        vm.Project.NextRevision.Should().Be(1, "前提: 画面が持つスナップショットは古いまま");

        // チェック状態を変える（デバウンス後にprojects.jsonへ保存される）。
        vm.CycleStateCommand.Execute(vm.Files.Single(f => f.RelativePath == "a.py"));
        await WaitUntilAsync(async () => (await LoadAsync(store, project.Id)).Overrides.ContextFileStates.Count > 0);

        var after = await LoadAsync(store, project.Id);
        after.Overrides.ContextFileStates.Should().ContainKey("a.py", "チェック状態自体は保存されるはず");
        after.NextRevision.Should().Be(8, "状態の保存で適用済みの番号を巻き戻してはいけない（修正前は1に戻っていた）");
        after.LastAppliedAt.Should().Be(appliedAt, "最終適用日時も巻き戻してはいけない");
        vm.Project.NextRevision.Should().Be(8, "保存の結果で画面側のスナップショットも最新に更新されるはず");
    }

    [AvaloniaFact(DisplayName = "除外パターンの追加・削除でも、NextRevisionは巻き戻らない")]
    public async Task 除外パターンの保存でNextRevisionが巻き戻らない()
    {
        var (vm, store, project) = await BuildAsync("excludes", ws => ws.WriteText("a.py", "x"));

        for (var i = 0; i < 4; i++) await store.ConsumeNextRevisionAsync(project.Id);

        vm.NewExcludePattern = "*.log";
        await ExecuteAsync(vm.AddExcludeCommand);

        var afterAdd = await LoadAsync(store, project.Id);
        afterAdd.Overrides.Excludes.Should().Contain("*.log");
        afterAdd.NextRevision.Should().Be(5, "除外パターンの保存で番号を巻き戻してはいけない");

        await store.ConsumeNextRevisionAsync(project.Id);
        vm.RemoveExcludeCommand.Execute("*.log");
        await WaitUntilAsync(async () => (await LoadAsync(store, project.Id)).Overrides.Excludes.Count == 0);

        var afterRemove = await LoadAsync(store, project.Id);
        afterRemove.Overrides.Excludes.Should().BeEmpty();
        afterRemove.NextRevision.Should().Be(6);
    }

    [AvaloniaFact(DisplayName = "別画面で変えた別のフィールド（既定テンプレート・表示名）も、チェック状態の保存で消えない")]
    public async Task 別フィールドの更新を巻き戻さない()
    {
        var (vm, store, project) = await BuildAsync("otherfields", ws => ws.WriteText("a.py", "x"));

        await store.UpdateAsync(project.Id, p => p with { PromptTemplateId = "builtin-continuation", Name = "別名" });

        vm.CycleStateCommand.Execute(vm.Files.Single(f => f.RelativePath == "a.py"));
        await WaitUntilAsync(async () => (await LoadAsync(store, project.Id)).Overrides.ContextFileStates.Count > 0);

        var after = await LoadAsync(store, project.Id);
        after.PromptTemplateId.Should().Be("builtin-continuation", "画面を開いたあとに変わった既定テンプレートを古い値で上書きしてはいけない");
        after.Name.Should().Be("別名");
    }

    [AvaloniaFact(DisplayName = "適用後フックの保存でも、画面を開いたあとに進んだNextRevisionは巻き戻らない")]
    public async Task フックの保存でNextRevisionが巻き戻らない()
    {
        var appPaths = new AppPaths(Path.Combine(_root, "hooks", "app"));
        appPaths.EnsureCoreDirectoriesExist();
        var projectDir = Path.Combine(_root, "hooks", "project");
        Directory.CreateDirectory(projectDir);
        var store = new ProjectStore(appPaths);
        var project = (await store.RegisterAsync(projectDir, "hooks")).Value;

        var vm = new HookSettingsViewModel(store, new NullDialogService());
        await vm.InitializeAsync();
        await ExecuteAsync(vm.AddCommand);
        vm.SelectedHook!.Name = "ビルド";
        vm.SelectedHook.Command = "echo build";

        // フック設定画面を開いたあとに適用が進む（画面が持つProjectのNextRevisionは古いまま）。
        for (var i = 0; i < 5; i++) await store.ConsumeNextRevisionAsync(project.Id);

        await ExecuteAsync(vm.SaveCommand);

        var after = await LoadAsync(store, project.Id);
        after.PostApplyHooks.Should().ContainSingle();
        after.NextRevision.Should().Be(6, "フックの保存でリスト丸ごとを古い値で上書きしてはいけない");
    }

    private static async Task<Project> LoadAsync(ProjectStore store, string projectId)
        => (await store.LoadAsync()).Value.Single(p => p.Id == projectId);

    private static async Task ExecuteAsync(System.Windows.Input.ICommand command)
    {
        command.Execute(null);
        if (command is AsyncRelayCommand async)
        {
            while (async.IsExecuting) await Task.Delay(10);
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 500; i++)
        {
            if (await condition().ConfigureAwait(true)) return;
            await Task.Delay(10);
        }
    }

    private async Task<(ContextCollectViewModel Vm, ProjectStore Store, Project Project)> BuildAsync(
        string caseName, Action<Workspace> setup)
    {
        var appPaths = new AppPaths(Path.Combine(_root, caseName, "app"));
        appPaths.EnsureCoreDirectoriesExist();
        var projectDir = Path.Combine(_root, caseName, "project");
        Directory.CreateDirectory(projectDir);
        setup(new Workspace(projectDir));

        var store = new ProjectStore(appPaths);
        var registered = (await store.RegisterAsync(projectDir, caseName)).Value;

        var vm = new ContextCollectViewModel(appPaths, store, registered, new Settings(), new AvaloniaUiServices(), new NullDialogService());
        await vm.InitializeAsync();
        return (vm, store, registered);
    }

    private sealed class Workspace
    {
        private readonly string _root;
        public Workspace(string root) => _root = root;

        public void WriteText(string relativePath, string content)
        {
            var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
    }
}
