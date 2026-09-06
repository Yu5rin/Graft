using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 異常系点検「中」1件目（多重起動・自己再起動でログファイルが欠落・破損する）の回帰テスト。
///
/// 修正前は<c>logs/yyyyMMdd.log</c>という日付単位1ファイルへ、多重起動時の2つ目のプロセス
/// （<see cref="Views.StartupCoordinator.LogSingleInstanceExitAsync"/>）や自己再起動後の
/// 新プロセス（<c>App.axaml.cs</c>の<c>_restartLogger</c>）が、既存プロセスと同時に
/// <c>FileMode.Append</c>で追記していた。実測（修正前のコードで2つの<see cref="Logger"/>に
/// 各300行=計600行を同時書き込み）では366行しか残らず、うち4行がJSONとして壊れていた。
///
/// 修正はファイル名にプロセスIDを含める（<see cref="AppPaths.GetLogFilePath"/>）ことで
/// 競合そのものを無くす方針のため、ここでは<see cref="Logger"/>に「プロセスIDを差し替える」
/// テスト専用の経路（<c>processIdOverride</c>）を使い、同一テストプロセス内で
/// 「別プロセストして振る舞う2つのLogger」が同時に書き込んでも、全行が失われず・
/// 壊れずに残ることを検証する。
/// </summary>
public class LoggerTests
{
    [Fact(DisplayName = "不具合1回帰: 異なるプロセスIDの2つのLoggerが同時書き込みしても、全600行が壊れずに残る")]
    public async Task 多重起動を模した同時書き込みで全行が残る()
    {
        using var ws = new TempWorkspace();
        var appPaths = new AppPaths(ws.Combine("app"));
        appPaths.EnsureCoreDirectoriesExist();

        var loggerA = new Logger(appPaths, autoCleanupOnStart: false, processIdOverride: 11111);
        var loggerB = new Logger(appPaths, autoCleanupOnStart: false, processIdOverride: 22222);

        const int countPerLogger = 300;
        var writeA = Task.Run(() =>
        {
            for (var i = 0; i < countPerLogger; i++)
            {
                loggerA.Info("probe", new string('A', 9) + i);
            }
        });
        var writeB = Task.Run(() =>
        {
            for (var i = 0; i < countPerLogger; i++)
            {
                loggerB.Info("probe", new string('B', 9) + i);
            }
        });
        await Task.WhenAll(writeA, writeB).ConfigureAwait(true);

        await loggerA.DisposeAsync().ConfigureAwait(true);
        await loggerB.DisposeAsync().ConfigureAwait(true);

        // 修正の核心: 2つのLoggerは物理的に別ファイルへ書いているはず。
        var today = DateOnly.FromDateTime(DateTime.Now);
        var pathA = appPaths.GetLogFilePath(today, 11111);
        var pathB = appPaths.GetLogFilePath(today, 22222);
        pathA.Should().NotBe(pathB, "プロセスIDが異なれば別ファイルになること（競合そのものを無くす修正の要）");

        var linesA = await File.ReadAllLinesAsync(pathA).ConfigureAwait(true);
        var linesB = await File.ReadAllLinesAsync(pathB).ConfigureAwait(true);

        linesA.Should().HaveCount(countPerLogger, "他プロセス分の書き込みが割り込んで行が失われないこと");
        linesB.Should().HaveCount(countPerLogger, "他プロセス分の書き込みが割り込んで行が失われないこと");

        foreach (var line in linesA.Concat(linesB))
        {
            Action parse = () => JsonDocument.Parse(line);
            parse.Should().NotThrow($"どの行もJSONとして壊れていないこと: {line}");
        }
    }

    [Fact(DisplayName = "ログファイル名にプロセスIDが含まれ、日付とプロセスの組ごとに別ファイルになる")]
    public async Task ファイル名にプロセスIDが含まれる()
    {
        using var ws = new TempWorkspace();
        var appPaths = new AppPaths(ws.Combine("app"));
        appPaths.EnsureCoreDirectoriesExist();

        var logger = new Logger(appPaths, autoCleanupOnStart: false, processIdOverride: 54321);
        logger.Info("probe", "ok");
        await logger.DisposeAsync().ConfigureAwait(true);

        var expected = appPaths.GetLogFilePath(DateOnly.FromDateTime(DateTime.Now), 54321);
        File.Exists(expected).Should().BeTrue();
        Path.GetFileName(expected).Should().MatchRegex(@"^\d{8}-54321\.log$");
    }

    [Fact(DisplayName = "90日超の古いログ削除は新形式（yyyyMMdd-pid.log）・旧形式（yyyyMMdd.log）の両方を対象にする")]
    public async Task 古いログ削除は新旧どちらの命名も対象にする()
    {
        using var ws = new TempWorkspace();
        var appPaths = new AppPaths(ws.Combine("app"));
        appPaths.EnsureCoreDirectoriesExist();

        var oldDate = DateTime.Today.AddDays(-100).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var recentDate = DateTime.Today.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var oldNewStyle = ws.WriteText($"app/logs/{oldDate}-999.log", "{}");
        var oldLegacyStyle = ws.WriteText($"app/logs/{oldDate}.log", "{}");
        var recentNewStyle = ws.WriteText($"app/logs/{recentDate}-999.log", "{}");

        var logger = new Logger(appPaths, autoCleanupOnStart: false);
        await logger.CleanupOldLogsAsync(retentionDays: 90).ConfigureAwait(true);
        await logger.DisposeAsync().ConfigureAwait(true);

        File.Exists(oldNewStyle).Should().BeFalse("新形式の古いログも削除対象になること");
        File.Exists(oldLegacyStyle).Should().BeFalse("旧形式の古いログも引き続き削除対象になること");
        File.Exists(recentNewStyle).Should().BeTrue("保持期間内のログは削除しないこと");
    }
}
