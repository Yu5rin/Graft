using System.IO;
using FluentAssertions;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 機能3（データ保存先の選択）を「コピー方式」から「移動方式」へ変更したことの回帰テスト。
/// 移行のその場では削除しない（<see cref="DataDirectoryMigrator.MigrateAndSwitchPointer"/>）
/// ことは<see cref="DataDirectoryMigratorTests"/>で確認済みのため、ここでは次回起動時の後始末
/// （<see cref="DataDirectoryMigrator.RunPendingCleanup"/>）を検証する。
/// </summary>
public class DataDirectoryPendingCleanupTests
{
    [Fact(DisplayName = "マーカーが無ければ何もしない")]
    public void マーカーが無ければ何もしない()
    {
        using var ws = new TempWorkspace();
        var currentDir = ws.CreateDirectory("current");

        var outcome = DataDirectoryMigrator.RunPendingCleanup(currentDir);

        outcome.Result.Should().Be(DataDirectoryMigrator.PendingCleanupResult.NoMarker);
    }

    [Fact(DisplayName = "ポータブル→ユーザーフォルダへの移動後、次回起動の後始末で旧保存先（exeフォルダ）の" +
        "既知の対象が削除され、datapath.txtと無関係なファイルは残る")]
    public void 移動後に旧保存先の既知の対象だけが削除される()
    {
        using var ws = new TempWorkspace();
        var exeDir = ws.CreateDirectory("exe");
        var userDir = ws.Combine("user-data"); // まだ存在しない状態から始める。

        // exeフォルダ側: Graftの既知データ一式に加え、配布物本体を模したファイルと
        // 利用者が置いたメモを用意する（削除されてはならないもの）。
        ws.WriteText("exe/settings.json", "{\"theme\":\"dark\"}");
        ws.WriteText("exe/projects.json", "[]");
        ws.WriteText("exe/back/proj1/r1_20260101_000000/manifest.json", "{}");
        ws.WriteText("exe/logs/20260101.log", "{\"eventType\":\"startup\"}");
        ws.WriteText("exe/Graft.exe", "dummy-binary"); // 配布物本体を模したダミー。
        ws.WriteText("exe/memo.txt", "利用者が置いたメモ"); // 利用者が置いた無関係なファイル。

        // 設定画面での「ユーザーフォルダへ移動」操作を再現する。
        var migrated = DataDirectoryMigrator.MigrateAndSwitchPointer(
            exeDir, sourceDirectory: exeDir, targetDirectory: userDir, switchToPortable: false);
        migrated.IsSuccess.Should().BeTrue();

        // この時点（再起動前）では、まだ元の場所は一切消えていないはず（コピー方式時代からの安全性）。
        File.Exists(Path.Combine(exeDir, "settings.json")).Should().BeTrue();

        // 次回起動時の後始末を実行する（AppPaths.BaseDirectoryはポインタにより既にuserDirを指す）。
        var outcome = DataDirectoryMigrator.RunPendingCleanup(userDir);

        outcome.Result.Should().Be(DataDirectoryMigrator.PendingCleanupResult.Completed);

        // 旧保存先（exeフォルダ）の既知の対象は削除されている。
        File.Exists(Path.Combine(exeDir, "settings.json")).Should().BeFalse();
        File.Exists(Path.Combine(exeDir, "projects.json")).Should().BeFalse();
        Directory.Exists(Path.Combine(exeDir, "back")).Should().BeFalse();
        Directory.Exists(Path.Combine(exeDir, "logs")).Should().BeFalse();

        // datapath.txt（保存先を指すポインタ）は消してはならない。
        File.Exists(DataDirectoryPointer.PointerFilePath(exeDir)).Should().BeTrue(
            "datapath.txtは保存先を指すポインタのためexeフォルダに残す必要がある");
        DataDirectoryPointer.TryRead(exeDir).Should().Be(userDir);

        // exeフォルダには他のファイル（Graft.exe・利用者のメモ）が残っているため、
        // exeフォルダ自体は丸ごと削除されない。
        Directory.Exists(exeDir).Should().BeTrue();
        File.Exists(Path.Combine(exeDir, "Graft.exe")).Should().BeTrue(
            "配布物本体（Graft.exe等）は削除対象一覧に含まれないため消えてはならない");
        File.Exists(Path.Combine(exeDir, "memo.txt")).Should().BeTrue(
            "利用者が置いた無関係なファイルは削除対象一覧に含まれないため消えてはならない");

        // 新しい保存先へは正しくデータが引き継がれている。
        File.Exists(Path.Combine(userDir, "settings.json")).Should().BeTrue();

        // マーカーは後始末完了により消える。
        DataDirectoryPendingCleanupMarker.TryRead(userDir).Should().BeNull();
    }

    [Fact(DisplayName = "ユーザーフォルダ→ポータブルへ戻したあとの後始末で、既知の対象を消した結果" +
        "ユーザーフォルダが空になればフォルダごと削除される")]
    public void ユーザーフォルダが空になればフォルダごと削除される()
    {
        using var ws = new TempWorkspace();
        var exeDir = ws.CreateDirectory("exe");
        var userDir = ws.CreateDirectory("user-data");
        ws.WriteText("user-data/settings.json", "{}"); // 既知データのみ（他に何も置いていない）。

        var migrated = DataDirectoryMigrator.MigrateAndSwitchPointer(
            exeDir, sourceDirectory: userDir, targetDirectory: exeDir, switchToPortable: true);
        migrated.IsSuccess.Should().BeTrue();

        var outcome = DataDirectoryMigrator.RunPendingCleanup(exeDir);

        outcome.Result.Should().Be(DataDirectoryMigrator.PendingCleanupResult.Completed);
        Directory.Exists(userDir).Should().BeFalse(
            "既知の対象を消した結果ユーザーフォルダが空になったため、フォルダ自体も削除されるべき");
    }

