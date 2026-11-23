using System.Windows.Input;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests.TestSupport;

/// <summary>
/// 操作まわりの不具合修正（C1〜C15）のテストで共通に使う、ShellViewModel（と必要ならShellWindow）の
/// 組み立て一式。実際の起動と同じ依存グラフ（<see cref="StartupCoordinator.BuildShellViewModel"/>）を
/// 使い、クリップボードとダイアログだけを差し替える。ダイアログは応答を台本で指定でき、
/// 出た確認・メッセージの文言を後から検証できる。
/// </summary>
public sealed class ShellHarness : IDisposable
{
    private readonly ShownWindowTracker _windows = new();

    private ShellHarness(string root, ScriptedDialogs dialogs, FakeClipboard clipboard)
    {
        Root = root;
        AppDirectory = Path.Combine(root, "app");
        ProjectDirectory = Path.Combine(root, "project");
        Directory.CreateDirectory(AppDirectory);
        Directory.CreateDirectory(ProjectDirectory);
        Dialogs = dialogs;
        Clipboard = clipboard;
    }

    public string Root { get; }
    public string AppDirectory { get; }
    public string ProjectDirectory { get; }
    public ScriptedDialogs Dialogs { get; }
    public FakeClipboard Clipboard { get; }
    public ShellViewModel Shell { get; private set; } = null!;
    public ShellWindow? Window { get; private set; }
    public AppPaths AppPaths { get; private set; } = null!;

    /// <summary>設定を開く要求の回数。</summary>
    public int OpenSettingsCount { get; private set; }

    public static async Task<ShellHarness> CreateAsync(Settings? settings = null, bool withWindow = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "graft-ops", Guid.NewGuid().ToString("N"));
        var harness = new ShellHarness(root, new ScriptedDialogs(), new FakeClipboard());
        await harness.StartAsync(settings ?? new Settings { ShowPreview = false }, withWindow).ConfigureAwait(true);
        return harness;
    }

    private async Task StartAsync(Settings settings, bool withWindow)
    {
        AppPaths = new AppPaths(AppDirectory);
        AppPaths.EnsureCoreDirectoriesExist();
        await new SettingsStore(AppPaths).SaveAsync(settings).ConfigureAwait(true);

        Shell = StartupCoordinator.BuildShellViewModel(
            AppPaths,
            settings,
            new SettingsStore(AppPaths),
            new PatchQueue(AppPaths),
            new ProjectStore(AppPaths),
            new RevisionStore(AppPaths),
            new RevisionRestorer(AppPaths),
            Dialogs,
            new FakeUiServices(Clipboard),
            openSettings: () => OpenSettingsCount++);

        // リボンの同期（取込）は、紐付けたブックのファイルを読む（仕様書_ブックの情報と依頼の観点 16.4）。Excel の代役
        // （FakeVbaBridge）が開いている「ブック」には実ファイルが無く、そのまま読むと「ブックのファイルを読めない」の警告が、
        // リボンと関係のないテストの取込の結果に混ざる。既定は「リボンの設定が無いブック」にし、リボンを確かめるテストは
        // VbaP3Rig.RibbonFile で一時ファイルの zip を指す。
        Shell.Graft.VbaRibbonPackageReader = _ => new VbaRibbonReadResult(
            VbaRibbonReadStatus.NoRibbon, null, Array.Empty<VbaRibbonRawPart>());

        if (withWindow)
        {
            // ShellWindow.OnLoadedが InitializeAsync を呼ぶため、ここでは呼ばない（二重初期化の回避）。
            Window = _windows.Track(new ShellWindow(Shell) { Width = 1280, Height = 800 });
            Window.Show();
            ShellWindowLoadWaiter.WaitForLayoutApplied(Window);
        }
        else
        {
            await Shell.Graft.InitializeAsync().ConfigureAwait(true);
        }
    }

    /// <summary>プロジェクトフォルダの中のパス。</summary>
    public string ProjectPath(string relative) => Path.Combine(ProjectDirectory, relative.Replace('/', Path.DirectorySeparatorChar));

    public Task RegisterProjectAsync() => Shell.Graft.ProjectPane.RegisterFolderAsync(ProjectDirectory);

    /// <summary>SEARCH/REPLACE形式のパッチ本文（仕様書4.1）。</summary>
    public static string BuildPatch(string relativePath, string search, string replace)
        => $"""
            <<<< FILE: {relativePath}
            summary: テスト用の変更
            <<<<<<< SEARCH
            {search}
            =======
            {replace}
            >>>>>>> REPLACE
            >>>> END

            """;

    /// <summary>クリップボードにパッチを置いて解析する（ドライランまで進む）。</summary>
    public async Task ParseAsync(string relativePath, string search, string replace)
    {
        Clipboard.Text = BuildPatch(relativePath, search, replace);
        await ExecuteAsync(Shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
    }

    /// <summary>コマンドを実行し、非同期なら完了まで待つ。</summary>
    public static async Task ExecuteAsync(ICommand command)
    {
        command.Execute(null);
        if (command is AsyncRelayCommand async)
        {
            while (async.IsExecuting) await Task.Delay(10).ConfigureAwait(true);
        }
        // fire-and-forgetの続き（async voidのイベント購読者など）が落ち着くまで少し譲る。
        await Task.Delay(20).ConfigureAwait(true);
    }

    /// <summary>条件が成り立つまで待つ（最大5秒）。</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var waited = 0;
        while (!condition() && waited < timeoutMs)
        {
            await Task.Delay(10).ConfigureAwait(true);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            waited += 10;
        }
    }

    public void Dispose()
    {
        _windows.Dispose();
        // ファイル監視（inotifyのインスタンス）を解放する。解放しないと、このハーネスを使うテストが
        // 積み重なって上限（既定128）に達し、無関係なテストの監視開始が失敗する。
        Shell.Dispose();
        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // 後始末の失敗は検証結果に影響しない。
        }
    }
}

