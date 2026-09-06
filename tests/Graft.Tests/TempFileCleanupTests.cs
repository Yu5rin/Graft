using System;
using System.IO;
using FluentAssertions;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 異常系点検「低」6件目（一時ファイル・退避ファイルの掃除が無い）の回帰テスト。
/// <see cref="JsonFileStore.WriteAsync{T}"/>の一時ファイル（<c>*.tmp.*</c>）・
/// <see cref="Graft.Core.SafeFileWriter"/>の退避ファイル（<c>*.graft-bak-*</c>）を模した
/// フィクスチャで、経過時間・深さ・件数上限の各観点を検証する。
/// </summary>
public class TempFileCleanupTests
{
    // ------------------------------------------------------------------
    // CleanupBaseDirectoryTempFiles
    // ------------------------------------------------------------------

    [Fact(DisplayName = "24時間より古いtmpファイルは削除される")]
    public void 古いtmpファイルは削除される()
    {
        using var ws = new TempWorkspace();
        var baseDir = ws.CreateDirectory("app");
        var tmpFile = ws.WriteText("app/settings.json.tmp.abc123", "{}");
        SetLastWriteTime(tmpFile, DateTimeOffset.Now.AddHours(-25));

        var removed = TempFileCleanup.CleanupBaseDirectoryTempFiles(baseDir);

        removed.Should().Be(1);
        File.Exists(tmpFile).Should().BeFalse();
    }

    [Fact(DisplayName = "24時間以内のtmpファイル（＝進行中の可能性がある）は削除しない")]
    public void 新しいtmpファイルは残す()
    {
        using var ws = new TempWorkspace();
        var baseDir = ws.CreateDirectory("app");
        var tmpFile = ws.WriteText("app/settings.json.tmp.abc123", "{}");
        SetLastWriteTime(tmpFile, DateTimeOffset.Now.AddMinutes(-1));

        var removed = TempFileCleanup.CleanupBaseDirectoryTempFiles(baseDir);

        removed.Should().Be(0);
        File.Exists(tmpFile).Should().BeTrue("進行中の書き込みかもしれないファイルを誤って消してはならない");
    }

    [Fact(DisplayName = "settings.json等の通常ファイルは対象外（*.tmp.*パターンに一致するものだけを消す）")]
    public void 通常ファイルは対象外()
    {
        using var ws = new TempWorkspace();
        var baseDir = ws.CreateDirectory("app");
        var settingsFile = ws.WriteText("app/settings.json", "{}");
        SetLastWriteTime(settingsFile, DateTimeOffset.Now.AddDays(-10));

        TempFileCleanup.CleanupBaseDirectoryTempFiles(baseDir);

        File.Exists(settingsFile).Should().BeTrue("settings.json自体を誤って消してはならない");
    }

    [Fact(DisplayName = "サブディレクトリ（back/・logs/）配下のtmpファイルには再帰しない")]
    public void サブディレクトリには再帰しない()
    {
        using var ws = new TempWorkspace();
        var baseDir = ws.CreateDirectory("app");
        var nestedTmp = ws.WriteText("app/logs/foo.tmp.abc", "x");
        SetLastWriteTime(nestedTmp, DateTimeOffset.Now.AddDays(-10));

        TempFileCleanup.CleanupBaseDirectoryTempFiles(baseDir);

        File.Exists(nestedTmp).Should().BeTrue("BaseDirectory直下のみを対象にする設計（クラスコメント参照）");
    }

    [Fact(DisplayName = "ディレクトリが存在しなくても例外にならない")]
    public void ディレクトリが無くても例外にならない()
    {
        using var ws = new TempWorkspace();
        var missing = ws.Combine("no-such-dir");

        Action act = () => TempFileCleanup.CleanupBaseDirectoryTempFiles(missing);

        act.Should().NotThrow();
    }

    // ------------------------------------------------------------------
    // CleanupProjectBackupFiles
    // ------------------------------------------------------------------

    [Fact(DisplayName = "24時間より古い.graft-bak-*ファイルはプロジェクト直下から削除される")]
    public void 古い退避ファイルは削除される()
    {
        using var ws = new TempWorkspace();
        var root = ws.CreateDirectory("project");
        var bakFile = ws.WriteText("project/main.cs.graft-bak-1234", "old content");
        SetLastWriteTime(bakFile, DateTimeOffset.Now.AddHours(-48));

        var removed = TempFileCleanup.CleanupProjectBackupFiles(root);

        removed.Should().Be(1);
        File.Exists(bakFile).Should().BeFalse();
    }

