using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Core;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests;

/// <summary>
/// 課題1「プロジェクト自動判定が呼ばれていない」の通しシナリオ（画面あり）。
/// <see cref="ProjectMatcher"/>自体は実装済み・単体テスト済みだったが、パッチ解析後の
/// フロー（MainViewModel.ParseTextAndLoadAsync → RunDryRunAsync）から一度も呼ばれておらず、
/// 選択中と無関係なプロジェクトのパッチでも無警告で適用できてしまっていた不具合の回帰テスト。
/// 仕様書v2.0 3.3の3段階（90%以上=自動選択／50〜90%=要確認／50%未満=ブロック）と、
/// 「別プロジェクトへ切り替わる場合は必ず確認を挟む」「誤検知しない（選択中と一致するパッチは
/// 無警告）」「登録プロジェクトが1つだけなら判定自体をスキップする」を検証する。
/// </summary>
public class ProjectMatchScenarioTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-projectmatch", Guid.NewGuid().ToString("N"));

    private readonly string _appDirectory;
    private readonly string _projectADirectory;
    private readonly string _projectBDirectory;
    private readonly FakeClipboard _clipboard = new();

    public ProjectMatchScenarioTests()
    {
        _appDirectory = Path.Combine(_root, "app");
        _projectADirectory = Path.Combine(_root, "projectA");
        _projectBDirectory = Path.Combine(_root, "projectB");
        Directory.CreateDirectory(_appDirectory);
        Directory.CreateDirectory(_projectADirectory);
        Directory.CreateDirectory(_projectBDirectory);
    }

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

    [AvaloniaFact(DisplayName = "選択中プロジェクトと一致するパッチは確認ダイアログなしで解析される（誤検知しない）")]
    public async Task 選択中と一致するパッチは警告なく解析される()
    {
        File.WriteAllText(Path.Combine(_projectADirectory, "a.py"), "x=1\n");
        File.WriteAllText(Path.Combine(_projectBDirectory, "b.py"), "x=1\n");

        var dialogs = new ThrowIfConfirmedDialogService();
        var (shell, _) = await OpenShellAsync(dialogs).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectADirectory).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectBDirectory).ConfigureAwait(true);
        SelectProject(shell, _projectADirectory);

        _clipboard.Text = BuildDeletePatch("a.py");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);

        shell.Graft.CenterError.Should().BeNull();
        shell.Graft.State.Should().Be(CenterPaneState.Content);
        shell.Graft.Blocks.Should().ContainSingle();
        shell.Graft.ProjectPane.SelectedItem!.Project.Root.Should().Be(_projectADirectory,
            "一致するプロジェクトが既に選択されているため切り替わってはいけない");
    }

    [AvaloniaFact(DisplayName = "一致率90%以上で別プロジェクトが対象なら、確認のうえプロジェクトが切り替わる")]
    public async Task 別プロジェクトへの高一致は確認のうえ切り替わる()
    {
        File.WriteAllText(Path.Combine(_projectADirectory, "a.py"), "x=1\n");
        File.WriteAllText(Path.Combine(_projectBDirectory, "b.py"), "x=1\n");

        var dialogs = new RecordingDialogService { ThreeWayResult = true };
        var (shell, _) = await OpenShellAsync(dialogs).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectADirectory).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectBDirectory).ConfigureAwait(true);
        SelectProject(shell, _projectADirectory);

        // b.pyだけを参照するパッチ → Aには存在せず(0%)、Bには100%一致する。
        _clipboard.Text = BuildDeletePatch("b.py");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);

        dialogs.ThreeWayCallCount.Should().Be(1, "別プロジェクトへ切り替える前に必ず確認を挟むはず");
        shell.Graft.ProjectPane.SelectedItem!.Project.Root.Should().Be(_projectBDirectory,
            "確認で「切り替える」を選んだのでプロジェクトBへ切り替わっているはず");
        shell.Graft.Blocks.Should().ContainSingle("切り替え後のプロジェクトBに対してドライランが行われているはず");
    }

    [AvaloniaFact(DisplayName = "切替確認で「現在のプロジェクトのまま続行」を選ぶとプロジェクトは切り替わらない")]
    public async Task 切替確認を断ると現在のプロジェクトのまま続行する()
    {
        File.WriteAllText(Path.Combine(_projectADirectory, "a.py"), "x=1\n");
        File.WriteAllText(Path.Combine(_projectBDirectory, "b.py"), "x=1\n");

        var dialogs = new RecordingDialogService { ThreeWayResult = false };
        var (shell, _) = await OpenShellAsync(dialogs).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectADirectory).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectBDirectory).ConfigureAwait(true);
        SelectProject(shell, _projectADirectory);

        _clipboard.Text = BuildDeletePatch("b.py");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);

        dialogs.ThreeWayCallCount.Should().Be(1);
        shell.Graft.ProjectPane.SelectedItem!.Project.Root.Should().Be(_projectADirectory,
            "「現在のプロジェクトのまま続行する」を選んだので切り替わってはいけない");
    }

    [AvaloniaFact(DisplayName = "一致率50〜90%（要確認）でも、別プロジェクトが対象なら確認ダイアログが出る")]
    public async Task 要確認レンジでも別プロジェクトなら確認ダイアログが出る()
    {
        // Bには b1〜b3 が実在し b4 は無い(3/4=75%=要確認)。Aにはどれも無い(0%)。
        File.WriteAllText(Path.Combine(_projectADirectory, "unrelated.py"), "x=1\n");
        File.WriteAllText(Path.Combine(_projectBDirectory, "b1.py"), "x=1\n");
        File.WriteAllText(Path.Combine(_projectBDirectory, "b2.py"), "x=1\n");
        File.WriteAllText(Path.Combine(_projectBDirectory, "b3.py"), "x=1\n");

        var dialogs = new RecordingDialogService { ThreeWayResult = true };
        var (shell, _) = await OpenShellAsync(dialogs).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectADirectory).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectBDirectory).ConfigureAwait(true);
        SelectProject(shell, _projectADirectory);

        _clipboard.Text = BuildDeletePatch("b1.py", "b2.py", "b3.py", "b4.py");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);

        dialogs.ThreeWayCallCount.Should().Be(1);
        dialogs.LastThreeWayMessage.Should().Contain("確実には判定できません", "要確認レンジ特有の文言で候補を提示するはず");
        shell.Graft.ProjectPane.SelectedItem!.Project.Root.Should().Be(_projectBDirectory);
    }

    [AvaloniaFact(DisplayName = "一致率50%未満（どのプロジェクトも）なら適用をブロックしE303を返す。確認ダイアログは出さない")]
    public async Task 一致率50パーセント未満はブロックされる()
    {
        File.WriteAllText(Path.Combine(_projectADirectory, "a.py"), "x=1\n");
        File.WriteAllText(Path.Combine(_projectBDirectory, "b.py"), "x=1\n");

        // どのプロジェクトにも存在しないパスばかりのパッチ → 両方0%でブロック。
        var dialogs = new ThrowIfConfirmedDialogService();
        var (shell, _) = await OpenShellAsync(dialogs).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectADirectory).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectBDirectory).ConfigureAwait(true);
        SelectProject(shell, _projectADirectory);

        _clipboard.Text = BuildDeletePatch("c1.py", "c2.py", "c3.py", "c4.py");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);

        shell.Graft.State.Should().Be(CenterPaneState.Error);
        shell.Graft.CenterError!.Code.Should().Be(ErrorCode.E303);
        shell.Graft.Blocks.Should().BeEmpty("ブロック時は解析結果ごと破棄されるはず");
        shell.Graft.ProjectPane.SelectedItem!.Project.Root.Should().Be(_projectADirectory,
            "ブロック時は切り替えを提案しない（確認ダイアログを出さない設計のため）");
    }

    [AvaloniaFact(DisplayName = "全プロジェクトが同率0%でも、たまたま選択中が一覧の先頭に来た場合はブロックされる（一致率タイの罠）")]
    public async Task 全滅タイで選択中がたまたま先頭でもブロックされる()
    {
        // 実機のXvfb確認で発見した不具合の回帰テスト: 一致率でOrderByDescendingした際、
        // 全プロジェクトが同率（ここでは0%）で並ぶと安定ソートにより「一覧の並び順が早い」
        // プロジェクトがBestになる。選択中のプロジェクトがその「たまたま先頭」だった場合に
        // 誤って無警告で通ってしまわないことを確認する。ProjectPaneの並び順は
        // ピン留め→最終使用日時降順のため、Bを先に登録してAを後に登録すると
        // （Aの方が新しいため）Aが先頭に来る。Aを選択した状態で検証する。
        File.WriteAllText(Path.Combine(_projectADirectory, "a.py"), "x=1\n");
        File.WriteAllText(Path.Combine(_projectBDirectory, "b.py"), "x=1\n");

        var dialogs = new ThrowIfConfirmedDialogService();
        var (shell, _) = await OpenShellAsync(dialogs).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectBDirectory).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectADirectory).ConfigureAwait(true);
        SelectProject(shell, _projectADirectory); // Aは最後に登録したため一覧の先頭に来ているはず。
        shell.Graft.ProjectPane.Items.First().Project.Root.Should().Be(_projectADirectory,
            "この回帰テストが再現するには選択中のプロジェクトが一覧の先頭に来ている必要がある");

        _clipboard.Text = BuildDeletePatch("c1.py", "c2.py", "c3.py", "c4.py");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);

        shell.Graft.State.Should().Be(CenterPaneState.Error, "全プロジェクト0%一致なのでブロックされるべき");
        shell.Graft.CenterError!.Code.Should().Be(ErrorCode.E303);
    }

    [AvaloniaFact(DisplayName = "新規ファイルのみのパッチは、無関係なプロジェクトが選択されていてもブロックされず解析できる")]
    public async Task 新規ファイルのみのパッチは無関係なプロジェクトでもブロックされない()
    {
        // 課題1の回帰テスト: 新規作成前提のパスは一致率算出の分母から除かれる。除外の結果
        // 判定対象が1件も残らない（＝完全に新規ファイルのみ）場合は、選択中のプロジェクトが
        // パッチの内容と無関係でも、判定自体をスキップして無警告で解析が進むはず。
        File.WriteAllText(Path.Combine(_projectADirectory, "a.py"), "x=1\n");
        File.WriteAllText(Path.Combine(_projectBDirectory, "b.py"), "x=1\n");

        var dialogs = new ThrowIfConfirmedDialogService();
        var (shell, _) = await OpenShellAsync(dialogs).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectADirectory).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectBDirectory).ConfigureAwait(true);
        SelectProject(shell, _projectADirectory);

        // AにもBにも存在しない新規ファイルのみを作成するパッチ。
        _clipboard.Text = BuildFullFilePatch("brandnew/feature.py", "print('new')");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);

        shell.Graft.CenterError.Should().BeNull("新規ファイルのみのパッチはプロジェクトが無関係でもブロックしてはいけない");
        shell.Graft.State.Should().Be(CenterPaneState.Content);
        shell.Graft.Blocks.Should().ContainSingle();
        shell.Graft.ProjectPane.SelectedItem!.Project.Root.Should().Be(_projectADirectory,
            "判定をスキップするだけで、選択中プロジェクトを勝手に切り替えてはいけない");
    }

    [AvaloniaFact(DisplayName = "新規ファイル作成と既存ファイル変更が混在するパッチでは、既存ファイル分だけで一致率が判定される")]
    public async Task 新規と既存が混在するパッチは既存ファイル分だけで判定される()
    {
        // 課題1の回帰テスト: 新規ファイルは分母から除かれるが、既存ファイルの変更が1件でも
        // 混ざっていれば、その既存ファイル分でこれまでどおりの一致率判定・切替確認が働くはず。
        File.WriteAllText(Path.Combine(_projectADirectory, "unrelated.py"), "x=1\n");
        File.WriteAllText(Path.Combine(_projectBDirectory, "b.py"), "x=1\n");

        var dialogs = new RecordingDialogService { ThreeWayResult = true };
        var (shell, _) = await OpenShellAsync(dialogs).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectADirectory).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectBDirectory).ConfigureAwait(true);
        SelectProject(shell, _projectADirectory);

        // brandnew.pyはどちらにも存在しない新規ファイル（分母から除外されるはず）。
        // b.pyはBにのみ存在する既存ファイルのDELETE（除外されず判定対象に残るはず）。
        _clipboard.Text = BuildFullFilePatch("brandnew.py", "print('new')") + BuildDeletePatch("b.py");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);

        dialogs.ThreeWayCallCount.Should().Be(1,
            "新規ファイルを除いた既存ファイル分（b.py）だけで判定すればBへの一致率100%になり、切替確認が働くはず");
        shell.Graft.ProjectPane.SelectedItem!.Project.Root.Should().Be(_projectBDirectory,
            "確認で「切り替える」を選んだのでプロジェクトBへ切り替わっているはず");
    }

    [AvaloniaFact(DisplayName = "新規ファイル作成が混ざっていても、既存ファイル参照が全滅（0%）ならブロックされる（タイの穴の再発防止）")]
    public async Task 新規ファイルが混ざっていても既存ファイル参照が全滅ならブロックされる()
    {
        // 課題1の回帰テスト: 新規ファイル分の除外ロジックを入れたことで、既存ファイル参照が
        // どのプロジェクトにも一致しない（全滅）パッチまで無警告で通ってしまわないこと、
        // かつ「全滅タイで選択中がたまたま先頭に来る」穴（前ラウンドで塞いだもの）が
        // 除外ロジック追加後も再発していないことを確認する。
        File.WriteAllText(Path.Combine(_projectADirectory, "a.py"), "x=1\n");
        File.WriteAllText(Path.Combine(_projectBDirectory, "b.py"), "x=1\n");

        var dialogs = new ThrowIfConfirmedDialogService();
        var (shell, _) = await OpenShellAsync(dialogs).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectADirectory).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectBDirectory).ConfigureAwait(true);
        SelectProject(shell, _projectADirectory);

        // brandnew.pyはどちらにも存在しない新規ファイル（分母から除外される）。
        // c1〜c4はどちらのプロジェクトにも存在しない（既存ファイル参照として全滅＝0%）。
        _clipboard.Text = BuildFullFilePatch("brandnew.py", "print('new')")
            + BuildDeletePatch("c1.py", "c2.py", "c3.py", "c4.py");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);

        shell.Graft.State.Should().Be(CenterPaneState.Error, "新規ファイル分を除いても既存ファイル参照が全滅なのでブロックされるべき");
        shell.Graft.CenterError!.Code.Should().Be(ErrorCode.E303);
    }

    [AvaloniaFact(DisplayName = "登録プロジェクトが1つだけの場合は判定自体をスキップし、無意味な警告は出さない")]
    public async Task プロジェクトが1つだけなら判定をスキップする()
    {
        File.WriteAllText(Path.Combine(_projectADirectory, "a.py"), "x=1\n");
        // b1〜b3はプロジェクトAには存在しない。2つ以上プロジェクトがあれば低一致率でブロックされる
        // 組み合わせだが、登録プロジェクトが1つしかないため判定自体が行われないはず。

        var dialogs = new ThrowIfConfirmedDialogService();
        var (shell, _) = await OpenShellAsync(dialogs).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectADirectory).ConfigureAwait(true);

        _clipboard.Text = BuildDeletePatch("a.py", "b1.py", "b2.py", "b3.py");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);

        shell.Graft.CenterError.Should().BeNull("判定対象が1件しかなければ一致率で警告を出す意味が無い");
        shell.Graft.State.Should().Be(CenterPaneState.Content);
        shell.Graft.Blocks.Should().HaveCount(4, "存在しないファイルのDELETEもドライラン自体は失敗せずCanApply=falseのブロックとして並ぶ");
    }

    private static void SelectProject(ShellViewModel shell, string root)
    {
        var item = shell.Graft.ProjectPane.Items.First(i => i.Project.Root == root);
        shell.Graft.ProjectPane.SelectedItem = item;
    }

    private async Task<(ShellViewModel Shell, Avalonia.Controls.Window Window)> OpenShellAsync(IDialogService dialogs)
    {
        var appPaths = new AppPaths(_appDirectory);
        appPaths.EnsureCoreDirectoriesExist();

        var shell = StartupCoordinator.BuildShellViewModel(
            appPaths,
            new Settings(),
            new SettingsStore(appPaths),
            new PatchQueue(appPaths),
            new ProjectStore(appPaths),
            new RevisionStore(appPaths),
            new RevisionRestorer(appPaths),
            dialogs,
            new FakeUiServices(_clipboard),
            openSettings: () => { });

        var window = new ShellWindow(shell) { Width = 1280, Height = 800 };
        window.Show();
        await shell.Graft.InitializeAsync().ConfigureAwait(true);
        return (shell, window);
    }

    /// <summary>DELETEブロックのみで構成するパッチ本文（3.4の一致率判定はパスの存在有無だけを見るため、
    /// SEARCH本文の内容一致を気にしなくてよいDELETEが最も単純に組み立てられる）。</summary>
    private static string BuildDeletePatch(params string[] relativePaths)
        => string.Join(Environment.NewLine, relativePaths.Select(p => $"<<<< DELETE: {p}")) + Environment.NewLine;

    /// <summary>FULL形式（新規作成前提）のパッチ本文を1件組み立てる。</summary>
    private static string BuildFullFilePatch(string relativePath, string content)
        => $"<<<< FILE: {relativePath} MODE=FULL{Environment.NewLine}{content}{Environment.NewLine}>>>> END{Environment.NewLine}";

    /// <summary>非同期コマンドを実行し、完了するまで待つ。</summary>
    private static async Task ExecuteAsync(System.Windows.Input.ICommand command)
    {
        command.Execute(null);
        if (command is AsyncRelayCommand async)
        {
            while (async.IsExecuting)
            {
                await Task.Delay(10).ConfigureAwait(true);
            }
        }
    }

    /// <summary>
    /// Confirm系ダイアログが呼ばれたら即座にテストを失敗させる。「誤検知しない」
    /// 「ブロック時は確認を挟まない」「単一プロジェクトでは判定しない」ことの検証に使う。
    /// </summary>
    private sealed class ThrowIfConfirmedDialogService : IDialogService
    {
        public Task<bool> ConfirmAsync(string title, string message)
            => throw new InvalidOperationException($"想定外の確認ダイアログが呼ばれました: {title} / {message}");

        public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel)
            => throw new InvalidOperationException($"想定外の確認ダイアログが呼ばれました: {title} / {message}");

        public Task<string?> PromptAsync(string title, string message, string? initial = null)
            => Task.FromResult<string?>(initial ?? "テスト");

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync(string title, IReadOnlyList<string>? extensions = null) => Task.FromResult<string?>(null);
        public Task<string?> SaveFileAsync(string title, string suggestedFileName, IReadOnlyList<string>? extensions = null) => Task.FromResult<string?>(null);

        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
    }

    /// <summary>ConfirmThreeWayAsyncの呼び出し回数・引数・戻り値を制御できるダイアログ。</summary>
    private sealed class RecordingDialogService : IDialogService
    {
        public bool? ThreeWayResult { get; set; } = true;

        public int ThreeWayCallCount { get; private set; }

        public string? LastThreeWayMessage { get; private set; }

        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);

        public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel)
        {
            ThreeWayCallCount++;
            LastThreeWayMessage = message;
            return Task.FromResult(ThreeWayResult);
        }

        public Task<string?> PromptAsync(string title, string message, string? initial = null)
            => Task.FromResult<string?>(initial ?? "テスト");

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync(string title, IReadOnlyList<string>? extensions = null) => Task.FromResult<string?>(null);
        public Task<string?> SaveFileAsync(string title, string suggestedFileName, IReadOnlyList<string>? extensions = null) => Task.FromResult<string?>(null);

        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
    }

    /// <summary>テストから内容を差し替えられるクリップボード。</summary>
    private sealed class FakeClipboard : IClipboardAccess
    {
        public string? Text { get; set; }

        public void SetText(string text) => Text = text;

        public Task<string?> GetTextAsync() => Task.FromResult(Text);
    }

    /// <summary>クリップボードだけ差し替えたUI機能一式。画面情報とタイマーは本物を使う。</summary>
    private sealed class FakeUiServices : IUiServices
    {
        private readonly AvaloniaUiServices _inner = new();

        public FakeUiServices(IClipboardAccess clipboard)
        {
            Clipboard = clipboard;
        }

        public IClipboardAccess Clipboard { get; }

        public IScreenInfo Screens => _inner.Screens;

        public IUiTimer CreateTimer(TimeSpan interval, Action onTick) => _inner.CreateTimer(interval, onTick);
    }
}