/// <summary>テストから内容を差し替えられるクリップボード。</summary>
public sealed class FakeClipboard : IClipboardAccess
{
    public string? Text { get; set; }

    public void SetText(string text) => Text = text;

    public Task<string?> GetTextAsync() => Task.FromResult(Text);
}

/// <summary>クリップボードだけ差し替えたUI機能一式。画面情報とタイマーは本物を使う。</summary>
public sealed class FakeUiServices : IUiServices
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

/// <summary>
/// 応答を台本で指定できるダイアログ。既定はすべて承諾。出た確認・メッセージを記録する。
/// </summary>
public sealed class ScriptedDialogs : IDialogService
{
    /// <summary>ConfirmAsyncの応答。titleを渡して決める。</summary>
    public Func<string, string, bool> ConfirmAnswer { get; set; } = (_, _) => true;

    /// <summary>ConfirmThreeWayAsyncの応答（true=はい／false=いいえ／null=キャンセル）。</summary>
    public Func<string, string, bool?> ThreeWayAnswer { get; set; } = (_, _) => true;

    /// <summary>
    /// ConfirmAsyncの応答を、完了のタイミングごと台本にしたいとき用（nullを返すと<see cref="ConfirmAnswer"/>に従う）。
    /// 適用の途中でダイアログを止めて、その間の挙動を確かめるのに使う。
    /// </summary>
    public Func<string, string, Task<bool>?>? ConfirmAnswerAsync { get; set; }

    public List<(string Title, string Message)> Confirms { get; } = new();
    public List<(string Title, string Message)> ThreeWays { get; } = new();
    public List<(string Title, string Message)> Messages { get; } = new();

    public Task<bool> ConfirmAsync(string title, string message)
    {
        Confirms.Add((title, message));
        return ConfirmAnswerAsync?.Invoke(title, message) ?? Task.FromResult(ConfirmAnswer(title, message));
    }

    public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel)
    {
        ThreeWays.Add((title, message));
        return Task.FromResult(ThreeWayAnswer(title, message));
    }

    /// <summary>PromptAsyncの応答（title, message, initial）。未指定なら初期値（無ければ「テスト」）。</summary>
    public Func<string, string, string?, string?>? PromptAnswer { get; set; }

    public Task<string?> PromptAsync(string title, string message, string? initial = null)
        => Task.FromResult(PromptAnswer is null ? initial ?? "テスト" : PromptAnswer(title, message, initial));

    /// <summary>PickFolderAsyncの応答（title）。未指定ならキャンセル（null）。</summary>
    public Func<string, string?>? PickFolderAnswer { get; set; }

    public Task<string?> PickFolderAsync(string title) => Task.FromResult(PickFolderAnswer?.Invoke(title));

    public Task<string?> PickFileAsync(string title, IReadOnlyList<string>? extensions = null) => Task.FromResult<string?>(null);

    public Task<string?> SaveFileAsync(string title, string suggestedFileName, IReadOnlyList<string>? extensions = null)
        => Task.FromResult<string?>(null);

    public Task ShowMessageAsync(string title, string message)
    {
        Messages.Add((title, message));
        return Task.CompletedTask;
    }
}
