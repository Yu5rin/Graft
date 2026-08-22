using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Core;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 依頼1対応の単体テスト。<see cref="EnvironmentSummaryLogger"/>が起動時・プロジェクト切替時に
/// 記録する「環境の要約」（プロジェクトルート・種別・データ保存先・取り込み結果を左右する
/// 設定）が期待どおりの内容になること、プロジェクト未選択でも例外にならないことを検証する。
/// 実際のロガー配線（StartupCoordinator・ShellViewModel）の結合はtests/Graft.UiTests側の
/// 責務とし、ここではCore層のみで完結する<see cref="EnvironmentSummaryLogger.Log"/>単体を扱う。
/// </summary>
public class EnvironmentSummaryLoggerTests
{
    private static async Task<string> ReadLogTextAsync(AppPaths appPaths)
    {
        var logPath = appPaths.GetLogFilePath(DateOnly.FromDateTime(DateTime.Now));
        return await File.ReadAllTextAsync(logPath).ConfigureAwait(false);
    }

    [Fact(DisplayName = "プロジェクト未選択（null）でも例外にならず「未選択」と記録される")]
    public async Task プロジェクト未選択_nullでも例外にならず未選択と記録される()
    {
        using var ws = new TempWorkspace();
        var appPaths = new AppPaths(ws.Combine("app"));
        appPaths.EnsureCoreDirectoriesExist();
        var logger = new Logger(appPaths, autoCleanupOnStart: false);

        var act = () => EnvironmentSummaryLogger.Log(logger, appPaths, appPaths.BaseDirectory, new Settings(), projectRoot: null);
        act.Should().NotThrow();

        await logger.DisposeAsync().ConfigureAwait(true);

        var logText = await ReadLogTextAsync(appPaths).ConfigureAwait(true);
        logText.Should().Contain("\"eventType\":\"environment\"");
        logText.Should().Contain("未選択");
    }

    [Fact(DisplayName = "プロジェクト未選択（空文字）でも例外にならず「未選択」と記録される")]
    public async Task プロジェクト未選択_空文字でも例外にならず未選択と記録される()
    {
        using var ws = new TempWorkspace();
        var appPaths = new AppPaths(ws.Combine("app"));
        appPaths.EnsureCoreDirectoriesExist();
        var logger = new Logger(appPaths, autoCleanupOnStart: false);

        var act = () => EnvironmentSummaryLogger.Log(logger, appPaths, appPaths.BaseDirectory, new Settings(), projectRoot: "   ");
        act.Should().NotThrow();

        await logger.DisposeAsync().ConfigureAwait(true);

        (await ReadLogTextAsync(appPaths).ConfigureAwait(true)).Should().Contain("未選択");
    }

    [Fact(DisplayName = "Loggerがnullなら何もせず、例外にもならない")]
    public void Loggerがnullなら何もしない()
    {
        using var ws = new TempWorkspace();
        var appPaths = new AppPaths(ws.Combine("app"));

        var act = () => EnvironmentSummaryLogger.Log(null, appPaths, appPaths.BaseDirectory, new Settings(), "/some/project");
        act.Should().NotThrow();
    }

    [Fact(DisplayName = "ローカルなプロジェクトルートは、PathGuardが実際に使う正規化後の絶対パスと種別=ローカルで記録される")]
    public async Task ローカルなプロジェクトルートは正規化後の絶対パスと種別ローカルで記録される()
    {
        using var ws = new TempWorkspace();
        var appPaths = new AppPaths(ws.Combine("app"));
        appPaths.EnsureCoreDirectoriesExist();
        var logger = new Logger(appPaths, autoCleanupOnStart: false);

        // 末尾に区切り文字を付け、PathGuardの正規化（NormalizeRoot）が実際に効いていることも
        // 合わせて確認する（素通しせず正規化後の値が記録されること）。
        var messyRoot = ws.Combine("project") + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(ws.Combine("project"));
        var expectedNormalized = PathGuard.NormalizeRoot(messyRoot);

        EnvironmentSummaryLogger.Log(logger, appPaths, appPaths.BaseDirectory, new Settings(), messyRoot);
        await logger.DisposeAsync().ConfigureAwait(true);

        var logText = await ReadLogTextAsync(appPaths).ConfigureAwait(true);
        logText.Should().Contain(expectedNormalized, "PathGuardが実際に使う正規化後の絶対パスがそのまま記録されるはず");
        logText.Should().Contain("ローカル", "ローカルディスク上のパスは種別=ローカルとして記録されるはず");
        logText.Should().NotContain(
            messyRoot, "正規化前（末尾の区切り文字付き）の値そのままでは記録されないはず");
    }