    [Fact(DisplayName = "24時間以内の.graft-bak-*ファイル（進行中の書き込みの可能性）は残す")]
    public void 新しい退避ファイルは残す()
    {
        using var ws = new TempWorkspace();
        var root = ws.CreateDirectory("project");
        var bakFile = ws.WriteText("project/main.cs.graft-bak-1234", "in progress");
        SetLastWriteTime(bakFile, DateTimeOffset.Now.AddSeconds(-5));

        var removed = TempFileCleanup.CleanupProjectBackupFiles(root);

        removed.Should().Be(0);
        File.Exists(bakFile).Should().BeTrue();
    }

    [Fact(DisplayName = "サブフォルダ配下の.graft-bak-*も、最大深さの範囲内なら削除される")]
    public void サブフォルダ配下も掃除される()
    {
        using var ws = new TempWorkspace();
        var root = ws.CreateDirectory("project");
        var nested = ws.CreateDirectory("project/src/feature");
        var bakFile = Path.Combine(nested, "impl.cs.graft-bak-5678");
        File.WriteAllText(bakFile, "old");
        SetLastWriteTime(bakFile, DateTimeOffset.Now.AddDays(-2));

        var removed = TempFileCleanup.CleanupProjectBackupFiles(root);

        removed.Should().Be(1);
        File.Exists(bakFile).Should().BeFalse();
    }

    [Fact(DisplayName = "最大深さを超えたフォルダの.graft-bak-*は対象外になる（無制限の再帰走査を避ける安全策）")]
    public void 最大深さを超えると対象外()
    {
        using var ws = new TempWorkspace();
        var root = ws.CreateDirectory("project");
        var deepDir = ws.CreateDirectory("project/a/b/c");
        var bakFile = Path.Combine(deepDir, "deep.graft-bak-9999");
        File.WriteAllText(bakFile, "old");
        SetLastWriteTime(bakFile, DateTimeOffset.Now.AddDays(-2));

        // maxDepth=1: root自身(0)とその直下(1)までしか降りないため、a/b/c（深さ3）は対象外。
        var removed = TempFileCleanup.CleanupProjectBackupFiles(root, maxDepth: 1);

        removed.Should().Be(0);
        File.Exists(bakFile).Should().BeTrue("深さ上限を超えた場所は走査対象外として残るべき（次回以降の起動に委ねる）");
    }

    [Fact(DisplayName = "node_modules等の重いフォルダ名には降りない（走査コストを抑える安全策）")]
    public void node_modules配下には降りない()
    {
        using var ws = new TempWorkspace();
        var root = ws.CreateDirectory("project");
        var nodeModules = ws.CreateDirectory("project/node_modules/some-package");
        var bakFile = Path.Combine(nodeModules, "index.js.graft-bak-1111");
        File.WriteAllText(bakFile, "old");
        SetLastWriteTime(bakFile, DateTimeOffset.Now.AddDays(-2));

        var removed = TempFileCleanup.CleanupProjectBackupFiles(root);

        removed.Should().Be(0);
        File.Exists(bakFile).Should().BeTrue("node_modules配下は通常.graft-bak-*が生まれる場所ではなく、走査コスト削減のため降りない設計");
    }

    [Fact(DisplayName = "走査件数の上限に達すると、それ以降のフォルダは対象外になる（無制限走査を避ける安全策）")]
    public void 走査件数の上限で打ち切られる()
    {
        using var ws = new TempWorkspace();
        var root = ws.CreateDirectory("project");
        // ルート直下に大量のダミーフォルダを作り、maxEntriesScannedをすぐ超えさせる。
        for (var i = 0; i < 10; i++)
        {
            ws.CreateDirectory($"project/dummy{i}");
        }
        var lateDir = ws.CreateDirectory("project/dummy9/late");
        var bakFile = Path.Combine(lateDir, "late.graft-bak-2222");
        File.WriteAllText(bakFile, "old");
        SetLastWriteTime(bakFile, DateTimeOffset.Now.AddDays(-2));

        // maxEntriesScanned=1: ルート自身の列挙(10件)で即座に上限へ達し、それ以降は打ち切られる。
        var removed = TempFileCleanup.CleanupProjectBackupFiles(root, maxEntriesScanned: 1);

        removed.Should().Be(0, "件数上限に達したら安全側で打ち切り、それ以降は次回起動に委ねるべき");
    }

    [Fact(DisplayName = "プロジェクトルートが存在しなくても例外にならない")]
    public void プロジェクトが無くても例外にならない()
    {
        using var ws = new TempWorkspace();
        var missing = ws.Combine("no-such-project");

        Action act = () => TempFileCleanup.CleanupProjectBackupFiles(missing);

        act.Should().NotThrow();
        TempFileCleanup.CleanupProjectBackupFiles(missing).Should().Be(0);
    }

    private static void SetLastWriteTime(string path, DateTimeOffset time)
        => File.SetLastWriteTimeUtc(path, time.UtcDateTime);
}
