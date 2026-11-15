using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
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
/// Windows実装はショートカットの書き込み（COM）を<c>IShortcutWriter</c>の裏に置いてあるため、
/// 差し替えを渡せばLinux上でも検証できる。Linux実装はBCLのファイルI/Oのみで構成される。
/// </summary>
public class AutoStartServiceTests
{
    // ------------------------------------------------------------------
    // Windows: スタートアップフォルダへ .lnk（ショートカット）を配置する方式
    // ------------------------------------------------------------------

    private const string JapaneseExePath = @"C:\追加\Graft\Graft.exe";

    /// <summary>
    /// COM を使わない差し替え。呼び出しを記録し、本物と同じく .lnk の実ファイルを作る
    /// （サービスが「実物ができたか」を確かめるため）。<see cref="Failure"/> があれば投げる。
    /// </summary>
    private sealed class FakeShortcutWriter : IShortcutWriter
    {
        public List<(string ShortcutPath, string Target, string WorkingDirectory, string Description)> Calls { get; } = new();

        public Exception? Failure { get; init; }

        public void Write(string shortcutPath, string targetPath, string workingDirectory, string description)
        {
            Calls.Add((shortcutPath, targetPath, workingDirectory, description));
            if (Failure is not null) throw Failure;
            File.WriteAllText(shortcutPath, "lnk");
        }
    }

    [Fact(DisplayName = "Windows: 有効化するとスタートアップフォルダにGraft.lnkを書く（日本語のパスでも対象と作業フォルダが正しい）")]
    public void Windows_有効化するとlnkを書く()
    {
        using var ws = new TempWorkspace();
        var startupDir = ws.Combine("Startup");
        var writer = new FakeShortcutWriter();
        // 実際の不具合は C:\追加\Graft\Graft.exe。Path.GetDirectoryName は Linux では「\」で区切らないため、
        // このテストだけは、同じ構造（日本語のフォルダ配下）を実行中のOSの区切りで組み立てて確かめる。
        var workDir = Path.Combine(ws.Combine("追加"), "Graft");
        var exePath = Path.Combine(workDir, "Graft.exe");
        var service = new WindowsAutoStartService(startupDir, () => exePath, writer);

        service.IsRegistered.Should().BeFalse("最初は未登録のはず");

        var result = service.Enable();

        result.Success.Should().BeTrue();
        service.IsRegistered.Should().BeTrue();
        var call = writer.Calls.Should().ContainSingle().Subject;
        call.ShortcutPath.Should().Be(Path.Combine(startupDir, "Graft.lnk"));
        call.Target.Should().Be(exePath);
        call.WorkingDirectory.Should().Be(workDir);
        call.Description.Should().Be("Graft");
    }

    [Fact(DisplayName = "Windows: 古いGraft.cmdがある状態で有効化すると、.lnkができて.cmdは消える")]
    public void Windows_有効化すると古いcmdが消える()
    {
        using var ws = new TempWorkspace();
        var startupDir = ws.Combine("Startup");
        Directory.CreateDirectory(startupDir);
        var cmdPath = Path.Combine(startupDir, "Graft.cmd");
        File.WriteAllText(cmdPath, "@echo off");
        var service = new WindowsAutoStartService(startupDir, () => JapaneseExePath, new FakeShortcutWriter());
        service.IsRegistered.Should().BeTrue("古い.cmdだけでも登録済みとみなす");

        // 起動時の StartupCoordinator が自動起動オンのたびに Enable を呼び直す流れの再現。
        service.Enable().Success.Should().BeTrue();

        File.Exists(Path.Combine(startupDir, "Graft.lnk")).Should().BeTrue();
        File.Exists(cmdPath).Should().BeFalse("更新後の最初の起動で.cmdが.lnkに置き換わるはず");
    }

    [Fact(DisplayName = "Windows: 無効化すると.lnkと古い.cmdの両方が消える")]
    public void Windows_無効化すると両方消える()
    {
        using var ws = new TempWorkspace();
        var startupDir = ws.Combine("Startup");
        var service = new WindowsAutoStartService(startupDir, () => JapaneseExePath, new FakeShortcutWriter());
        service.Enable().Success.Should().BeTrue();
        File.WriteAllText(Path.Combine(startupDir, "Graft.cmd"), "@echo off");

        var result = service.Disable();

        result.Success.Should().BeTrue();
        service.IsRegistered.Should().BeFalse();
        File.Exists(Path.Combine(startupDir, "Graft.lnk")).Should().BeFalse();
        File.Exists(Path.Combine(startupDir, "Graft.cmd")).Should().BeFalse();
    }

    [Fact(DisplayName = "Windows: 未登録のまま無効化しても失敗にならない")]
    public void Windows_未登録のまま無効化しても成功する()
    {
        using var ws = new TempWorkspace();
        var service = new WindowsAutoStartService(ws.Combine("Startup"), () => JapaneseExePath, new FakeShortcutWriter());

        service.Disable().Success.Should().BeTrue();
    }

