using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Graft.Core;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests;

/// <summary>
/// 利用者報告「『使い方を学ぶ』終了後にProjectが消える」の追加回帰テスト。TutorialTests.cs
/// （単一の実プロジェクトでの通しシナリオ・Esc/終了ボタンでの中断）に対して、このファイルは
/// (1) 複数の実プロジェクトがある状態、(2) プロジェクトが1件も無い状態、(3) 複数プロジェクトが
/// ある状態での中断、という3パターンを追加でカバーする。
///
/// 【調査結果】実機・自動テストの両方でこの一連の操作（登録→解析→差分確認→適用→履歴確認→
/// 復元→終了、複数プロジェクトでの実行、途中でのEsc中断を含む）を通しても、実プロジェクトが
/// projects.jsonから失われる直接の再現は得られなかった（ShellWindow.Tutorial.cs・
/// ProjectStore.cs・ProjectPaneViewModel.cs自体のロジックは、プロジェクト件数に関わらず
/// 正しく動作することをこのファイルで確認している）。
///
/// 調査の過程で、原因になりうる実際の欠陥を1件特定して修正した
/// （<see cref="ProjectStoreConcurrentWriteTests"/>・StartupCoordinator.Validation.csの
/// ReconcileRevisionsAsync参照）: 起動のたびに必ずバックグラウンドで走る起動時検証
/// （StartupCoordinator.RunStartupValidationAsync）が、起動直後に読み込んだ古い
/// プロジェクト一覧のスナップショットを、画面上のチュートリアルの完了を待たずに丸ごと
/// 書き戻すことがあり、その間にチュートリアルや利用者自身が行った変更（サンプルの登録・
/// 実プロジェクトの登録等）を消してしまいうるという欠陥。この修正はここでは直接検証できない
/// （StartupCoordinator.StartAsyncの起動には多重起動防止Mutex・トレイ・グローバルホットキー
/// 等のOS資源が絡み、既存のテスト方針でもStartAsync自体はheadlessテストの対象にしていない。
/// ShutdownLoggingTests.csの該当コメント参照）ため、同じ「読んでから書くまでの時間差」パターンを
/// ProjectStoreの公開APIだけで直接再現する<see cref="ProjectStoreConcurrentWriteTests"/>
/// （tests/Graft.Tests）で検証している。
/// </summary>
public class TutorialProjectPreservationTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-tutorial-preservation-tests", Guid.NewGuid().ToString("N"));

    private readonly ShownWindowTracker _windows = new();

    public void Dispose()
    {
        _windows.Dispose();
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

    [AvaloniaFact(DisplayName = "回帰: 実プロジェクトが複数ある状態でチュートリアルを完走しても、すべて残る")]
    public async Task 実プロジェクトが複数ある状態でチュートリアルを完走してもすべて残る()
    {
        var (shell, window) = await OpenShellAsync().ConfigureAwait(true);

        var realIds = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var dir = Path.Combine(_root, $"real-{i}");
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, "real.txt"), $"実プロジェクト{i}\n").ConfigureAwait(true);
            var result = await shell.Graft.ProjectPane.RegisterFolderAsync(dir).ConfigureAwait(true);
            result.IsSuccess.Should().BeTrue();
            realIds.Add(result.Value.Id);
        }

        window.StartTutorial();
        await CompleteTutorialAsync(window).ConfigureAwait(true);

        var finalIds = shell.Graft.ProjectPane.Items.Select(i => i.Project.Id).ToList();
        finalIds.Should().HaveCount(3, "サンプルは片付き、登録した実プロジェクト3件だけが残る必要がある");
        foreach (var id in realIds)
        {
            finalIds.Should().Contain(id);
        }
    }

    [AvaloniaFact(DisplayName = "回帰: 実プロジェクトが複数ある状態でチュートリアルをEscで中断しても、すべて残りサンプルは片付く")]
    public async Task 実プロジェクトが複数ある状態でEsc中断してもすべて残る()
    {
        var (shell, window) = await OpenShellAsync().ConfigureAwait(true);

        var realIds = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var dir = Path.Combine(_root, $"real-{i}");
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, "real.txt"), $"実プロジェクト{i}\n").ConfigureAwait(true);
            var result = await shell.Graft.ProjectPane.RegisterFolderAsync(dir).ConfigureAwait(true);
            result.IsSuccess.Should().BeTrue();
            realIds.Add(result.Value.Id);
        }
        // 3件登録すると最後に登録したものが選択されている。中断後に元へ戻ることを
        // 検証するため、あえて先頭（realIds[0]）を選び直しておく。
        shell.Graft.ProjectPane.SelectedItem =
            shell.Graft.ProjectPane.Items.Single(i => i.Project.Id == realIds[0]);

        window.StartTutorial();
        await WaitForStepAsync(window, 1).ConfigureAwait(true);
        var sampleRoot = shell.Graft.ProjectPane.SelectedItem!.Project.Root;

        // 差分確認まで進めてから中断する（TutorialTests.csの中断テストと同じ深さ）。
        ClickPrimary(window);
        await WaitForStepAsync(window, 2).ConfigureAwait(true);
        ClickPrimary(window);
        await WaitForStepAsync(window, 3).ConfigureAwait(true);

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        await WaitUntilAsync(() => !window.IsTutorialActive).ConfigureAwait(true);

        var finalIds = shell.Graft.ProjectPane.Items.Select(i => i.Project.Id).ToList();
        finalIds.Should().HaveCount(3, "中断してもサンプルだけが片付き、実プロジェクト3件は残る必要がある");
        foreach (var id in realIds)
        {
            finalIds.Should().Contain(id);
        }
        shell.Graft.ProjectPane.SelectedItem!.Project.Id.Should().Be(realIds[0], "中断前に選んでいたプロジェクトへ戻る必要がある");
        Directory.Exists(sampleRoot).Should().BeFalse("中断してもサンプルの一時フォルダは片付く必要がある");
    }

    [AvaloniaFact(DisplayName = "回帰: プロジェクトが1件も無い状態でチュートリアルを完走しても壊れない")]
    public async Task プロジェクトが1件も無い状態でチュートリアルを完走しても壊れない()
    {
        var (shell, window) = await OpenShellAsync().ConfigureAwait(true);
        shell.Graft.ProjectPane.Items.Should().BeEmpty("前提: 実プロジェクトは1件も登録しない");

        window.StartTutorial();
        await CompleteTutorialAsync(window).ConfigureAwait(true);

        shell.Graft.ProjectPane.Items.Should().BeEmpty("サンプルが片付いた結果、起動直後と同じ空の状態に戻る必要がある");
        shell.Graft.ProjectPane.State.Should().Be(ProjectPaneState.Empty);

        // 中断後と同じく、完走後もアプリが正常に操作できること。
        var act = () => shell.SelectSideView(SideViewKind.Explorer);
        act.Should().NotThrow();
        window.CaptureRenderedFrame().Should().NotBeNull("完走後も通常どおり描画できる必要がある");
    }

    // ------------------------------------------------------------------
    // ヘルパ（TutorialTests.csと同じ作法）
    // ------------------------------------------------------------------

    private static async Task CompleteTutorialAsync(ShellWindow window)
    {
        await WaitForStepAsync(window, 1).ConfigureAwait(true);
        for (var step = 1; step <= 7; step++)
        {
            ClickPrimary(window);
            if (step >= 7)
            {
                await WaitUntilAsync(() => !window.IsTutorialActive).ConfigureAwait(true);
            }
            else
            {
                await WaitForStepAsync(window, step + 1).ConfigureAwait(true);
            }
        }
    }

    private static void ClickPrimary(Window window)
    {
        var button = window.GetVisualDescendants().OfType<Button>()
            .Single(b => Equals(AutomationProperties.GetName(b), "次のステップへ進む"));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static async Task WaitForStepAsync(ShellWindow window, int expectedStepNumber)
    {
        await WaitUntilAsync(() => window.IsTutorialActive && window.TutorialStepNumber == expectedStepNumber)
            .ConfigureAwait(true);
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        Dispatcher.UIThread.RunJobs();
        while (!condition())
        {
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(15))
            {
                throw new TimeoutException("チュートリアルの状態変化が15秒以内に起きませんでした。");
            }
            await Task.Delay(10).ConfigureAwait(true);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private async Task<(ShellViewModel Shell, ShellWindow Window)> OpenShellAsync()
    {
        var appDirectory = Path.Combine(_root, "app");
        Directory.CreateDirectory(appDirectory);
        var appPaths = new AppPaths(appDirectory);
        appPaths.EnsureCoreDirectoriesExist();

        var settingsStore = new SettingsStore(appPaths);
        await settingsStore.SaveAsync(new Graft.Infra.Settings { ShowPreview = false }).ConfigureAwait(true);

        var shell = StartupCoordinator.BuildShellViewModel(
            appPaths,
            new Graft.Infra.Settings { ShowPreview = false },
            settingsStore,
            new PatchQueue(appPaths),
            new ProjectStore(appPaths),
            new RevisionStore(appPaths),
            new RevisionRestorer(appPaths),
            new AutoConfirmDialogService(),
            new AvaloniaUiServices(),
            openSettings: () => { });

        var window = _windows.Track(new ShellWindow(shell) { Width = 1280, Height = 800 });
        window.Show();
        await WaitUntilAsync(() => shell.Graft.ProjectPane.State != ProjectPaneState.Loading).ConfigureAwait(true);
        return (shell, window);
    }

    /// <summary>TutorialTests.AutoConfirmDialogServiceと同じ役割の使い捨て実装。</summary>
    private sealed class AutoConfirmDialogService : IDialogService
    {
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);

        public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel)
            => Task.FromResult<bool?>(true);

        public Task<string?> PromptAsync(string title, string message, string? initial = null)
            => Task.FromResult(initial);

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync(string title, IReadOnlyList<string>? extensions = null)
            => Task.FromResult<string?>(null);

        public Task<string?> SaveFileAsync(string title, string suggestedFileName, IReadOnlyList<string>? extensions = null)
            => Task.FromResult<string?>(null);

        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
    }
}
