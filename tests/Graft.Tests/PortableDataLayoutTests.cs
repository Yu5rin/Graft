using System.IO;
using FluentAssertions;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 1.0.26: ポータブルのデータ保存先を exe 直下の data フォルダにする起動時処理
/// （<see cref="PortableDataLayout.Resolve"/>）の検証。旧配置・ユーザーフォルダからの自動移行、
/// 失敗時の継続、途中で落ちた場合の再開、明示的に選んだ場所に触れないこと、を固定する。
///
/// 書き込めない状況は、権限ではなく「data という名前のファイルを置く」ことで再現する
/// （root で動く環境でも、CreateDirectory が確実に失敗する）。コピーの失敗は
/// 「data/back という名前のファイルを置く」ことで再現する。
/// </summary>
public class PortableDataLayoutTests
{
    private static void WriteLegacyData(TempWorkspace ws, string dir)
    {
        ws.WriteText($"{dir}/settings.json", "{\"theme\":\"dark\"}");
        ws.WriteText($"{dir}/projects.json", "[]");
        ws.WriteText($"{dir}/update-check.json", "{\"lastCheckedAt\":null}");
        ws.WriteText($"{dir}/back/p1/r1_20260101_000000/manifest.json", "{\"r\":1}");
        ws.WriteText($"{dir}/back/p1/r1_20260101_000000/files/a.txt", "元のa");
        ws.WriteText($"{dir}/logs/20260101-1.log", "log");
    }

    private static void AssertMoved(string data)
    {
        File.ReadAllText(Path.Combine(data, "settings.json")).Should().Be("{\"theme\":\"dark\"}");
        File.Exists(Path.Combine(data, "projects.json")).Should().BeTrue();
        File.Exists(Path.Combine(data, "update-check.json")).Should().BeTrue("更新確認の記録も一緒に移る");
        File.ReadAllText(Path.Combine(data, "back", "p1", "r1_20260101_000000", "files", "a.txt")).Should().Be("元のa");
        File.Exists(Path.Combine(data, "logs", "20260101-1.log")).Should().BeTrue();
        DataDirectoryPendingCleanupMarker.TryRead(data).Should().BeNull("後始末まで終わっている");
    }

    [Fact(DisplayName = "新規インストール: data が作られ、そこが保存先になる。exe の横には何も作られない")]
    public void 新規インストールはdataが作られる()
    {
        using var ws = new TempWorkspace();
        var exe = ws.CreateDirectory("exe");
        ws.WriteText("exe/Graft.exe", "dummy");

        var outcome = PortableDataLayout.Resolve(exe);

        outcome.Result.Should().Be(PortableDataResult.PortableReady);
        outcome.BaseDirectory.Should().Be(AppPaths.PortableDataDirectory(exe));
        Directory.Exists(outcome.BaseDirectory).Should().BeTrue();
        Directory.GetFileSystemEntries(exe).Select(Path.GetFileName)
            .Should().BeEquivalentTo(new[] { "Graft.exe", "data" });
    }

    [Fact(DisplayName = "旧配置: exe の横のデータが data へ移り、中身が一致し、exe の横から消える。配布物のファイルは残る")]
    public void 旧配置からdataへ移る()
    {
        using var ws = new TempWorkspace();
        var exe = ws.CreateDirectory("exe");
        ws.WriteText("exe/Graft.exe", "dummy");
        WriteLegacyData(ws, "exe");

        var outcome = PortableDataLayout.Resolve(exe);

        outcome.Result.Should().Be(PortableDataResult.MigratedFromLegacy);
        outcome.BaseDirectory.Should().Be(Path.Combine(exe, "data"));
        AssertMoved(outcome.BaseDirectory);
        Directory.GetFileSystemEntries(exe).Select(Path.GetFileName)
            .Should().BeEquivalentTo(new[] { "Graft.exe", "data" }, "データは data にまとまり、横には配布物だけが残る");
    }