    [Theory(DisplayName = "Windows: IsRegisteredは.lnkか.cmdのどちらかがあればtrue")]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public void Windows_IsRegisteredの判定(bool hasLnk, bool hasCmd, bool expected)
    {
        using var ws = new TempWorkspace();
        var startupDir = ws.Combine("Startup");
        Directory.CreateDirectory(startupDir);
        if (hasLnk) File.WriteAllText(Path.Combine(startupDir, "Graft.lnk"), "lnk");
        if (hasCmd) File.WriteAllText(Path.Combine(startupDir, "Graft.cmd"), "@echo off");
        var service = new WindowsAutoStartService(startupDir, () => JapaneseExePath, new FakeShortcutWriter());

        service.IsRegistered.Should().Be(expected);
    }

    [Fact(DisplayName = "Windows: ショートカットの作成が失敗すると失敗を返し、古い.cmdは残る")]
    public void Windows_作成失敗時は失敗を返し古いcmdを残す()
    {
        using var ws = new TempWorkspace();
        var startupDir = ws.Combine("Startup");
        Directory.CreateDirectory(startupDir);
        var cmdPath = Path.Combine(startupDir, "Graft.cmd");
        File.WriteAllText(cmdPath, "@echo off");
        var writer = new FakeShortcutWriter { Failure = new COMException("失敗", unchecked((int)0x80004005)) };
        var service = new WindowsAutoStartService(startupDir, () => JapaneseExePath, writer);

        var result = service.Enable();

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrEmpty("利用者に分かる理由を返す必要がある");
        File.Exists(cmdPath).Should().BeTrue("動いていた人の自動起動を失わないため、.cmdは消さない");
        File.Exists(Path.Combine(startupDir, "Graft.lnk")).Should().BeFalse();
    }

    [Fact(DisplayName = "Windows: 書いたはずの.lnkが実在しなければ失敗を返す")]
    public void Windows_lnkが無ければ失敗を返す()
    {
        using var ws = new TempWorkspace();
        var startupDir = ws.Combine("Startup");
        var service = new WindowsAutoStartService(startupDir, () => JapaneseExePath, new NoFileShortcutWriter());

        var result = service.Enable();

        result.Success.Should().BeFalse();
        service.IsRegistered.Should().BeFalse();
    }

    private sealed class NoFileShortcutWriter : IShortcutWriter
    {
        public void Write(string shortcutPath, string targetPath, string workingDirectory, string description)
        {
            // 何も書かない（書いたつもりで実物が無い状況）。
        }
    }

    [Fact(DisplayName = "Windows: 実行ファイルのパスが取れないときは失敗を返し、何も書かない")]
    public void Windows_実行ファイルのパスが取れないと失敗する()
    {
        using var ws = new TempWorkspace();
        var writer = new FakeShortcutWriter();
        var service = new WindowsAutoStartService(ws.Combine("Startup"), () => string.Empty, writer);

        var result = service.Enable();

        result.Success.Should().BeFalse();
        writer.Calls.Should().BeEmpty();
    }

    [Fact(DisplayName = "Windows: 再度有効化すると新しいパスへ書き直される")]
    public void Windows_再登録すると新しいパスで書く()
    {
        using var ws = new TempWorkspace();
        var startupDir = ws.Combine("Startup");
        var writer = new FakeShortcutWriter();

        new WindowsAutoStartService(startupDir, () => @"C:\Old\Graft.exe", writer).Enable().Success.Should().BeTrue();
        new WindowsAutoStartService(startupDir, () => @"D:\New\Graft.exe", writer).Enable().Success.Should().BeTrue();

        writer.Calls.Select(c => c.Target).Should().Equal(@"C:\Old\Graft.exe", @"D:\New\Graft.exe");
    }

    [Fact(DisplayName = "Windows実機: 日本語を含むフォルダに本物の.lnkを作ると、対象パスを読み戻せる")]
    public void Windows実機_本物のlnkを作って読み戻せる()
    {
        // COM を使う本物の実装は Windows でしか動かない。CI は Linux なので、ここでは何もしない。
        if (!OperatingSystem.IsWindows()) return;

        using var ws = new TempWorkspace();
        var dir = ws.Combine("追加");
        Directory.CreateDirectory(dir);
        var shortcutPath = Path.Combine(dir, "Graft.lnk");
        var target = Path.Combine(dir, "Graft.exe");

        new WindowsShortcutWriter().Write(shortcutPath, target, dir, "Graft");

        File.Exists(shortcutPath).Should().BeTrue();
        var link = (IShellLinkW)new ShellLinkCoClass();
        try
        {
            ((IPersistFile)link).Load(shortcutPath, 0);
            var sb = new StringBuilder(1024);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            sb.ToString().Should().Be(target);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
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