    [Fact(DisplayName = "ユーザーフォルダに無関係なファイルが残っていれば、既知の対象を消してもフォルダ自体は残る")]
    public void ユーザーフォルダに無関係なファイルが残っていればフォルダは残る()
    {
        using var ws = new TempWorkspace();
        var exeDir = ws.CreateDirectory("exe");
        var userDir = ws.CreateDirectory("user-data");
        ws.WriteText("user-data/settings.json", "{}");
        ws.WriteText("user-data/readme.txt", "利用者が置いたファイル");

        var migrated = DataDirectoryMigrator.MigrateAndSwitchPointer(
            exeDir, sourceDirectory: userDir, targetDirectory: exeDir, switchToPortable: true);
        migrated.IsSuccess.Should().BeTrue();

        var outcome = DataDirectoryMigrator.RunPendingCleanup(exeDir);

        outcome.Result.Should().Be(DataDirectoryMigrator.PendingCleanupResult.Completed);
        File.Exists(Path.Combine(userDir, "settings.json")).Should().BeFalse("既知の対象は削除される");
        Directory.Exists(userDir).Should().BeTrue("無関係なファイルが残っているためフォルダ自体は削除されない");
        File.Exists(Path.Combine(userDir, "readme.txt")).Should().BeTrue();
    }

    [Fact(DisplayName = "取り込み直し（再Migrate）に失敗した場合、削除を行わずマーカーも残す" +
        "（次回起動でもう一度試みられる。生きている現在の保存先を巻き添えで削除しないことも確認する）")]
    public void 取り込み直しに失敗したら削除せずマーカーが残る()
    {
        using var ws = new TempWorkspace();
        var exeDir = ws.CreateDirectory("exe");
        var userDir = ws.Combine("user-data");

        var migrated = DataDirectoryMigrator.MigrateAndSwitchPointer(
            exeDir, sourceDirectory: exeDir, targetDirectory: userDir, switchToPortable: false);
        migrated.IsSuccess.Should().BeTrue();
        DataDirectoryPendingCleanupMarker.TryRead(userDir).Should().Be(exeDir);

        // 再起動を後回しにした間に旧保存先（exeDir）側で新しい変更があった、という状況を作る。
        ws.WriteText("exe/queue.json", "[]");

        // 次回起動時の取り込み直し（Migrate(exeDir, userDir)）を確実に失敗させるため、
        // コピー先のqueue.jsonという名前の場所に"ディレクトリ"を先に置く
        // （OS・権限に依存せず再現できる。DataDirectoryMigratorTestsの他のテストと同じ手法）。
        ws.CreateDirectory("user-data/queue.json");

        var outcome = DataDirectoryMigrator.RunPendingCleanup(userDir);

        outcome.Result.Should().Be(DataDirectoryMigrator.PendingCleanupResult.MigrateFailed);

        // 削除は行われず、マーカーも残る（次回起動でもう一度試みられる）。
        DataDirectoryPendingCleanupMarker.TryRead(userDir).Should().Be(exeDir,
            "取り込みに失敗したらマーカーは消さず残す必要がある");

        // 生きている現在の保存先（userDir）は、取り込み失敗時も巻き添えで消えてはならない
        // （Migrateのcleanup IncompleteTargetOnFailure: falseの回帰）。
        Directory.Exists(userDir).Should().BeTrue(
            "現在使用中の保存先を、失敗した取り込み処理の後始末で丸ごと削除してはならない");
    }

    [Fact(DisplayName = "再起動を後回しにして旧保存先を書き換えた場合、その変更が次回起動時に新しい場所へ取り込まれる" +
        "（移動方式の肝: 再起動前の変更が失われない）")]
    public void 再起動前の変更が次回起動時に取り込まれる()
    {
        using var ws = new TempWorkspace();
        var exeDir = ws.CreateDirectory("exe");
        var userDir = ws.Combine("user-data");

        ws.WriteText("exe/settings.json", "{\"theme\":\"dark\"}");

        var migrated = DataDirectoryMigrator.MigrateAndSwitchPointer(
            exeDir, sourceDirectory: exeDir, targetDirectory: userDir, switchToPortable: false);
        migrated.IsSuccess.Should().BeTrue();
        File.ReadAllText(Path.Combine(userDir, "settings.json")).Should().Contain("dark");

        // 案内どおりすぐに再起動せず、アプリを使い続けた想定。この間の変更は引き続き
        // 旧保存先（exeDir、まだ使用中のAppPathsが指す場所）へ書かれる。
        ws.WriteText("exe/settings.json", "{\"theme\":\"light\"}");
        ws.WriteText("exe/queue.json", "[{\"id\":\"pending-patch\"}]");

        // ようやく再起動した想定。次回起動時の後始末を実行する。
        var outcome = DataDirectoryMigrator.RunPendingCleanup(userDir);

        outcome.Result.Should().Be(DataDirectoryMigrator.PendingCleanupResult.Completed);

        // 再起動前に行った変更（テーマ変更・キュー追加）が、新しい保存先へきちんと引き継がれている。
        File.ReadAllText(Path.Combine(userDir, "settings.json")).Should().Contain("light",
            "再起動を後回しにした間の変更も、次回起動時の取り込み直しで新しい場所へ反映される必要がある");
        File.Exists(Path.Combine(userDir, "queue.json")).Should().BeTrue(
            "再起動前に新規追加されたファイルも取り込まれる必要がある");

        // 旧保存先は削除済み。
        File.Exists(Path.Combine(exeDir, "settings.json")).Should().BeFalse();
        File.Exists(Path.Combine(exeDir, "queue.json")).Should().BeFalse();
    }
}
