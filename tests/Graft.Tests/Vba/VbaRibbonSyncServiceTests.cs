using System.IO;
using System.Text;
using FluentAssertions;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Features;
using Graft.Tests.TestSupport;
using Graft.UiTests.Vba;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// リボンのミラーと取込（仕様書_ブックの情報と依頼の観点 16.3・16.4。段階 F2）を、<see cref="VbaSyncService"/> と
/// <see cref="FakeVbaBridge"/>・一時フォルダのブック（zip）で確かめる。ブックへは書かない（書き換えは F3）。
/// <para>
/// ブックの「ファイル」は、Fake のブックのパス（<c>C:\Work\計画.xlsm</c>）には無いので、<see cref="VbaRibbonSyncService.PackageReader"/> を
/// 一時ファイルの zip を読む処理に差し替える（本番は <c>MainViewModel</c> が実際のファイルを読む処理を渡す）。
/// </para>
/// </summary>
public sealed class VbaRibbonSyncServiceTests : VbaServiceFixture
{
    private const string Ribbon14 = "CustomUI/customUI14.xml";
    private const string Ribbon12 = "CustomUI/customUI.xml";

    private static readonly string XmlA = RibbonPackageBuilder.SampleRibbon;
    private static readonly string XmlB = RibbonPackageBuilder.SampleRibbon.Replace("label=\"業務\"", "label=\"業務B\"", StringComparison.Ordinal);
    private static readonly string XmlC = RibbonPackageBuilder.SampleRibbon.Replace("label=\"業務\"", "label=\"業務C\"", StringComparison.Ordinal);

    private readonly string _bookFile;

    public VbaRibbonSyncServiceTests()
    {
        _bookFile = Path.Combine(Ws.CreateDirectory("books"), "計画.xlsm");
        Service.Ribbon.PackageReader = _ => VbaRibbonPackageReader.Read(_bookFile);
    }

    private void SetBook(string? customUi14 = null, string? customUiOld = null)
        => File.WriteAllBytes(_bookFile, RibbonPackageBuilder.Workbook(customUi14, customUiOld));

    private async Task<Project> ProjectAsync()
        => await CreatedProjectAsync(FakeVbaBridge.Std("Module1", "Sub A()\r\nEnd Sub"));

    private string StateFileText(Project project) => File.ReadAllText(Paths.GetVbaSyncFilePath(project.Id));

    [Fact(DisplayName = "既存のプロジェクトで最初の取込が CustomUI/ を作る（記録なし・ブックにだけある）。基準は部品名つきで同期状態の ribbon に残る")]
    public async Task 最初の取込がCustomUIを作る()
    {
        var project = await ProjectAsync();
        Directory.Exists(MirrorFile("CustomUI")).Should().BeFalse("ミラーを作った時点では、まだ取り込んでいない");
        SetBook(customUi14: XmlA);

        var pulled = await Service.PullAsync(project);

        pulled.IsSuccess.Should().BeTrue();
        pulled.Value.Pulled.Should().Equal(Ribbon14);
        File.ReadAllText(MirrorFile(Ribbon14)).Should().Be(XmlA, "部品の XML をそのまま（UTF-8・改行は部品のまま）書く");
        File.ReadAllBytes(MirrorFile(Ribbon14)).Take(3).Should().NotEqual(new byte[] { 0xEF, 0xBB, 0xBF }, "新しく作るときは BOM なし");

        var state = await StateAsync();
        state.Ribbon.Should().ContainKey(Ribbon14);
        var record = state.Ribbon![Ribbon14];
        record.PartName.Should().Be("customUI/customUI14.xml");
        record.MirrorHash.Should().Be(VbaTextNormalizer.Hash(XmlA)).And.Be(record.FileHash);
        StateFileText(project).Should().Contain("\"ribbon\"").And.Contain("\"partName\"").And.Contain("\"mirrorHash\"").And.Contain("\"fileHash\"");

        // 2 回目は何も起きない（基準と同じ）。
        var again = await Service.PullAsync(project);
        again.Value.IsEmpty.Should().BeTrue();
    }

