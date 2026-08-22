using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Infra;
using Graft.Platform;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 依頼2（E706・トレイ常駐/自動起動が使えない環境の記録）・依頼3（E709・OSのハイコントラスト
/// モード検出の記録）の単体テスト。<see cref="PlatformDiagnosticsLogging"/>はIPlatformService・
/// GraftIssue・Loggerのみに依存しUIフレームワークに触れないため、実際の
/// StartupCoordinator（Avalonia依存、tests/Graft.UiTests側の担当）を経由せず、
/// EnvironmentSummaryLoggerTestsと同じ手法（実ファイルへ書いたログを読み直す）で検証する。
/// </summary>
public class PlatformDiagnosticsLoggingTests
{
    private sealed class FakePlatformService : IPlatformService
    {
        public bool IsSupported { get; init; }
        public string? UnsupportedReason { get; init; }
    }

    private static async Task<string> ReadLogTextAsync(AppPaths appPaths)
    {
        var logPath = appPaths.GetLogFilePath(DateOnly.FromDateTime(DateTime.Now));
        return await File.ReadAllTextAsync(logPath).ConfigureAwait(false);
    }

    private static (AppPaths AppPaths, Logger Logger, TempWorkspace Workspace) CreateLogger()
    {
        var ws = new TempWorkspace();
        var appPaths = new AppPaths(ws.Combine("app"));
        appPaths.EnsureCoreDirectoriesExist();
        var logger = new Logger(appPaths, autoCleanupOnStart: false);
        return (appPaths, logger, ws);
    }

    // ------------------------------------------------------------------
    // 依頼2（E706）
    // ------------------------------------------------------------------

    [Fact(DisplayName = "利用できない機能はE706付きでログへ記録される")]
    public async Task 利用できない機能はE706付きで記録される()
    {
        var (appPaths, logger, ws) = CreateLogger();
        using var _ = ws;

        var service = new FakePlatformService { IsSupported = false, UnsupportedReason = "テスト環境では未対応です。" };
        PlatformDiagnosticsLogging.LogUnsupportedFeature(logger, "タスクトレイ常駐", service);

        await logger.DisposeAsync().ConfigureAwait(true);

        var logText = await ReadLogTextAsync(appPaths).ConfigureAwait(true);
        logText.Should().Contain("E706", "E706付きで記録される必要がある");
        logText.Should().Contain("タスクトレイ常駐", "どの機能が対象かを含める必要がある");
        logText.Should().Contain("テスト環境では未対応です。", "UnsupportedReasonの内容を含める必要がある");
    }

    [Fact(DisplayName = "利用できる機能（IsSupported=true）は何も記録しない")]
    public async Task 利用できる機能は記録しない()
    {
        var (appPaths, logger, ws) = CreateLogger();
        using var _ = ws;

        var service = new FakePlatformService { IsSupported = true, UnsupportedReason = null };
        PlatformDiagnosticsLogging.LogUnsupportedFeature(logger, "タスクトレイ常駐", service);

        await logger.DisposeAsync().ConfigureAwait(true);

        // 一切書き込みが無い場合、Loggerは遅延オープンのためログファイル自体が作られない
        // （Logger.OpenWriterSafe参照）。
        var logPath = appPaths.GetLogFilePath(DateOnly.FromDateTime(DateTime.Now));
        File.Exists(logPath).Should().BeFalse("利用可能な機能について何もログへ残してはならない");
    }

    [Fact(DisplayName = "Loggerがnullでも例外にならない（後始末の順序等でロガー未生成の場合の保険）")]
    public void ロガーがnullでも例外にならない()
    {
        var service = new FakePlatformService { IsSupported = false, UnsupportedReason = "理由" };
        var act = () => PlatformDiagnosticsLogging.LogUnsupportedFeature(null, "タスクトレイ常駐", service);
        act.Should().NotThrow();
    }

    // ------------------------------------------------------------------
    // 依頼3（E709）
    // ------------------------------------------------------------------

    [Fact(DisplayName = "ハイコントラストが有効(true)のときE709付きでログへ記録される")]
    public async Task ハイコントラスト有効はE709付きで記録される()
    {
        var (appPaths, logger, ws) = CreateLogger();
        using var _ = ws;

        PlatformDiagnosticsLogging.LogHighContrastIfDetected(logger, isHighContrastActive: true);

        await logger.DisposeAsync().ConfigureAwait(true);

        var logText = await ReadLogTextAsync(appPaths).ConfigureAwait(true);
        logText.Should().Contain("E709", "E709付きで記録される必要がある");
        logText.Should().Contain("ハイコントラスト", "検出した内容が分かる文言を含める必要がある");
    }

    [Theory(DisplayName = "ハイコントラストが無効(false)・判定不能(null)のときは何も記録しない")]
    [InlineData(false)]
    [InlineData(null)]
    public async Task ハイコントラスト無効または判定不能は記録しない(bool? isHighContrastActive)
    {
        var (appPaths, logger, ws) = CreateLogger();
        using var _ = ws;

        PlatformDiagnosticsLogging.LogHighContrastIfDetected(logger, isHighContrastActive);

        await logger.DisposeAsync().ConfigureAwait(true);

        var logPath = appPaths.GetLogFilePath(DateOnly.FromDateTime(DateTime.Now));
        File.Exists(logPath).Should().BeFalse("無効または判定不能の場合は何もログへ残してはならない（9.3のとおり配色は切り替えないため、誤検出でも利用者を煩わせない）");
    }

    [Fact(DisplayName = "Loggerがnullでも例外にならない")]
    public void ハイコントラスト検出でロガーがnullでも例外にならない()
    {
        var act = () => PlatformDiagnosticsLogging.LogHighContrastIfDetected(null, true);
        act.Should().NotThrow();
    }
}