    [Fact(DisplayName = "旧版の「exe自身を指す」datapath.txt がある旧配置も移り、datapath.txt は消える")]
    public void 旧版の明示ポータブルのポインタも移る()
    {
        using var ws = new TempWorkspace();
        var exe = ws.CreateDirectory("exe");
        WriteLegacyData(ws, "exe");
        DataDirectoryPointer.TryWrite(exe, exe);

        var outcome = PortableDataLayout.Resolve(exe);

        outcome.Result.Should().Be(PortableDataResult.MigratedFromLegacy);
        File.Exists(DataDirectoryPointer.PointerFilePath(exe)).Should().BeFalse();
        AssertMoved(outcome.BaseDirectory);
    }

    [Fact(DisplayName = "移行は冪等: 2回目の起動では何も起きず、data のデータがそのまま使われる")]
    public void 移行後の再起動は何もしない()
    {
        using var ws = new TempWorkspace();
        var exe = ws.CreateDirectory("exe");
        WriteLegacyData(ws, "exe");
        var first = PortableDataLayout.Resolve(exe);
        File.WriteAllText(Path.Combine(first.BaseDirectory, "settings.json"), "{\"theme\":\"light\"}");

        var second = PortableDataLayout.Resolve(exe);

        second.Result.Should().Be(PortableDataResult.PortableReady);
        File.ReadAllText(Path.Combine(second.BaseDirectory, "settings.json")).Should().Be("{\"theme\":\"light\"}");
    }

    [Fact(DisplayName = "移行中に失敗したら旧配置のまま起動でき、データは失われない。原因を除くと次の起動で完了する")]
    public void 移行失敗時は旧配置で起動し次回再試行する()
    {
        using var ws = new TempWorkspace();
        var exe = ws.CreateDirectory("exe");
        WriteLegacyData(ws, "exe");
        // data/back を「ファイル」にして、back/ のコピーを確実に失敗させる。
        ws.WriteText("exe/data/back", "邪魔");

        var failed = PortableDataLayout.Resolve(exe);

        failed.Result.Should().Be(PortableDataResult.FailedKeptLegacy);
        failed.BaseDirectory.Should().Be(exe, "旧配置のまま起動する");
        failed.Detail.Should().NotBeNullOrEmpty();
        failed.ShouldNotifyUser.Should().BeTrue();
        File.ReadAllText(Path.Combine(exe, "settings.json")).Should().Be("{\"theme\":\"dark\"}");
        File.Exists(Path.Combine(exe, "back", "p1", "r1_20260101_000000", "files", "a.txt")).Should().BeTrue("元は消えていない");

        // 旧配置で動いている間に設定が変わる。次の起動の取り直しは、元が正本なのでこれを拾う。
        File.WriteAllText(Path.Combine(exe, "settings.json"), "{\"theme\":\"light\"}");
        File.Delete(Path.Combine(exe, "data", "back"));

        var retried = PortableDataLayout.Resolve(exe);

        retried.Result.Should().Be(PortableDataResult.MigratedFromLegacy);
        File.ReadAllText(Path.Combine(retried.BaseDirectory, "settings.json")).Should().Be("{\"theme\":\"light\"}");
        File.Exists(Path.Combine(retried.BaseDirectory, "back", "p1", "r1_20260101_000000", "files", "a.txt")).Should().BeTrue();
        File.Exists(Path.Combine(exe, "settings.json")).Should().BeFalse();
    }

    [Fact(DisplayName = "コピーの途中で落ちた状態（半端な data と移行中の印）でも、元から取り直して完了し、半端なコピーを使わない")]
    public void コピー途中で落ちたら元から取り直す()
    {
        using var ws = new TempWorkspace();
        var exe = ws.CreateDirectory("exe");
        WriteLegacyData(ws, "exe");
        // 落ちた直後の状態: 印は書かれ、settings.json だけコピー済み（しかも古い内容）。
        ws.WriteText("exe/data/settings.json", "{\"theme\":\"半端\"}");
        DataDirectoryPendingCleanupMarker.TryWrite(Path.Combine(exe, "data"), exe);

        var outcome = PortableDataLayout.Resolve(exe);

        outcome.Result.Should().Be(PortableDataResult.MigratedFromLegacy, "半端な data を「両方にある」と誤認しない");
        AssertMoved(outcome.BaseDirectory);
    }