    [Fact(DisplayName = "customUI の無いブック: CustomUI/ を作らず、同期状態に ribbon を書かない（機能 F を入れる前と同じ形）")]
    public async Task customUIが無いブックは何も作らない()
    {
        var project = await ProjectAsync();
        SetBook();
        var before = StateFileText(project);

        var pulled = await Service.PullAsync(project);

        pulled.IsSuccess.Should().BeTrue();
        pulled.Value.IsEmpty.Should().BeTrue();
        pulled.Issues.Should().BeEmpty();
        Directory.Exists(MirrorFile("CustomUI")).Should().BeFalse();
        StateFileText(project).Should().Be(before, "記録が変わらなければ書き直さない");
        StateFileText(project).Should().NotContain("ribbon");
    }

    [Fact(DisplayName = "設定でオフ（Vba.RibbonEditEnabled）・ブックの読み方が未配線: 取り込まず、CustomUI/ のファイルは従来どおり E810")]
    public async Task オフなら従来どおり()
    {
        var project = await ProjectAsync();
        SetBook(customUi14: XmlA);
        Directory.CreateDirectory(MirrorFile("CustomUI"));
        File.WriteAllText(MirrorFile(Ribbon14), XmlB);

        Service.Ribbon.IsEnabled = () => false;
        var off = await Service.PullAsync(project);
        off.Value.Pulled.Should().BeEmpty();
        off.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E810 && i.Path == Ribbon14);
        File.ReadAllText(MirrorFile(Ribbon14)).Should().Be(XmlB);

