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
/// 課題1（終了時のログが一切残らない）の回帰テスト。実機では、起動時のログ
/// （logs/&lt;日付&gt;.log の "startup" イベント）は残るのに終了時のログが一切残らず、
/// 「勝手に落ちた」「終了できない」「終了に時間がかかる」という報告があっても手がかりが
/// 無いという診断上の欠陥があった。ここでは、終了処理の各経路（ウィンドウを閉じた／
/// トレイメニューの終了／タスクトレイへ隠しただけ／多重起動検出による自動終了）で
/// 実際にログファイルへ記録されることを検証する。
/// </summary>
public class ShutdownLoggingTests : IDisposable
{
    private readonly string _baseDirectory =
        Path.Combine(Path.GetTempPath(), "graft-ui-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
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

    [AvaloniaFact(DisplayName = "課題1: ×で閉じると終了処理の開始経路とレイアウト保存成功がログに記録される")]
    public async Task ウィンドウを閉じるとログが記録される()
    {
        var appPaths = new AppPaths(_baseDirectory);
        appPaths.EnsureCoreDirectoriesExist();
        var logger = new Logger(appPaths, autoCleanupOnStart: false);

        var window = BuildWindow(appPaths);
        window.Logger = logger;
        window.CloseBehavior = "exit";
        window.Show();

        window.Close();

        var lines = await ReadLogLinesAsync(logger, appPaths);

        lines.Should().Contain(l => l.Contains("終了処理を開始しました") && l.Contains("ウィンドウを閉じた"),
            "どの経路から終了処理が始まったかが分からないと、実機での不具合報告を調査できない");
        lines.Should().Contain(l => l.Contains("レイアウト・タブ構成の保存に成功しました"));

        // 「後始末が完了しました」「終了処理を完了しました（所要時間）」はStartupCoordinator.
        // DisposeAsyncが記録する（App.OnShutdownRequestedから呼ばれる、このテストのように
        // ShellWindow単体では届かない後半部分）。DisposeAsyncを単独で検証するには
        // StartupCoordinator.StartAsyncを呼ぶ必要があるが、それは実際のトレイ・
        // ホットキー登録等OS資源に触れるため、既存のテスト方針（CloseBehaviorTests・
        // StartupTestsもBuildShellViewModelのみを使いStartAsyncは呼ばない）に合わせ、
        // ここでは深入りしない。この経路は実機（Xvfb）での起動→終了確認で担保する。
    }

    [AvaloniaFact(DisplayName = "課題1: トレイメニューの「終了」（IsForceClosing）は経路として区別してログに記録される")]
    public async Task トレイメニューの終了は経路が区別される()
    {
        var appPaths = new AppPaths(_baseDirectory);
        appPaths.EnsureCoreDirectoriesExist();
        var logger = new Logger(appPaths, autoCleanupOnStart: false);

        var window = BuildWindow(appPaths);
        window.Logger = logger;
        window.CloseBehavior = "tray";
        window.IsTraySupported = true;
        window.IsForceClosing = true; // トレイメニュー「終了」相当（StartupCoordinator.ForceExit）。
        window.Show();

        window.Close();

        var lines = await ReadLogLinesAsync(logger, appPaths);

        lines.Should().Contain(l => l.Contains("終了処理を開始しました") && l.Contains("トレイメニューの終了"));
    }

    [AvaloniaFact(DisplayName = "課題1: タスクトレイへ隠しただけ（実際には終了しない）場合もその旨がログに記録される")]
    public async Task トレイへ隠しただけでもログに記録される()
    {
        var appPaths = new AppPaths(_baseDirectory);
        appPaths.EnsureCoreDirectoriesExist();
        var logger = new Logger(appPaths, autoCleanupOnStart: false);

        var window = BuildWindow(appPaths);
        window.Logger = logger;
        window.CloseBehavior = "tray";
        window.IsTraySupported = true;
        window.Show();

        window.Close();

        var lines = await ReadLogLinesAsync(logger, appPaths);

        lines.Should().Contain(l => l.Contains("非表示にしました"),
            "「終了できない」という報告の中には、実際には常駐設定で隠れているだけのものが" +
            "混ざりうる。隠れただけであることをログから判別できる必要がある");
        lines.Should().NotContain(l => l.Contains("終了処理を開始しました"),
            "隠しただけの場合は終了処理そのものは走っていないため、開始ログを出してはならない");
    }

    [AvaloniaFact(DisplayName = "課題1: 多重起動検出による自動終了もログに記録される（StartAsyncを呼ばない経路）")]
    public async Task 多重起動検出時もログに記録される()
    {
        var appPaths = new AppPaths(_baseDirectory);
        appPaths.EnsureCoreDirectoriesExist();
        var coordinator = new StartupCoordinator(_baseDirectory);

        // この経路はStartAsyncを一切呼ばないため、通常のLoggerが存在しない
        // （課題1の本題そのもの）。LogSingleInstanceExitAsync自身が使い捨てのロガーで記録する。
        await coordinator.LogSingleInstanceExitAsync();

        var logPath = appPaths.GetLogFilePath(DateOnly.FromDateTime(DateTime.Now));
        File.Exists(logPath).Should().BeTrue();
        var text = await File.ReadAllTextAsync(logPath);
        text.Should().Contain("多重起動を検出したため");
    }

    /// <summary>ロガーの書き込みキューをフラッシュしてから、当日分のログをすべて読み出す。</summary>
    private static async Task<string[]> ReadLogLinesAsync(Logger logger, AppPaths appPaths)
    {
        // Loggerはチャネル経由で非同期に書き込むため、DisposeAsyncで書き込みタスクの完了を
        // 待ってから読む（課題1の注意点: ここで例外にならないことも合わせて確認している）。
        await logger.DisposeAsync();

        var logPath = appPaths.GetLogFilePath(DateOnly.FromDateTime(DateTime.Now));
        File.Exists(logPath).Should().BeTrue("終了時のログが1行も残らない、という課題1の欠陥そのものを検証する");
        return await File.ReadAllLinesAsync(logPath);
    }

    private static ShellWindow BuildWindow(AppPaths appPaths)
    {
        IDialogService dialogs = new NullDialogService();
        IUiServices ui = new AvaloniaUiServices();

        var shell = StartupCoordinator.BuildShellViewModel(
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

        return new ShellWindow(shell) { Width = 1280, Height = 800 };
    }
}