    [Fact(DisplayName = "コピー済み・削除の途中で落ちた状態（元の一部だけ消えた）でも、データは欠けずに完了する")]
    public void 削除途中で落ちても欠けない()
    {
        using var ws = new TempWorkspace();
        var exe = ws.CreateDirectory("exe");
        WriteLegacyData(ws, "exe");
        // 全部コピー済みで、元の settings.json と projects.json だけ消えた状態を作る。
        var data = Path.Combine(exe, "data");
        DataDirectoryMigrator.Migrate(exe, data, cleanupIncompleteTargetOnFailure: false).IsSuccess.Should().BeTrue();
        DataDirectoryPendingCleanupMarker.TryWrite(data, exe);
        File.Delete(Path.Combine(exe, "settings.json"));
        File.Delete(Path.Combine(exe, "projects.json"));

        var outcome = PortableDataLayout.Resolve(exe);

        outcome.Result.Should().Be(PortableDataResult.MigratedFromLegacy);
        AssertMoved(data);
        Directory.Exists(Path.Combine(exe, "back")).Should().BeFalse();
    }

    [Fact(DisplayName = "書き込めない（data を作れない）場合は旧配置のまま起動し、データに触れない")]
    public void 書き込めなければ旧配置のまま()
    {
        using var ws = new TempWorkspace();
        var exe = ws.CreateDirectory("exe");
        WriteLegacyData(ws, "exe");
        ws.WriteText("exe/data", "ファイルが data の場所を塞いでいる");

        var outcome = PortableDataLayout.Resolve(exe);

        outcome.Result.Should().Be(PortableDataResult.NotWritableKeptLegacy);
        outcome.BaseDirectory.Should().Be(exe);
        outcome.Detail.Should().NotBeNullOrEmpty();
        outcome.ShouldNotifyUser.Should().BeFalse("既存の書き込み警告が出るので重ねない");
        File.Exists(Path.Combine(exe, "settings.json")).Should().BeTrue();
        File.ReadAllText(Path.Combine(exe, "data")).Should().Be("ファイルが data の場所を塞いでいる");
    }

    [Fact(DisplayName = "exe の横と data の両方にデータがあれば、どちらも上書き・削除せず data を使い、警告する")]
    public void 両方にあれば上書きしない()
    {
        using var ws = new TempWorkspace();
        var exe = ws.CreateDirectory("exe");
        ws.WriteText("exe/settings.json", "{\"from\":\"exe\"}");
        ws.WriteText("exe/data/settings.json", "{\"from\":\"data\"}");

        var outcome = PortableDataLayout.Resolve(exe);

        outcome.Result.Should().Be(PortableDataResult.BothHaveData);
        outcome.BaseDirectory.Should().Be(Path.Combine(exe, "data"));
        outcome.IsProblem.Should().BeTrue();
        File.ReadAllText(Path.Combine(exe, "settings.json")).Should().Be("{\"from\":\"exe\"}");
        File.ReadAllText(Path.Combine(exe, "data", "settings.json")).Should().Be("{\"from\":\"data\"}");
    }

    [Fact(DisplayName = "1.0.25以前のユーザーフォルダ（印の無い datapath.txt）は、起動時に data へ自動で移り、datapath.txt とユーザーフォルダの中身が消える")]
    public void ユーザーフォルダから自動で移る()
    {
        using var ws = new TempWorkspace();
        var exe = ws.CreateDirectory("exe");
        var user = ws.CreateDirectory("appdata/Graft");
        WriteLegacyData(ws, "appdata/Graft");
        DataDirectoryPointer.TryWrite(exe, user);

        var outcome = PortableDataLayout.Resolve(exe);

        outcome.Result.Should().Be(PortableDataResult.MigratedFromUserFolder);
        outcome.BaseDirectory.Should().Be(Path.Combine(exe, "data"));
        AssertMoved(outcome.BaseDirectory);
        File.Exists(DataDirectoryPointer.PointerFilePath(exe)).Should().BeFalse("ポータブル（data）が既定に戻る");
        File.Exists(Path.Combine(user, "settings.json")).Should().BeFalse();
        Directory.Exists(Path.Combine(user, "back")).Should().BeFalse();
        AppPaths.ResolveBaseDirectory(exe).Should().Be(outcome.BaseDirectory);
    }