        Service.Ribbon.IsEnabled = () => true;
        Service.Ribbon.PackageReader = null;
        var unwired = await Service.PullAsync(project);
        unwired.Value.Pulled.Should().BeEmpty();
        unwired.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E810 && i.Path == Ribbon14);
        (await StateAsync()).Ribbon.Should().BeNull();
    }

    [Fact(DisplayName = "E810 の除外: リボンの 2 パスは警告にせず、CustomUI/ のそれ以外（画像・別名・入れ子）は「CustomUI/ に置けるのは…」の E810")]
    public async Task E810の除外()
    {
        var project = await ProjectAsync();
        SetBook(customUi14: XmlA);
        Directory.CreateDirectory(MirrorFile("CustomUI/images"));
        File.WriteAllBytes(MirrorFile("CustomUI/images/a.png"), new byte[] { 1, 2, 3 });
        File.WriteAllText(MirrorFile("CustomUI/ribbon.xml"), XmlA);

        var pulled = await Service.PullAsync(project);

        var e810 = pulled.Issues.Where(i => i.Code == ErrorCode.E810).ToList();
        e810.Select(i => i.Path).Should().BeEquivalentTo("CustomUI/images/a.png", "CustomUI/ribbon.xml");
        e810.Should().OnlyContain(i => i.Detail == VbaRibbonMirrorLayout.OtherFileReason && i.Severity == Severity.Warning);
    }

    [Fact(DisplayName = "Excel が開いていなくても（E804 でモジュールの取込が失敗しても）、リボンは取り込む。結果はモジュールの失敗のまま")]
    public async Task Excelが閉じていてもリボンは取り込む()
    {
        var project = await ProjectAsync();
        SetBook(customUi14: XmlA);
        Bridge.CloseWorkbook(Book);

        var pulled = await Service.PullAsync(project);

        pulled.IsSuccess.Should().BeFalse("モジュールは取り込めていない（呼び出し側が『取り込めなかった』と伝えられる）");
        pulled.Issues.Should().Contain(i => i.Code == ErrorCode.E804);
        File.ReadAllText(MirrorFile(Ribbon14)).Should().Be(XmlA);
        (await StateAsync()).Ribbon.Should().ContainKey(Ribbon14);
        (await StateAsync()).Modules.Should().ContainKey("Modules/Module1.bas", "モジュールの記録は消えない");
    }

    [Fact(DisplayName = "範囲がリボンだけ（解析の前・復元）なら Excel を読まない。閉じていても成功する（検査も取込も）")]
    public async Task 範囲がリボンだけならExcelを読まない()
    {
        var project = await ProjectAsync();
        SetBook(customUi14: XmlA);
        Bridge.CloseWorkbook(Book);
        var reads = Bridge.ReadNameRequests.Count;

        var check = await Service.CheckAsync(project, new[] { Ribbon14 });
        check.IsSuccess.Should().BeTrue();
        check.Value.Pull.Should().Equal(Ribbon14);
        check.Value.Conflicts.Should().BeEmpty();

        var pulled = await Service.PullAsync(project, new[] { Ribbon14 });
        pulled.IsSuccess.Should().BeTrue();
        pulled.Value.Pulled.Should().Equal(Ribbon14);
        Bridge.ReadNameRequests.Count.Should().Be(reads, "Excel（COM）を 1 回も読まない");
    }

    [Fact(DisplayName = "範囲がモジュールだけなら、リボンは取り込まない（解析の前の取込は、パッチの対象に絞る）")]
    public async Task 範囲がモジュールだけならリボンは取り込まない()
    {
        var project = await ProjectAsync();
        SetBook(customUi14: XmlA);

        var pulled = await Service.PullAsync(project, new[] { "Modules/Module1.bas" });

        pulled.Value.Pulled.Should().BeEmpty();
        File.Exists(MirrorFile(Ribbon14)).Should().BeFalse();
    }

    [Fact(DisplayName = "ブック側だけ変わった → 取込。ミラー側だけ変わった → 書かずに「リボン未反映」。両方変わった → 衝突（E812・取り込まない）")]
    public async Task 三方比較の取込()
    {
        var project = await ProjectAsync();
        SetBook(customUi14: XmlA);
        await Service.PullAsync(project);

        // ブック側だけ。
        SetBook(customUi14: XmlB);
        var fileOnly = await Service.PullAsync(project);
        fileOnly.Value.Pulled.Should().Equal(Ribbon14);
        File.ReadAllText(MirrorFile(Ribbon14)).Should().Be(XmlB);
        (await Service.GetLocalStatusAsync(project)).RibbonUnpushed.Should().BeFalse();

        // ミラー側だけ（AI のパッチを当てた）。F2 ではブックへ書かない。
        File.WriteAllText(MirrorFile(Ribbon14), XmlC);
        var mirrorOnly = await Service.PullAsync(project);
        mirrorOnly.Value.IsEmpty.Should().BeTrue();
        File.ReadAllText(MirrorFile(Ribbon14)).Should().Be(XmlC);
        (await Service.GetLocalStatusAsync(project)).RibbonUnpushed.Should().BeTrue("バッジに「リボン未反映」");
        new FileInfo(_bookFile).Length.Should().Be(RibbonPackageBuilder.Workbook(XmlB).Length, "ブックのファイルは変えない（書き換えは F3）");

        // 両方。
        SetBook(customUi14: XmlA);
        var both = await Service.PullAsync(project);
        both.IsSuccess.Should().BeTrue();
        both.Value.Pulled.Should().BeEmpty();
        both.Value.RibbonConflicts.Should().Equal(Ribbon14);
        both.Value.Conflicts.Should().BeEmpty("モジュールの衝突ダイアログには載せない（F5 まで）");
        both.Value.IsEmpty.Should().BeFalse();
        both.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E812 && i.Path == Ribbon14 && i.Severity == Severity.Warning);
        File.ReadAllText(MirrorFile(Ribbon14)).Should().Be(XmlC, "衝突は黙って上書きしない");

        // 「ブック側を採用」（F5 の衝突ダイアログの入口。サービスは F2 で受け付ける）。
        var adopted = await Service.PullAsync(project, new[] { Ribbon14 }, adoptConflicts: new[] { Ribbon14 });
        adopted.Value.Pulled.Should().Equal(Ribbon14);
        adopted.Value.RibbonConflicts.Should().BeEmpty();
        File.ReadAllText(MirrorFile(Ribbon14)).Should().Be(XmlA);
    }

    [Fact(DisplayName = "既存のファイルへ取り込むときは、文字コード（BOM）・改行・末尾の改行を保つ")]
    public async Task 既存のファイルの形を保つ()
    {
        var project = await ProjectAsync();
        SetBook(customUi14: XmlA);
        await Service.PullAsync(project);
        var bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        File.WriteAllText(MirrorFile(Ribbon14), XmlA.Replace("\n", "\r\n", StringComparison.Ordinal), bom);
        // 本文は同じ（改行と BOM だけの違い）なので、まだ未反映ではない。
        (await Service.GetLocalStatusAsync(project)).RibbonUnpushed.Should().BeFalse("改行・BOM だけの違いは変更にしない");

        SetBook(customUi14: XmlB);
        await Service.PullAsync(project);

        var bytes = File.ReadAllBytes(MirrorFile(Ribbon14));
        bytes.Take(3).Should().Equal(new byte[] { 0xEF, 0xBB, 0xBF });
        var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        text.Should().Be(XmlB.Replace("\n", "\r\n", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "削除保留: ミラーのファイルを消すと自動の取込では復活させず、「Excel から復元」（restoreDeletePending）で戻す")]
    public async Task 削除保留()
    {
        var project = await ProjectAsync();
        SetBook(customUi14: XmlA);
        await Service.PullAsync(project);
        File.Delete(MirrorFile(Ribbon14));

        var status = await Service.GetLocalStatusAsync(project);
        status.DeletePendingCount.Should().Be(1);
        (await Service.GetRestorablePathsAsync(project)).Should().Equal(Ribbon14);

        var auto = await Service.PullAsync(project);
        auto.Value.Pulled.Should().BeEmpty();
        File.Exists(MirrorFile(Ribbon14)).Should().BeFalse("自動の取込では復活させない");
        (await StateAsync()).Ribbon![Ribbon14].DeletePending.Should().BeTrue("削除保留の印が残る");

        // 復元はリボンだけの範囲なので、Excel が閉じていても戻せる。
        Bridge.CloseWorkbook(Book);
        var restored = await Service.PullAsync(project, await Service.GetRestorablePathsAsync(project), restoreDeletePending: true);
        restored.IsSuccess.Should().BeTrue();
        restored.Value.Pulled.Should().Equal(Ribbon14);
        File.ReadAllText(MirrorFile(Ribbon14)).Should().Be(XmlA);
        (await StateAsync()).Ribbon![Ribbon14].DeletePending.Should().BeFalse();
    }

    [Fact(DisplayName = "ブックから部品が無くなった・両方無くなった: 警告（E810）と記録の破棄。ミラーは消さない")]
    public async Task 部品が無くなった()
    {
        var project = await ProjectAsync();
        SetBook(customUi14: XmlA);
        await Service.PullAsync(project);

        SetBook();
        var missing = await Service.PullAsync(project);
        missing.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E810 && i.Path == Ribbon14
                                                   && i.Detail!.Contains("ブックからリボンの部品が無くなっています"));
        File.Exists(MirrorFile(Ribbon14)).Should().BeTrue();

        File.Delete(MirrorFile(Ribbon14));
        await Service.PullAsync(project);
        (await StateAsync()).Ribbon.Should().BeNull("両方無くなったので記録を捨てる（最後の 1 件なら ribbon ごと消える）");
        StateFileText(project).Should().NotContain("ribbon");
    }

    [Fact(DisplayName = "三方比較の Skip・SkippedReason を警告に写す: 壊れた部品・壊れたブックは E810。モジュールの取込は止めない")]
    public async Task 読めないものは警告()
    {
        var project = await ProjectAsync();
        SetBook(customUi14: XmlA);
        await Service.PullAsync(project);

        // ブック全体が読めない（壊れた zip）。
        File.WriteAllBytes(_bookFile, new byte[] { 1, 2, 3, 4 });
        Excel("Module1").Code = "Sub A()\r\n  ' 変更\r\nEnd Sub";
        var corrupt = await Service.PullAsync(project);
        corrupt.IsSuccess.Should().BeTrue();
        corrupt.Value.Pulled.Should().Equal("Modules/Module1.bas");
        corrupt.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E810 && i.Path == "CustomUI/"
                                                   && i.Detail!.Contains("リボンの同期を見送りました"));
        File.ReadAllText(MirrorFile(Ribbon14)).Should().Be(XmlA, "読めないブックを「部品が無い」と扱わない");
        (await StateAsync()).Ribbon.Should().ContainKey(Ribbon14);

        // 部品だけ壊れている（DTD つき）。
        SetBook(customUi14: "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY a \"b\">]><customUI/>");
        var broken = await Service.PullAsync(project);
        broken.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E810 && i.Path == Ribbon14);
        File.ReadAllText(MirrorFile(Ribbon14)).Should().Be(XmlA);
    }

    [Fact(DisplayName = "ミラーのファイルを読めないときは「無い」と扱わない（文字コードを判定できないファイル）: 警告して取り込まない")]
    public async Task 読めないミラー()
    {
        var project = await ProjectAsync();
        SetBook(customUi14: XmlA);
        await Service.PullAsync(project);
        SetBook(customUi14: XmlB);
        // EUC-JP の「日本語」（UTF-8 としても Shift_JIS としても正しく読めないバイト列。UnsupportedEncodingTests と同じ）。
        File.WriteAllBytes(MirrorFile(Ribbon14), new byte[] { 0x3C, 0x61, 0x3E, 0xC6, 0xFC, 0xCB, 0xDC, 0xB8, 0xEC, 0x0A });
        var bytes = File.ReadAllBytes(MirrorFile(Ribbon14));

        var pulled = await Service.PullAsync(project);

        pulled.Value.Pulled.Should().BeEmpty();
        pulled.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E810 && i.Path == Ribbon14);
        File.ReadAllBytes(MirrorFile(Ribbon14)).Should().Equal(bytes);
        var status = await Service.GetLocalStatusAsync(project);
        status.DeletePendingCount.Should().Be(0);
        status.RibbonUnpushed.Should().BeFalse("読めないものは未反映にも数えない");
    }

    [Fact(DisplayName = "別のブックの記録は使わない（E812）: リボンの記録も含めて「記録なし」から突き合わせ、違えば衝突にする")]
    public async Task 別のブックの記録はE812()
    {
        var project = await ProjectAsync();
        SetBook(customUi14: XmlA);
        await Service.PullAsync(project);
        File.WriteAllText(MirrorFile(Ribbon14), XmlC);
        var state = await StateAsync();
        await States.SaveAsync(project.Id, state with { WorkbookFullName = @"C:\Work\別.xlsm" });

        var pulled = await Service.PullAsync(project);

        pulled.Issues.Should().Contain(i => i.Code == ErrorCode.E812 && i.Path == null && i.Detail!.Contains("別のブック"));
        pulled.Value.RibbonConflicts.Should().Equal(new[] { Ribbon14 }, "記録なし・両方ある・本文が違う → 衝突（黙って取り込まない）");
        File.ReadAllText(MirrorFile(Ribbon14)).Should().Be(XmlC);
    }

    [Fact(DisplayName = "新旧両方の部品: 2 つのファイルに分けて取り込み、それぞれ部品名を記録する")]
    public async Task 新旧両方()
    {
        var project = await ProjectAsync();
        var old = XmlA.Replace("2009/07", "2006/01", StringComparison.Ordinal);
        SetBook(customUi14: XmlA, customUiOld: old);

        var pulled = await Service.PullAsync(project);

        pulled.Value.Pulled.Should().BeEquivalentTo(Ribbon14, Ribbon12);
        File.ReadAllText(MirrorFile(Ribbon12)).Should().Be(old);
        (await StateAsync()).Ribbon![Ribbon12].PartName.Should().Be("customUI/customUI.xml");
    }

    [Fact(DisplayName = "未保存の編集の確認: 断ったリボンのファイルは取り込まず（E702）、スキップに載る")]
    public async Task 未保存の確認()
    {
        var project = await ProjectAsync();
        SetBook(customUi14: XmlA);
        await Service.PullAsync(project);
        SetBook(customUi14: XmlB);
        var asked = new List<string>();
        Service.BeforePullAsync = files =>
        {
            asked.AddRange(files);
            return Task.FromResult(new VbaPullSaveOutcome(files.ToList(), false));
        };

        var pulled = await Service.PullAsync(project);

        asked.Should().ContainSingle().Which.Should().Be(MirrorFile(Ribbon14));
        pulled.Value.Skipped.Should().Equal(Ribbon14);
        pulled.Value.Pulled.Should().BeEmpty();
        pulled.Issues.Should().Contain(i => i.Code == ErrorCode.E702 && i.Path == Ribbon14);
        File.ReadAllText(MirrorFile(Ribbon14)).Should().Be(XmlA);
    }

    [Fact(DisplayName = "作り直し: CustomUI/ はミラーのフォルダとして数え、リボンの 2 ファイルもごみ箱へ送る（画像などは巻き込まない）")]
    public async Task 作り直し()
    {
        var project = await ProjectAsync();
        SetBook(customUi14: XmlA);
        await Service.PullAsync(project);
        Directory.CreateDirectory(MirrorFile("CustomUI/images"));
        File.WriteAllBytes(MirrorFile("CustomUI/images/a.png"), new byte[] { 1 });

        var info = Service.InspectMirrorFolder(MirrorRoot);
        info.Exists.Should().BeTrue();
        info.HasForeignFiles.Should().BeFalse("CustomUI/ は Graft が作るミラーのフォルダ");
        info.CanRebuild.Should().BeTrue();

        var rebuilt = await Service.CreateMirrorAsync(Book, MirrorRoot, rebuild: true);

        rebuilt.IsSuccess.Should().BeTrue();
        Trash.Sent.Should().Contain(MirrorFile(Ribbon14));
        Trash.Sent.Should().NotContain(MirrorFile("CustomUI/images/a.png"));
        File.Exists(MirrorFile("CustomUI/images/a.png")).Should().BeTrue();
        (await StateAsync()).Ribbon.Should().BeNull("同期状態も作り直す。次の取込で突き合わせ直す");
        // このテストのごみ箱の偽物はファイルを消さない（フォルダだけ消す）ので、ミラーとブックが同じ本文の「記録なし・両方ある」になり、
        // 基準だけを作り直す。
        await Service.PullAsync(project);
        (await StateAsync()).Ribbon.Should().ContainKey(Ribbon14);
    }

    [Fact(DisplayName = "モジュールの反映（Push）は、リボンの記録を消さずに残す")]
    public async Task 反映でリボンの記録が残る()
    {
        var project = await ProjectAsync();
        SetBook(customUi14: XmlA);
        await Service.PullAsync(project);
        File.WriteAllText(MirrorFile("Modules/Module1.bas"), "Sub A()\r\n  ' 反映\r\nEnd Sub\r\n");

        var pushed = await Service.PushAsync(project);

        pushed.Value.Pushed.Should().Equal("Modules/Module1.bas");
        (await StateAsync()).Ribbon.Should().ContainKey(Ribbon14);
    }
}
