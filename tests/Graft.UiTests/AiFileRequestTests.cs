using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Core;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;
using Graft.Platform.Null;
using Graft.ViewModels;
using Graft.Views;
using Xunit;

namespace Graft.UiTests;

/// <summary>
/// AIの「このファイルも見せて」（<c>NEED_MORE_CONTEXT: &lt;パス&gt;</c>、E710）を、コンテキスト収集の
/// 選択状態へそのまま反映する機能（<see cref="ContextCollectViewModel.ApplyRequestedFilesAsync"/>、
/// 中央ペインのエラー表示の操作ボタン）のテスト。
///
/// これまでは E710 の案内文が出るだけで、利用者がコンテキスト収集の窓を開いて該当ファイルを
/// 手で探し直す必要があった。要求されたファイルだけが「内容も出す」になり、ほかのファイルの
/// 状態が変わらないこと、区切り文字の違いを吸収すること、該当しなかったものが理由つきで
/// 分かること、選択がプロジェクトごとの保存先に乗ることを固定する。
/// <see cref="IUiServices.CreateTimer"/> が DispatcherTimer を使うため AvaloniaFact で実行する。
/// </summary>
public class AiFileRequestTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-aifilerequest", Guid.NewGuid().ToString("N"));

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

    // ------------------------------------------------------------------
    // ContextCollectViewModel.ApplyRequestedFilesAsync
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "要求されたファイルが「内容も出す」になり、ほかのファイルの状態は変わらない")]
    public async Task 要求されたファイルだけが内容も出すになる()
    {
        var (vm, _, _) = await BuildAsync("only-requested", ws =>
        {
            ws.WriteText("src/a.py", "x");
            ws.WriteText("src/b.py", "x");
            ws.WriteText("c.py", "x");
            ws.WriteText("d.py", "x");
        });

        // 前提: a は「構成だけ」、b は「出さない」、c は触らない（Full）、d は「構成だけ」。
        vm.CycleStateCommand.Execute(Find(vm, "src/a.py"));
        vm.CycleStateCommand.Execute(Find(vm, "src/b.py"));
        vm.CycleStateCommand.Execute(Find(vm, "src/b.py"));
        vm.CycleStateCommand.Execute(Find(vm, "d.py"));
        Find(vm, "src/a.py").State.Should().Be(ContextFileState.StructureOnly);
        Find(vm, "src/b.py").State.Should().Be(ContextFileState.Hidden);

        var result = await vm.ApplyRequestedFilesAsync(new[] { "src/a.py", "src/b.py" });

        Find(vm, "src/a.py").State.Should().Be(ContextFileState.Full, "要求されたファイルは「内容も出す」になる");
        Find(vm, "src/b.py").State.Should().Be(ContextFileState.Full, "「出さない」だったファイルも要求されれば「内容も出す」になる");
        Find(vm, "c.py").State.Should().Be(ContextFileState.Full, "触っていないファイルは変わらない");
        Find(vm, "d.py").State.Should().Be(ContextFileState.StructureOnly, "要求されていないファイルは、以前の状態のまま変わらない");
        result.Matched.Should().BeEquivalentTo("src/a.py", "src/b.py");
        result.IsFullyApplied.Should().BeTrue();
        vm.Dispose();
    }

    [AvaloniaFact(DisplayName = "要求されていないファイルの状態は、要求の前後で1つも変わらない")]
    public async Task 要求されていないファイルは1つも変わらない()
    {
        var (vm, _, _) = await BuildAsync("others-unchanged", ws =>
        {
            ws.WriteText("a.py", "x");
            ws.WriteText("b.py", "x");
            ws.WriteText("c.py", "x");
            ws.WriteText("package-lock.json", "{}"); // 既定は「構成だけ」
        });
        vm.CycleStateCommand.Execute(Find(vm, "b.py")); // b: 構成だけ
        vm.CycleStateCommand.Execute(Find(vm, "c.py"));
        vm.CycleStateCommand.Execute(Find(vm, "c.py")); // c: 出さない
        var before = SnapshotFileStates(vm);

        await vm.ApplyRequestedFilesAsync(new[] { "a.py" });

        var after = SnapshotFileStates(vm);
        foreach (var path in new[] { "b.py", "c.py", "package-lock.json" })
        {
            after[path].Should().Be(before[path], $"{path} は要求されていないので変わらない");
        }
        vm.Dispose();
    }

    [AvaloniaFact(DisplayName = "\\ と / の区切りや大文字小文字の違いがあっても、実在のファイルが「内容も出す」になる")]
    public async Task 区切りと大文字小文字の違いを吸収する()
    {
        var (vm, _, _) = await BuildAsync("separator", ws =>
        {
            ws.WriteText("Lib/Helper.py", "x");
            ws.WriteText("Lib/Other.py", "x");
            ws.WriteText("main.py", "x");
        });
        vm.CycleStateCommand.Execute(Find(vm, "Lib/Helper.py"));
        vm.CycleStateCommand.Execute(Find(vm, "Lib/Other.py"));
        vm.CycleStateCommand.Execute(Find(vm, "main.py"));

        var result = await vm.ApplyRequestedFilesAsync(new[] { @"Lib\Helper.py", "./lib/other.py", @".\MAIN.PY" });

        Find(vm, "Lib/Helper.py").State.Should().Be(ContextFileState.Full, @"バックスラッシュ区切りを吸収する");
        Find(vm, "Lib/Other.py").State.Should().Be(ContextFileState.Full, "先頭の ./ と大文字小文字の違いを吸収する");
        Find(vm, "main.py").State.Should().Be(ContextFileState.Full, @"先頭の .\ と大文字小文字の違いを吸収する");
        result.NotFound.Should().BeEmpty();
        vm.Dispose();
    }

    [AvaloniaFact(DisplayName = "見つからないパスは結果とステータス表示で報告され、見つかったものだけが反映される")]
    public async Task 見つからないパスが報告される()
    {
        var (vm, _, _) = await BuildAsync("notfound", ws =>
        {
            ws.WriteText("a.py", "x");
            ws.WriteText("b.py", "x");
        });
        vm.CycleStateCommand.Execute(Find(vm, "a.py"));

        var result = await vm.ApplyRequestedFilesAsync(new[] { "a.py", "missing/ghost.py", "also-missing.txt" });

        result.NotFound.Should().Equal("missing/ghost.py", "also-missing.txt");
        result.Matched.Should().Equal("a.py");
        result.IsFullyApplied.Should().BeFalse();
        Find(vm, "a.py").State.Should().Be(ContextFileState.Full);
        vm.StatusMessage.Should().Contain("3件のうち1件").And.Contain("見つからない")
            .And.Contain("missing/ghost.py").And.Contain("also-missing.txt");
        vm.Dispose();
    }

    [AvaloniaFact(DisplayName = "除外されていて選べないファイルは、理由つきでステータス表示に出て、状態は変えない")]
    public async Task 除外されたファイルが理由つきで報告される()
    {
        var (vm, _, _) = await BuildAsync("excluded", ws =>
        {
            ws.WriteText(".gitignore", "secret.env\n");
            ws.WriteText("secret.env", "KEY=1");
            ws.WriteText("ok.py", "x");
        });
        vm.CycleStateCommand.Execute(Find(vm, "ok.py"));

        var result = await vm.ApplyRequestedFilesAsync(new[] { "secret.env", "ok.py" });

        result.Excluded.Should().ContainSingle().Which.Requested.Should().Be("secret.env");
        result.Matched.Should().Equal("ok.py");
        Find(vm, "secret.env").State.Should().BeNull("除外されたファイルは選択の対象外のまま");
        vm.StatusMessage.Should().Contain("除外されていて選べない").And.Contain("secret.env");
        vm.Dispose();
    }

    [AvaloniaFact(DisplayName = "すべて反映できたときは、ステータス表示にその件数が出て、該当しなかったものの注記は出ない")]
    public async Task すべて反映できたときのステータス()
    {
        var (vm, _, _) = await BuildAsync("allok", ws =>
        {
            ws.WriteText("a.py", "x");
            ws.WriteText("b.py", "x");
        });

        await vm.ApplyRequestedFilesAsync(new[] { "a.py", "b.py" });

        vm.StatusMessage.Should().Contain("2件のうち2件").And.NotContain("見つからない").And.NotContain("除外");
        vm.Dispose();
    }

    [AvaloniaFact(DisplayName = "要求されたファイルの状態は、チェックを手で切り替えたときと同じ保存先（ContextFileStates）に保存され、次回開いても復元される")]
    public async Task 要求の反映が保存され復元される()
    {
        var (vm, appPaths, _) = await BuildAsync("persist", ws =>
        {
            ws.WriteText("package-lock.json", "{}"); // 既定は「構成だけ」→ Full は既定から外れるので記録される
            ws.WriteText("secret.env", "x");
            ws.WriteText("main.py", "x");
        });
        vm.CycleStateCommand.Execute(Find(vm, "secret.env"));
        vm.CycleStateCommand.Execute(Find(vm, "secret.env")); // 出さない（これは保存される）

        await vm.ApplyRequestedFilesAsync(new[] { "package-lock.json" });

        // 画面を閉じた直後に別の経路で読み直しても反映されている（保存を待たずその場で行うため）。
        var store = new ProjectStore(appPaths);
        var saved = (await store.LoadAsync()).Value.Single();
        saved.Overrides.ContextFileStates["package-lock.json"].Should().Be(ContextFileState.Full.ToString());

        var vm2 = new ContextCollectViewModel(appPaths, store, saved, new Settings(), new AvaloniaUiServices(), new NullDialogService());
        await vm2.InitializeAsync();
        Find(vm2, "package-lock.json").State.Should().Be(ContextFileState.Full, "要求の反映は次回開いたときにも残る");
        Find(vm2, "secret.env").State.Should().Be(ContextFileState.Hidden, "要求されなかったファイルの保存済みの状態は消えない");
        vm.Dispose();
        vm2.Dispose();
    }

    [AvaloniaFact(DisplayName = "「ツリーのみ」「差分のみ」モードでは、選んだファイルが出力に載るよう「ツリー＋選択」へ切り替え、その旨を表示する")]
    public async Task 内容が出ないモードでは切り替える()
    {
        var (vm, _, _) = await BuildAsync("mode", ws => ws.WriteText("a.py", "x"));
        vm.SelectedMode = ContextMode.TreeOnly;

        var result = await vm.ApplyRequestedFilesAsync(new[] { "a.py" });

        vm.SelectedMode.Should().Be(ContextMode.TreeAndSelected);
        result.ModeChanged.Should().BeTrue();
        vm.StatusMessage.Should().Contain("ツリーのみ").And.Contain("ツリー＋選択");
        vm.Dispose();
    }

    [AvaloniaFact(DisplayName = "「選択ファイル」モードは利用者の選択を尊重して切り替えない")]
    public async Task 選択ファイルモードは切り替えない()
    {
        var (vm, _, _) = await BuildAsync("mode-keep", ws => ws.WriteText("a.py", "x"));
        vm.SelectedMode = ContextMode.SelectedFiles;

        var result = await vm.ApplyRequestedFilesAsync(new[] { "a.py" });

        vm.SelectedMode.Should().Be(ContextMode.SelectedFiles);
        result.ModeChanged.Should().BeFalse();
        vm.Dispose();
    }

    [AvaloniaFact(DisplayName = "反映した結果は、コピーされる内容に実際に載る（要求したファイルの中身が出て、要求しなかった「出さない」は出ない）")]
    public async Task 反映した結果がコピー内容に載る()
    {
        var clipboard = new FakeClipboard();
        var (vm, _, _) = await BuildAsync("copy", ws =>
        {
            ws.WriteText("wanted.py", "WANTED_CONTENT = 1\n");
            ws.WriteText("unwanted.py", "UNWANTED_CONTENT = 2\n");
        }, new FakeUi(clipboard));
        vm.CycleStateCommand.Execute(Find(vm, "wanted.py")); // 構成だけ
        vm.CycleStateCommand.Execute(Find(vm, "unwanted.py"));
        vm.CycleStateCommand.Execute(Find(vm, "unwanted.py")); // 出さない

        await vm.ApplyRequestedFilesAsync(new[] { "wanted.py" });
        vm.CopyCommand.Execute(null);
        for (var i = 0; i < 500 && vm.CopyCommand.IsExecuting; i++) await Task.Delay(10);

        clipboard.Text.Should().Contain("WANTED_CONTENT").And.NotContain("UNWANTED_CONTENT");
        vm.Dispose();
    }

    [AvaloniaFact(DisplayName = "要求が空のときは何も変えず、ステータス表示も書き換えない")]
    public async Task 要求が空なら何もしない()
    {
        var (vm, _, _) = await BuildAsync("empty", ws => ws.WriteText("a.py", "x"));
        vm.CycleStateCommand.Execute(Find(vm, "a.py"));

        var result = await vm.ApplyRequestedFilesAsync(Array.Empty<string>());

        result.TotalCount.Should().Be(0);
        Find(vm, "a.py").State.Should().Be(ContextFileState.StructureOnly);
        vm.Dispose();
    }

    // ------------------------------------------------------------------
    // 中央ペインのエラー表示（MainViewModel / EmptyStateView）
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "E710で求められたファイルがあるとき、エラー表示に「要求されたファイルを収集に追加」の操作が出て、押すと要求パスつきで窓を開く通知が飛ぶ")]
    public async Task E710のエラー表示に操作が出る()
    {
        var projectDir = Path.Combine(_root, "scenario", "project");
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, "a.py"), "x");
        var shell = BuildShell(Path.Combine(_root, "scenario", "app"));
        try
        {
            await shell.Graft.ProjectPane.RegisterFolderAsync(projectDir);
            var patchPath = Path.Combine(_root, "scenario", "reply.md");
            await File.WriteAllTextAsync(patchPath, "```text\nNEED_MORE_CONTEXT: src/a.py\nNEED_MORE_CONTEXT: lib\\b.py\n```\n");

            await shell.Graft.LoadPatchFromFileAsync(patchPath);

            shell.Graft.CenterError!.Code.Should().Be(ErrorCode.E710, "パス付きの合図もE001ではなくE710になる");
            shell.Graft.RequestedFilesActionText.Should().Contain("2件");
            shell.Graft.AddRequestedFilesToContextCommand.CanExecute(null).Should().BeTrue();

            IReadOnlyList<string>? requested = null;
            shell.Graft.RequestOpenContextCollect += (_, e) => requested = e.RequestedPaths;
            shell.Graft.AddRequestedFilesToContextCommand.Execute(null);

            requested.Should().Equal("src/a.py", @"lib\b.py");
        }
        finally
        {
            shell.Dispose();
        }
    }

    [AvaloniaFact(DisplayName = "E710でもファイルを求められていない（語だけ）場合や、別のエラーでは、操作は出ない")]
    public async Task 求められたファイルが無ければ操作は出ない()
    {
        var projectDir = Path.Combine(_root, "scenario2", "project");
        Directory.CreateDirectory(projectDir);
        var shell = BuildShell(Path.Combine(_root, "scenario2", "app"));
        try
        {
            await shell.Graft.ProjectPane.RegisterFolderAsync(projectDir);

            var wordOnly = Path.Combine(_root, "scenario2", "word.md");
            await File.WriteAllTextAsync(wordOnly, "NEED_MORE_CONTEXT\n");
            await shell.Graft.LoadPatchFromFileAsync(wordOnly);
            shell.Graft.CenterError!.Code.Should().Be(ErrorCode.E710);
            shell.Graft.RequestedFilesActionText.Should().BeEmpty();
            shell.Graft.AddRequestedFilesToContextCommand.CanExecute(null).Should().BeFalse();

            var other = Path.Combine(_root, "scenario2", "other.md");
            await File.WriteAllTextAsync(other, "ただの文章です\n");
            await shell.Graft.LoadPatchFromFileAsync(other);
            shell.Graft.CenterError!.Code.Should().NotBe(ErrorCode.E710);
            shell.Graft.RequestedFilesActionText.Should().BeEmpty();
        }
        finally
        {
            shell.Dispose();
        }
    }

    [AvaloniaFact(DisplayName = "エラー表示のコントロールは、操作の文言が空なら操作ボタンを隠し、文言があれば出す")]
    public void エラー表示の操作ボタンは文言があるときだけ出る()
    {
        var view = new EmptyStateView { State = EmptyStateMode.Error };
        var button = view.FindControl<Button>("IssueActionButton")!;

        button.IsVisible.Should().BeFalse("文言が空なら（多くのエラーや他の画面では）ボタンごと隠す");

        view.IssueActionText = "要求されたファイルを収集に追加（2件）";
        button.IsVisible.Should().BeTrue();
        button.Content.Should().Be("要求されたファイルを収集に追加（2件）");

        view.IssueActionText = string.Empty;
        button.IsVisible.Should().BeFalse();
    }

    // ------------------------------------------------------------------
    // 補助
    // ------------------------------------------------------------------

    private static ContextFileNodeViewModel Find(ContextCollectViewModel vm, string relativePath)
        => vm.Files.Single(f => f.RelativePath == relativePath);

    private static Dictionary<string, ContextFileState?> SnapshotFileStates(ContextCollectViewModel vm)
        => vm.Files.Where(f => !f.IsDirectory).ToDictionary(f => f.RelativePath, f => f.State);

    private ShellViewModel BuildShell(string appDirectory)
    {
        Directory.CreateDirectory(appDirectory);
        var appPaths = new AppPaths(appDirectory);
        appPaths.EnsureCoreDirectoriesExist();
        return StartupCoordinator.BuildShellViewModel(
            appPaths,
            new Settings(),
            new SettingsStore(appPaths),
            new PatchQueue(appPaths),
            new ProjectStore(appPaths),
            new RevisionStore(appPaths),
            new RevisionRestorer(appPaths),
            new NullDialogService(),
            new FakeUi(new FakeClipboard()),
            openSettings: () => { });
    }

    private Task<(ContextCollectViewModel Vm, AppPaths AppPaths, Project Project)> BuildAsync(
        string caseName, Action<Workspace> setup)
        => BuildAsync(caseName, setup, new AvaloniaUiServices());

    private async Task<(ContextCollectViewModel Vm, AppPaths AppPaths, Project Project)> BuildAsync(
        string caseName, Action<Workspace> setup, IUiServices ui)
    {
        var appPaths = new AppPaths(Path.Combine(_root, caseName, "app"));
        appPaths.EnsureCoreDirectoriesExist();
        var projectDir = Path.Combine(_root, caseName, "project");
        Directory.CreateDirectory(projectDir);
        setup(new Workspace(projectDir));

        var store = new ProjectStore(appPaths);
        var registered = (await store.RegisterAsync(projectDir, caseName)).Value;

        var vm = new ContextCollectViewModel(appPaths, store, registered, new Settings(), ui, new NullDialogService());
        await vm.InitializeAsync();
        return (vm, appPaths, registered);
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

    private sealed class FakeClipboard : IClipboardAccess
    {
        public string? Text { get; private set; }
        public void SetText(string text) => Text = text;
        public Task<string?> GetTextAsync() => Task.FromResult(Text);
    }

    /// <summary>クリップボードだけフェイクにしたUI機能一式（画面情報・タイマーは本物）。</summary>
    private sealed class FakeUi : IUiServices
    {
        private readonly AvaloniaUiServices _inner = new();
        public FakeUi(IClipboardAccess clipboard) => Clipboard = clipboard;
        public IClipboardAccess Clipboard { get; }
        public IScreenInfo Screens => _inner.Screens;
        public IUiTimer CreateTimer(TimeSpan interval, Action onTick) => _inner.CreateTimer(interval, onTick);
    }
}
