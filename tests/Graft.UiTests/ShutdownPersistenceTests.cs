using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Core;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;
using Graft.Platform.Null;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests;

/// <summary>
/// 課題2（終了処理が確実に完了することの検証を強化する）の回帰テスト。
///
/// 既存の <see cref="CloseBehaviorTests"/> は <c>Window.Close()</c> を直接呼ぶ経路
/// （Avalonia内部が実際に使うのと同じコードパス）を検証しているが、「終了時にレイアウトが
/// ディスクへ保存されること」までは確認していなかった。実機（Linux + Xvfb）にはウィンドウ
/// マネージャが無く、<c>xdotool windowclose</c> はAvaloniaのClosingを経由せずウィンドウを
/// 破棄してしまうため、この経路のエンドツーエンド検証が実機ではできない（layout.jsonが
/// 保存されないことを実測で確認済み）。したがってここでテストにより担保する。
/// </summary>
public class ShutdownPersistenceTests : IDisposable
{
    private readonly string _baseDirectory =
        Path.Combine(Path.GetTempPath(), "graft-ui-tests", Guid.NewGuid().ToString("N"));

    private readonly ShownWindowTracker _windows = new();

    public void Dispose()
    {
        // 表示したShellWindowを後始末する（ShownWindowTracker参照。Close()呼び出しの前に
        // アサーションを挟むテストがあり、そこで失敗するとClose()自体が素通りされてしまう。
        // 閉じ忘れると「Unable to locate 'Avalonia.Platform.IFontManagerImpl'」がCIで
        // 不定期に出る）。
        _windows.Dispose();

        try
        {
            if (Directory.Exists(_baseDirectory)) Directory.Delete(_baseDirectory, recursive: true);
        }
        catch (IOException)
        {
            // 後始末の失敗は検証結果に影響しない。
        }

        GC.SuppressFinalize(this);
    }

    private string LayoutFilePath => Path.Combine(_baseDirectory, "layout.json");

    [AvaloniaFact(DisplayName = "課題2: ウィンドウを閉じるとlayout.jsonへ位置・サイズ・最大化状態・ペイン寸法・タブ構成が実際に書き込まれる")]
    public async Task 閉じるとlayoutJsonへ実際に書き込まれる()
    {
        var appPaths = new AppPaths(_baseDirectory);
        appPaths.EnsureCoreDirectoriesExist();

        var shell = BuildShell(appPaths);
        var window = _windows.Track(new ShellWindow(shell) { Width = 1000, Height = 700 });
        window.CloseBehavior = "exit";
        window.Show();
        await shell.Graft.InitializeAsync();

        var projectDirectory = Path.Combine(_baseDirectory, "project");
        Directory.CreateDirectory(projectDirectory);
        var pathA = Path.Combine(projectDirectory, "a.txt");
        var pathB = Path.Combine(projectDirectory, "b.txt");
        await File.WriteAllTextAsync(pathA, "A\n");
        await File.WriteAllTextAsync(pathB, "B\n");

        await shell.Graft.ProjectPane.RegisterFolderAsync(projectDirectory);
        var project = shell.Graft.ProjectPane.SelectedItem!.Project;

        await shell.Editor.OpenFileAsync(pathA);
        var tabB = (await shell.Editor.OpenFileAsync(pathB)).Value;
        shell.Editor.ActiveTab = tabB;

        // 最大化していない状態で位置・サイズが保存されることを確認するため、明示的にNormalへ。
        window.WindowState = WindowState.Normal;
        window.Position = new PixelPoint(123, 45);
        window.Width = 1000;
        window.Height = 700;

        File.Exists(LayoutFilePath).Should().BeFalse("閉じる前はまだ保存されていないはず");

        window.Close();

        File.Exists(LayoutFilePath).Should().BeTrue("×で閉じた時点でlayout.jsonが実際にディスクへ書き込まれる必要がある");

        var json = await File.ReadAllTextAsync(LayoutFilePath);
        var state = JsonSerializer.Deserialize<WindowLayoutState>(json, JsonFileStore.DefaultOptions);
        state.Should().NotBeNull();
        state!.Width.Should().Be(1000);
        state.Height.Should().Be(700);
        state.Left.Should().Be(123);
        state.Top.Should().Be(45);
        state.IsMaximized.Should().BeFalse();

        state.ProjectPaneWidths.Should().ContainKey(project.Id);
        var paneLayout = state.ProjectPaneWidths[project.Id];
        paneLayout.OpenTabs.Should().HaveCount(2, "開いていた2枚のタブが保存される必要がある");
        paneLayout.OpenTabs.Select(t => t.RelativePath).Should().Contain(new[] { "a.txt", "b.txt" });
        paneLayout.ActiveTabPath.Should().Be("b.txt");
    }