    [Fact(DisplayName = "データ保存先がexeと同じフォルダならポータブルと、実際の絶対パス付きで記録される")]
    public async Task データ保存先がexeと同じならポータブルと記録される()
    {
        using var ws = new TempWorkspace();
        var exeDirectory = ws.Combine("app");
        var appPaths = new AppPaths(exeDirectory);
        appPaths.EnsureCoreDirectoriesExist();
        var logger = new Logger(appPaths, autoCleanupOnStart: false);

        EnvironmentSummaryLogger.Log(logger, appPaths, exeDirectory, new Settings(), projectRoot: null);
        await logger.DisposeAsync().ConfigureAwait(true);

        var logText = await ReadLogTextAsync(appPaths).ConfigureAwait(true);
        logText.Should().Contain("ポータブル");
        logText.Should().Contain(appPaths.BaseDirectory, "データ保存先の絶対パスが記録されるはず");
        logText.Should().NotContain("ユーザーフォルダ");
    }

    [Fact(DisplayName = "データ保存先がexeと異なるフォルダならユーザーフォルダと記録される")]
    public async Task データ保存先がexeと異なるならユーザーフォルダと記録される()
    {
        using var ws = new TempWorkspace();
        var exeDirectory = ws.Combine("app");
        var userDataDirectory = ws.Combine("userdata");
        var appPaths = new AppPaths(userDataDirectory);
        appPaths.EnsureCoreDirectoriesExist();
        var logger = new Logger(appPaths, autoCleanupOnStart: false);

        EnvironmentSummaryLogger.Log(logger, appPaths, exeDirectory, new Settings(), projectRoot: null);
        await logger.DisposeAsync().ConfigureAwait(true);

        var logText = await ReadLogTextAsync(appPaths).ConfigureAwait(true);
        logText.Should().Contain("ユーザーフォルダ");
    }

    [Fact(DisplayName = "取り込み結果を左右する設定（少なくともapplyMode）が値付きで記録される")]
    public async Task 取り込み結果を左右する設定が記録される()
    {
        using var ws = new TempWorkspace();
        var appPaths = new AppPaths(ws.Combine("app"));
        appPaths.EnsureCoreDirectoriesExist();
        var logger = new Logger(appPaths, autoCleanupOnStart: false);

        var settings = new Settings
        {
            ApplyMode = "allOrNothing",
            RequireSummary = false,
            Matching = new MatchingSettings { SimilarityThreshold = 0.42, AllowSimilarityMatch = false, RangeWarningLines = 123 },
            Safety = new SafetySettings { MaxFileSizeMB = 7, MaxFilesPerRevision = 99 },
        };

        EnvironmentSummaryLogger.Log(logger, appPaths, appPaths.BaseDirectory, settings, projectRoot: null);
        await logger.DisposeAsync().ConfigureAwait(true);

        var logText = await ReadLogTextAsync(appPaths).ConfigureAwait(true);
        logText.Should().Contain("applyMode=allOrNothing");
        logText.Should().Contain("requireSummary=False");
        logText.Should().Contain("similarityThreshold=0.42");
        logText.Should().Contain("allowSimilarityMatch=False");
        logText.Should().Contain("rangeWarningLines=123");
        logText.Should().Contain("maxFileSizeMB=7");
        logText.Should().Contain("maxFilesPerRevision=99");
    }

    [Fact(DisplayName = "正規化に失敗するような壊れたプロジェクトルートでも例外にならず、そのままログへ残る")]
    public async Task 正規化に失敗する壊れたルートでも例外にならない()
    {
        using var ws = new TempWorkspace();
        var appPaths = new AppPaths(ws.Combine("app"));
        appPaths.EnsureCoreDirectoriesExist();
        var logger = new Logger(appPaths, autoCleanupOnStart: false);

        // NUL文字を含む文字列はPath.GetFullPathがArgumentExceptionを投げる
        // （不正な文字。全OS共通でチェックされる）。
        var brokenRoot = "/tmp/broken\0path";

        var act = () => EnvironmentSummaryLogger.Log(logger, appPaths, appPaths.BaseDirectory, new Settings(), brokenRoot);
        act.Should().NotThrow();

        await logger.DisposeAsync().ConfigureAwait(true);

        var logText = await ReadLogTextAsync(appPaths).ConfigureAwait(true);
        logText.Should().Contain("正規化に失敗", "正規化できなくても原因調査の手がかりとして記録され続けるはず");
    }
}
