using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Platform;
using Graft.Platform.Linux;
using Graft.Platform.Null;
using Graft.Platform.Windows;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 課題3（PC起動時の自動起動）の回帰テスト。仕様書2.1「レジストリ書き込みは行わない」に
/// 従うスタートアップフォルダ方式が、実際にファイルを作成・削除することを検証する。
/// Windows/Linuxいずれの実装もBCLのファイルI/Oのみで構成されるため、実行しているOSに
/// かかわらず両方を直接検証できる。
/// </summary>
public class AutoStartServiceTests
{
    // ------------------------------------------------------------------
    // Windows: スタートアップフォルダへ .cmd を配置する方式
    // ------------------------------------------------------------------

    [Fact(DisplayName = "Windows: 有効化するとスタートアップフォルダに実行ファイルを指す.cmdができる")]
    public void Windows_有効化するとcmdファイルができる()
    {
        using var ws = new TempWorkspace();
        var startupDir = ws.Combine("Startup");
        const string exePath = @"C:\Program Files\Graft\Graft.exe";
        var service = new WindowsAutoStartService(startupDir, () => exePath);

        service.IsRegistered.Should().BeFalse("最初は未登録のはず");

        var result = service.Enable();

        result.Success.Should().BeTrue();
        service.IsRegistered.Should().BeTrue();
        var scriptPath = Path.Combine(startupDir, "Graft.cmd");
        File.Exists(scriptPath).Should().BeTrue();
        var content = File.ReadAllText(scriptPath);
        content.Should().Contain(exePath, "Exec=相当の行が実際の実行ファイルパスを指す必要がある");
        content.Should().Contain("start", "コンソールウィンドウを残さず起動するためstartコマンドを使う");
    }

    [Fact(DisplayName = "Windows: 無効化するとcmdファイルが削除される")]
    public void Windows_無効化するとcmdファイルが削除される()
    {
        using var ws = new TempWorkspace();
        var startupDir = ws.Combine("Startup");
        var service = new WindowsAutoStartService(startupDir, () => @"C:\Graft\Graft.exe");
        service.Enable().Success.Should().BeTrue();

        var result = service.Disable();

        result.Success.Should().BeTrue();
        service.IsRegistered.Should().BeFalse();
        File.Exists(Path.Combine(startupDir, "Graft.cmd")).Should().BeFalse();
    }

    [Fact(DisplayName = "Windows: 未登録のまま無効化しても失敗にならない")]
    public void Windows_未登録のまま無効化しても成功する()
    {
        using var ws = new TempWorkspace();
        var service = new WindowsAutoStartService(ws.Combine("Startup"), () => @"C:\Graft\Graft.exe");

        service.Disable().Success.Should().BeTrue();
    }

    [Fact(DisplayName = "Windows: アプリを別の場所へ移動した後に再度有効化すると新しいパスへ書き直される")]
    public void Windows_再登録すると新しいパスへ上書きされる()
    {
        using var ws = new TempWorkspace();
        var startupDir = ws.Combine("Startup");
        var oldPath = @"C:\Old\Graft.exe";
        var newPath = @"D:\New\Graft.exe";

        new WindowsAutoStartService(startupDir, () => oldPath).Enable().Success.Should().BeTrue();
        var scriptPath = Path.Combine(startupDir, "Graft.cmd");
        File.ReadAllText(scriptPath).Should().Contain(oldPath);

        // アプリを移動した後、設定画面でオン・オフし直す想定（オンに戻す＝再度Enable）。
        new WindowsAutoStartService(startupDir, () => newPath).Enable().Success.Should().BeTrue();

        var content = File.ReadAllText(scriptPath);
        content.Should().Contain(newPath);
        content.Should().NotContain(oldPath, "古いパスの残骸が残ってはならない");
    }

    // ------------------------------------------------------------------
    // Linux: XDG autostart仕様の .desktop ファイルを配置する方式
    // ------------------------------------------------------------------

    [Fact(DisplayName = "Linux: 有効化するとautostartディレクトリにgraft.desktopができ、Execが実行ファイルパスを指す")]
    public void Linux_有効化するとdesktopファイルができる()
    {
        using var ws = new TempWorkspace();
        var autostartDir = ws.Combine("autostart");
        const string exePath = "/opt/graft/Graft";
        var service = new LinuxAutoStartService(autostartDir, () => exePath);

        service.IsRegistered.Should().BeFalse();

        var result = service.Enable();

        result.Success.Should().BeTrue();
        service.IsRegistered.Should().BeTrue();
        var desktopPath = Path.Combine(autostartDir, "graft.desktop");
        File.Exists(desktopPath).Should().BeTrue();
        var content = File.ReadAllText(desktopPath);
        content.Should().Contain("[Desktop Entry]");
        content.Should().Contain("Type=Application");
        content.Should().Contain($"Exec=\"{exePath}\"", "Exec=が実際の実行ファイルの絶対パスを指す必要がある");
    }

    [Fact(DisplayName = "Linux: 無効化するとgraft.desktopが削除される")]
    public void Linux_無効化するとdesktopファイルが削除される()
    {
        using var ws = new TempWorkspace();
        var autostartDir = ws.Combine("autostart");
        var service = new LinuxAutoStartService(autostartDir, () => "/opt/graft/Graft");
        service.Enable().Success.Should().BeTrue();

        var result = service.Disable();

        result.Success.Should().BeTrue();
        service.IsRegistered.Should().BeFalse();
        File.Exists(Path.Combine(autostartDir, "graft.desktop")).Should().BeFalse();
    }

    [Fact(DisplayName = "Linux: 未登録のまま無効化しても失敗にならない")]
    public void Linux_未登録のまま無効化しても成功する()
    {
        using var ws = new TempWorkspace();
        var service = new LinuxAutoStartService(ws.Combine("autostart"), () => "/opt/graft/Graft");

        service.Disable().Success.Should().BeTrue();
    }

    [Fact(DisplayName = "Linux: アプリを別の場所へ移動した後に再度有効化すると新しいパスへ書き直される")]
    public void Linux_再登録すると新しいパスへ上書きされる()
    {
        using var ws = new TempWorkspace();
        var autostartDir = ws.Combine("autostart");
        var oldPath = "/opt/old/Graft";
        var newPath = "/home/user/apps/Graft";

        new LinuxAutoStartService(autostartDir, () => oldPath).Enable().Success.Should().BeTrue();
        var desktopPath = Path.Combine(autostartDir, "graft.desktop");
        File.ReadAllText(desktopPath).Should().Contain(oldPath);

        new LinuxAutoStartService(autostartDir, () => newPath).Enable().Success.Should().BeTrue();

        var content = File.ReadAllText(desktopPath);
        content.Should().Contain(newPath);
        content.Should().NotContain(oldPath, "古いパスの残骸が残ってはならない");
    }

    // ------------------------------------------------------------------
    // Null実装（未対応環境）: 静かに失敗するのではなく、失敗を明示する
    // ------------------------------------------------------------------

    [Fact(DisplayName = "未対応環境ではIsSupportedがfalseで、Enableは理由付きで失敗する")]
    public void 未対応環境ではEnableが理由付きで失敗する()
    {
        var service = new NullAutoStartService();

        service.IsSupported.Should().BeFalse();
        service.UnsupportedReason.Should().NotBeNullOrEmpty();

        var result = service.Enable();

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrEmpty("黙って失敗させず理由を伝える必要がある");
    }
}