    [AvaloniaFact(DisplayName = "課題2: 最大化した状態で閉じるとIsMaximized=trueが保存される")]
    public async Task 最大化状態も保存される()
    {
        var appPaths = new AppPaths(_baseDirectory);
        appPaths.EnsureCoreDirectoriesExist();

        var shell = BuildShell(appPaths);
        var window = _windows.Track(new ShellWindow(shell) { Width = 1000, Height = 700 });
        window.CloseBehavior = "exit";
        window.Show();
        await shell.Graft.InitializeAsync();

        window.WindowState = WindowState.Maximized;

        window.Close();

        var json = await File.ReadAllTextAsync(LayoutFilePath);
        var state = JsonSerializer.Deserialize<WindowLayoutState>(json, JsonFileStore.DefaultOptions);
        state!.IsMaximized.Should().BeTrue();
    }

    [AvaloniaFact(DisplayName = "課題2: タスクトレイに常駐する設定で閉じても終了処理（layout.jsonの保存）は走らない")]
    public async Task 常駐設定では保存されない()
    {
        var appPaths = new AppPaths(_baseDirectory);
        appPaths.EnsureCoreDirectoriesExist();

        var shell = BuildShell(appPaths);
        var window = _windows.Track(new ShellWindow(shell) { Width = 1000, Height = 700 });
        window.CloseBehavior = "tray";
        window.IsTraySupported = true;
        window.Show();
        await shell.Graft.InitializeAsync();

        window.Close();

        window.IsVisible.Should().BeFalse("×で閉じたら見た目上は隠れる必要がある");
        File.Exists(LayoutFilePath).Should().BeFalse(
            "「タスクトレイに常駐する」設定のときは、閉じても終了処理（レイアウト保存を含む）が走ってはならない");
    }

    [AvaloniaFact(DisplayName = "課題2: 常駐設定でもトレイメニューの「終了」（IsForceClosing）なら終了処理が走りlayout.jsonへ保存される")]
    public async Task トレイメニューの終了では保存される()
    {
        var appPaths = new AppPaths(_baseDirectory);
        appPaths.EnsureCoreDirectoriesExist();

        var shell = BuildShell(appPaths);
        var window = _windows.Track(new ShellWindow(shell) { Width = 1000, Height = 700 });
        window.CloseBehavior = "tray";
        window.IsTraySupported = true;
        window.IsForceClosing = true; // StartupCoordinator.ForceExit相当。
        window.Show();
        await shell.Graft.InitializeAsync();

        window.Close();

        File.Exists(LayoutFilePath).Should().BeTrue(
            "トレイメニューの「終了」は常駐設定に関わらず必ず終了処理を実行しなければならない");
    }

    [AvaloniaFact(DisplayName = "課題2: 保存先が書き込めない状況でもプロセスはハングせずウィンドウを閉じられる")]
    public async Task 保存に失敗してもハングせず閉じる()
    {
        var appPaths = new AppPaths(_baseDirectory);
        appPaths.EnsureCoreDirectoriesExist();

        var shell = BuildShell(appPaths);
        var window = _windows.Track(new ShellWindow(shell) { Width = 1000, Height = 700 });
        var logger = new Logger(appPaths, autoCleanupOnStart: false);
        window.Logger = logger;
        window.CloseBehavior = "exit";
        window.Show();
        await shell.Graft.InitializeAsync();

        // layout.json という名前のディレクトリを事前に作っておくと、保存処理の
        // File.Move（および代替のFile.Copy）が「書き込み先が読み取り専用」と同種の
        // I/O失敗として必ず失敗する。chmodによる読み取り専用化はテスト実行ユーザーが
        // rootの場合に無効化されてしまうため、権限に依存しないこの方法を使う。
        Directory.CreateDirectory(LayoutFilePath);

        var closed = 0;
        window.Closed += (_, _) => closed++;

        var act = () => window.Close();

        act.Should().NotThrow("レイアウト保存の失敗がOnClosingの外まで伝播してはならない（プロセスが終了できなくなる）");
        closed.Should().Be(1, "保存に失敗してもウィンドウは実際に閉じて、プロセスが終了できる必要がある");

        await logger.DisposeAsync();
        var logPath = appPaths.GetLogFilePath(DateOnly.FromDateTime(DateTime.Now));
        var text = await File.ReadAllTextAsync(logPath);
        text.Should().Contain("レイアウト・タブ構成の保存に失敗しました",
            "異常終了はlevelを上げて記録し、原因調査の手がかりを残す必要がある");
        text.Should().Contain("\"level\":\"error\"");
    }

    private static ShellViewModel BuildShell(AppPaths appPaths)
    {
        IDialogService dialogs = new NullDialogService();
        IUiServices ui = new AvaloniaUiServices();

        return StartupCoordinator.BuildShellViewModel(
            appPaths,
            new Settings(),
            new SettingsStore(appPaths),
            new PatchQueue(appPaths),
            new ProjectStore(appPaths),
            new RevisionStore(appPaths),
            new RevisionRestorer(appPaths),
            dialogs,
            ui,
            openSettings: () => { });
    }
}
