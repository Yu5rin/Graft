using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Core;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;
using Graft.Platform.Null;
using Graft.ViewModels;
using Xunit;

namespace Graft.UiTests;

/// <summary>
/// コンテキスト収集画面（<see cref="ContextCollectViewModel"/>）のチェック状態まわりの単体テスト。
/// 追加要件のうち、画面を開かずに<see cref="ContextCollectViewModel"/>を直接操作して検証できる
/// 4点（中間状態からのトグル・永続化の往復・失効パスの掃除・ロックファイルの初期オフと記録）を
/// ここへ集約する。<see cref="IUiServices.CreateTimer"/>が<see cref="Avalonia.Threading.DispatcherTimer"/>
/// を使うため、UIスレッドのディスパッチャーを用意できるAvaloniaFactで実行する
/// （HookSettingsViewModelTestsと同じ理由）。
/// </summary>
public class ContextCollectViewModelTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-contextcollectvm", Guid.NewGuid().ToString("N"));

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

    [AvaloniaFact(DisplayName = "配下の状態が混在するフォルダ（中間状態）をトグルすると、配下すべてが「内容も出す」に揃う")]
    public async Task 中間状態のフォルダをトグルすると全チェックになる()
    {
        var (vm, _, _) = await BuildAsync("mixed", ws =>
        {
            ws.WriteText("lib/a.py", "x");
            ws.WriteText("lib/b.py", "x");
        });

        var a = FindByPath(vm, "lib/a.py");
        var lib = FindByPath(vm, "lib");

        // a.pyだけ「構成だけ」に切り替え、bはFullのまま → libは中間状態(null)になるはず。
        vm.CycleStateCommand.Execute(a);
        lib.State.Should().BeNull("配下の状態が混在しているので中間状態のはず");
        a.State.Should().Be(ContextFileState.StructureOnly);

        // 中間状態のフォルダをクリックすると、標準の3状態巡回ではなく必ず「内容も出す」へ揃う。
        vm.CycleStateCommand.Execute(lib);

        lib.State.Should().Be(ContextFileState.Full, "中間状態からのトグルは常に全チェックになるはず");
        a.State.Should().Be(ContextFileState.Full);
        FindByPath(vm, "lib/b.py").State.Should().Be(ContextFileState.Full);
    }

    [AvaloniaFact(DisplayName = "チェック状態はプロジェクトごとに保存され、次回開いたときに復元される")]
    public async Task チェック状態が保存され復元される()
    {
        var (vm, appPaths, project) = await BuildAsync("persist", ws =>
        {
            ws.WriteText("lib/helper.py", "x");
            ws.WriteText("secret.env", "x");
            ws.WriteText("main.py", "x");
        });

        var helper = FindByPath(vm, "lib/helper.py");
        var secret = FindByPath(vm, "secret.env");

        vm.CycleStateCommand.Execute(helper); // Full → StructureOnly
        vm.CycleStateCommand.Execute(secret); // Full → StructureOnly
        vm.CycleStateCommand.Execute(secret); // StructureOnly → Hidden

        await WaitUntilAsync(async () =>
        {
            var reloaded = await new ProjectStore(appPaths).LoadAsync();
            var p = reloaded.Value.Single();
            return p.Overrides.ContextFileStates.ContainsKey("lib/helper.py")
                   && p.Overrides.ContextFileStates.ContainsKey("secret.env");
        });

        var afterSave = (await new ProjectStore(appPaths).LoadAsync()).Value.Single();
        afterSave.Overrides.ContextFileStates["lib/helper.py"].Should().Be(ContextFileState.StructureOnly.ToString());
        afterSave.Overrides.ContextFileStates["secret.env"].Should().Be(ContextFileState.Hidden.ToString());

        // 新しいProjectStore・新しいContextCollectViewModelインスタンスで開き直す（=ウィンドウの再オープンを模す）。
        var reopenedStore = new ProjectStore(appPaths);
        var reopenedProject = (await reopenedStore.LoadAsync()).Value.Single();
        var vm2 = new ContextCollectViewModel(appPaths, reopenedStore, reopenedProject, new Settings(), new AvaloniaUiServices(), new NullDialogService());
        await vm2.InitializeAsync();

        FindByPath(vm2, "lib/helper.py").State.Should().Be(ContextFileState.StructureOnly, "外したチェックが復元されるはず");
        FindByPath(vm2, "secret.env").State.Should().Be(ContextFileState.Hidden, "「出さない」も復元されるはず");
        FindByPath(vm2, "main.py").State.Should().Be(ContextFileState.Full, "触っていないファイルは既定のままのはず");
        vm2.Dispose();
    }

    [AvaloniaFact(DisplayName = "記録済みのパスが実際にはもう存在しない場合は無視され、次回保存時に集合からも掃除される")]
    public async Task 失効したパスは無視され掃除される()
    {
        var appPaths = new AppPaths(Path.Combine(_root, "stale", "app"));
        appPaths.EnsureCoreDirectoriesExist();
        var projectDir = Path.Combine(_root, "stale", "project");
        Directory.CreateDirectory(projectDir);
        await File.WriteAllTextAsync(Path.Combine(projectDir, "main.py"), "x");

        var store = new ProjectStore(appPaths);
        var registered = (await store.RegisterAsync(projectDir, "失効テスト")).Value;
        var projects = (await store.LoadAsync()).Value.ToList();
        var index = projects.FindIndex(p => p.Id == registered.Id);
        projects[index] = projects[index] with
        {
            Overrides = projects[index].Overrides with
            {
                ContextFileStates = new Dictionary<string, string>
                {
                    ["main.py"] = ContextFileState.StructureOnly.ToString(),
                    ["deleted-long-ago.py"] = ContextFileState.Hidden.ToString(), // もう存在しないファイル
                },
            },
        };
        await store.SaveAsync(projects);

        var project = (await store.LoadAsync()).Value.Single();
        var vm = new ContextCollectViewModel(appPaths, store, project, new Settings(), new AvaloniaUiServices(), new NullDialogService());
        await vm.InitializeAsync();

        FindByPath(vm, "main.py").State.Should().Be(ContextFileState.StructureOnly, "現存するパスの記録は復元されるはず");

        await WaitUntilAsync(async () =>
        {
            var reloaded = await new ProjectStore(appPaths).LoadAsync();
            return !reloaded.Value.Single().Overrides.ContextFileStates.ContainsKey("deleted-long-ago.py");
        });

        var cleaned = (await new ProjectStore(appPaths).LoadAsync()).Value.Single();
        cleaned.Overrides.ContextFileStates.Should().ContainKey("main.py");
        cleaned.Overrides.ContextFileStates.Should().NotContainKey("deleted-long-ago.py", "失効したパスは掃除されるはず");
        vm.Dispose();
    }

    [AvaloniaFact(DisplayName = "ロックファイルは初期状態でオフ（構成だけ）になり、手でオンにすると記録され次回復元される")]
    public async Task ロックファイルは初期オフで手動オンが記録される()
    {
        var (vm, appPaths, _) = await BuildAsync("lockfile", ws =>
        {
            ws.WriteText("package-lock.json", "{}");
            ws.WriteText("app.py", "x");
        });

        var lockFile = FindByPath(vm, "package-lock.json");
        lockFile.State.Should().Be(ContextFileState.StructureOnly, "ロックファイルは初期状態で「構成だけ」のはず");
        FindByPath(vm, "app.py").State.Should().Be(ContextFileState.Full, "通常のファイルは既定どおり「内容も出す」のはず");

        // ユーザーが手でオンにする（StructureOnly → Hidden → Full の順で巡回）。
        vm.CycleStateCommand.Execute(lockFile);
        vm.CycleStateCommand.Execute(lockFile);
        lockFile.State.Should().Be(ContextFileState.Full);

        await WaitUntilAsync(async () =>
        {
            var reloaded = await new ProjectStore(appPaths).LoadAsync();
            return reloaded.Value.Single().Overrides.ContextFileStates.ContainsKey("package-lock.json");
        });

        var saved = (await new ProjectStore(appPaths).LoadAsync()).Value.Single();
        saved.Overrides.ContextFileStates["package-lock.json"].Should().Be(ContextFileState.Full.ToString(),
            "既定(構成だけ)から外れた「内容も出す」への変更は記録されるはず");

        var reopenedStore = new ProjectStore(appPaths);
        var reopenedProject = (await reopenedStore.LoadAsync()).Value.Single();
        var vm2 = new ContextCollectViewModel(appPaths, reopenedStore, reopenedProject, new Settings(), new AvaloniaUiServices(), new NullDialogService());
        await vm2.InitializeAsync();

        FindByPath(vm2, "package-lock.json").State.Should().Be(ContextFileState.Full, "手動でオンにした状態が復元されるはず");
        vm2.Dispose();
    }

    private async Task<(ContextCollectViewModel Vm, AppPaths AppPaths, Project Project)> BuildAsync(string caseName, Action<Workspace> setup)
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
        return (vm, appPaths, registered);
    }

    private static ContextFileNodeViewModel FindByPath(ContextCollectViewModel vm, string relativePath)
        => vm.Files.Single(f => f.RelativePath == relativePath);

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 500; i++)
        {
            if (await condition().ConfigureAwait(true)) return;
            await Task.Delay(10);
        }
    }

    /// <summary>テスト用ワークスペースへの相対パス書き込みヘルパー（TempWorkspaceはGraft.Tests側のためここでは持たない）。</summary>
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
