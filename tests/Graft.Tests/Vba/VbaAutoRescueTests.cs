using System.IO;
using FluentAssertions;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Features;
using Graft.UiTests.Vba;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// 反映前の自動控え（機能 C。仕様書_ブックの情報と依頼の観点 7.2・7.3）と、控えのミラーへの取り込み（同 7.5）。
/// <see cref="FakeVbaBridge"/> で <see cref="VbaSyncService"/> と <see cref="VbaRescueService"/> を動かす。
/// </summary>
public class VbaAutoRescueTests : VbaServiceFixture
{
    private const string Module1 = "Modules/Module1.bas";

    private string RescueDir(Project project) => Paths.GetVbaRescueDirectory(project.Id);

    private static string Crlf(string text) => text.Replace("\n", "\r\n");

    // ------------------------------------------------------------------
    // 反映前の自動控え
    // ------------------------------------------------------------------

    [Fact(DisplayName = "反映: 上書きされる前の Excel 側の本文が -auto の控えとして残る（本文はそのまま）")]
    public async Task 反映の前に自動控えが残る()
    {
        var project = await CreatedProjectAsync(FakeVbaBridge.Std("Module1", "Sub A()\r\nEnd Sub"));
        File.WriteAllText(MirrorFile(Module1), "Sub A()\r\n  x = 1\r\nEnd Sub\r\n");

        var push = await Service.PushAsync(project);

        push.Value.Pushed.Should().Equal(Module1);
        push.Issues.Should().NotContain(i => i.Code == ErrorCode.E816);
        RescueFileNames().Should().Equal("Module1-20261121-100000-auto.txt");
        File.ReadAllText(Path.Combine(RescueDir(project), "Module1-20261121-100000-auto.txt")).Should().Be("Sub A()\r\nEnd Sub");
    }

    [Fact(DisplayName = "反映: 新規作成（PushCreate）は上書きする本文が無いので、控えを作らない")]
    public async Task 新規作成は控えない()
    {
        var project = await CreatedProjectAsync(FakeVbaBridge.Std("Module1", "x"));
        Directory.CreateDirectory(Path.GetDirectoryName(MirrorFile("Classes/Fresh.cls"))!);
        File.WriteAllText(MirrorFile("Classes/Fresh.cls"), "Option Explicit\r\n");

        var push = await Service.PushAsync(project);

        push.Value.Created.Should().Equal("Classes/Fresh.cls");
        Directory.Exists(RescueDir(project)).Should().BeFalse();
    }

    [Fact(DisplayName = "反映: 衝突の「Graft 側で上書き」は従来の控え（Legacy）だけ。自動控えは重ねて作らない")]
    public async Task 衝突の上書きはLegacyだけ()
    {
        var project = await CreatedProjectAsync(FakeVbaBridge.Std("Module1", "orig"));
        Excel("Module1").Code = "excel side";
        File.WriteAllText(MirrorFile(Module1), "graft side\r\n");

        var push = await Service.PushAsync(project, overwriteConflicts: new[] { Module1 });

        push.Value.Pushed.Should().Equal(Module1);
        RescueFileNames().Should().Equal("Module1-20261121-100000.txt");
        VbaRescueNaming.Parse(RescueFileNames()[0])!.Kind.Should().Be(VbaRescueKind.Legacy);
    }

    [Fact(DisplayName = "反映: 同じ内容の自動控えは積み上げない（失敗して Excel 側が同じまま、もう一度反映したとき）")]
    public async Task 同じ内容は増えない()
    {
        var project = await CreatedProjectAsync(FakeVbaBridge.Std("Module1", "orig"));
        File.WriteAllText(MirrorFile(Module1), "changed\r\n");
        Bridge.CorruptOnWrite.Add("Module1"); // 書き込みが食い違い、元に戻される（Excel 側は orig のまま）。
        await Service.PushAsync(project);
        Bridge.CorruptOnWrite.Clear();
        RescueFileNames().Should().HaveCount(1, "1 回目の自動控え");

        await Service.PushAsync(project);

        RescueFileNames().Should().HaveCount(1, "Excel 側の本文が前回の自動控えと同じなので、増やさない");
        Excel("Module1").Code.Should().Be("changed");
    }

