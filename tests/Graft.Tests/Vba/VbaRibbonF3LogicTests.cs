using System.IO;
using System.IO.Compression;
using FluentAssertions;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// 段階 F3 の純ロジック: 確認ダイアログの文面の出し分け（16.9）、途中で終わった書き換えの判定と知らせ（16.11）、画像の関係の読み取り
/// （16.8 の #13 の材料）、<see cref="VbaRibbonPackageWriter.RewriteFile"/> の一時ファイルの名前と置き換えの差し替え口。
/// </summary>
public sealed class VbaRibbonF3LogicTests
{
    private static VbaRibbonRewritePlan Plan(bool newPart = false, int warnings = 0, bool both = false)
    {
        var targets = new List<VbaRibbonRewriteTarget>
        {
            new(VbaRibbonFormat.Office2010, "CustomUI/customUI14.xml", "<customUI/>", "h14", newPart ? null : "<customUI/>", newPart),
        };
        if (both) targets.Add(new(VbaRibbonFormat.Office2007, "CustomUI/customUI.xml", "<customUI/>", "h12", "<customUI/>", false));
        var list = Enumerable.Range(1, warnings)
            .Select(i => new VbaRibbonRewriteWarning("CustomUI/customUI14.xml", new VbaRibbonFinding(Severity.Warning, 12, i, 3, $"警告{i}")))
            .ToList();
        return new VbaRibbonRewritePlan("p", @"C:\Work\計画.xlsm", "計画.xlsm", targets, list, @"D:\data\vba-sync\p.workbooks", 1000, DateTime.UtcNow);
    }

    [Fact(DisplayName = "確認の文面（開いていないブック）: 手順は 2 つ（控え → 差し替え）。マクロ・「元に戻す」の行は出さない。控えにシートの値が含まれることを明記")]
    public void 開いていないブックの文面()
    {
        var plan = Plan();
        var text = VbaRibbonRewriteText.FullText(plan);

        VbaRibbonRewriteText.Lead(plan).Should().Contain("「計画.xlsm」のリボン（customUI）を書き換えます").And.Contain("開いていません");
        VbaRibbonRewriteText.Steps(plan).Should().Equal(
            "1. 書き換える前のブックを、控えとして丸ごと残す", "2. リボンの XML を差し替える（CustomUI/customUI14.xml）");
        text.Should().Contain(@"控えの場所: D:\data\vba-sync\p.workbooks（ブック全体の写しです。シートの値も含みます）");
        text.Should().NotContain("Workbook_BeforeClose").And.NotContain("元に戻す").And.NotContain("次から確認しない");
        VbaRibbonRewriteText.WarningsHeading(plan).Should().BeNull();
        VbaRibbonRewriteText.RewriteButton.Should().Be("書き換える");
        VbaRibbonRewriteText.CancelButton.Should().Be("中止");
    }

    [Fact(DisplayName = "確認の文面: 新規の追加は「新しく足す」、新旧の 2 形式は 1 つの手順に並べる")]
    public void 新規と両形式()
    {
        VbaRibbonRewriteText.Steps(Plan(newPart: true))[1].Should().Be("2. リボンの XML を新しく足す（CustomUI/customUI14.xml）");
        VbaRibbonRewriteText.Steps(Plan(both: true))[1].Should().Be("2. リボンの XML を差し替える（CustomUI/customUI14.xml・CustomUI/customUI.xml）");
    }

    [Fact(DisplayName = "確認の文面: 警告は先頭 5 件と「ほか N 件」。全件の指定ですべて")]
    public void 警告の上限()
    {
        var plan = Plan(warnings: 8);
        VbaRibbonRewriteText.WarningsHeading(plan).Should().Be("検査の警告: 8 件（書き換えは止めません）");
        var lines = VbaRibbonRewriteText.WarningLines(plan);
        lines.Should().HaveCount(6);
        lines[0].Should().Be("customUI14.xml 1 行 3 列: 警告1");
        lines[^1].Should().Be("ほか 3 件");
        VbaRibbonRewriteText.WarningLines(plan, all: true).Should().HaveCount(8);
        VbaRibbonRewriteText.WarningLines(Plan(warnings: 5)).Should().HaveCount(5);
    }

