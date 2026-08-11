using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Core;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;
using Graft.Platform.Null;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests;

/// <summary>
/// 不具合の回帰テスト: 「適用後にファイルを編集していると復元が必ず失敗する」。
///
/// RevisionRestorer.RestoreAsync/RestoreThroughAsyncは、適用後にファイルがさらに変更されて
/// いた場合、E301（Severity.Warning）を issues に積んで失敗を返す。呼び出し側
/// （HistoryPaneViewModel）は本来ここで「上書きして続行しますか？」と確認し、forceで
/// 再試行する設計だったが、その分岐が <c>result.Errors.Any(i =&gt; i.Code == ErrorCode.E301)</c>
/// と誤って書かれていた。GraftResult&lt;T&gt;.ErrorsはSeverity.Errorのみを対象とするため、
/// Warningとして発行されるE301はここで検出できず、確認ダイアログへ一度も到達しないまま
/// 復元が失敗して終わっていた（適用→手直し→やっぱり戻したい、という日常的な流れが
/// 復元できなかった）。単発復元・「ここまで戻す」の両方に同じ誤りがあった。
///
/// 本テストは、この確認ダイアログが実際に呼ばれること・「はい」で続行すると適用前の内容へ
/// 正しく戻ること・「いいえ」で中止するとファイルが一切変更されないことを、実際に
/// HistoryPaneViewModelのコマンドを通して検証する。分岐が再び死んでいれば、この確認
/// ダイアログ自体が呼ばれなくなり（ConfirmMessagesに現れず）、いずれのテストも失敗する。
/// </summary>
public class RestoreAppliedAfterChangeConfirmTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-restore-force-confirm", Guid.NewGuid().ToString("N"));

    private readonly string _appDirectory;
    private readonly string _projectDirectory;
    private readonly FakeClipboard _clipboard = new();

    public RestoreAppliedAfterChangeConfirmTests()
    {
        _appDirectory = Path.Combine(_root, "app");
        _projectDirectory = Path.Combine(_root, "project");
        Directory.CreateDirectory(_appDirectory);
        Directory.CreateDirectory(_projectDirectory);
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

    private const string ConfirmTitle = "適用後の変更を検出";

    // ------------------------------------------------------------------
    // 単発復元（RestoreCommand）
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "単発復元: 適用後に手で編集していると「適用後の変更を検出」の確認ダイアログが出る")]
    public async Task 単発復元_適用後の変更で確認ダイアログが出る()
    {
        var target = Path.Combine(_projectDirectory, "sample.txt");
        var dialogs = new SelectiveDialogService();
        var shell = await OpenShellAsync(dialogs).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        await ApplyFullAsync(shell, "sample.txt", "v1").ConfigureAwait(true); // r1
        await ApplyFullAsync(shell, "sample.txt", "v2").ConfigureAwait(true); // r2

        // 適用後にファイルを手で編集する（=E301が警告として発行される状況を再現）。
        await File.WriteAllTextAsync(target, "手で書き換えた内容\n").ConfigureAwait(true);

        var history = shell.Graft.History;
        history.SelectedItem = history.Items.Single(i => i.RevisionLabel == "r2");
        await ExecuteAsync(history.RestoreCommand).ConfigureAwait(true);

        dialogs.ConfirmMessages.Should().Contain(m => m.Title == ConfirmTitle,
            "E301はSeverity.Warningで発行されるため、Errorsではなくissues全体を見て初めてこの分岐へ到達できる");
    }

    [AvaloniaFact(DisplayName = "単発復元: 確認ダイアログで「はい」を選ぶと、適用前の内容へ正しく上書きされる")]
    public async Task 単発復元_はいで続行すると適用前の内容に戻る()
    {
        var target = Path.Combine(_projectDirectory, "sample.txt");
        var dialogs = new SelectiveDialogService();
        var shell = await OpenShellAsync(dialogs).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        await ApplyFullAsync(shell, "sample.txt", "v1").ConfigureAwait(true); // r1
        await ApplyFullAsync(shell, "sample.txt", "v2").ConfigureAwait(true); // r2
        await File.WriteAllTextAsync(target, "手で書き換えた内容\n").ConfigureAwait(true);

        var history = shell.Graft.History;
        history.SelectedItem = history.Items.Single(i => i.RevisionLabel == "r2");
        await ExecuteAsync(history.RestoreCommand).ConfigureAwait(true);

        var content = await File.ReadAllTextAsync(target).ConfigureAwait(true);
        content.Should().Contain("v1").And.NotContain("v2").And.NotContain("手で書き換えた内容");

        // 内容だけでなく、r1適用直後のhashAfterとハッシュでも厳密に一致することを確認する。
        var hashNow = FileTextIO.ComputeHash(content);
        var r1 = await new RevisionStore(new AppPaths(_appDirectory)).ReadAsync(
            shell.Graft.ProjectPane.SelectedItem!.Project.Id, 1).ConfigureAwait(true);
        hashNow.Should().Be(r1.Value.Manifest.Entries.Single(e => e.Path == "sample.txt").HashAfter,
            "r1適用直後のhashAfterと完全に一致するはず");
    }

    [AvaloniaFact(DisplayName = "単発復元: 確認ダイアログで「いいえ」を選ぶと、ファイルは一切変更されない")]
    public async Task 単発復元_いいえで中止するとファイルは変わらない()
    {
        var target = Path.Combine(_projectDirectory, "sample.txt");
        var dialogs = new SelectiveDialogService { ConfirmResponder = title => title != ConfirmTitle };
        var shell = await OpenShellAsync(dialogs).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        await ApplyFullAsync(shell, "sample.txt", "v1").ConfigureAwait(true); // r1
        await ApplyFullAsync(shell, "sample.txt", "v2").ConfigureAwait(true); // r2
        await File.WriteAllTextAsync(target, "手で書き換えた内容\n").ConfigureAwait(true);

        var history = shell.Graft.History;
        history.SelectedItem = history.Items.Single(i => i.RevisionLabel == "r2");
        await ExecuteAsync(history.RestoreCommand).ConfigureAwait(true);

        dialogs.ConfirmMessages.Should().Contain(m => m.Title == ConfirmTitle, "中止する前提でも確認自体は出るはず");
        var content = await File.ReadAllTextAsync(target).ConfigureAwait(true);
        content.Should().Be("手で書き換えた内容\n", "「いいえ」を選んだのでファイルへは一切触れないはず");
    }

    // ------------------------------------------------------------------
    // 「ここまで戻す」（RestoreThroughCommand）
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "ここまで戻す: 起点リビジョン適用後に手で編集していると「適用後の変更を検出」の確認ダイアログが出る")]
    public async Task ここまで戻す_適用後の変更で確認ダイアログが出る()
    {
        var target = Path.Combine(_projectDirectory, "sample.txt");
        var dialogs = new SelectiveDialogService();
        var shell = await OpenShellAsync(dialogs).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        await ApplyFullAsync(shell, "sample.txt", "v1").ConfigureAwait(true); // r1
        await ApplyFullAsync(shell, "sample.txt", "v2").ConfigureAwait(true); // r2
        await File.WriteAllTextAsync(target, "手で書き換えた内容\n").ConfigureAwait(true);

        var history = shell.Graft.History;
        history.SelectedItem = history.Items.Single(i => i.RevisionLabel == "r1");
        await ExecuteAsync(history.RestoreThroughCommand).ConfigureAwait(true);

        dialogs.ConfirmMessages.Should().Contain(m => m.Title == ConfirmTitle,
            "E301はSeverity.Warningで発行されるため、Errorsではなくissues全体を見て初めてこの分岐へ到達できる");
    }

    [AvaloniaFact(DisplayName = "ここまで戻す: 確認ダイアログで「はい」を選ぶと、選択リビジョン適用直後の内容へ正しく戻る")]
    public async Task ここまで戻す_はいで続行すると選択リビジョン直後に戻る()
    {
        var target = Path.Combine(_projectDirectory, "sample.txt");
        var dialogs = new SelectiveDialogService();
        var shell = await OpenShellAsync(dialogs).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        await ApplyFullAsync(shell, "sample.txt", "v1").ConfigureAwait(true); // r1
        await ApplyFullAsync(shell, "sample.txt", "v2").ConfigureAwait(true); // r2
        await File.WriteAllTextAsync(target, "手で書き換えた内容\n").ConfigureAwait(true);

        var history = shell.Graft.History;
        history.SelectedItem = history.Items.Single(i => i.RevisionLabel == "r1");
        await ExecuteAsync(history.RestoreThroughCommand).ConfigureAwait(true);

        var content = await File.ReadAllTextAsync(target).ConfigureAwait(true);
        content.Should().Contain("v1").And.NotContain("v2").And.NotContain("手で書き換えた内容");
    }

    [AvaloniaFact(DisplayName = "ここまで戻す: 確認ダイアログで「いいえ」を選ぶと、ファイルは一切変更されない")]
    public async Task ここまで戻す_いいえで中止するとファイルは変わらない()
    {
        var target = Path.Combine(_projectDirectory, "sample.txt");
        var dialogs = new SelectiveDialogService { ConfirmResponder = title => title != ConfirmTitle };
        var shell = await OpenShellAsync(dialogs).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        await ApplyFullAsync(shell, "sample.txt", "v1").ConfigureAwait(true); // r1
        await ApplyFullAsync(shell, "sample.txt", "v2").ConfigureAwait(true); // r2
        await File.WriteAllTextAsync(target, "手で書き換えた内容\n").ConfigureAwait(true);

        var history = shell.Graft.History;
        history.SelectedItem = history.Items.Single(i => i.RevisionLabel == "r1");
        await ExecuteAsync(history.RestoreThroughCommand).ConfigureAwait(true);

        dialogs.ConfirmMessages.Should().Contain(m => m.Title == ConfirmTitle, "中止する前提でも確認自体は出るはず");
        var content = await File.ReadAllTextAsync(target).ConfigureAwait(true);
        content.Should().Be("手で書き換えた内容\n", "「いいえ」を選んだのでファイルへは一切触れないはず");
    }

    // ------------------------------------------------------------------
    // ヘルパ
    // ------------------------------------------------------------------

    private async Task<ShellViewModel> OpenShellAsync(IDialogService dialogs)
    {
        var appPaths = new AppPaths(_appDirectory);
        appPaths.EnsureCoreDirectoriesExist();

        var settings = new Settings { ShowPreview = false };
        await new SettingsStore(appPaths).SaveAsync(settings).ConfigureAwait(true);

        var shell = StartupCoordinator.BuildShellViewModel(
            appPaths,
            settings,
            new SettingsStore(appPaths),
            new PatchQueue(appPaths),
            new ProjectStore(appPaths),
            new RevisionStore(appPaths),
            new RevisionRestorer(appPaths),
            dialogs,
            new FakeUiServices(_clipboard),
            openSettings: () => { });

        await shell.Graft.InitializeAsync().ConfigureAwait(true);
        return shell;
    }

    /// <summary>
    /// FULL形式のパッチを貼り付け→解析→適用まで実際に通す
    /// （HistoryRestoreThroughScenarioTests.ApplyFullAsyncと同じ役割）。
    /// </summary>
    private async Task ApplyFullAsync(ShellViewModel shell, string relativePath, string content)
    {
        _clipboard.Text = $"<<<< FILE: {relativePath} MODE=FULL\n{content}\n>>>> END\n";
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        await ExecuteAsync(shell.Graft.ApplyCommand).ConfigureAwait(true);
    }

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
    /// ConfirmAsyncの呼び出しを記録しつつ、タイトルごとに応答を切り替えられるダイアログ。
    /// 既定はすべて「はい」（承諾）。ConfirmResponderで特定のタイトルだけ「いいえ」にできる。
    /// </summary>
    private sealed class SelectiveDialogService : IDialogService
    {
        public List<(string Title, string Message)> ConfirmMessages { get; } = new();

        public Func<string, bool> ConfirmResponder { get; set; } = _ => true;

        public Task<bool> ConfirmAsync(string title, string message)
        {
            ConfirmMessages.Add((title, message));
            return Task.FromResult(ConfirmResponder(title));
        }

        public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel)
            => Task.FromResult<bool?>(true);

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