    [Fact(DisplayName = "反映: 内容が違えば世代が増え、同じ秒なら連番（_2）になる")]
    public async Task 違えば世代が増える()
    {
        var project = await CreatedProjectAsync(FakeVbaBridge.Std("Module1", "v0"));
        File.WriteAllText(MirrorFile(Module1), "v1\r\n");
        await Service.PushAsync(project);
        File.WriteAllText(MirrorFile(Module1), "v2\r\n");

        await Service.PushAsync(project);

        RescueFileNames().Should().Equal("Module1-20261121-100000-auto.txt", "Module1-20261121-100000-auto_2.txt");
        File.ReadAllText(Path.Combine(RescueDir(project), "Module1-20261121-100000-auto.txt")).Should().Be("v0");
        File.ReadAllText(Path.Combine(RescueDir(project), "Module1-20261121-100000-auto_2.txt")).Should().Be("v1");
    }

    [Fact(DisplayName = "反映: 控えを書けなくても反映は続く。そのモジュールに E816（警告）が付く")]
    public async Task 控えを書けなくても反映は続く()
    {
        var project = await CreatedProjectAsync(FakeVbaBridge.Std("Module1", "orig"));
        File.WriteAllText(MirrorFile(Module1), "changed\r\n");
        Directory.CreateDirectory(Paths.VbaSyncDirectory);
        File.WriteAllText(RescueDir(project), "blocker"); // 控えのフォルダの場所をファイルで塞ぐ。

        var push = await Service.PushAsync(project);

        push.IsSuccess.Should().BeTrue();
        push.Value.Pushed.Should().Equal(Module1);
        Excel("Module1").Code.Should().Be("changed");
        var warning = push.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E816).Subject;
        warning.Severity.Should().Be(Severity.Warning);
        warning.Detail.Should().Contain(Module1).And.Contain("反映は続けました").And.Contain(RescueDir(project));
    }

    [Fact(DisplayName = "反映: 控えは、全モジュールの分を書き終えてから Excel へ書く（Excel への書き込みの時点で、すべて残っている）")]
    public async Task 控えは書き込みの前に揃う()
    {
        var project = await CreatedProjectAsync(FakeVbaBridge.Std("Module1", "a0"), FakeVbaBridge.Cls("Cls1", "b0"));
        File.WriteAllText(MirrorFile(Module1), "a1\r\n");
        File.WriteAllText(MirrorFile("Classes/Cls1.cls"), "b1\r\n");
        var seen = new List<string>();
        Bridge.BeforeWrite = () =>
        {
            seen.AddRange(RescueFileNames());
            return Task.CompletedTask;
        };

        await Service.PushAsync(project);

        seen.Distinct().Should().BeEquivalentTo("Module1-20261121-100000-auto.txt", "Cls1-20261121-100000-auto.txt");
    }

    [Fact(DisplayName = "反映: 世代数を超えたら、書いた直後に古い自動控えを掃除する。従来の控えは数えず、消さない")]
    public async Task 世代数で掃除される()
    {
        var project = await CreatedProjectAsync(FakeVbaBridge.Std("Module1", "v0"));
        Service.Rescue.MaxGenerations = 2;
        Directory.CreateDirectory(RescueDir(project));
        File.WriteAllText(Path.Combine(RescueDir(project), "Module1-20200101-000000.txt"), "legacy");
        File.WriteAllText(Path.Combine(RescueDir(project), "memo.txt"), "利用者のメモ");
        for (var i = 1; i <= 4; i++)
        {
            File.WriteAllText(MirrorFile(Module1), $"v{i}\r\n");
            await Service.PushAsync(project);
        }

        var names = RescueFileNames();
        names.Where(n => n.Contains("-auto")).Should().HaveCount(2, "新しい 2 世代だけ残る");
        names.Should().Contain("Module1-20200101-000000.txt").And.Contain("memo.txt");
        RescueTexts(includeAuto: true).Should().Contain(new[] { "v2", "v3" }).And.NotContain("v0");
    }

    // ------------------------------------------------------------------
    // ミラーへ戻す
    // ------------------------------------------------------------------

    private async Task<(Project Project, VbaRescueItem Item)> ProjectWithAutoRescueAsync()
    {
        var project = await CreatedProjectAsync(FakeVbaBridge.Std("Module1", "excel v0"));
        File.WriteAllText(MirrorFile(Module1), "mirror v1\r\n");
        await Service.PushAsync(project);
        var listing = await Service.Rescue.ListAsync(project);
        listing.Items.Should().ContainSingle();
        return (project, listing.Items[0]);
    }

    [Fact(DisplayName = "戻す: ミラーに控えの本文が書かれ、同期基準は変わらず、「未反映」になる。Excel には書かない")]
    public async Task 戻すとミラーに書かれる()
    {
        var (project, item) = await ProjectWithAutoRescueAsync();
        var stateBefore = await StateAsync();
        Bridge.Calls.Clear();

        var outcome = await Service.Rescue.RestoreAsync(project, item);

        outcome.Status.Should().Be(VbaRescueRestoreStatus.Restored);
        outcome.RelativePath.Should().Be(Module1);
        File.ReadAllText(MirrorFile(Module1)).Should().Be("excel v0\r\n", "既存の形（CRLF・末尾改行）を保って書く");
        (await StateAsync()).Modules.Should().BeEquivalentTo(stateBefore.Modules, "同期基準には触らない");
        (await Service.GetLocalStatusAsync(project)).UnpushedCount.Should().Be(1, "ミラーが基準と違うので「未反映」になる");
        Bridge.Calls.Should().NotContain(c => c.StartsWith("Write:"), "Excel へは書かない");
        Excel("Module1").Code.Should().Be("mirror v1");
    }

    [Fact(DisplayName = "戻す: 戻す前のミラーが -mirror の控えとして残る。ミラーが控えと同じなら何もしない")]
    public async Task 戻す前のミラーが控えに残る()
    {
        var (project, item) = await ProjectWithAutoRescueAsync();

        var outcome = await Service.Rescue.RestoreAsync(project, item);

        outcome.MirrorBackupPath.Should().NotBeNull();
        Path.GetFileName(outcome.MirrorBackupPath!).Should().Be("Module1-20261121-100000-mirror.txt");
        File.ReadAllText(outcome.MirrorBackupPath!).Should().Be("mirror v1\r\n", "正規化せず、そのまま残す");

        var again = await Service.Rescue.RestoreAsync(project, item);

        again.Status.Should().Be(VbaRescueRestoreStatus.AlreadySame);
        RescueFileNames().Count(n => n.Contains("-mirror")).Should().Be(1, "同じ内容なので、控えを増やさない");
    }

    [Fact(DisplayName = "戻す: 戻す前のミラーを控えに残せなければ、戻さない（E816。ミラーはそのまま）")]
    public async Task 控えを残せなければ戻さない()
    {
        var (project, item) = await ProjectWithAutoRescueAsync();
        // 控えのフォルダに、次に書く名前と同じフォルダを置いて、書けなくする。
        Directory.CreateDirectory(Path.Combine(RescueDir(project), "Module1-20261121-100000-mirror.txt"));

        var outcome = await Service.Rescue.RestoreAsync(project, item);

        outcome.Status.Should().Be(VbaRescueRestoreStatus.Failed);
        outcome.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E816);
        outcome.Issues[0].Severity.Should().Be(Severity.Error);
        File.ReadAllText(MirrorFile(Module1)).Should().Be("mirror v1\r\n");
    }

    [Fact(DisplayName = "戻す: 対象のミラーに未保存の変更があって保存を断られたら、戻さない")]
    public async Task 未保存の確認を断ると戻さない()
    {
        var (project, item) = await ProjectWithAutoRescueAsync();
        var asked = new List<string>();
        Service.Rescue.BeforeRestoreAsync = paths =>
        {
            asked.AddRange(paths);
            return Task.FromResult(new VbaPullSaveOutcome(paths.ToArray(), false));
        };

        var outcome = await Service.Rescue.RestoreAsync(project, item);

        outcome.Status.Should().Be(VbaRescueRestoreStatus.Declined);
        asked.Should().Equal(MirrorFile(Module1));
        File.ReadAllText(MirrorFile(Module1)).Should().Be("mirror v1\r\n");
        RescueFileNames().Should().NotContain(n => n.Contains("-mirror"));
    }

    [Fact(DisplayName = "戻す: 保存を済ませた（断られなかった）ときは、保存後のミラーを控えにして戻す")]
    public async Task 保存したあとで戻す()
    {
        var (project, item) = await ProjectWithAutoRescueAsync();
        Service.Rescue.BeforeRestoreAsync = paths =>
        {
            File.WriteAllText(paths[0], "saved in editor\r\n"); // 確認で保存された、という想定。
            return Task.FromResult(new VbaPullSaveOutcome(Array.Empty<string>(), true));
        };

        var outcome = await Service.Rescue.RestoreAsync(project, item);

        outcome.Status.Should().Be(VbaRescueRestoreStatus.Restored);
        File.ReadAllText(outcome.MirrorBackupPath!).Should().Be("saved in editor\r\n");
        File.ReadAllText(MirrorFile(Module1)).Should().Be("excel v0\r\n");
    }

    [Fact(DisplayName = "戻す: ミラーのファイルを消して削除保留になっているモジュールでも、ファイルが再び現れ、保留が解ける")]
    public async Task 削除保留が解除される()
    {
        var (project, item) = await ProjectWithAutoRescueAsync();
        File.Delete(MirrorFile(Module1));
        (await Service.GetLocalStatusAsync(project)).DeletePendingCount.Should().Be(1);

        var outcome = await Service.Rescue.RestoreAsync(project, item);

        outcome.Status.Should().Be(VbaRescueRestoreStatus.Restored);
        outcome.MirrorBackupPath.Should().BeNull("残すミラーの本文が無い");
        File.ReadAllText(MirrorFile(Module1)).Should().Be("excel v0\r\n", "新しいファイルは UTF-8（BOM なし）・CRLF・末尾改行");
        var status = await Service.GetLocalStatusAsync(project);
        status.DeletePendingCount.Should().Be(0);
        status.UnpushedCount.Should().Be(1);
    }

    [Fact(DisplayName = "戻す: ミラーの既存の形（LF 改行・末尾改行なし）を保って書く")]
    public async Task 既存の形を保つ()
    {
        var (project, item) = await ProjectWithAutoRescueAsync();
        File.WriteAllBytes(MirrorFile(Module1), new[] { (byte)'a', (byte)'\n', (byte)'b' });

        await Service.Rescue.RestoreAsync(project, item);

        File.ReadAllText(MirrorFile(Module1)).Should().Be("excel v0");
    }

    // ------------------------------------------------------------------
    // 一覧
    // ------------------------------------------------------------------

    [Fact(DisplayName = "一覧: 新しい順で、解析できないファイルは載せない。行数・サイズ・種別が付く")]
    public async Task 一覧の中身()
    {
        var project = await CreatedProjectAsync(FakeVbaBridge.Std("Module1", "x"));
        Directory.CreateDirectory(RescueDir(project));
        File.WriteAllText(Path.Combine(RescueDir(project), "Module1-20261121-090000-auto.txt"), "a\r\nb\r\nc");
        File.WriteAllText(Path.Combine(RescueDir(project), "Module1-20261121-100000-mirror.txt"), "1\n2\n");
        File.WriteAllText(Path.Combine(RescueDir(project), "Module1-20261120-100000.txt"), "");
        File.WriteAllText(Path.Combine(RescueDir(project), "readme.txt"), "x");

        var listing = await Service.Rescue.ListAsync(project);

        listing.Items.Select(i => i.FileName).Should().Equal(
            "Module1-20261121-100000-mirror.txt", "Module1-20261121-090000-auto.txt", "Module1-20261120-100000.txt");
        listing.Items.Select(i => i.Lines).Should().Equal(2, 3, 0);
        listing.Items.Select(i => i.KindLabel).Should().Equal("戻す前のミラー", "反映前の自動控え", "上書き前");
        listing.OverLimit.Should().BeFalse();
    }

    [Fact(DisplayName = "一覧: 開いたときの掃除（prune）で、世代数を超えた分が消える。従来の控えだけで容量を超えるときは超過を知らせる")]
    public async Task 一覧で掃除と超過()
    {
        var project = await CreatedProjectAsync(FakeVbaBridge.Std("Module1", "x"));
        Directory.CreateDirectory(RescueDir(project));
        Service.Rescue.MaxGenerations = 1;
        File.WriteAllText(Path.Combine(RescueDir(project), "Module1-20261121-090000-auto.txt"), "old");
        File.WriteAllText(Path.Combine(RescueDir(project), "Module1-20261121-100000-auto.txt"), "new");

        var listing = await Service.Rescue.ListAsync(project, prune: true);

        listing.Pruned.Should().Be(1);
        listing.Items.Should().ContainSingle().Which.FileName.Should().Be("Module1-20261121-100000-auto.txt");

        Service.Rescue.MaxTotalMB = 1;
        File.WriteAllBytes(Path.Combine(RescueDir(project), "Big-20200101-000000.txt"), new byte[2 * 1024 * 1024]);
        var over = await Service.Rescue.ListAsync(project, prune: true);

        over.OverLimit.Should().BeTrue("Legacy は自動では消さない");
        File.Exists(Path.Combine(RescueDir(project), "Big-20200101-000000.txt")).Should().BeTrue();
    }

    [Fact(DisplayName = "控えの削除: 選んだ 1 件だけが消える")]
    public async Task 控えを削除する()
    {
        var (project, item) = await ProjectWithAutoRescueAsync();

        var (deleted, failure) = Service.Rescue.Delete(item);

        deleted.Should().BeTrue();
        failure.Should().BeNull();
        (await Service.Rescue.ListAsync(project)).Items.Should().BeEmpty();
    }

    [Fact(DisplayName = "疑わしい 5: 掃除は控えの中身を読まない（行数を数えない）。読めない状態のファイルがあっても、世代数を超えた古いものは掃除される")]
    public async Task 疑わしい5_掃除は中身を読まない()
    {
        if (OperatingSystem.IsWindows()) return; // 掴まれたファイルは Windows では消せない（消せなければ次回に回す仕様）。
        var project = await CreatedProjectAsync(FakeVbaBridge.Std("Module1", "v0"));
        Service.Rescue.MaxGenerations = 1;
        Directory.CreateDirectory(RescueDir(project));
        var oldest = Path.Combine(RescueDir(project), "Module1-20200101-000001-auto.txt");
        File.WriteAllText(oldest, "古い");
        File.WriteAllText(Path.Combine(RescueDir(project), "Module1-20200101-000002-auto.txt"), "新しい");

        // 行数を数えるために全ファイルを開く実装だと、他のアプリが掴んでいて開けないこのファイルは、計画から外れて残る。
        await using (new FileStream(oldest, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var listing = await Service.Rescue.ListAsync(project, prune: true);

            listing.Pruned.Should().Be(1);
        }

        File.Exists(oldest).Should().BeFalse("掃除はファイル名と大きさだけで計画する（中身は読まない）");
    }

    [Fact(DisplayName = "疑わしい 5: 控えを書くとき、フォルダの列挙（数千件の解析）は呼び出し元のスレッドで同期的に行わない")]
    public async Task 疑わしい5_書くときの列挙は呼び出し元で行わない()
    {
        var project = await CreatedProjectAsync(FakeVbaBridge.Std("Module1", "v0"));
        Directory.CreateDirectory(RescueDir(project));
        for (var i = 0; i < 6000; i++)
        {
            File.WriteAllText(Path.Combine(RescueDir(project), $"Other{i}-20200101-000000-auto.txt"), "x");
        }

        // await より前（呼び出し元のスレッドで走る部分）に、6000 件の列挙と名前の解析を含めない。
        // 修正前は、ここだけで数十ミリ秒かかった（保存のたびに UI が止まる）。
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var task = Service.Rescue.SaveAsync(project, "Module1", "code", VbaRescueKind.Auto, skipIfSameAsLatestAuto: true);
        watch.Stop();
        var saved = await task;

        saved.Status.Should().Be(VbaRescueSaveStatus.Saved);
        watch.ElapsedMilliseconds.Should().BeLessThan(15, "列挙はスレッドプールで行い、呼び出し元は先へ進める");
    }
}