    [Fact(DisplayName = "開いているアドインの案内は、利用者向けの言い方（段階の名前を出さない）")]
    public void 開いているときの案内()
    {
        VbaRibbonRewriteText.OpenAddinReason("道具.xlam").Should().Contain("アドインを閉じて").And.NotContain("F4");
        VbaRibbonRewriteText.IsAddinPath(@"C:\a\道具.XLAM").Should().BeTrue();
        VbaRibbonRewriteText.IsAddinPath(@"C:\a\計画.xlsm").Should().BeFalse();
        VbaRibbonRewriteText.FileNameOf(@"C:\Work\計画.xlsm").Should().Be("計画.xlsm");
        VbaRibbonRewriteText.FileNameOf("/tmp/a/b.xlsm").Should().Be("b.xlsm");
    }

    private static VbaRibbonRewriteJob Job(string? backupSha = "aa", bool closed = false) => new()
    {
        ProjectId = "p",
        WorkbookPath = @"C:\Work\計画.xlsm",
        BackupPath = @"D:\b\計画-20261123-100000-ribbon.xlsm",
        BackupSha256 = backupSha,
        ExpectedHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["CustomUI/customUI14.xml"] = "new" },
        ClosedWorkbook = closed,
        StartedAt = new DateTimeOffset(2026, 11, 23, 10, 5, 0, TimeSpan.FromHours(9)),
    };

    [Fact(DisplayName = "途中で終わった書き換えの判定: 控えと同じ＝元のまま、書く予定の本文＝書き換え済み、どちらでもない＝分からない、無い＝見つからない")]
    public void 後始末の判定()
    {
        var expected = new Dictionary<string, string> { ["CustomUI/customUI14.xml"] = "new" };
        VbaRibbonRecoveryLogic.Judge(Job(), "AA", null).Should().Be(VbaRibbonInterruptedState.Original, "ハッシュの大文字小文字は問わない");
        VbaRibbonRecoveryLogic.Judge(Job(), "bb", expected).Should().Be(VbaRibbonInterruptedState.Rewritten);
        VbaRibbonRecoveryLogic.Judge(Job(), "bb", new Dictionary<string, string> { ["CustomUI/customUI14.xml"] = "other" })
            .Should().Be(VbaRibbonInterruptedState.Unknown);
        VbaRibbonRecoveryLogic.Judge(Job(), "bb", null).Should().Be(VbaRibbonInterruptedState.Unknown);
        VbaRibbonRecoveryLogic.Judge(Job(), null, expected).Should().Be(VbaRibbonInterruptedState.Missing);
        VbaRibbonRecoveryLogic.Judge(Job(backupSha: null), "bb", expected).Should().Be(VbaRibbonInterruptedState.Original, "控えを書き終える前ならブックには触れていない");
    }

    [Fact(DisplayName = "後始末の知らせ: 仕様 16.11 の文。Excel で閉じたときだけ「Excel では閉じたままです」を添える")]
    public void 後始末の知らせ()
    {
        var original = VbaRibbonRecoveryLogic.Message(Job(), VbaRibbonInterruptedState.Original);
        original.Should().StartWith("前回のリボンの書き換え").And.Contain("途中で終わりました").And.Contain("「計画.xlsm」は元のままです")
            .And.Contain(@"書き換える前の控え: D:\b\計画-20261123-100000-ribbon.xlsm").And.NotContain("閉じたまま");
        VbaRibbonRecoveryLogic.Message(Job(closed: true), VbaRibbonInterruptedState.Rewritten)
            .Should().Contain("書き換え済み").And.Contain("Excel では閉じたままです");
        VbaRibbonRecoveryLogic.Message(Job(backupSha: null), VbaRibbonInterruptedState.Original).Should().NotContain("書き換える前の控え");
        VbaRibbonRecoveryLogic.Message(null, VbaRibbonInterruptedState.Unknown).Should().Contain("読めませんでした")
            .And.NotContain("ブックの中身は変えていません", "記録が読めないので、どこまで進んでいたか分からない。変えていないとは言い切らない");
    }

    [Fact(DisplayName = "画像の関係の Id: 部品の _rels を読む。無ければ空、壊れていれば null（#13 を省く）")]
    public void 画像の関係()
    {
        var rels = "<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                   + "<Relationship Id=\"logo\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"images/logo.png\"/>"
                   + "</Relationships>";
        using (var archive = Open(("customUI/customUI14.xml", "<customUI/>"), ("customUI/_rels/customUI14.xml.rels", rels)))
        {
            VbaRibbonImageRelations.ReadIds(archive, "customUI/customUI14.xml").Should().Equal("logo");
        }

        using (var archive = Open(("customUI/customUI14.xml", "<customUI/>")))
        {
            VbaRibbonImageRelations.ReadIds(archive, "/customUI/customUI14.xml").Should().BeEmpty();
        }

        using (var archive = Open(("customUI/_rels/customUI14.xml.rels", "<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><Relationships>&e;</Relationships>")))
        {
            VbaRibbonImageRelations.ReadIds(archive, "customUI/customUI14.xml").Should().BeNull("DTD を含む関係は読まない");
        }

        VbaRibbonImageRelations.ReadIds(Path.Combine(Path.GetTempPath(), "graft-no-such-" + Guid.NewGuid() + ".xlsm"), "customUI/customUI14.xml")
            .Should().BeNull();
    }

    [Fact(DisplayName = "RewriteFile: 渡した一時ファイルの名前で書き、置き換えの差し替え口を通す。置き換えの失敗は ReplaceFailed で、一時ファイルは消さずに残す（不具合 1）。置き換えの前の失敗は消す")]
    public void RewriteFileの差し替え口()
    {
        using var ws = new TempWorkspace();
        var book = ws.WriteBytes("計画.xlsm", RibbonPackageBuilder.ExcelWorkbook(RibbonPackageBuilder.SampleRibbon));
        var original = File.ReadAllBytes(book);
        var temp = VbaRibbonPackageWriter.NewTempPath(book);
        Path.GetFileName(temp).Should().MatchRegex("^~graft-計画-[0-9a-f]{8}\\.tmp$");
        Path.GetFileName(VbaRibbonPackageWriter.NewTempPath(book, ".restore")).Should().MatchRegex("^~graft-計画-[0-9a-f]{8}\\.restore\\.tmp$");

        string? seenTemp = null;
        var requests = new[] { new VbaRibbonWriteRequest(VbaRibbonFormat.Office2010, RibbonPackageBuilder.SampleRibbon.Replace("業務", "業務X", StringComparison.Ordinal)) };
        var failed = VbaRibbonPackageWriter.RewriteFile(book, requests, tempPath: temp, replace: (t, _) =>
        {
            seenTemp = t;
            File.Exists(t).Should().BeTrue();
            throw new IOException("置き換えに失敗");
        });

        // 置き換えを始めてからの失敗: 元のブックが無傷とは限らない（Win32 の ReplaceFile の 1176 など）ので「変えていません」とは言わず、
        // 呼び出し側に状態を確かめさせる。一時ファイルは、失われたかもしれない新しい内容の唯一の写しなので、消さずに残す。
        failed.Status.Should().Be(VbaRibbonWriteStatus.ReplaceFailed);
        failed.Problem.Should().NotContain("変えていません");
        seenTemp.Should().Be(temp);
        File.Exists(temp).Should().BeTrue("置き換えの途中で失敗したときは、一時ファイルを消さずに残す");
        File.ReadAllBytes(book).Should().Equal(original);
        File.Delete(temp);

        // 置き換えを始める前の失敗（一時ファイルを作れない）: 元は無傷と言い切れるので、従来どおり IoFailed。
        var noFolder = Path.Combine(ws.RootPath, "no-such-folder", "~graft-x.tmp");
        var early = VbaRibbonPackageWriter.RewriteFile(book, requests, tempPath: noFolder);
        early.Status.Should().Be(VbaRibbonWriteStatus.IoFailed);
        early.Problem.Should().Contain("変えていません");
        File.ReadAllBytes(book).Should().Equal(original);

        var ok = VbaRibbonPackageWriter.RewriteFile(book, requests, tempPath: temp);
        ok.Succeeded.Should().BeTrue();
        VbaRibbonPackageReader.Read(book).Parts.Single().Xml.Should().Contain("業務X");
    }

    private static ZipArchive Open(params (string Name, string Text)[] entries)
    {
        var bytes = RibbonPackageBuilder.Zip(entries.Select(e => (e.Name, RibbonPackageBuilder.Utf8(e.Text))).ToArray());
        return new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
    }
}
