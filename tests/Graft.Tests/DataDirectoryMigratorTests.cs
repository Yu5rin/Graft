using System.IO;
using System.Runtime.Versioning;
using FluentAssertions;
using Graft.Core;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 機能3（データ保存先の選択）の回帰テスト。<see cref="DataDirectoryMigrator"/>による
/// 「既存データ一式のコピー」「検証」「不完全ならポインタファイルを書かない」の3点を検証する。
/// </summary>
public class DataDirectoryMigratorTests
{
    [Fact(DisplayName = "settings.json・projects.json・back/・logs/ をすべて新しい場所へコピーする")]
    public void 既存データ一式をコピーする()
    {
        using var ws = new TempWorkspace();
        var sourceDir = ws.CreateDirectory("source");
        var targetDir = ws.Combine("target"); // まだ存在しない状態から始める

        ws.WriteText("source/settings.json", "{\"theme\":\"dark\"}");
        ws.WriteText("source/projects.json", "[]");
        ws.WriteText("source/templates.json", "[]");
        ws.WriteText("source/queue.json", "[]");
        ws.WriteText("source/back/proj1/r1_20260101_000000/manifest.json", "{}");
        ws.WriteText("source/logs/20260101.log", "{\"eventType\":\"startup\"}");

        var result = DataDirectoryMigrator.Migrate(sourceDir, targetDir);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(targetDir);

        File.Exists(Path.Combine(targetDir, "settings.json")).Should().BeTrue();
        File.Exists(Path.Combine(targetDir, "projects.json")).Should().BeTrue();
        File.Exists(Path.Combine(targetDir, "templates.json")).Should().BeTrue();
        File.Exists(Path.Combine(targetDir, "queue.json")).Should().BeTrue();
        File.Exists(Path.Combine(targetDir, "back", "proj1", "r1_20260101_000000", "manifest.json")).Should().BeTrue();
        File.Exists(Path.Combine(targetDir, "logs", "20260101.log")).Should().BeTrue();

        // 元データは一切変更されない（コピー元には触れない安全方針）。
        File.Exists(Path.Combine(sourceDir, "settings.json")).Should().BeTrue("元データを消してはいけない");
    }

    /// <summary>
    /// 不具合2の回帰テスト。「保存先を変えたらチュートリアルがまた出る」という利用者報告の直接の
    /// 原因は、初回起動ガイドの完了マーカー（<see cref="AppPaths.OnboardingMarkerFilePath"/>、
    /// <see cref="Views.OnboardingWindow.HasCompleted"/>が存在確認するファイル）が移行対象一覧に
    /// 含まれておらず、移行後の新しい場所に無いままだったこと。同じくAppPathsにプロパティが無く
    /// 見落とされていたウィンドウレイアウト（<see cref="AppPaths.WindowLayoutFilePath"/>）も
    /// 合わせて確認する。
    /// </summary>
    [Fact(DisplayName = "初回起動ガイドの完了マーカー（onboarding.done）とウィンドウレイアウト（layout.json）も移行後に引き継がれる")]
    public void オンボーディング完了マーカーとレイアウトが引き継がれる()
    {
        using var ws = new TempWorkspace();
        var sourceDir = ws.CreateDirectory("source");
        var targetDir = ws.Combine("target");

        ws.WriteText("source/settings.json", "{}");
        ws.WriteText("source/onboarding.done", string.Empty);
        ws.WriteText("source/layout.json", "{\"width\":1024}");

        var result = DataDirectoryMigrator.Migrate(sourceDir, targetDir);

        result.IsSuccess.Should().BeTrue();

        var sourcePaths = new AppPaths(sourceDir);
        var targetPaths = new AppPaths(targetDir);

        File.Exists(sourcePaths.OnboardingMarkerFilePath).Should().BeTrue("前提: 移行元にマーカーがある");
        File.Exists(targetPaths.OnboardingMarkerFilePath).Should().BeTrue(
            "移行後もOnboardingWindow.HasCompletedがtrueを返せるよう、マーカーファイルを引き継ぐ必要がある");

        File.Exists(targetPaths.WindowLayoutFilePath).Should().BeTrue("ウィンドウレイアウトも引き継ぐ必要がある");
        File.ReadAllText(targetPaths.WindowLayoutFilePath).Should().Contain("1024");

        // Views.OnboardingWindow.HasCompleted(appPaths) は
        // File.Exists(appPaths.OnboardingMarkerFilePath) と等価（Views層はAvalonia依存のため
        // Graft.Tests には取り込めない。実際のHasCompleted経由の確認は
        // Graft.UiTests側のシナリオテストで行う）。
        File.Exists(targetPaths.OnboardingMarkerFilePath).Should().BeTrue(
            "移行後に初回起動ガイドが再表示されてはならない（不具合2）");
    }

    [Fact(DisplayName = "対象ファイルが元々存在しない項目（未使用のtemplates.json等）はコピーしなくても成功扱いにする")]
    public void 存在しない項目はコピー対象外として成功扱いになる()
    {
        using var ws = new TempWorkspace();
        var sourceDir = ws.CreateDirectory("source");
        var targetDir = ws.Combine("target");
        ws.WriteText("source/settings.json", "{}"); // settings.jsonだけ存在する最小構成

        var result = DataDirectoryMigrator.Migrate(sourceDir, targetDir);

        result.IsSuccess.Should().BeTrue();
        File.Exists(Path.Combine(targetDir, "settings.json")).Should().BeTrue();
        File.Exists(Path.Combine(targetDir, "projects.json")).Should().BeFalse();
    }

