using System.IO;
using FluentAssertions;
using Graft.Core;
using Graft.Features;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// <c>AppPaths.VbaMirrorsDirectory</c>／<c>VbaSyncDirectory</c> と、データ保存先の移行・旧保存先の掃除の
/// テスト（仕様書_VBA連携 4.4・10.1）。ミラーと同期状態が移行の対象から漏れると、保存先を切り替えた途端に
/// ミラーが消えたように見え、旧保存先に取り残される。
/// <para>
/// 移行後の Root と Id の付け替え（<c>projects.json</c>・<c>back/</c>・<c>vba-sync/</c>）は P2 で実装する。
/// ここでは付け替えの純計算（<see cref="Graft.Core.Vba.VbaMirrorFolderName.RelocateRoot"/>、別ファイルで検証）と、
/// 今回入れた「移行対象への追加」だけを検証する。
/// </para>
/// </summary>
public class VbaDataDirectoryTests
{
    [Fact(DisplayName = "VbaMirrorsDirectory／VbaSyncDirectory は BaseDirectory 直下の vba-mirrors／vba-sync")]
    public void パスの定義()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "base"));

        paths.VbaMirrorsDirectory.Should().Be(Path.Combine(paths.BaseDirectory, "vba-mirrors"));
        paths.VbaSyncDirectory.Should().Be(Path.Combine(paths.BaseDirectory, "vba-sync"));
    }

    [Fact(DisplayName = "移行: vba-mirrors/ と vba-sync/ の中身（ミラーのモジュール、同期状態、控え）が新しい保存先へコピーされる")]
    public void 移行でミラーと同期状態がコピーされる()
    {
        using var ws = new TempWorkspace();
        var sourceDir = ws.CreateDirectory("source");
        var targetDir = ws.Combine("target");
        ws.WriteText("source/settings.json", "{}");
        ws.WriteText("source/vba-mirrors/計画-1a2b3c4d/Modules/Module1.bas", "Sub A()\r\nEnd Sub");
        ws.WriteText("source/vba-mirrors/計画-1a2b3c4d/Documents/Sheet1.cls", "' sheet");
        ws.WriteText("source/vba-sync/p_aaaaaa.json", "{ \"version\": 1 }");
        ws.WriteText("source/vba-sync/p_aaaaaa.rescue/Module1-20261121.txt", "控え");

        var result = DataDirectoryMigrator.Migrate(sourceDir, targetDir);

        result.IsSuccess.Should().BeTrue();
        File.ReadAllText(Path.Combine(targetDir, "vba-mirrors", "計画-1a2b3c4d", "Modules", "Module1.bas")).Should().Be("Sub A()\r\nEnd Sub");
        File.Exists(Path.Combine(targetDir, "vba-mirrors", "計画-1a2b3c4d", "Documents", "Sheet1.cls")).Should().BeTrue();
        File.Exists(Path.Combine(targetDir, "vba-sync", "p_aaaaaa.json")).Should().BeTrue();
        File.ReadAllText(Path.Combine(targetDir, "vba-sync", "p_aaaaaa.rescue", "Module1-20261121.txt")).Should().Be("控え");
        File.Exists(Path.Combine(sourceDir, "vba-mirrors", "計画-1a2b3c4d", "Modules", "Module1.bas"))
            .Should().BeTrue("コピー元には触れない");
    }

    [Fact(DisplayName = "移行のあとで旧保存先に書かれたミラーの変更も、旧保存先を消す前に取り込み直される")]
    public void 移行後の変更も取り込み直される()
    {
        using var ws = new TempWorkspace();
        var oldDir = ws.CreateDirectory("old");
        var newDir = ws.Combine("new");
        ws.WriteText("old/vba-mirrors/book-1/Modules/A.bas", "Sub A()\r\nEnd Sub");

        DataDirectoryMigrator.MigrateAndSwitchPointer(oldDir, oldDir, newDir, switchToPortable: false)
            .IsSuccess.Should().BeTrue();

        // 再起動を後回しにして使い続けた間に、旧保存先のミラーが更新された状況。
        ws.WriteText("old/vba-mirrors/book-1/Modules/A.bas", "Sub A()\r\n  MsgBox 1\r\nEnd Sub");

        DataDirectoryMigrator.RunPendingCleanup(newDir).Result.Should().Be(DataDirectoryMigrator.PendingCleanupResult.Completed);

        File.ReadAllText(Path.Combine(newDir, "vba-mirrors", "book-1", "Modules", "A.bas"))
            .Should().Be("Sub A()\r\n  MsgBox 1\r\nEnd Sub");
        Directory.Exists(Path.Combine(oldDir, "vba-mirrors")).Should().BeFalse();
    }

    [Fact(DisplayName = "HasKnownContents: vba-mirrors/ か vba-sync/ だけがあるフォルダも「Graft のデータがある場所」と判定される")]
    public void 既知のデータの判定にも含まれる()
    {
        using var ws = new TempWorkspace();
        ws.CreateDirectory("only-mirrors/vba-mirrors");
        ws.CreateDirectory("only-sync/vba-sync");
        ws.CreateDirectory("empty");

        DataDirectoryMigrator.HasKnownContents(ws.Combine("only-mirrors")).Should().BeTrue();
        DataDirectoryMigrator.HasKnownContents(ws.Combine("only-sync")).Should().BeTrue();
        DataDirectoryMigrator.HasKnownContents(ws.Combine("empty")).Should().BeFalse();
    }

    [Fact(DisplayName = "旧保存先の掃除: 移行後の次回起動で、旧保存先の vba-mirrors/ と vba-sync/ が消え、無関係なファイルは残る")]
    public void 旧保存先の掃除で消える()
    {
        using var ws = new TempWorkspace();
        var oldDir = ws.CreateDirectory("old");
        var newDir = ws.Combine("new");
        ws.WriteText("old/settings.json", "{}");
        ws.WriteText("old/vba-mirrors/book-1/Modules/A.bas", "Sub A()\r\nEnd Sub");
        ws.WriteText("old/vba-sync/p_aaaaaa.json", "{}");
        ws.WriteText("old/memo.txt", "利用者のメモ");

        var migrated = DataDirectoryMigrator.MigrateAndSwitchPointer(
            oldDir, sourceDirectory: oldDir, targetDirectory: newDir, switchToPortable: false);
        migrated.IsSuccess.Should().BeTrue();
        Directory.Exists(Path.Combine(oldDir, "vba-mirrors")).Should().BeTrue("移行のその場では消さない");

        var outcome = DataDirectoryMigrator.RunPendingCleanup(newDir);

        outcome.Result.Should().Be(DataDirectoryMigrator.PendingCleanupResult.Completed);
        Directory.Exists(Path.Combine(oldDir, "vba-mirrors")).Should().BeFalse();
        Directory.Exists(Path.Combine(oldDir, "vba-sync")).Should().BeFalse();
        File.Exists(Path.Combine(oldDir, "memo.txt")).Should().BeTrue();
        File.Exists(Path.Combine(newDir, "vba-mirrors", "book-1", "Modules", "A.bas")).Should().BeTrue();
    }

    [Fact(DisplayName = "データ保存先の配下のミラーを PathGuard が拒否しない（適用が通る）。ProjectStore も登録できる")]
    public async Task 保存先の配下のミラーは書き込める()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var mirror = Path.Combine(paths.VbaMirrorsDirectory, "計画-1a2b3c4d");
        Directory.CreateDirectory(Path.Combine(mirror, "Modules"));

        // 登録検査と PathGuard が拒否するのは、ルートが保存先の「祖先（または同じ）」になる場合だけ。
        var registered = await new ProjectStore(paths).RegisterAsync(mirror, null);
        registered.IsSuccess.Should().BeTrue("ミラーは保存先の配下にあるだけで、保存先の設定ファイルには到達できない");

        var previous = PathGuard.ProtectedDataDirectory;
        try
        {
            PathGuard.ProtectedDataDirectory = paths.BaseDirectory;
            var guard = new PathGuard(mirror, new PathGuardOptions
            {
                AllowedExtensions = Graft.Core.Vba.VbaExtensionPolicy.Effective(new SafetySettings().AllowedExtensions, isVbaProject: true),
            });

            var resolved = guard.Resolve("Modules/Module1.bas");

            resolved.IsSuccess.Should().BeTrue(string.Join(" / ", resolved.Issues.Select(i => i.ToDisplayText())));
            guard.Resolve("Classes/Cls.cls").IsSuccess.Should().BeTrue();
            guard.Resolve("Forms/UserForm1.frm").IsSuccess.Should().BeTrue();
            guard.Resolve("Documents/Sheet1.cls").IsSuccess.Should().BeTrue();
        }
        finally
        {
            PathGuard.ProtectedDataDirectory = previous;
        }
    }

    [Fact(DisplayName = "対照: 実効の許可拡張子を渡さないと .bas は E202（VbaExtensionPolicy が必要な理由）")]
    public void 拡張子を足さないとE202()
    {
        using var ws = new TempWorkspace();
        var mirror = ws.CreateDirectory("mirror");
        var guard = new PathGuard(mirror, new PathGuardOptions { AllowedExtensions = new SafetySettings().AllowedExtensions });

        var result = guard.Resolve("Modules/Module1.bas");

        result.IsSuccess.Should().BeFalse();
        result.Errors.Single().Code.Should().Be(ErrorCode.E202);
    }
}