    [Fact(DisplayName = "利用者が明示的に選んだユーザーフォルダ（印つき）は、自動では移さない")]
    public void 明示的に選んだユーザーフォルダは移さない()
    {
        using var ws = new TempWorkspace();
        var exe = ws.CreateDirectory("exe");
        var user = ws.CreateDirectory("appdata/Graft");
        WriteLegacyData(ws, "appdata/Graft");
        DataDirectoryPointer.TryWrite(exe, user, explicitChoice: true);

        var outcome = PortableDataLayout.Resolve(exe);

        outcome.Result.Should().Be(PortableDataResult.CustomLocation);
        outcome.BaseDirectory.Should().Be(user);
        File.Exists(Path.Combine(user, "settings.json")).Should().BeTrue();
        Directory.Exists(Path.Combine(exe, "data")).Should().BeFalse();
        DataDirectoryPointer.TryRead(exe).Should().Be(user);
    }

    [Fact(DisplayName = "設定画面の「ユーザーフォルダへ移動」が書く datapath.txt は明示扱いになり、次の起動で data へ戻らない")]
    public void 設定画面の移動は明示扱い()
    {
        using var ws = new TempWorkspace();
        var exe = ws.CreateDirectory("exe");
        var data = ws.CreateDirectory("exe/data");
        var user = ws.Combine("appdata/Graft");
        ws.WriteText("exe/data/settings.json", "{}");

        DataDirectoryMigrator.MigrateAndSwitchPointer(exe, data, user, switchToPortable: false).IsSuccess.Should().BeTrue();
        // 次の起動: まず後始末（StartAsyncと同じ）、そのうえで保存先の確定。
        DataDirectoryMigrator.RunPendingCleanup(user);
        var outcome = PortableDataLayout.Resolve(exe);

        outcome.Result.Should().Be(PortableDataResult.CustomLocation);
        outcome.BaseDirectory.Should().Be(user);
        File.Exists(Path.Combine(user, "settings.json")).Should().BeTrue();
    }

    [Fact(DisplayName = "ユーザーフォルダからの移行に失敗したら、ユーザーフォルダのまま起動し、datapath.txt もデータも残る")]
    public void ユーザーフォルダからの移行失敗は継続する()
    {
        using var ws = new TempWorkspace();
        var exe = ws.CreateDirectory("exe");
        var user = ws.CreateDirectory("appdata/Graft");
        WriteLegacyData(ws, "appdata/Graft");
        DataDirectoryPointer.TryWrite(exe, user);
        ws.WriteText("exe/data/back", "邪魔");

        var outcome = PortableDataLayout.Resolve(exe);

        outcome.Result.Should().Be(PortableDataResult.FailedKeptUserFolder);
        outcome.BaseDirectory.Should().Be(user);
        DataDirectoryPointer.TryRead(exe).Should().Be(user);
        File.Exists(Path.Combine(user, "back", "p1", "r1_20260101_000000", "files", "a.txt")).Should().BeTrue();

        File.Delete(Path.Combine(exe, "data", "back"));
        PortableDataLayout.Resolve(exe).Result.Should().Be(PortableDataResult.MigratedFromUserFolder, "次の起動で再試行して完了する");
    }

    [Fact(DisplayName = "ユーザーフォルダ側を消した直後に落ちた状態（datapath.txt が残る）でも、次の起動で data を使い datapath.txt を片付ける")]
    public void ユーザーフォルダ削除後に落ちても片付く()
    {
        using var ws = new TempWorkspace();
        var exe = ws.CreateDirectory("exe");
        var user = ws.Combine("appdata/Graft"); // 空で消えた
        ws.CreateDirectory("appdata");
        ws.WriteText("exe/data/settings.json", "{}");
        DataDirectoryPointer.TryWrite(exe, user);

        var outcome = PortableDataLayout.Resolve(exe);

        outcome.BaseDirectory.Should().Be(Path.Combine(exe, "data"));
        File.Exists(DataDirectoryPointer.PointerFilePath(exe)).Should().BeFalse();
    }

