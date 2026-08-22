using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 依頼2対応の単体テスト。<see cref="SuppressedExceptionTracker"/>が握りつぶした例外を
/// 種類（発生箇所＋例外の型）ごとに正しく数え、終了時のログには1回以上発生した種類だけを
/// 件数付きで出す（0件の種類は出さない）ことを検証する。
/// <see cref="SuppressedExceptionTracker.Shared"/>はプロセス全体で共有するため、テスト間の
/// 汚染を避けるためここでは常に<c>new SuppressedExceptionTracker()</c>の個別インスタンスを使う
/// （本番コードは<c>Shared</c>を使うが、集計ロジック自体はインスタンスメソッドのため
/// 個別インスタンスでも同一の挙動を検証できる）。
/// </summary>
public class SuppressedExceptionTrackerTests
{
    private static async Task<string> ReadLogTextAsync(AppPaths appPaths)
    {
        var logPath = appPaths.GetLogFilePath(DateOnly.FromDateTime(DateTime.Now));
        return await File.ReadAllTextAsync(logPath).ConfigureAwait(false);
    }

    [Fact(DisplayName = "同じ発生箇所・同じ例外の型を複数回記録すると、件数が積み上がる")]
    public async Task 同じ種類を複数回記録すると件数が積み上がる()
    {
        using var ws = new TempWorkspace();
        var appPaths = new AppPaths(ws.Combine("app"));
        appPaths.EnsureCoreDirectoriesExist();
        var logger = new Logger(appPaths, autoCleanupOnStart: false);

        var tracker = new SuppressedExceptionTracker();
        tracker.Record("indent-guide-draw", new InvalidOperationException("1回目"));
        tracker.Record("indent-guide-draw", new InvalidOperationException("2回目"));
        tracker.Record("indent-guide-draw", new InvalidOperationException("3回目"));

        tracker.LogSummary(logger);
        await logger.DisposeAsync().ConfigureAwait(true);

        var logText = await ReadLogTextAsync(appPaths).ConfigureAwait(true);
        logText.Should().Contain("indent-guide-draw:InvalidOperationException");
        logText.Should().Contain("3 回");
    }

    [Fact(DisplayName = "同じ発生箇所でも例外の型が異なれば別の種類として数える")]
    public async Task 型が異なれば別の種類として数える()
    {
        using var ws = new TempWorkspace();
        var appPaths = new AppPaths(ws.Combine("app"));
        appPaths.EnsureCoreDirectoriesExist();
        var logger = new Logger(appPaths, autoCleanupOnStart: false);

        var tracker = new SuppressedExceptionTracker();
        tracker.Record("clipboard-x11-write-loop", new InvalidOperationException());
        tracker.Record("clipboard-x11-write-loop", new InvalidOperationException());
        tracker.Record("clipboard-x11-write-loop", new IOException());

        tracker.LogSummary(logger);
        await logger.DisposeAsync().ConfigureAwait(true);

        var logText = await ReadLogTextAsync(appPaths).ConfigureAwait(true);
        logText.Should().Contain("clipboard-x11-write-loop:InvalidOperationException");
        logText.Should().Contain("clipboard-x11-write-loop:IOException");
        logText.Should().Contain("2 回");
        logText.Should().Contain("1 回");
    }

    [Fact(DisplayName = "1度も発生しなかった種類はshutdownログに一切出ない（0件は出さない）")]
    public async Task 発生しなかった種類は出ない()
    {
        using var ws = new TempWorkspace();
        var appPaths = new AppPaths(ws.Combine("app"));
        appPaths.EnsureCoreDirectoriesExist();
        var logger = new Logger(appPaths, autoCleanupOnStart: false);

        var tracker = new SuppressedExceptionTracker();
        // 何も記録しない。

        tracker.LogSummary(logger);
        await logger.DisposeAsync().ConfigureAwait(true);

        var logPath = appPaths.GetLogFilePath(DateOnly.FromDateTime(DateTime.Now));
        // 何も書かれていなければログファイル自体が作られないはず
        // （Loggerは書き込みが1件も無ければファイルを開かないため）。
        File.Exists(logPath).Should().BeFalse("発生が0件の種類しか無い場合はshutdownログへ何も出さないはず");
    }

    [Fact(DisplayName = "記録した種類だけが出て、記録していない種類は混ざらない（部分的な0件の除外）")]
    public async Task 記録した種類だけが出る()
    {
        using var ws = new TempWorkspace();
        var appPaths = new AppPaths(ws.Combine("app"));
        appPaths.EnsureCoreDirectoriesExist();
        var logger = new Logger(appPaths, autoCleanupOnStart: false);

        var tracker = new SuppressedExceptionTracker();
        tracker.Record("titlebar-theme", new InvalidOperationException());

        tracker.LogSummary(logger);
        await logger.DisposeAsync().ConfigureAwait(true);

        var logText = await ReadLogTextAsync(appPaths).ConfigureAwait(true);
        logText.Should().Contain("titlebar-theme:InvalidOperationException");
        logText.Should().NotContain("font-family-converter", "記録していない種類は出ないはず");
        logText.Should().NotContain("clipboard-x11-read-request", "記録していない種類は出ないはず");
    }

    [Fact(DisplayName = "eventTypeはshutdownとして記録される")]
    public async Task eventTypeはshutdown()
    {
        using var ws = new TempWorkspace();
        var appPaths = new AppPaths(ws.Combine("app"));
        appPaths.EnsureCoreDirectoriesExist();
        var logger = new Logger(appPaths, autoCleanupOnStart: false);

        var tracker = new SuppressedExceptionTracker();
        tracker.Record("indent-guide-draw", new InvalidOperationException());

        tracker.LogSummary(logger);
        await logger.DisposeAsync().ConfigureAwait(true);

        var logText = await ReadLogTextAsync(appPaths).ConfigureAwait(true);
        logText.Should().Contain("\"eventType\":\"shutdown\"");
    }

    [Fact(DisplayName = "Record: contextがnull・空文字は例外")]
    public void Recordはcontextがnull空文字で例外()
    {
        var tracker = new SuppressedExceptionTracker();
        var ex = new InvalidOperationException();

        Action withNull = () => tracker.Record(null!, ex);
        Action withEmpty = () => tracker.Record("", ex);

        withNull.Should().Throw<ArgumentException>();
        withEmpty.Should().Throw<ArgumentException>();
    }

    [Fact(DisplayName = "Record: exceptionがnullは例外")]
    public void Recordは例外がnullで例外()
    {
        var tracker = new SuppressedExceptionTracker();

        Action act = () => tracker.Record("context", null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
