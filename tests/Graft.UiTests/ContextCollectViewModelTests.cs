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

    [AvaloniaFact(DisplayName = "「プレビュー」を実行するとPreviewLinesのPropertyChangedが発火し、プレビュー行が表示状態になる")]
    public async Task プレビュー実行後にプレビュー行が表示状態になる()
    {
        var (vm, _, _) = await BuildAsync("previewbug", ws =>
        {
            ws.WriteText("main.py", "print(1)\n");
        });

        // バグ1: ContextCollectWindow.axamlのプレビュー欄・空状態プレースホルダは
        // IsVisible="{Binding PreviewLines, Converter=...HasItems/IsEmptyCollection}" という
        // コレクション「への参照」を対象にした値バインディングのため、PreviewLines自体の
        // PropertyChangedが飛ばないと再評価されない（Clear()/Add()だけではCollectionChangedしか
        // 飛ばない）。ここではPropertyChangedの発火そのものを検証する。
        var raisedForPreviewLines = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ContextCollectViewModel.PreviewLines)) raisedForPreviewLines = true;
        };

        vm.PreviewLines.Should().BeEmpty("プレビュー実行前は空のはず");

        await ExecuteAsync(vm.PreviewCommand);

        raisedForPreviewLines.Should().BeTrue(
            "PreviewLinesのCollectionChangedを受けてPropertyChangedが再発火するはず（IsVisibleバインディングの再評価に必要）");
        vm.PreviewLines.Should().NotBeEmpty("プレビュー実行後は出力内容の行が入っているはず");
    }

    [AvaloniaFact(DisplayName = "全ファイルを「構成だけ」にしても、ライブ表示の推定トークン数は実際の出力の推定値と大きく乖離しない")]
    public async Task ライブ推定と実値のトークン数が大きく乖離しない()
    {
        var (vm, _, _) = await BuildAsync("tokenest", ws =>
        {
            for (var i = 0; i < 8; i++)
            {
                ws.WriteText($"src/module{i}.py", new string('x', 200));
            }
        });

        // バグ2の再現条件: 全ファイルを「構成だけ」に切り替える（内容を出さないため
        // 選択ファイルのバイト数だけを見るとほぼ0になり、構成ツリー分を数えていないと
        // ライブ表示が実値と桁違いにずれる）。
        foreach (var file in vm.Files.Where(f => !f.IsDirectory).ToList())
        {
            vm.CycleStateCommand.Execute(file); // 内容も出す → 構成だけ
        }

        var liveEstimate = vm.EstimatedTokens;
        liveEstimate.Should().BeGreaterThan(0, "構成ツリー分は必ず出力されるため、ライブ表示も0にはならないはず");

        await ExecuteAsync(vm.PreviewCommand);
        var actualEstimate = vm.EstimatedTokens;

        actualEstimate.Should().BeGreaterThan(0);
        var ratio = (double)Math.Max(liveEstimate, actualEstimate) / Math.Min(liveEstimate, actualEstimate);
        ratio.Should().BeLessThan(3.0, $"ライブ表示({liveEstimate}件)と実値({actualEstimate}件)の差が大きすぎる");
    }

    /// <summary>非同期コマンドを実行し、完了するまで待つ（HookSettingsViewModelTestsと同じ手法）。</summary>
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

    /// <summary>
    /// <see cref="BuildAsync(string, System.Action{Workspace})"/>の拡張版。コピー・保存まわりの
    /// テスト（下記）はクリップボードの実際の値・保存先ファイルの実際の内容を検証したいため、
    /// 設定（トークン警告閾値）・<see cref="IUiServices"/>（クリップボードのフェイク）・
    /// <see cref="IDialogService"/>（保存先パスのフェイク）を差し替えられるようにする。
    /// </summary>
    private async Task<(ContextCollectViewModel Vm, AppPaths AppPaths, Project Project)> BuildAsync(
        string caseName, Action<Workspace> setup, Settings settings, IUiServices ui, IDialogService dialogs)
    {
        var appPaths = new AppPaths(Path.Combine(_root, caseName, "app"));
        appPaths.EnsureCoreDirectoriesExist();
        var projectDir = Path.Combine(_root, caseName, "project");
        Directory.CreateDirectory(projectDir);
        setup(new Workspace(projectDir));

        var store = new ProjectStore(appPaths);
        var registered = (await store.RegisterAsync(projectDir, caseName)).Value;

        var vm = new ContextCollectViewModel(appPaths, store, registered, settings, ui, dialogs);
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

        /// <summary>1MB超過ファイルのフィクスチャ等、バイト単位でサイズを厳密に作りたい場合に使う。</summary>
        public void WriteBytes(string relativePath, byte[] content)
        {
            var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, content);
        }
    }

    // ------------------------------------------------------------------
    // 本命の不具合: 推定トークン数が閾値を超えると、コピーがクリップボードへ
    // 一切書き込まずに終わっていた（ステータス表示だけ変わるため、利用者には
    // 「コピーした」ように見えるが実際には前回コピーした古い内容のまま）。
    // ------------------------------------------------------------------

    /// <summary>実際のクリップボードに触れないフェイク（ClipboardWatchTests.FakeClipboardAccessと同じ方針）。</summary>
    private sealed class FakeClipboardAccess : IClipboardAccess
    {
        /// <summary>テストから直接差し替え可能（不具合の再現に「前回コピーした内容」を仕込むため）。</summary>
        public string? Text { get; set; }

        public void SetText(string text) => Text = text;

        public Task<string?> GetTextAsync() => Task.FromResult(Text);
    }

    /// <summary>クリップボードだけフェイクへ差し替えたUI機能一式。画面情報・タイマーは本物（AvaloniaUiServices）を使う。</summary>
    private sealed class FakeUiServices : IUiServices
    {
        private readonly AvaloniaUiServices _inner = new();

        public FakeUiServices(IClipboardAccess clipboard) => Clipboard = clipboard;

        public IClipboardAccess Clipboard { get; }

        public IScreenInfo Screens => _inner.Screens;

        public IUiTimer CreateTimer(TimeSpan interval, Action onTick) => _inner.CreateTimer(interval, onTick);
    }

    /// <summary>
    /// 「名前を付けて保存」を、実際にダイアログを出さずあらかじめ決めたパスへ即決定するフェイク。
    /// それ以外の確認系はNullDialogServiceと同じ安全側（キャンセル扱い）に倣う。
    /// </summary>
    private sealed class FakeSaveDialogService : IDialogService
    {
        private readonly string _path;
        public FakeSaveDialogService(string path) => _path = path;

        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(false);

        public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel)
            => Task.FromResult((bool?)null);

        public Task<string?> PromptAsync(string title, string message, string? initial = null)
            => Task.FromResult((string?)null);

        public Task<string?> PickFolderAsync(string title) => Task.FromResult((string?)null);

        public Task<string?> PickFileAsync(string title, IReadOnlyList<string>? extensions = null)
            => Task.FromResult((string?)null);

        public Task<string?> SaveFileAsync(string title, string suggestedFileName, IReadOnlyList<string>? extensions = null)
            => Task.FromResult((string?)_path);

        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
    }

    [AvaloniaFact(DisplayName = "推定トークン数が閾値を超えても、コピーは必ずクリップボードへ全文を書く（本命）")]
    public async Task 閾値超過でもコピーは全文を書く()
    {
        var clipboard = new FakeClipboardAccess();
        // TokenWarnThresholdを極端に小さくし、通常サイズのファイル1つだけで確実に超過させる
        // （巨大なフィクスチャを用意しなくても閾値超過を再現できる）。
        var settings = new Settings { Context = new ContextSettings { TokenWarnThreshold = 1 } };
        var (vm, _, _) = await BuildAsync(
            "copy-exceeds", ws => ws.WriteText("main.py", "print('graftコンテキスト収集の本文')\n"),
            settings, new FakeUiServices(clipboard), new NullDialogService());

        vm.ExceedsWarnThreshold.Should().BeTrue("極端に小さい閾値のため必ず超過するはず（前提の確認）");

        clipboard.Text = "前回コピーした古い内容"; // 修正前の不具合を再現する呼び水（早期returnなら書き換わらずこれが残る）

        await ExecuteAsync(vm.CopyCommand);

        clipboard.Text.Should().NotBe("前回コピーした古い内容",
            "早期returnで書き込まれず、前回コピーした内容が残ったままなのが今回の不具合そのもの");
        clipboard.Text.Should().Contain("main.py").And.Contain("graftコンテキスト収集の本文",
            "閾値を超えていても全文（選択ファイルの内容）がクリップボードへ書かれるはず");

        vm.StatusMessage.Should().Contain("コピーしました", "コピーされたことがステータスへ先に伝わるはず");
        vm.StatusMessage.Should().NotContain("上限", "Graftが拒否しているように読める語は使わないはず");
        vm.StatusMessage.Should().Contain("目安", "大きさについては拒否ではなく助言として続けて伝えるはず");
    }

    [AvaloniaFact(DisplayName = "推定トークン数が閾値以下のときは、従来どおりコピーされ助言文言は付かない（デグレ防止）")]
    public async Task 閾値以下ならコピーは従来どおり()
    {
        var clipboard = new FakeClipboardAccess();
        var (vm, _, _) = await BuildAsync(
            "copy-within", ws => ws.WriteText("main.py", "print(1)\n"),
            new Settings(), new FakeUiServices(clipboard), new NullDialogService());

        vm.ExceedsWarnThreshold.Should().BeFalse("既定の閾値（5万トークン）に対し、この程度の内容は超過しないはず（前提の確認）");

        await ExecuteAsync(vm.CopyCommand);

        clipboard.Text.Should().Contain("main.py").And.Contain("print(1)");
        vm.StatusMessage.Should().Be("クリップボードにコピーしました。", "閾値以下では従来どおり助言なしの短いメッセージのはず");
    }

    [AvaloniaFact(DisplayName = "推定トークン数が閾値を超えても、保存されたファイルの内容はコピーした内容と完全に一致する")]
    public async Task 閾値超過でも保存内容はコピー内容と完全一致する()
    {
        var clipboard = new FakeClipboardAccess();
        var settings = new Settings { Context = new ContextSettings { TokenWarnThreshold = 1 } };
        var savePath = Path.Combine(_root, "save-exceeds", "out.md");
        Directory.CreateDirectory(Path.GetDirectoryName(savePath)!);

        var (vm, _, _) = await BuildAsync(
            "save-exceeds", ws => ws.WriteText("main.py", "print('graftコンテキスト収集の本文')\n"),
            settings, new FakeUiServices(clipboard), new FakeSaveDialogService(savePath));

        vm.ExceedsWarnThreshold.Should().BeTrue("前提の確認: 極端に小さい閾値のため必ず超過する");

        // 同一のvm・同一の選択状態で、コピー→保存の順に実行する。生成日時（分単位）を含む
        // 出力のため、テスト実行中（数十ms）に分をまたがない前提で、両者は完全一致するはず。
        await ExecuteAsync(vm.CopyCommand);
        var copiedText = clipboard.Text;
        copiedText.Should().NotBeNullOrEmpty();

        await ExecuteAsync(vm.SaveToFileCommand);

        File.Exists(savePath).Should().BeTrue();
        var savedText = await File.ReadAllTextAsync(savePath);
        savedText.Should().Be(copiedText, "保存されたファイルの内容はコピーした内容と先頭・末尾まで完全に一致するはず");

        vm.StatusMessage.Should().StartWith("保存しました。", "保存できたことが先に伝わるはず（『超えていますが保存しました』という特別扱いの言い方をしない）");
        vm.StatusMessage.Should().NotContain("上限", "Graftが拒否しているように読める語は使わないはず");
        vm.StatusMessage.Should().Contain("目安", "大きさについては助言として続けて伝えるはず");
    }

    [AvaloniaFact(DisplayName = "「選択ファイル」モードでチェック済みのファイルが後から1MBを超えても、保存先にその旨が記載される（チェック時点では対象外だった除外が保存直前に効くケース）")]
    public async Task 選択ファイルモードで選択後に1MBを超えたファイルは省略の旨が保存先に残る()
    {
        // 実機で起こりうる経緯: チェックした時点ではbig.txtは1MB未満で対象内だった
        // （ContextCollectViewModel.Filesはこの時点のスナップショット）。ところが
        // 「保存」を押した瞬間にContextCollector.CollectAsyncは必ず走査をやり直す
        // （ScanAsyncを毎回呼ぶ実装）ため、その間にファイルが1MBを超えていれば
        // 保存直前に初めて除外対象になる。ツリーの無い「選択ファイル」モードでは、
        // 修正前はこの場合も本文から痕跡なく消えていた。
        var savePath = Path.Combine(_root, "save-oversize", "out.md");
        Directory.CreateDirectory(Path.GetDirectoryName(savePath)!);

        var (vm, _, project) = await BuildAsync(
            "save-oversize",
            ws =>
            {
                ws.WriteText("big.txt", "x"); // チェック時点では1MB未満
                ws.WriteText("normal.py", "print(1)");
            },
            new Settings(), new FakeUiServices(new FakeClipboardAccess()), new FakeSaveDialogService(savePath));

        vm.SelectedMode = ContextMode.SelectedFiles;
        FindByPath(vm, "big.txt").State.Should().Be(ContextFileState.Full, "既定で選択済みのはず（チェック時点ではまだ1MB未満）");

        // 保存を押す前に、実際のファイルを1MB超へ書き換える（上記の経緯を再現）。
        File.WriteAllBytes(Path.Combine(project.Root, "big.txt"), new byte[1024 * 1024 + 1]);

        await ExecuteAsync(vm.SaveToFileCommand);

        File.Exists(savePath).Should().BeTrue();
        var savedText = await File.ReadAllTextAsync(savePath);
        savedText.Should().Contain("normal.py").And.Contain("print(1)");
        savedText.Should().Contain("big.txt", "選択されていたが保存直前に除外された事実が保存先にも明記されるはず");
        savedText.Should().Contain("サイズが1MBを超過", "除外理由が保存先からも読み取れるはず");
    }
}
