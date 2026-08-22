using System.IO;
using FluentAssertions;
using Graft.Core.Update;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// <see cref="PendingUpdateWorkDirCleanup"/>: 自動更新中にGraftがクラッシュした場合に残る
/// 一時作業フォルダ（%TEMP%\GraftUpdate\&lt;GUID&gt;\）を、次回起動時に掃除する
/// （利用者からの指摘・穴2の回帰テスト）。
///
/// 【安全策の検証】実行中の更新（=作成されたばかりのフォルダ）を誤って消さないよう、
/// 一定時間（既定24時間）以上前に作成されたフォルダだけを対象にすることを固定する。また、
/// 掃除対象を%TEMP%\GraftUpdate\直下だけに構造的に限定し、それ以外の場所には一切触れない
/// ことも固定する。
/// </summary>
public class PendingUpdateWorkDirCleanupTests
{
    [Fact(DisplayName = "既定の経過時間より古いフォルダは削除される")]
    public void 古いフォルダは削除される()
    {
        using var ws = new TempWorkspace();
        var root = ws.CreateDirectory("GraftUpdate");
        var oldDir = Path.Combine(root, "old-guid");
        Directory.CreateDirectory(oldDir);
        File.WriteAllText(Path.Combine(oldDir, "graft-update.zip"), "leftover");
        var now = DateTimeOffset.Now;
        Directory.SetCreationTimeUtc(oldDir, now.UtcDateTime.AddHours(-25));

        var removed = PendingUpdateWorkDirCleanup.Run(now: now, rootOverride: root);

        removed.Should().Be(1);
        Directory.Exists(oldDir).Should().BeFalse();
    }

    [Fact(DisplayName = "実行中とみなすべき新しいフォルダは残る（実行中の更新を誤って消さない安全策）")]
    public void 新しいフォルダは残る()
    {
        using var ws = new TempWorkspace();
        var root = ws.CreateDirectory("GraftUpdate");
        var freshDir = Path.Combine(root, "fresh-guid");
        Directory.CreateDirectory(freshDir);
        File.WriteAllText(Path.Combine(freshDir, "graft-update.zip"), "downloading");
        var now = DateTimeOffset.Now;
        // 作成からまだ1時間しか経っていない（既定の閾値24時間より十分内側）＝実行中かもしれない。
        Directory.SetCreationTimeUtc(freshDir, now.UtcDateTime.AddHours(-1));

        var removed = PendingUpdateWorkDirCleanup.Run(now: now, rootOverride: root);

        removed.Should().Be(0);
        Directory.Exists(freshDir).Should().BeTrue("実行中かもしれないフォルダを誤って消してはいけない");
        File.Exists(Path.Combine(freshDir, "graft-update.zip")).Should().BeTrue();
    }

    [Fact(DisplayName = "古い・新しいフォルダが混在していても、古いものだけを選んで削除する")]
    public void 古いものだけ選んで削除する()
    {
        using var ws = new TempWorkspace();
        var root = ws.CreateDirectory("GraftUpdate");
        var oldDir1 = Path.Combine(root, "old-1");
        var oldDir2 = Path.Combine(root, "old-2");
        var freshDir = Path.Combine(root, "fresh");
        Directory.CreateDirectory(oldDir1);
        Directory.CreateDirectory(oldDir2);
        Directory.CreateDirectory(freshDir);
        var now = DateTimeOffset.Now;
        Directory.SetCreationTimeUtc(oldDir1, now.UtcDateTime.AddHours(-48));
        Directory.SetCreationTimeUtc(oldDir2, now.UtcDateTime.AddHours(-25));
        Directory.SetCreationTimeUtc(freshDir, now.UtcDateTime.AddMinutes(-5));

        var removed = PendingUpdateWorkDirCleanup.Run(now: now, rootOverride: root);

        removed.Should().Be(2);
        Directory.Exists(oldDir1).Should().BeFalse();
        Directory.Exists(oldDir2).Should().BeFalse();
        Directory.Exists(freshDir).Should().BeTrue();
    }

    [Fact(DisplayName = "対象ルートが存在しなければ何もせず0を返す")]
    public void ルートが無ければ何もしない()
    {
        using var ws = new TempWorkspace();
        var root = ws.Combine("GraftUpdate"); // わざと作らない。

        var removed = PendingUpdateWorkDirCleanup.Run(rootOverride: root);

        removed.Should().Be(0);
    }

    [Fact(DisplayName = "指定したルートの外にあるフォルダ・ファイルには一切触れない")]
    public void ルートの外には触れない()
    {
        using var ws = new TempWorkspace();
        var root = ws.CreateDirectory("GraftUpdate");
        var oldDir = Path.Combine(root, "old-guid");
        Directory.CreateDirectory(oldDir);
        var now = DateTimeOffset.Now;
        Directory.SetCreationTimeUtc(oldDir, now.UtcDateTime.AddHours(-25));

        // ルートの隣（GraftUpdateの外）に、同じくらい古い無関係なフォルダ・ファイルを置く。
        var siblingDir = ws.CreateDirectory("NotGraftUpdate/some-old-folder");
        Directory.SetCreationTimeUtc(siblingDir, now.UtcDateTime.AddHours(-100));
        var siblingFile = ws.WriteText("sibling.txt", "無関係なファイル");

        var removed = PendingUpdateWorkDirCleanup.Run(now: now, rootOverride: root);

        removed.Should().Be(1);
        Directory.Exists(oldDir).Should().BeFalse();
        Directory.Exists(siblingDir).Should().BeTrue("GraftUpdateの外には一切触れないはず");
        File.Exists(siblingFile).Should().BeTrue("GraftUpdateの外には一切触れないはず");
    }

    [Fact(DisplayName = "minimumAgeを指定すれば、その経過時間で判定する")]
    public void 経過時間を差し替えられる()
    {
        using var ws = new TempWorkspace();
        var root = ws.CreateDirectory("GraftUpdate");
        var dir = Path.Combine(root, "guid");
        Directory.CreateDirectory(dir);
        var now = DateTimeOffset.Now;
        Directory.SetCreationTimeUtc(dir, now.UtcDateTime.AddMinutes(-30));

        // 既定（24時間）なら残るはずだが、閾値を10分に縮めれば削除される。
        var removed = PendingUpdateWorkDirCleanup.Run(minimumAge: TimeSpan.FromMinutes(10), now: now, rootOverride: root);

        removed.Should().Be(1);
        Directory.Exists(dir).Should().BeFalse();
    }
}
