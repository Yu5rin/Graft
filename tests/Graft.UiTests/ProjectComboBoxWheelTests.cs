using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
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
/// 利用者からの要望: コマンドバー左端のプロジェクト選択ComboBox（<see cref="ShellWindow.ProjectComboBox"/>）を、
/// クリックしてフォーカスを当てなくても、マウスカーソルを乗せてホイールを回すだけで切り替えられるように
/// する。実体はShellWindow.ProjectComboBox.cs（トンネリング段階でPointerWheelChangedを拾う）。
///
/// EditorTabStripOverflowTests（タブ列のホイール横スクロール）と同じ作法（<c>window.MouseWheel</c>を
/// headlessで<c>RaiseEvent</c>する）で検証する。プロジェクトの切り替え自体を検証するにはフル機能の
/// ShellWindow・ShellViewModelが要る（ProjectPaneOperationsScenarioTestsと同じ作法）ため、
/// EditorPane単体を使うEditorTabStripOverflowTestsとは異なりShellWindow全体を組み立てる。
/// </summary>
public class ProjectComboBoxWheelTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-projectcombo-wheel", Guid.NewGuid().ToString("N"));
    private readonly string _appDirectory;
    private readonly ShownWindowTracker _windows = new();

    public ProjectComboBoxWheelTests()
    {
        _appDirectory = Path.Combine(_root, "app");
        Directory.CreateDirectory(_appDirectory);
    }

    public void Dispose()
    {
        _windows.Dispose();
        TempDirectoryCleanup.TryDeleteRecursive(_root);
        GC.SuppressFinalize(this);
    }

    [AvaloniaFact(DisplayName = "ComboBoxにカーソルが乗っているだけ（フォーカス無し）でホイールを回すと選択が1つ進む／戻る")]
    public async Task ホバー中のホイールで選択が前後する()
    {
        var projectA = CreateProjectDir("hover-a");
        var projectB = CreateProjectDir("hover-b");
        var projectC = CreateProjectDir("hover-c");

        var (shell, window) = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(projectA).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(projectB).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(projectC).ConfigureAwait(true);
        var items = shell.Graft.ProjectPane.Items;
        items.Should().HaveCount(3, "登録順（A・B・Cとも新規なので並び替えは起きない。ProjectStore.Sort参照）");

        // Bを選び、フォーカスは一切当てない（RootWindow.FocusManager等を一切触らない）まま
        // カーソルだけComboBoxへ乗せる。「未選択でもクリック不要で」という要望の核心。
        shell.Graft.ProjectPane.SelectedItem = items[1];
        Dispatcher.UIThread.RunJobs();

        var comboBox = FindProjectComboBox(window);
        var pos = ComboBoxCenter(comboBox, window);

        // 下方向へのホイール（Delta.Y負）は一覧の次（下）の項目へ進む。
        window.MouseWheel(pos, new Vector(0, -3));
        Dispatcher.UIThread.RunJobs();
        await WaitForSwitchAsync(shell).ConfigureAwait(true);

        shell.Graft.ProjectPane.SelectedItem.Should().Be(items[2], "下方向のホイールで次（C）へ進むはず");

        // 上方向（Delta.Y正）で1つ戻る。
        window.MouseWheel(pos, new Vector(0, 3));
        Dispatcher.UIThread.RunJobs();
        await WaitForSwitchAsync(shell).ConfigureAwait(true);

        shell.Graft.ProjectPane.SelectedItem.Should().Be(items[1], "上方向のホイールで前（B）へ戻るはず");
    }

    [AvaloniaFact(DisplayName = "先頭で上、末尾で下へホイールを回しても何も起きない（端で止まる・ラップしない）")]
    public async Task 端で止まる()
    {
        var projectA = CreateProjectDir("edge-a");
        var projectB = CreateProjectDir("edge-b");

        var (shell, window) = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(projectA).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(projectB).ConfigureAwait(true);
        var items = shell.Graft.ProjectPane.Items;

        var comboBox = FindProjectComboBox(window);
        var pos = ComboBoxCenter(comboBox, window);

        // 先頭（A）で上方向へ回しても先頭のまま。
        shell.Graft.ProjectPane.SelectedItem = items[0];
        Dispatcher.UIThread.RunJobs();
        window.MouseWheel(pos, new Vector(0, 3));
        Dispatcher.UIThread.RunJobs();
        await WaitForSwitchAsync(shell).ConfigureAwait(true);
        shell.Graft.ProjectPane.SelectedItem.Should().Be(items[0], "先頭で上方向へ回しても、別のプロジェクトへ飛んではいけない");

        // 末尾（B）で下方向へ回しても末尾のまま。
        shell.Graft.ProjectPane.SelectedItem = items[1];
        Dispatcher.UIThread.RunJobs();
        window.MouseWheel(pos, new Vector(0, -3));
        Dispatcher.UIThread.RunJobs();
        await WaitForSwitchAsync(shell).ConfigureAwait(true);
        shell.Graft.ProjectPane.SelectedItem.Should().Be(items[1], "末尾で下方向へ回しても、先頭へ回り込んではいけない（ラップ禁止）");
    }

    [AvaloniaFact(DisplayName = "未選択（SelectedItemがnull）の状態でホイールを回すと先頭が選ばれる")]
    public async Task 未選択からホイールを回すと先頭が選ばれる()
    {
        var projectA = CreateProjectDir("unselected-a");
        var projectB = CreateProjectDir("unselected-b");

        var (shell, window) = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(projectA).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(projectB).ConfigureAwait(true);
        var items = shell.Graft.ProjectPane.Items;

        // 登録直後の自動選択を解除し、一度もクリックで選んだことが無い状態を作る。
        shell.Graft.ProjectPane.SelectedItem = null;
        Dispatcher.UIThread.RunJobs();
        shell.Graft.ProjectPane.SelectedItem.Should().BeNull("前提: 未選択の状態から始める");

        var comboBox = FindProjectComboBox(window);
        comboBox.SelectedIndex.Should().Be(-1, "前提: ComboBox側も未選択(-1)になっているはず");
        var pos = ComboBoxCenter(comboBox, window);

        window.MouseWheel(pos, new Vector(0, -3)); // 下方向でも
        Dispatcher.UIThread.RunJobs();
        await WaitForSwitchAsync(shell).ConfigureAwait(true);

        shell.Graft.ProjectPane.SelectedItem.Should().Be(items[0], "未選択からホイールを回すと先頭が選ばれるはず");
    }

    [AvaloniaFact(DisplayName = "ドロップダウンが開いているときはホイールで選択を横取りしない")]
    public async Task ドロップダウンが開いていると横取りしない()
    {
        var projectA = CreateProjectDir("dropdown-a");
        var projectB = CreateProjectDir("dropdown-b");
        var projectC = CreateProjectDir("dropdown-c");

        var (shell, window) = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(projectA).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(projectB).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(projectC).ConfigureAwait(true);
        var items = shell.Graft.ProjectPane.Items;

        shell.Graft.ProjectPane.SelectedItem = items[1];
        Dispatcher.UIThread.RunJobs();

        var comboBox = FindProjectComboBox(window);
        comboBox.IsDropDownOpen = true;
        Dispatcher.UIThread.RunJobs();

        var pos = ComboBoxCenter(comboBox, window);
        window.MouseWheel(pos, new Vector(0, -3));
        Dispatcher.UIThread.RunJobs();
        await WaitForSwitchAsync(shell).ConfigureAwait(true);

        shell.Graft.ProjectPane.SelectedItem.Should().Be(items[1],
            "ドロップダウンが開いている間は一覧のスクロールに譲るべきで、選択そのものは変わらないはず");
        comboBox.IsDropDownOpen.Should().BeTrue("横取りせず開いたままのはず");
    }

    [AvaloniaFact(DisplayName = "プロジェクトが0件のときにホイールを回しても例外にならない")]
    public async Task プロジェクト0件でも例外にならない()
    {
        var (_, window) = await OpenShellAsync().ConfigureAwait(true);

        var comboBox = FindProjectComboBox(window);
        comboBox.ItemCount.Should().Be(0);
        var pos = ComboBoxCenter(comboBox, window);

        var act = () =>
        {
            window.MouseWheel(pos, new Vector(0, -3));
            window.MouseWheel(pos, new Vector(0, 3));
            Dispatcher.UIThread.RunJobs();
        };

        act.Should().NotThrow("プロジェクトが1件も無い状態でホイールを回しても例外になってはいけない");
    }

    [AvaloniaFact(DisplayName = "プロジェクトが1件のときにホイールを回しても選択は変わらず例外にもならない")]
    public async Task プロジェクト1件でも例外にならない()
    {
        var projectA = CreateProjectDir("single-a");
        var (shell, window) = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(projectA).ConfigureAwait(true);
        var items = shell.Graft.ProjectPane.Items;
        items.Should().ContainSingle();

        var comboBox = FindProjectComboBox(window);
        var pos = ComboBoxCenter(comboBox, window);

        var act = () =>
        {
            window.MouseWheel(pos, new Vector(0, -3));
            window.MouseWheel(pos, new Vector(0, 3));
            Dispatcher.UIThread.RunJobs();
        };

        act.Should().NotThrow("プロジェクトが1件だけの状態でホイールを回しても例外になってはいけない");
        shell.Graft.ProjectPane.SelectedItem.Should().Be(items[0], "1件しか無いので選択は変わらないはず");
    }

    // ------------------------------------------------------------------
    // 連続切り替え対策（案B: 前の切り替えが終わるまで後続のホイール入力を無視する）
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "連続切り替え対策: 切り替え中（未保存確認ダイアログ待ち）のホイールは無視され、ダイアログが積み重ならない")]
    public async Task 切り替え中のホイールは無視される()
    {
        var projectA = CreateProjectDir("busy-a");
        var projectB = CreateProjectDir("busy-b");
        var projectC = CreateProjectDir("busy-c");
        var filePath = Path.Combine(projectA, "note.txt");
        await File.WriteAllTextAsync(filePath, "元の内容").ConfigureAwait(true);

        var dialogs = new BlockingDialogService();
        var (shell, window) = await OpenShellAsync(dialogs).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(projectA).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(projectB).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(projectC).ConfigureAwait(true);
        var items = shell.Graft.ProjectPane.Items;

        shell.Graft.ProjectPane.SelectedItem = items[0]; // A
        Dispatcher.UIThread.RunJobs();
        await WaitForSwitchAsync(shell).ConfigureAwait(true);

        // Aで未保存のタブを作る。CloseAllAsync（Editor.CloseAllAsync→EditorTabManager.CloseAsync）が
        // 保存確認ダイアログ（IDialogService.ConfirmThreeWayAsync）を出す経路を実際に踏ませる。
        var opened = await shell.Editor.OpenFileAsync(filePath).ConfigureAwait(true);
        opened.IsSuccess.Should().BeTrue();
        var tab = opened.Value;
        tab.Session.Document.Insert(tab.Session.Document.TextLength, "追記");
        tab.Session.IsModified.Should().BeTrue("前提: 保存確認ダイアログが出るには未保存の変更が要る");

        var comboBox = FindProjectComboBox(window);
        var pos = ComboBoxCenter(comboBox, window);

        // 1回目のホイール: ComboBoxのSelectedIndexはこの場で即座にA→Bへ動く（ComboBoxの
        // SelectedItemはViewModelとTwoWayで直結しているため、ここは通常のComboBox操作と同じ）。
        // その裏でOnProjectSelectedが走り出し、未保存タブの確認ダイアログの応答待ちで止まる
        // （BlockingDialogServiceがTaskCompletionSourceで応答を保留する）。
        window.MouseWheel(pos, new Vector(0, -3));
        Dispatcher.UIThread.RunJobs();

        shell.IsProjectSwitchBusy.Should().BeTrue("未保存確認ダイアログの応答待ちで切り替え中のはず");
        dialogs.ThreeWayCallCount.Should().Be(1);
        shell.Graft.ProjectPane.SelectedItem.Should().Be(items[1], "1回目のホイールで直ちにBへ動くはず（表示のずれは無い）");

        // 応答待ちの間にさらに2回ホイールを回しても、切り替え中なら無視される（案B）。
        // 無視されなければBの次（C）まで選択が進んでしまうはずなので、Cへ進んでいないことで
        // 「積み重ならなかった」ことを確認する（確認ダイアログの呼び出し回数でも二重に確認する）。
        window.MouseWheel(pos, new Vector(0, -3));
        window.MouseWheel(pos, new Vector(0, -3));
        Dispatcher.UIThread.RunJobs();

        dialogs.ThreeWayCallCount.Should().Be(1, "切り替え中のホイール入力は無視され、確認ダイアログが積み重なってはいけない");
        shell.Graft.ProjectPane.SelectedItem.Should().Be(items[1], "切り替え中のホイールは無視されるので、Bのまま（Cへは進まない）はず");

        // 「いいえ」（保存しない）で応答し、保留していた切り替えを完了させる。
        dialogs.CompleteThreeWay(false);
        await WaitForSwitchAsync(shell).ConfigureAwait(true);

        shell.IsProjectSwitchBusy.Should().BeFalse();
        shell.Graft.ProjectPane.SelectedItem.Should().Be(items[1], "Bへの切り替えが完了しているはず");

        // busyが解けた後のホイールは通常どおり効く。
        window.MouseWheel(pos, new Vector(0, -3));
        Dispatcher.UIThread.RunJobs();
        await WaitForSwitchAsync(shell).ConfigureAwait(true);

        shell.Graft.ProjectPane.SelectedItem.Should().Be(items[2], "busy解除後のホイールは通常どおりCへ進むはず");
    }

    // ------------------------------------------------------------------
    // ヘルパ
    // ------------------------------------------------------------------

    private string CreateProjectDir(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static ComboBox FindProjectComboBox(Window window)
        => window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "ProjectComboBox");

    private static Point ComboBoxCenter(ComboBox comboBox, Window window)
        => comboBox.TranslatePoint(new Point(comboBox.Bounds.Width / 2, comboBox.Bounds.Height / 2), window)!.Value;

    /// <summary>
    /// プロジェクト切り替え（<see cref="ShellViewModel.OnProjectSelected"/>）はEditor.CloseAllAsync・
    /// Explorer.SetProjectAsync等、実ファイルI/Oを伴いうる非同期処理のため、RunJobs()を1回呼ぶだけでは
    /// 完了しないことがある（ShellWindowLoadWaiterと同じ事情）。<see cref="ShellViewModel.IsProjectSwitchBusy"/>
    /// が下りるまで実時間ベースで待つ。
    /// </summary>
    private static async Task WaitForSwitchAsync(ShellViewModel shell)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            while (shell.IsProjectSwitchBusy)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(10, cts.Token).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException ex)
        {
            throw new TimeoutException("プロジェクトの切り替えが10秒以内に完了しませんでした（IsProjectSwitchBusyが下りたままにならない）。", ex);
        }

        Dispatcher.UIThread.RunJobs();
    }

    private async Task<(ShellViewModel Shell, Window Window)> OpenShellAsync(IDialogService? dialogs = null)
    {
        var appPaths = new AppPaths(_appDirectory);
        appPaths.EnsureCoreDirectoriesExist();

        var settingsStore = new SettingsStore(appPaths);
        await settingsStore.SaveAsync(new Settings { ShowPreview = false }).ConfigureAwait(true);

        var shell = StartupCoordinator.BuildShellViewModel(
            appPaths,
            new Settings { ShowPreview = false },
            settingsStore,
            new PatchQueue(appPaths),
            new ProjectStore(appPaths),
            new RevisionStore(appPaths),
            new RevisionRestorer(appPaths),
            dialogs ?? new BlockingDialogService(),
            new AvaloniaUiServices(),
            openSettings: () => { });

        var window = _windows.Track(new ShellWindow(shell) { Width = 1280, Height = 800 });
        window.Show();
        await WaitForShellInitializedAsync(shell).ConfigureAwait(true);
        return (shell, window);
    }

    private static async Task WaitForShellInitializedAsync(ShellViewModel shell)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            while (shell.Graft.ProjectPane.State == ProjectPaneState.Loading)
            {
                await Task.Delay(10, cts.Token).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException ex)
        {
            throw new TimeoutException(
                "ShellWindow.OnLoaded経由の初期化が30秒以内に完了しませんでした（ProjectPane.StateがLoadingのまま）。", ex);
        }
    }

    /// <summary>
    /// 未保存確認（ConfirmThreeWayAsync）の応答を、<see cref="CompleteThreeWay"/>を呼ぶまで
    /// 保留するダイアログ。連続切り替え対策（案B）の検証専用で、切り替え処理を実際に
    /// 「応答待ちで止まっている」状態に固定するために使う。それ以外の確認は即座に承諾する。
    /// </summary>
    private sealed class BlockingDialogService : IDialogService
    {
        private TaskCompletionSource<bool?>? _pendingThreeWay;

        public int ThreeWayCallCount { get; private set; }

        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);

        public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel)
        {
            ThreeWayCallCount++;
            _pendingThreeWay = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
            return _pendingThreeWay.Task;
        }

        /// <summary>保留中のConfirmThreeWayAsync呼び出しへ応答する。</summary>
        public void CompleteThreeWay(bool? result) => _pendingThreeWay?.TrySetResult(result);

        public Task<string?> PromptAsync(string title, string message, string? initial = null)
            => Task.FromResult<string?>(initial ?? "テスト");

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync(string title, IReadOnlyList<string>? extensions = null) => Task.FromResult<string?>(null);
        public Task<string?> SaveFileAsync(string title, string suggestedFileName, IReadOnlyList<string>? extensions = null) => Task.FromResult<string?>(null);

        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
    }
}