    [Fact(DisplayName = "外付けドライブが外れているなど、親フォルダごと無い場所を指すポインタは、消さず触れない")]
    public void 存在しない場所を指すポインタには触れない()
    {
        using var ws = new TempWorkspace();
        var exe = ws.CreateDirectory("exe");
        var missing = ws.Combine("no-such-drive/Graft");
        ws.WriteText("exe/data/settings.json", "{}");
        DataDirectoryPointer.TryWrite(exe, missing);

        var outcome = PortableDataLayout.Resolve(exe);

        outcome.Result.Should().Be(PortableDataResult.CustomLocation);
        outcome.BaseDirectory.Should().Be(missing);
        DataDirectoryPointer.TryRead(exe).Should().Be(missing);
    }

    [Fact(DisplayName = "ユーザーフォルダと data の両方にデータがあれば、どちらも上書きせずユーザーフォルダのまま使う")]
    public void ユーザーフォルダとdataの両方にあれば移さない()
    {
        using var ws = new TempWorkspace();
        var exe = ws.CreateDirectory("exe");
        var user = ws.CreateDirectory("appdata/Graft");
        ws.WriteText("appdata/Graft/settings.json", "{\"from\":\"user\"}");
        ws.WriteText("exe/data/settings.json", "{\"from\":\"data\"}");
        DataDirectoryPointer.TryWrite(exe, user);

        var outcome = PortableDataLayout.Resolve(exe);

        outcome.Result.Should().Be(PortableDataResult.UserFolderAndDataConflict);
        outcome.BaseDirectory.Should().Be(user);
        File.ReadAllText(Path.Combine(exe, "data", "settings.json")).Should().Be("{\"from\":\"data\"}");
        File.ReadAllText(Path.Combine(user, "settings.json")).Should().Be("{\"from\":\"user\"}");
    }

    [Fact(DisplayName = "ログ・バックアップ・更新確認の記録など、保存先を使う各パスが exe 直下の data を指す（ログフォルダを開く操作もこれを使う）")]
    public void 各パスがdataを指す()
    {
        using var ws = new TempWorkspace();
        var exe = ws.CreateDirectory("exe");
        var paths = new AppPaths(PortableDataLayout.Resolve(exe).BaseDirectory);
        var data = Path.Combine(exe, "data");

        paths.LogsDirectory.Should().Be(Path.Combine(data, "logs"));
        paths.BackupRootDirectory.Should().Be(Path.Combine(data, "back"));
        paths.UpdateCheckStateFilePath.Should().Be(Path.Combine(data, "update-check.json"));
        paths.SettingsFilePath.Should().Be(Path.Combine(data, "settings.json"));
    }

    [Fact(DisplayName = "同じ理由の通知は一度だけ。理由が変われば改めて出す")]
    public void 通知は同じ理由なら一度だけ()
    {
        using var ws = new TempWorkspace();
        var baseDir = ws.CreateDirectory("base");

        PortableDataLayout.TryMarkNoticeShown(baseDir, "理由A").Should().BeTrue();
        PortableDataLayout.TryMarkNoticeShown(baseDir, "理由A").Should().BeFalse();
        PortableDataLayout.TryMarkNoticeShown(baseDir, "理由B").Should().BeTrue();
    }

    [Fact(DisplayName = "「戻す」操作（MigrateAndSwitchPointer）は exe 直下の data へ移し、datapath.txt を消す")]
    public void 戻す操作はdataへ移す()
    {
        using var ws = new TempWorkspace();
        var exe = ws.CreateDirectory("exe");
        var user = ws.CreateDirectory("appdata/Graft");
        ws.WriteText("appdata/Graft/settings.json", "{}");
        DataDirectoryPointer.TryWrite(exe, user, explicitChoice: true);
        var data = AppPaths.PortableDataDirectory(exe);

        DataDirectoryMigrator.MigrateAndSwitchPointer(exe, user, data, switchToPortable: true).IsSuccess.Should().BeTrue();

        File.Exists(Path.Combine(data, "settings.json")).Should().BeTrue();
        File.Exists(DataDirectoryPointer.PointerFilePath(exe)).Should().BeFalse();
        AppPaths.ResolveBaseDirectory(exe).Should().Be(data);
        DataDirectoryPendingCleanupMarker.TryRead(data).Should().Be(user);
    }
}
