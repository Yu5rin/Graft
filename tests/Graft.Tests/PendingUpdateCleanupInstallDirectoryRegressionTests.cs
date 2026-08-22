using System.IO;
using FluentAssertions;
using Graft.Core.Update;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// v1.0.10利用者からの実機報告「フォルダの中が.oldで溢れる。古いファイルは消してください」の
/// 回帰テスト。
///
/// 【原因】<see cref="Views.StartupCoordinator.StartAsync"/>が、次回起動時の
/// <c>*.old</c>掃除（<see cref="PendingUpdateCleanup.Run"/>）に渡すディレクトリとして
/// <see cref="AppPaths.BaseDirectory"/>（settings.json等の"データ保存先"）を使っていた。
/// しかし<c>.old</c>ファイルができるのは常に"実行ファイルが実際に置かれているフォルダ"
/// （<see cref="Core.Update.SelfUpdateInstaller"/>がGraft.exe等をリネームする場所）であり、
/// 両者は別物。「設定画面からデータ保存先をユーザーフォルダへ移動」した環境では実行ファイルの
/// フォルダにできた.oldへ一切触れられず、更新のたびに.oldが積み上がり続けていた。
///
/// これはPR #41（<see cref="UpdateInstallDirectoryRegressionTests"/>参照）で修正したのと
/// 全く同じ種類の取り違えで、あちらは<c>SettingsViewModel.Update.cs</c>（更新の"インストール先"）
/// だけを直し、この掃除処理（<c>StartupCoordinator.cs</c>）は取り残されていた。
///
/// <see cref="StartupCoordinator.StartAsync"/>自体はトレイ・ホットキー等の実OS資源に触れるため
/// 単体テストの対象にできない（既存の<c>ShutdownLoggingTests</c>のコメント参照、実機（Xvfb）での
/// 起動確認で別途担保する）。ここでは修正後にStartAsyncが実際に使う解決経路
/// （<see cref="AppRestart.TryResolveExecutableDirectory"/>）を直接検証し、
/// (1) 修正前の誤り（データ保存先を渡す）では実行ファイル側の.oldが掃除されず、
/// (2) 修正後（実行ファイルのフォルダを渡す）では掃除されることを固定する。
/// </summary>
public class PendingUpdateCleanupInstallDirectoryRegressionTests
{
    [Fact(DisplayName = "不具合再現: データ保存先（AppPaths.BaseDirectory）を渡すと、実行ファイル側の.oldは掃除されない")]
    public void 修正前_データ保存先を渡すと実行ファイル側のoldが残る()
    {
        using var ws = new TempWorkspace();
        var exeDir = ws.CreateDirectory("exe");
        var userDataDir = ws.CreateDirectory("userdata");
        // 「設定画面からデータ保存先をユーザーフォルダへ移動」を実行済みの状態を、
        // ポインタファイルで模す（UpdateInstallDirectoryRegressionTestsと同じ手法）。
        DataDirectoryPointer.TryWrite(exeDir, userDataDir).Should().BeTrue();
        foreach (var fileName in UpdateFiles.RequiredFileNames)
        {
            File.WriteAllText(Path.Combine(exeDir, fileName + UpdateFiles.OldFileSuffix), "old");
        }

        // 修正前のStartupCoordinator.StartAsyncが実際に渡していた値。
        var buggyCleanupDirectory = AppPaths.ResolveBaseDirectory(exeDir);
        buggyCleanupDirectory.Should().Be(userDataDir, "移行済みならBaseDirectoryはユーザーフォルダを指す");

        var removed = PendingUpdateCleanup.Run(buggyCleanupDirectory);

        removed.Should().BeEmpty("データ保存先には.oldが存在しないため、削除できたものが無い");
        foreach (var fileName in UpdateFiles.RequiredFileNames)
        {
            File.Exists(Path.Combine(exeDir, fileName + UpdateFiles.OldFileSuffix)).Should().BeTrue(
                "実行ファイル側に残った.oldは、データ保存先を渡す限り永久に掃除されない（今回の不具合そのもの）");
        }
    }

    [Fact(DisplayName = "修正後: AppRestart.TryResolveExecutableDirectoryを渡すと、実行ファイル側の.oldが掃除される")]
    public void 修正後_実行ファイルのフォルダを渡すとoldが掃除される()
    {
        using var ws = new TempWorkspace();
        var exeDir = ws.CreateDirectory("exe");
        var userDataDir = ws.CreateDirectory("userdata");
        DataDirectoryPointer.TryWrite(exeDir, userDataDir).Should().BeTrue();
        foreach (var fileName in UpdateFiles.RequiredFileNames)
        {
            File.WriteAllText(Path.Combine(exeDir, fileName + UpdateFiles.OldFileSuffix), "old");
        }
        // AppRestart.TryResolveExecutableDirectoryはFile.Existsで実在確認するため、
        // ダミーの実行ファイル本体も置いておく（.oldではなく本体）。
        var fakeExePath = ws.WriteText("exe/Graft.exe", "dummy");

        // StartupCoordinator.StartAsync修正後の実際の呼び出しと同じ解決経路。
        var fixedCleanupDirectory = AppRestart.TryResolveExecutableDirectory(fakeExePath);
        fixedCleanupDirectory.Should().Be(exeDir, "実行ファイルの場所はデータ保存先の移行の影響を受けない");
        fixedCleanupDirectory.Should().NotBe(AppPaths.ResolveBaseDirectory(exeDir),
            "この食い違いこそが今回の不具合の直接の原因");

        var removed = PendingUpdateCleanup.Run(fixedCleanupDirectory!);

        removed.Should().BeEquivalentTo(UpdateFiles.RequiredFileNames);
        foreach (var fileName in UpdateFiles.RequiredFileNames)
        {
            File.Exists(Path.Combine(exeDir, fileName + UpdateFiles.OldFileSuffix)).Should().BeFalse(
                "修正後は実行ファイル側の.oldがきちんと掃除される");
        }
        // データ保存先（settings.json相当が置かれうる場所）には一切触れないはず。
        Directory.GetFileSystemEntries(userDataDir).Should().BeEmpty("掃除はデータ保存先を一切経由しないはず");
    }

    [Fact(DisplayName = "ポータブル運用（移行していない）なら、修正前後どちらの解決経路でも同じ結果になる")]
    public void ポータブル運用では修正前後で差が無い()
    {
        using var ws = new TempWorkspace();
        var exeDir = ws.CreateDirectory("portable-exe");
        foreach (var fileName in UpdateFiles.RequiredFileNames)
        {
            File.WriteAllText(Path.Combine(exeDir, fileName + UpdateFiles.OldFileSuffix), "old");
        }
        var fakeExePath = ws.WriteText("portable-exe/Graft.exe", "dummy");

        var dataDirectory = AppPaths.ResolveBaseDirectory(exeDir);
        var executableDirectory = AppRestart.TryResolveExecutableDirectory(fakeExePath);

        dataDirectory.Should().Be(executableDirectory, "ポータブル運用では両者が一致するため、この不具合は表面化しない");

        var removed = PendingUpdateCleanup.Run(executableDirectory!);
        removed.Should().BeEquivalentTo(UpdateFiles.RequiredFileNames);
    }
}
