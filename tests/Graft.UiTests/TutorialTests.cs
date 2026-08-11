using System.Linq;
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
/// 画面上のチュートリアル（コーチマーク方式、ShellWindow.Tutorial.cs）の通しシナリオ・
/// 回帰テスト。利用者からの指摘「接ぎ木が体験できないので、ソフトの中核を体験できない」への
/// 対応として追加した機能。ScenarioTests.csと同じ作法（実際の起動と同じ依存グラフで
/// ShellViewModelを組み立て、実ボタンと同じICommandを実際に実行して結果まで確認する）で、
/// 「サンプルを用意→解析→差分確認→適用→履歴確認→復元→完了」の7ステップが実際に進むこと、
/// Esc・「終了」ボタンでいつでも中断できて壊れた状態が残らないこと、チュートリアル前に
/// 選んでいたプロジェクトが終了時に戻ること、実プロジェクトのフォルダには一切書き込まれない
/// ことを検証する。
/// </summary>
public class TutorialTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-tutorial-tests", Guid.NewGuid().ToString("N"));

    private readonly string _appDirectory;
    private readonly string _realProjectDirectory;
    private readonly ShownWindowTracker _windows = new();

    public TutorialTests()
    {
        _appDirectory = Path.Combine(_root, "app");
        _realProjectDirectory = Path.Combine(_root, "real-project");
        Directory.CreateDirectory(_appDirectory);
        Directory.CreateDirectory(_realProjectDirectory);
    }

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

    // 各ステップ（1始まり）に入ったときの主ボタン（PrimaryButton）の表示文言。
    // GetTutorialStepTextと対になる一覧（ShellWindow.Tutorial.cs参照）。文言を変えたら
    // 追随して直す必要がある。ステップ番号自体（ShellWindow.TutorialStepNumber）を
    // 待ち合わせに使うのは、複数のステップが同じ文言（「次へ」）を持ち、文言だけでは
    // 「本当に次のステップまで進んだか」を判定できないため（テスト側の見かけ上の
    // 一致による誤判定を避ける）。
    private static readonly string[] ExpectedPrimaryLabels =
        { "次へ", "次へ", "次へ", "適用する", "次へ", "元に戻す", "終了" };

    [AvaloniaFact(DisplayName = "「使い方を学ぶ」で開始すると7ステップが順に進み、実際に解析・適用・履歴記録・復元まで行われて完走できる")]
    public async Task 使い方を学ぶで開始すると7ステップが順に進み完走できる()
    {
        var markerPath = Path.Combine(_realProjectDirectory, "real.txt");
        await File.WriteAllTextAsync(markerPath, "実プロジェクトの内容\n").ConfigureAwait(true);
        var markerWriteTimeBefore = File.GetLastWriteTimeUtc(markerPath);

        var (shell, window) = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_realProjectDirectory).ConfigureAwait(true);
        var realProjectId = shell.Graft.ProjectPane.SelectedItem!.Project.Id;

        window.StartTutorial();
        await WaitForStepAsync(window, 1).ConfigureAwait(true);

        window.IsTutorialActive.Should().BeTrue();
        AssertPrimaryLabel(window, ExpectedPrimaryLabels[0]);
        // ステップ1: サンプルが一時フォルダに生成・登録され、選択状態になる（実プロジェクトの
        // 選択を奪うが、あとで元に戻ることを最後に確認する）。
        shell.Graft.ProjectPane.Items.Should().HaveCount(2, "実プロジェクト1件＋サンプル1件");
        var sample = shell.Graft.ProjectPane.SelectedItem!;
        sample.Project.Id.Should().NotBe(realProjectId);
        Path.GetFullPath(sample.Project.Root).Should()
            .StartWith(Path.GetFullPath(Path.GetTempPath()), "サンプルの生成先は一時フォルダである必要がある");
        var sampleRoot = sample.Project.Root;
        var sampleFile = Path.Combine(sampleRoot, OnboardingSample.SampleFileName);
        var originalContent = await File.ReadAllTextAsync(sampleFile).ConfigureAwait(true);

        // ステップ2: パッチが解析され、接ぎ木パネルにブロックが1件出る（クリップボードは使わない）。
        ClickPrimary(window);
        await WaitForStepAsync(window, 2).ConfigureAwait(true);
        shell.Graft.Blocks.Should().ContainSingle();
        shell.IsGraftPanelOpen.Should().BeTrue();

        // ステップ3: 差分がエディタ領域のタブとして開く。
        ClickPrimary(window);
        await WaitForStepAsync(window, 3).ConfigureAwait(true);
        shell.Editor.ActiveTab.Should().NotBeNull();
        shell.Editor.ActiveTab!.Kind.Should().Be(EditorTabKind.Diff);

        // ステップ4: 「適用する」を押すと実際にファイルが書き換わる。
        ClickPrimary(window);
        await WaitForStepAsync(window, 4).ConfigureAwait(true);
        AssertPrimaryLabel(window, ExpectedPrimaryLabels[3]);
        ClickPrimary(window); // 「適用する」を実行。
        await WaitForStepAsync(window, 5).ConfigureAwait(true);
        var appliedContent = await File.ReadAllTextAsync(sampleFile).ConfigureAwait(true);
        appliedContent.Should().NotBe(originalContent, "適用によりサンプルファイルの内容が実際に書き換わっている必要がある");
        appliedContent.Should().Contain("こんにちは");

        // ステップ5: 履歴ビューへ切り替わり、直前の適用が1件記録されている。
        shell.SelectedSideView.Should().Be(SideViewKind.History);
        shell.Graft.History.Items.Should().NotBeEmpty("適用が履歴に記録されている必要がある");

        // ステップ6: 「元に戻す」を押すと実際に復元される。
        ClickPrimary(window);
        await WaitForStepAsync(window, 6).ConfigureAwait(true);
        AssertPrimaryLabel(window, ExpectedPrimaryLabels[5]);
        ClickPrimary(window); // 「元に戻す」を実行。
        await WaitForStepAsync(window, 7).ConfigureAwait(true);
        AssertPrimaryLabel(window, ExpectedPrimaryLabels[6]);
        var revertedContent = await File.ReadAllTextAsync(sampleFile).ConfigureAwait(true);
        revertedContent.Should().Be(originalContent, "復元により適用前の内容に戻っている必要がある");

        // ステップ7: 「終了」で完走する。
        ClickPrimary(window);
        await WaitUntilAsync(() => !window.IsTutorialActive).ConfigureAwait(true);

        // 完走後: サンプルはプロジェクト一覧・ディスクの両方から取り除かれ、
        // チュートリアル前に選んでいた実プロジェクトへ選択が戻る。
        shell.Graft.ProjectPane.Items.Should().ContainSingle(i => i.Project.Id == realProjectId);
        shell.Graft.ProjectPane.SelectedItem!.Project.Id.Should().Be(realProjectId);
        Directory.Exists(sampleRoot).Should().BeFalse("サンプルの一時フォルダは完走後に削除されている必要がある");

        // 実データ（実プロジェクトのファイル）には一切書き込まれていないこと。
        var realContent = await File.ReadAllTextAsync(markerPath).ConfigureAwait(true);
        realContent.Should().Be("実プロジェクトの内容\n");
        File.GetLastWriteTimeUtc(markerPath).Should().Be(markerWriteTimeBefore, "実プロジェクトのファイルは更新日時も変わっていない必要がある");
    }

    [AvaloniaFact(DisplayName = "Escキーでいつでも中断でき、中断後もアプリは正常な状態に戻る（サンプルは片付き、元のプロジェクトへ戻る）")]
    public async Task Escでいつでも中断できアプリは正常な状態に戻る()
    {
        var (shell, window) = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_realProjectDirectory).ConfigureAwait(true);
        var realProjectId = shell.Graft.ProjectPane.SelectedItem!.Project.Id;

        window.StartTutorial();
        await WaitForStepAsync(window, 1).ConfigureAwait(true);
        var sampleRoot = shell.Graft.ProjectPane.SelectedItem!.Project.Root;

        // 途中（差分確認ステップ）まで進めてから中断する。
        ClickPrimary(window);
        await WaitForStepAsync(window, 2).ConfigureAwait(true);
        ClickPrimary(window);
        await WaitForStepAsync(window, 3).ConfigureAwait(true);

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        await WaitUntilAsync(() => !window.IsTutorialActive).ConfigureAwait(true);

        shell.Graft.ProjectPane.Items.Should().ContainSingle(i => i.Project.Id == realProjectId);
        shell.Graft.ProjectPane.SelectedItem!.Project.Id.Should().Be(realProjectId);
        Directory.Exists(sampleRoot).Should().BeFalse("中断してもサンプルの一時フォルダは片付く必要がある");

        // 中断後もアプリが正常に操作できること（壊れた状態が残らない）。
        var act = () => shell.SelectSideView(SideViewKind.Explorer);
        act.Should().NotThrow();
        shell.Graft.ApplyCommand.CanExecute(null).Should().BeFalse("解析結果は残っていない（DiscardCurrentPatchがプロジェクト切替のたびに走る）");
        window.CaptureRenderedFrame().Should().NotBeNull("中断後も通常どおり描画できる必要がある");
    }

    [AvaloniaFact(DisplayName = "吹き出しの「終了」ボタンでも中断でき、サンプルが片付く")]
    public async Task 終了ボタンでも中断できる()
    {
        var (shell, window) = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_realProjectDirectory).ConfigureAwait(true);
        var realProjectId = shell.Graft.ProjectPane.SelectedItem!.Project.Id;

        window.StartTutorial();
        await WaitForStepAsync(window, 1).ConfigureAwait(true);
        var sampleRoot = shell.Graft.ProjectPane.SelectedItem!.Project.Root;

        var exitButton = window.GetVisualDescendants().OfType<Button>()
            .Single(b => Equals(AutomationProperties.GetName(b), "チュートリアルを終了"));
        exitButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => !window.IsTutorialActive).ConfigureAwait(true);

        shell.Graft.ProjectPane.SelectedItem!.Project.Id.Should().Be(realProjectId);
        Directory.Exists(sampleRoot).Should().BeFalse();
    }

    [AvaloniaFact(DisplayName = "「？」メニューの「使い方を学ぶ」からいつでも再実行できる")]
    public async Task ヘルプメニューから再実行できる()
    {
        var (shell, window) = await OpenShellAsync().ConfigureAwait(true);
        window.IsTutorialActive.Should().BeFalse();

        // ManualWindowTests.ヘルプメニューは両方の項目に到達できると同じ作法（Focus+Enter）で
        // メニューを開く。ポップアップの内容はIsOpen確定後の再レイアウトを経て初めて可視ツリーへ
        // 反映されるため、RaiseEvent(Click)より確実に確定させられるこちらに揃える。
        var helpButton = window.GetVisualDescendants().OfType<Button>()
            .Single(b => Equals(AutomationProperties.GetName(b), "ヘルプメニューを開く"));
        helpButton.Focus();
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        await SettleAsync().ConfigureAwait(true);

        // 不具合対応: このボタンはClickイベントハンドラではなくCommandバインディング
        // （Command="{Binding StartTutorialCommand}"）のため、RaiseEvent(ClickEvent)では
        // Avalonia標準のButton.OnClick（コマンド実行を担う）を経由せず、Commandが実行されない。
        // 実際のキー操作（Focus+Enter）を経由させ、標準の入力パイプラインでコマンドを実行させる
        // （toggleButtonの開閉と同じ作法。ExitButton等のClickハンドラ方式のボタンは
        // RaiseEvent(ClickEvent)のままでよい）。
        var startButton = window.GetVisualDescendants().OfType<Button>()
            .Single(b => Equals(AutomationProperties.GetName(b), "使い方を学ぶ（画面上のチュートリアルを開始）"));
        startButton.Focus();
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);

        await WaitUntilAsync(() => window.IsTutorialActive).ConfigureAwait(true);
        shell.IsHelpMenuOpen.Should().BeFalse("メニューから選んだら、他の項目と同様にメニュー自体も閉じる必要がある");

        // 後始末: このテストではEscで中断してから終える。
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        await WaitUntilAsync(() => !window.IsTutorialActive).ConfigureAwait(true);
    }

    [AvaloniaFact(DisplayName = "コマンドパレットの「使い方を学ぶ」からもいつでも再実行できる")]
    public async Task コマンドパレットから再実行できる()
    {
        var (shell, window) = await OpenShellAsync().ConfigureAwait(true);
        window.IsTutorialActive.Should().BeFalse();

        shell.CommandPalette.Open();
        shell.CommandPalette.Query = "使い方を学ぶ";
        var item = shell.CommandPalette.Results.Should()
            .ContainSingle(r => r.Title == "使い方を学ぶ（画面上のチュートリアル）").Subject;
        shell.CommandPalette.SelectedResult = item;
        shell.CommandPalette.ConfirmSelection();

        await WaitUntilAsync(() => window.IsTutorialActive).ConfigureAwait(true);
        shell.CommandPalette.IsOpen.Should().BeFalse("実行したらパレット自体も閉じる（QuickOpenと同じ作法）");

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        await WaitUntilAsync(() => !window.IsTutorialActive).ConfigureAwait(true);
    }

    [AvaloniaFact(DisplayName = "「戻る」で前のステップに戻れ、その後「次へ」で再度進められる")]
    public async Task 戻るで前のステップに戻り次へで再度進められる()
    {
        var (shell, window) = await OpenShellAsync().ConfigureAwait(true);

        window.StartTutorial();
        await WaitForStepAsync(window, 1).ConfigureAwait(true);

        ClickPrimary(window);
        await WaitForStepAsync(window, 2).ConfigureAwait(true);
        ClickPrimary(window);
        await WaitForStepAsync(window, 3).ConfigureAwait(true);

        var backButton = window.GetVisualDescendants().OfType<Button>()
            .Single(b => Equals(AutomationProperties.GetName(b), "前のステップに戻る"));
        backButton.IsEnabled.Should().BeTrue();
        backButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitForStepAsync(window, 2).ConfigureAwait(true);

        window.IsTutorialActive.Should().BeTrue("戻る操作自体でチュートリアルが終了してはならない");
        shell.Graft.Blocks.Should().ContainSingle("戻ってもサンプルの再生成・再解析は起きず、解析済みのブロックはそのまま残る");

        ClickPrimary(window);
        await WaitForStepAsync(window, 3).ConfigureAwait(true);

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        await WaitUntilAsync(() => !window.IsTutorialActive).ConfigureAwait(true);
    }

    private static void ClickPrimary(Window window)
    {
        var button = window.GetVisualDescendants().OfType<Button>()
            .Single(b => Equals(AutomationProperties.GetName(b), "次のステップへ進む"));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static void AssertPrimaryLabel(Window window, string expectedLabel)
    {
        var button = window.GetVisualDescendants().OfType<Button>()
            .Single(b => Equals(AutomationProperties.GetName(b), "次のステップへ進む"));
        button.Content.Should().Be(expectedLabel);
    }

    /// <summary>
    /// <see cref="ShellWindow.TutorialStepNumber"/>が期待するステップ番号（1始まり）になるまで
    /// 待つ。ボタンの表示文言（複数のステップで「次へ」が重複する）ではなく、この整数を
    /// 待ち合わせに使うことで、見かけ上の一致による誤判定を避ける（クラス冒頭のコメント参照）。
    /// </summary>
    private static async Task WaitForStepAsync(ShellWindow window, int expectedStepNumber)
    {
        await WaitUntilAsync(() => window.IsTutorialActive && window.TutorialStepNumber == expectedStepNumber)
            .ConfigureAwait(true);

        // TutorialStepNumberはShellWindow.Tutorial.cs側の値の更新（純粋なC#の非同期継続）を
        // 反映するが、その値と同期して更新される実際のControl（TutorialOverlayのボタン等）が
        // Avaloniaの可視ツリーへ反映されるにはレイアウトパスが要る
        // （ShellWindowLoadWaiter.WaitForLayoutAppliedと同じ事情）。ここで明示的に流す。
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// 条件が満たされるまで待つ。毎回<see cref="Dispatcher.UIThread.RunJobs()"/>も呼び、
    /// 保留中のレイアウト（TutorialOverlayのボタン等、IsVisibleがfalseから初めてtrueになる
    /// 瞬間に生じるテンプレート適用を含む）を都度流してから条件を再評価する
    /// （ShellWindowLoadWaiter.WaitForLayoutAppliedと同じ「RunJobs＋ポーリング」の作法。
    /// awaitだけでは可視ツリーへの反映がAvaloniaのディスパッチャ都合で遅れることがある）。
    /// </summary>
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

    private static async Task SettleAsync()
    {
        for (var i = 0; i < 10; i++)
        {
            await Task.Delay(10).ConfigureAwait(true);
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>
    /// window.Show()（ShellWindow.OnLoaded経由）が裏で走らせているMainViewModel.InitializeAsyncの
    /// 完了を待つ（ScenarioTests.csと同じ作法。詳細は同ファイルのコメント参照）。
    /// </summary>
    private async Task<(ShellViewModel Shell, ShellWindow Window)> OpenShellAsync()
    {
        var appPaths = new AppPaths(_appDirectory);
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

    /// <summary>
    /// 適用・復元の確認ダイアログをすべて承諾するダイアログ（実際の操作では利用者が「はい」を
    /// 押す場面にあたる。ScenarioTests.AutoConfirmDialogServiceと同じ役割の使い捨て実装）。
    /// </summary>
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
