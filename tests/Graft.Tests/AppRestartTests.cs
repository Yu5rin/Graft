using FluentAssertions;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 不具合3の回帰テスト。<see cref="AppRestart"/>は実行ファイルパスの解決とプロセス起動情報の
/// 組み立てのみを担う純粋な部分（実際に<see cref="System.Diagnostics.Process.Start(System.Diagnostics.ProcessStartInfo)"/>
/// は呼ばない）。単一ファイル発行では<see cref="System.Reflection.Assembly.Location"/>が空になる
/// ため<see cref="Environment.ProcessPath"/>を使う必要がある、という前提を、パスを差し替えて検証する。
/// </summary>
public class AppRestartTests
{
    [Fact(DisplayName = "実行ファイルが実在するパスならCanRestartはtrue、BuildStartInfoも組み立てられる")]
    public void 実在するパスなら再起動できる()
    {
        using var ws = new TempWorkspace();
        var exePath = ws.WriteText("fake-graft.exe", "dummy");

        AppRestart.CanRestart(exePath).Should().BeTrue();

        var startInfo = AppRestart.BuildStartInfo(exePath);
        startInfo.Should().NotBeNull();
        startInfo!.FileName.Should().Be(exePath);
        startInfo.UseShellExecute.Should().BeFalse();
        startInfo.WorkingDirectory.Should().Be(Path.GetDirectoryName(exePath));
    }

    [Fact(DisplayName = "パスが空文字ならCanRestartはfalse、BuildStartInfoはnullを返す（手動再起動へ倒す）")]
    public void パスが空なら再起動できない()
    {
        // nullは「省略」と区別できない（processPathの既定値がnullのため）ので、
        // Environment.ProcessPathへフォールバックする（省略時はEnvironment.ProcessPathを使うテスト参照）。
        // ここでは「パスは指定されたが解決できない」ケースを空文字で表す。
        AppRestart.CanRestart(string.Empty).Should().BeFalse();
        AppRestart.BuildStartInfo(string.Empty).Should().BeNull();
    }

    [Fact(DisplayName = "パスが指すファイルが実在しなければCanRestartはfalse（実行ファイルが移動・削除された場合）")]
    public void 実在しないファイルは再起動できない()
    {
        using var ws = new TempWorkspace();
        var missing = ws.Combine("does-not-exist.exe");

        AppRestart.CanRestart(missing).Should().BeFalse();
        AppRestart.BuildStartInfo(missing).Should().BeNull();
    }

    [Fact(DisplayName = "processPathを省略するとEnvironment.ProcessPathを使う（現在のテストホストで実在確認できる）")]
    public void 省略時はEnvironmentProcessPathを使う()
    {
        // dotnet testのテストホスト自体は実在する実行ファイルであるはずなので、
        // 既定の解決先（Environment.ProcessPath）を使ってもtrueになる。
        AppRestart.CanRestart().Should().BeTrue();
    }
}
