using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Core;
using Graft.Infra;
using Graft.Platform;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests;

/// <summary>
/// 差分画面のインライン編集（仕様書8.7）で書き換えたSEARCH部を、そのまま適用に含める経路の検証。
/// 以前は「一致した」と分かっても適用へ反映する経路が無く（<c>BuildEditedPair</c> の呼び出し元が
/// 1つも無かった）、AIへ依頼し直すしかなかった。
/// 前半は <see cref="InlineEditViewModel"/> 単体（押せる条件）、後半は接ぎ木パネル全体の流れ
/// （差し替え→ドライランのやり直し→適用→履歴）を検証する。
/// 200msのデバウンスは実時間を待たず、手動で発火できるタイマーに差し替えて決定的にしている。
/// </summary>
public class InlineEditAdoptTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-inline-edit-adopt", Guid.NewGuid().ToString("N"));
    private readonly string _appDirectory;
    private readonly string _projectDirectory;
    private readonly FakeClipboard _clipboard = new();
    private readonly CountingDialogService _dialogs = new();
    private readonly FakeUiServices _ui;

    public InlineEditAdoptTests()
    {
        _appDirectory = Path.Combine(_root, "app");
        _projectDirectory = Path.Combine(_root, "project");
        Directory.CreateDirectory(_appDirectory);
        Directory.CreateDirectory(_projectDirectory);
        _ui = new FakeUiServices(_clipboard);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------------
    // InlineEditViewModel 単体: 「この修正で適用に含める」を押せる条件
    // ------------------------------------------------------------------

    private const string FileText = "one\ntwo\nthree\ntwo\n";

    private static SearchReplacePair Pair(string search) => new()
    {
        SearchText = search, ReplaceText = "REPLACED", Description = "説明", SourceLine = 7,
    };

    private (InlineEditViewModel Vm, List<SearchReplacePair> Adopted) NewVm(
        string originalSearch, string fileText = FileText, OccurrenceSpec? occurrence = null, bool withHandler = true)
    {
        var adopted = new List<SearchReplacePair>();
        var vm = new InlineEditViewModel(
            "a.txt", Pair(originalSearch), fileText, occurrence ?? OccurrenceSpec.Single,
            new MatchOptions(), syntaxEnabled: false, _ui,
            withHandler ? (edited => { adopted.Add(edited); return Task.CompletedTask; }) : null);
        return (vm, adopted);
    }

    [AvaloniaFact(DisplayName = "適用に含める: 一致しない間は押せない（元のSEARCHのまま・編集しても一致しない）")]
    public void 一致しない間は押せない()
    {
        var (vm, _) = NewVm("存在しない行");
        vm.CanAdopt.Should().BeFalse("編集前で、しかも元のSEARCHは失敗している");

        vm.SearchText = "まだ違う行";
        _ui.FireLastDebounce();

        vm.IsMatchSuccessful.Should().BeFalse();
        vm.CanAdopt.Should().BeFalse("再判定しても一致しないので押せない");
        vm.AdoptCommand.CanExecute(null).Should().BeFalse();
    }

    [AvaloniaFact(DisplayName = "適用に含める: 書き換えて一意に一致したら押せる（入力直後の再判定前は押せない）")]
    public void 一致したら押せる()
    {
        var (vm, _) = NewVm("存在しない行");

        vm.SearchText = "three";
        vm.CanAdopt.Should().BeFalse("入力から200ms以内は、画面の判定が古い入力のものなので押せない");
        vm.AdoptCommand.CanExecute(null).Should().BeFalse();

        _ui.FireLastDebounce();

        vm.IsMatchSuccessful.Should().BeTrue();
        vm.CanAdopt.Should().BeTrue();
        vm.AdoptCommand.CanExecute(null).Should().BeTrue();
    }

    [AvaloniaFact(DisplayName = "適用に含める: 複数箇所に一致する間は押せない（OCCURRENCE未指定のE102）")]
    public void 複数箇所に一致する間は押せない()
    {
        var (vm, _) = NewVm("存在しない行");

        vm.SearchText = "two"; // FileTextには2箇所ある
        _ui.FireLastDebounce();

        vm.IsMatchSuccessful.Should().BeFalse();
        vm.ResultSummary.Should().Contain("E102");
        vm.CanAdopt.Should().BeFalse();
    }

    [AvaloniaFact(DisplayName = "適用に含める: 一致した後でまた一致しない内容に直すと、押せなくなる")]
    public void 一致後に崩すと押せなくなる()
    {
        var (vm, _) = NewVm("存在しない行");
        vm.SearchText = "three";
        _ui.FireLastDebounce();
        vm.CanAdopt.Should().BeTrue();

        vm.SearchText = "thre";
        vm.CanAdopt.Should().BeFalse("直後は判定待ち");
        _ui.FireLastDebounce();
        vm.CanAdopt.Should().BeFalse("再判定で一致しなくなった");
    }

    [AvaloniaFact(DisplayName = "適用に含める: 差し替えの受け皿が無い画面ではボタン自体が出ず、押せない")]
    public void 受け皿が無ければ出ない()
    {
        var (vm, _) = NewVm("存在しない行", withHandler: false);
        vm.SearchText = "three";
        _ui.FireLastDebounce();

        vm.IsMatchSuccessful.Should().BeTrue();
        vm.HasAdoptHandler.Should().BeFalse();
        vm.CanAdopt.Should().BeFalse();
    }

    [AvaloniaFact(DisplayName = "適用に含める: 押すと、REPLACEや説明は元のまま、SEARCHだけ書き換えたペアが渡る")]
    public async Task 渡るペアはREPLACEを変えない()
    {
        var (vm, adopted) = NewVm("存在しない行");
        vm.SearchText = "three";
        _ui.FireLastDebounce();

        await vm.AdoptAsync();

        var edited = adopted.Should().ContainSingle().Subject;
        edited.SearchText.Should().Be("three");
        edited.ReplaceText.Should().Be("REPLACED", "REPLACE部は変えない");
        edited.Description.Should().Be("説明");
        edited.SourceLine.Should().Be(7);
        edited.IsSearchEdited.Should().BeTrue("履歴に残すための印");
        vm.OriginalSearchText.Should().Be("存在しない行", "編集前のSEARCH部（元のペア）は保たれる");
    }

    [AvaloniaFact(DisplayName = "適用に含める: 押せない状態では、直接呼んでも何も渡さない（一致しない・入力直後に崩した場合）")]
    public async Task 押せない状態では渡さない()
    {
        var (vm, adopted) = NewVm("存在しない行");
        vm.SearchText = "three";
        _ui.FireLastDebounce();
        vm.SearchText = "thre"; // 判定前に崩した（画面の「一致」は古い）

        await vm.AdoptAsync();

        adopted.Should().BeEmpty("押された瞬間の入力で再判定し直し、一致していなければ渡さない");
        vm.IsMatchSuccessful.Should().BeFalse();
    }

    [AvaloniaFact(DisplayName = "BuildEditedPair: 編集していなければ元のペアをそのまま返し、書き換えの印を立てない")]
    public void 編集していなければ印を立てない()
    {
        var (vm, _) = NewVm("存在しない行");

        vm.BuildEditedPair().IsSearchEdited.Should().BeFalse();
        vm.BuildEditedPair().SearchText.Should().Be("存在しない行");
    }

    // ------------------------------------------------------------------
    // 接ぎ木パネル全体: 差し替え → ドライランのやり直し → 適用 → 履歴
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "適用に含める: 書き換えたSEARCHが一致したら、差し替えでそのブロックが適用可能になり、適用できて履歴に残る")]
    public async Task 差し替えて適用できる()
    {
        await File.WriteAllTextAsync(Path.Combine(_projectDirectory, "ok.txt"), "alpha\nbeta\n").ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(_projectDirectory, "bad.txt"), "one\ntwo\nthree\n").ConfigureAwait(true);
        var shell = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);
        var projectId = shell.Graft.ProjectPane.SelectedItem!.Project.Id;

        var patchText = BuildPatch(("ok.txt", "alpha", "ALPHA"), ("bad.txt", "two (AIが思い込んだ内容)", "TWO"));
        _clipboard.Text = patchText;
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);

        var okBlock = shell.Graft.Blocks.Single(b => b.Plan.Path == "ok.txt");
        var badBlock = shell.Graft.Blocks.Single(b => b.Plan.Path == "bad.txt");
        badBlock.IsError.Should().BeTrue();
        okBlock.IsSelected = false; // 利用者が意図してチェックを外した状態

        shell.Graft.SelectedBlock = badBlock;
        var edit = shell.Graft.Diff.InlineEdits.Should().ContainSingle().Subject;
        edit.CanAdopt.Should().BeFalse("まだ書き換えていない");

        edit.SearchText = "two";
        _ui.FireLastDebounce();
        edit.IsMatchSuccessful.Should().BeTrue();
        edit.AdoptCommand.CanExecute(null).Should().BeTrue();

        await ExecuteAsync(edit.AdoptCommand).ConfigureAwait(true);

        // 差し替え後のドライラン: そのブロックが適用可能・選択済みになり、REPLACEは変わらない。
        var adopted = shell.Graft.Blocks.Single(b => b.Plan.Path == "bad.txt");
        adopted.IsOk.Should().BeTrue("書き換えたSEARCHで適用可能になるはず");
        adopted.IsSelected.Should().BeTrue();
        adopted.Plan.Pair!.ReplaceText.Should().Be("TWO");
        adopted.Plan.Pair.IsSearchEdited.Should().BeTrue();
        shell.Graft.SelectedBlock.Should().BeSameAs(adopted, "差し替えたブロックを選択して、結果をすぐ見られるようにする");

        // 他のブロックの状態（利用者が外したチェック）は黙って戻されない。
        shell.Graft.Blocks.Single(b => b.Plan.Path == "ok.txt").IsSelected.Should().BeFalse();

        // 差し替えるのはメモリ上のパッチだけ。クリップボード（元のテキスト）は変わらない。
        _clipboard.Text.Should().Be(patchText);

        // 適用: 差し替えたSEARCHで書き換わり、チェックを外したブロックは適用されない。
        await ExecuteAsync(shell.Graft.ApplyCommand).ConfigureAwait(true);
        (await File.ReadAllTextAsync(Path.Combine(_projectDirectory, "bad.txt")).ConfigureAwait(true))
            .Should().Be("one\nTWO\nthree\n");
        (await File.ReadAllTextAsync(Path.Combine(_projectDirectory, "ok.txt")).ConfigureAwait(true))
            .Should().Be("alpha\nbeta\n");

        // 履歴: 書き換えたSEARCHが含まれたことが残る。
        var revisions = (await new RevisionStore(new AppPaths(_appDirectory)).ListAsync(projectId).ConfigureAwait(true)).Value;
        revisions.Should().ContainSingle().Which.Manifest.Entries
            .Should().ContainSingle(e => e.Path == "bad.txt").Which.InlineEditedPairs.Should().Be(1);
    }

    [AvaloniaFact(DisplayName = "適用に含める: 一致しないSEARCHのままでは、コマンドを直接実行してもパッチは差し替わらない")]
    public async Task 一致しないままでは差し替わらない()
    {
        await File.WriteAllTextAsync(Path.Combine(_projectDirectory, "bad.txt"), "one\ntwo\nthree\n").ConfigureAwait(true);
        var shell = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        _clipboard.Text = BuildPatch(("bad.txt", "two (AIが思い込んだ内容)", "TWO"));
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        shell.Graft.SelectedBlock = shell.Graft.Blocks.Single();
        var edit = shell.Graft.Diff.InlineEdits.Should().ContainSingle().Subject;
        var planBefore = shell.Graft.Blocks.Single().Plan;

        edit.SearchText = "まだ違う内容";
        _ui.FireLastDebounce();
        edit.AdoptCommand.CanExecute(null).Should().BeFalse();
        await ExecuteAsync(edit.AdoptCommand).ConfigureAwait(true);

        shell.Graft.Blocks.Single().Plan.Should().BeSameAs(planBefore, "ドライランはやり直されていない");
        shell.Graft.Blocks.Single().IsError.Should().BeTrue();
        shell.Graft.ApplyCommand.CanExecute(null).Should().BeFalse("適用できるブロックは増えていない");
    }

    [AvaloniaFact(DisplayName = "適用に含める: 実際のドライランでも通らなかった場合（既に別の経路で状態が変わった等）は理由を伝える")]
    public async Task 差し替え後も失敗なら理由を伝える()
    {
        var target = Path.Combine(_projectDirectory, "bad.txt");
        await File.WriteAllTextAsync(target, "one\ntwo\nthree\n").ConfigureAwait(true);
        var shell = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        _clipboard.Text = BuildPatch(("bad.txt", "two (AIが思い込んだ内容)", "TWO"));
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        shell.Graft.SelectedBlock = shell.Graft.Blocks.Single();
        var edit = shell.Graft.Diff.InlineEdits.Single();
        edit.SearchText = "two";
        _ui.FireLastDebounce();
        edit.CanAdopt.Should().BeTrue();

        // 画面で一致を確認した後、押すまでの間にファイルの中身が変わった（他のツールで編集された等）。
        await File.WriteAllTextAsync(target, "one\nTWO-changed\nthree\n").ConfigureAwait(true);
        await ExecuteAsync(edit.AdoptCommand).ConfigureAwait(true);

        shell.Graft.Blocks.Single().IsError.Should().BeTrue("ドライランをやり直した結果、実際のファイルには一致しなかった");
        _dialogs.Messages.Should().ContainSingle(m => m.Title == "まだ適用できません");
    }

    // ------------------------------------------------------------------
    // 部品
    // ------------------------------------------------------------------

    private async Task<ShellViewModel> OpenShellAsync()
    {
        var appPaths = new AppPaths(_appDirectory);
        appPaths.EnsureCoreDirectoriesExist();
        var settings = new Settings { ShowPreview = false };
        await new SettingsStore(appPaths).SaveAsync(settings).ConfigureAwait(true);

        var shell = StartupCoordinator.BuildShellViewModel(
            appPaths, settings, new SettingsStore(appPaths), new Graft.Features.PatchQueue(appPaths),
            new Graft.Features.ProjectStore(appPaths), new RevisionStore(appPaths), new RevisionRestorer(appPaths),
            _dialogs, _ui, openSettings: () => { });

        await shell.Graft.InitializeAsync().ConfigureAwait(true);
        return shell;
    }

    private static string BuildPatch(params (string Path, string Search, string Replace)[] blocks)
        => "<<<< PATCH\nsummary: test\ntype: fix\n>>>>\n\n" + string.Concat(blocks.Select(b =>
            "<<<< FILE: " + b.Path + "\n<<<<<<< SEARCH\n" + b.Search + "\n=======\n" + b.Replace + "\n>>>>>>> REPLACE\n\n"));

    private static async Task ExecuteAsync(System.Windows.Input.ICommand command)
    {
        command.Execute(null);
        if (command is AsyncRelayCommand async)
        {
            while (async.IsExecuting) await Task.Delay(10).ConfigureAwait(true);
        }
    }

    private sealed class CountingDialogService : IDialogService
    {
        public List<(string Title, string Message)> Messages { get; } = new();
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);
        public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel) => Task.FromResult<bool?>(true);
        public Task<string?> PromptAsync(string title, string message, string? initial = null) => Task.FromResult<string?>(initial ?? "test");
        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
        public Task<string?> PickFileAsync(string title, IReadOnlyList<string>? extensions = null) => Task.FromResult<string?>(null);
        public Task<string?> SaveFileAsync(string title, string suggestedFileName, IReadOnlyList<string>? extensions = null) => Task.FromResult<string?>(null);
        public Task ShowMessageAsync(string title, string message)
        {
            Messages.Add((title, message));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeClipboard : IClipboardAccess
    {
        public string? Text { get; set; }
        public void SetText(string text) => Text = text;
        public Task<string?> GetTextAsync() => Task.FromResult(Text);
    }

    /// <summary>手動で発火するタイマー。インライン編集の200msデバウンスを実時間待ちなしで検証する。</summary>
    private sealed class ManualTimer : IUiTimer
    {
        private readonly Action _onTick;
        private bool _running;
        public ManualTimer(Action onTick) => _onTick = onTick;
        public void Restart() => _running = true;
        public void Stop() => _running = false;
        public void Dispose() => _running = false;
        public void Fire()
        {
            if (_running) _onTick();
        }
    }

    private sealed class FakeUiServices : IUiServices
    {
        private readonly AvaloniaUiServices _inner = new();
        private readonly List<ManualTimer> _debounceTimers = new();

        public FakeUiServices(IClipboardAccess clipboard) { Clipboard = clipboard; }
        public IClipboardAccess Clipboard { get; }
        public IScreenInfo Screens => _inner.Screens;

        public IUiTimer CreateTimer(TimeSpan interval, Action onTick)
        {
            // InlineEditViewModelのデバウンス（200ms）だけ手動タイマーにし、他の用途は実物のまま。
            if (interval != TimeSpan.FromMilliseconds(200)) return _inner.CreateTimer(interval, onTick);
            var timer = new ManualTimer(onTick);
            _debounceTimers.Add(timer);
            return timer;
        }

        /// <summary>直近に作られたデバウンスタイマー（＝いま画面にあるインライン編集のもの）を発火する。</summary>
        public void FireLastDebounce() => _debounceTimers[^1].Fire();
    }
}