    [Fact(DisplayName = "コピー先の作成自体に失敗する場合、ポインタファイルを書かない（不完全な移行を許さない）")]
    public void コピー先を作れない場合はポインタを書かない()
    {
        using var ws = new TempWorkspace();
        var exeDir = ws.CreateDirectory("exe");
        var sourceDir = ws.CreateDirectory("source");
        ws.WriteText("source/settings.json", "{}");

        // targetDirと同名の「ファイル」を先に置くことで、Directory.CreateDirectory(target)が
        // 確実に失敗する状況を作る（OS・権限に依存せず再現できる）。
        var targetDir = ws.Combine("target-blocked");
        ws.WriteText("target-blocked", "邪魔なファイル");

        var result = DataDirectoryMigrator.MigrateAndSwitchPointer(exeDir, sourceDir, targetDir, switchToPortable: false);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(i => i.Code == ErrorCode.E407);
        File.Exists(DataDirectoryPointer.PointerFilePath(exeDir)).Should().BeFalse(
            "コピーが不完全な場合はポインタファイルを書いてはいけない");

        // 元データは無傷のまま。
        File.Exists(Path.Combine(sourceDir, "settings.json")).Should().BeTrue();
    }

    [Fact(DisplayName = "コピー・検証が成功すればポインタファイルがターゲットを指す")]
    public void 成功時はポインタファイルが新しい場所を指す()
    {
        using var ws = new TempWorkspace();
        var exeDir = ws.CreateDirectory("exe");
        var sourceDir = ws.CreateDirectory("source");
        var targetDir = ws.Combine("target");
        ws.WriteText("source/settings.json", "{}");

        var result = DataDirectoryMigrator.MigrateAndSwitchPointer(exeDir, sourceDir, targetDir, switchToPortable: false);

        result.IsSuccess.Should().BeTrue();
        DataDirectoryPointer.TryRead(exeDir).Should().Be(targetDir);
        AppPaths.ResolveBaseDirectory(exeDir).Should().Be(targetDir);
    }

    [Fact(DisplayName = "ポータブルへ戻すときはポインタファイルを削除する")]
    public void ポータブルへ戻すとポインタファイルが消える()
    {
        using var ws = new TempWorkspace();
        var exeDir = ws.CreateDirectory("exe");
        var userDir = ws.CreateDirectory("user-data");
        ws.WriteText("user-data/settings.json", "{}");
        DataDirectoryPointer.TryWrite(exeDir, userDir);

        var portableTarget = exeDir; // ポータブル＝exeと同じ階層へ戻す
        var result = DataDirectoryMigrator.MigrateAndSwitchPointer(exeDir, userDir, portableTarget, switchToPortable: true);

        result.IsSuccess.Should().BeTrue();
        File.Exists(DataDirectoryPointer.PointerFilePath(exeDir)).Should().BeFalse();
        AppPaths.ResolveBaseDirectory(exeDir).Should().Be(exeDir);

        // 元の場所（ユーザーフォルダ）のデータは残したまま（安全側）。
        File.Exists(Path.Combine(userDir, "settings.json")).Should().BeTrue();
    }

    [Fact(DisplayName = "コピー元とコピー先が同じ場所なら何もせず成功扱いにする")]
    public void 同じ場所への移行は何もしない()
    {
        using var ws = new TempWorkspace();
        var dir = ws.CreateDirectory("same");
        ws.WriteText("same/settings.json", "{}");

        var result = DataDirectoryMigrator.Migrate(dir, dir);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(dir);
    }

    [Fact(DisplayName = "コピー先がexeフォルダへ書き込めずポインタを書けない場合、データは残るがFailを返す（Linux実機のみ）")]
    public void ポインタを書き込めない場合はコピー成功でも全体としてFailになる()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var ws = new TempWorkspace();
        var exeDir = ws.CreateDirectory("readonly-exe");
        var sourceDir = ws.CreateDirectory("source2");
        var targetDir = ws.Combine("target2");
        ws.WriteText("source2/settings.json", "{}");

        if (!TryMakeUnwritable(exeDir))
        {
            return; // root環境ではパーミッションが効かずスキップ（AppPathsWritabilityTestsと同じ方針）。
        }

        try
        {
            var result = DataDirectoryMigrator.MigrateAndSwitchPointer(exeDir, sourceDir, targetDir, switchToPortable: false);

            result.IsSuccess.Should().BeFalse("ポインタファイルを書けなかった以上、全体としては失敗として報告する");
            // データ自体はコピーできている（実害が無いことの確認）。
            File.Exists(Path.Combine(targetDir, "settings.json")).Should().BeTrue(
                "コピー自体は成功しているため、データはtargetDirに残る");
            File.Exists(Path.Combine(sourceDir, "settings.json")).Should().BeTrue("元データも無傷のまま残る");
        }
        finally
        {
            MakeWritable(exeDir);
        }
    }

    [SupportedOSPlatform("linux")]
    private static bool TryMakeUnwritable(string dir)
    {
        File.SetUnixFileMode(dir,
            UnixFileMode.UserRead | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        var probe = Path.Combine(dir, ".perm_probe");
        try
        {
            using (File.Create(probe))
            {
            }

            File.Delete(probe);
            return false; // 権限チェックがバイパスされている（root等）。
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    [SupportedOSPlatform("linux")]
    private static void MakeWritable(string dir)
    {
        File.SetUnixFileMode(dir,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }
}
