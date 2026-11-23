using System.IO;
using FluentAssertions;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Features;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// 「閉じたのに ROT（実行中オブジェクト表）に残る」状況（実機検証 6-34 の 5-b。2026-11-24 に、閉じたブックが 10 秒以上 ROT に残ることを
/// 5 回とも確かめた。仕様書_ブックの情報と依頼の観点 16.6・付録A-84）。
/// <para>
/// 本物のブリッジは、ROT の項目を候補にとどめ、項目の Excel の <c>Workbooks</c> に実在するブックだけを「開いている」とする。
/// Excel の代役（<see cref="Graft.UiTests.Vba.FakeVbaBridge"/>）も同じ判定（<see cref="VbaRotNames.IsLiveEntry"/>）で、閉じたあとも古い項目を
/// 残す設定（<c>StaleRotAfterClose</c>）を持つ。ここで固定するのは次の 4 つ。
/// (1) F3 の「開いていたら止める」が、閉じた直後に誤って止めない、(2) 書き換えのあとの開き直しが二重に開かない、
/// (3) 保存して閉じるの時間切れのあと、閉じていたのを「開いたまま」と取り違えない、(4) 開き直しの時間切れのあと、
/// 開いていないのを「開いた」と取り違えない。
/// </para>
/// </summary>
public sealed partial class VbaRibbonRewriteServiceTests
{
    [Theory(DisplayName = "閉じたのに ROT に残る: 準備（F3 の「開いていたら止める」）は誤って止めず、ファイルだけを書き換える経路に進む（読み取り専用・アドインだった項目でも）")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task 閉じた直後の古い項目で誤って止めない(bool objectDead, bool wasReadOnlyAddin)
    {
        _bridge.StaleRotAfterClose = true;
        _bridge.StaleRotObjectDead = objectDead;
        File.WriteAllBytes(_book, Graft.Tests.TestSupport.RibbonPackageBuilder.ExcelWorkbook(XmlA));
        var workbook = _bridge.AddWorkbook(_book, Graft.UiTests.Vba.FakeVbaBridge.Std("Module1", "Public Sub OnRun(control As IRibbonControl)\r\nEnd Sub"));
        (await _service.CreateMirrorAsync(_book, _mirror, rebuild: false)).IsSuccess.Should().BeTrue();
        (await _service.PullAsync(Project)).IsSuccess.Should().BeTrue();
        // 止めるブックだったことにして閉じる（古い項目の側の状態に引きずられて止めないこと）。
        workbook.IsReadOnly = wasReadOnlyAddin;
        workbook.IsAddin = wasReadOnlyAddin;
        _bridge.CloseWorkbook(_book);
        _bridge.RotEntryCount.Should().BeGreaterThan(0, "閉じても ROT の項目は残っている（実機の挙動）");
        WriteMirror(Ribbon14, XmlB);

        var prepared = await Ribbon.PrepareRewriteAsync(Project);

        prepared.IsSuccess.Should().BeTrue(string.Join(" / ", prepared.Issues.Select(i => i.ToDisplayText())));
        prepared.Value.Kind.Should().Be(VbaRibbonRewritePreparationKind.Ready);
        prepared.Value.Plan!.IsOpen.Should().BeFalse("閉じたブックは、ROT に残っていても「開いていない」");
        _bridge.StaleRotRejected.Should().BeGreaterThan(0, "古い項目を確かめて読み飛ばした");
        (await _bridge.ListOpenWorkbooksAsync()).Value.Should().BeEmpty("一覧にも出さない（バッジの「ブックが開いていません」も同じ根拠）");

        var result = await Ribbon.RewriteAsync(Project, prepared.Value.Plan);
        result.IsSuccess.Should().BeTrue(string.Join(" / ", result.Issues.Select(i => i.ToDisplayText())));
        result.Value.WasOpen.Should().BeFalse();
        CloseOpenCalls().Should().BeEmpty("開いていないので、保存も閉じるも開き直しも呼ばない");
        PartText(File.ReadAllBytes(_book), "customUI/customUI14.xml").Should().Be(XmlB);
        NothingLeftBehind();
    }

    [Theory(DisplayName = "閉じ開きの全体で ROT に古い項目が残っても、開き直しは 1 回だけ（二重に開かない）。券は残らない")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 古い項目が残っても二重に開かない(bool objectDead)
    {
        _bridge.StaleRotAfterClose = true;
        _bridge.StaleRotObjectDead = objectDead;
        await ArrangeOpenAsync(XmlA);
        WriteMirror(Ribbon14, XmlB);
        var plan = await PlanAsync();
        plan.IsOpen.Should().BeTrue();

        var result = await Ribbon.RewriteAsync(Project, plan);

        result.IsSuccess.Should().BeTrue(string.Join(" / ", result.Issues.Select(i => i.ToDisplayText())));
        result.Value.Reopened.Should().BeTrue();
        CloseOpenCalls().Should().Equal("SaveClose", "Reopen");
        OpenBook.ReopenCount.Should().Be(1, "古い項目と新しい項目が並んでも、開き直しは 1 回");
        OpenBook.CloseCount.Should().Be(1);
        _bridge.RotEntryCount.Should().Be(2, "古い項目と、開き直しで増えた項目が並ぶ（実機の挙動）");
        var listed = (await _bridge.ListOpenWorkbooksAsync()).Value;
        listed.Should().ContainSingle("一覧には、実在する 1 冊だけ（同じパスが二重に載らない）");
        NoTicketLeft();
        NothingLeftBehind();

        // もう一度書き換えても、古い項目に惑わされず、開いているブックとして扱って閉じ開きをやり直せる。
        WriteMirror(Ribbon14, XmlA);
        var again = await PlanAsync();
        again.IsOpen.Should().BeTrue("開き直したブックは、古い項目と並んでいても「開いている」");
        (await Ribbon.RewriteAsync(Project, again)).IsSuccess.Should().BeTrue();
        OpenBook.ReopenCount.Should().Be(2);
        NoTicketLeft();
    }

    [Fact(DisplayName = "保存して閉じるが時間切れ、Excel 側では閉じていて ROT に古い項目が残る: 「開いたまま」と取り違えず、E820 で「Excel で開いてください」と案内する")]
    public async Task 時間切れのあと古い項目が残っていても閉じたと分かる()
    {
        _bridge.StaleRotAfterClose = true;
        var original = await ArrangeOpenAsync(XmlA);
        WriteMirror(Ribbon14, XmlB);
        var plan = await PlanAsync();
        _bridge.HangOperations.Add("SaveClose");
        _bridge.CompleteLateAfterHang = true;

        var result = await Ribbon.RewriteAsync(Project, plan);

        var issue = result.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E820).Subject;
        issue.Detail.Should().Contain("閉じられていました").And.NotContain("開いたままです");
        VbaRibbonRewriteText.OffersOpenInExcel(issue).Should().BeTrue();
        BookShouldBeUnchanged(original, "書き換えない");
        NoTicketLeft();
        NothingLeftBehind();
    }

    [Fact(DisplayName = "開き直しが時間切れで開いておらず、ROT に古い項目が残る: 「開いた」と取り違えず、E822（警告）にする")]
    public async Task 開き直しの時間切れで古い項目に惑わされない()
    {
        _bridge.StaleRotAfterClose = true;
        await ArrangeOpenAsync(XmlA);
        WriteMirror(Ribbon14, XmlB);
        var plan = await PlanAsync();
        _bridge.HangOperations.Add("Reopen");

        var result = await Ribbon.RewriteAsync(Project, plan);

        result.IsSuccess.Should().BeTrue("書き換えは済んでいる");
        result.Value.Reopened.Should().BeFalse("開いていない。古い項目で「開いた」と見なさない");
        result.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E822);
        _bridge.Get(_book).Should().BeNull();
        PartText(File.ReadAllBytes(_book), "customUI/customUI14.xml").Should().Be(XmlB);
        NoTicketLeft();
    }

    [Fact(DisplayName = "利用者が Excel でブックを閉じたあと（ROT に古い項目が残る）の書き換え: 確認の前に閉じていた場合も、閉じ開きの経路へ進まない")]
    public async Task 利用者が閉じた直後の古い項目()
    {
        _bridge.StaleRotAfterClose = true;
        await ArrangeOpenAsync(XmlA);
        WriteMirror(Ribbon14, XmlB);
        var plan = await PlanAsync();
        plan.IsOpen.Should().BeTrue();

        // 確認のあいだに、利用者が Excel でブックを閉じた（古い項目は ROT に残る）。
        _bridge.CloseWorkbook(_book);
        var result = await Ribbon.RewriteAsync(Project, plan);

        result.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E821 && i.Detail!.Contains("閉じられた", StringComparison.Ordinal),
            "確認した経路（開いている）と違うので止める。ROT の古い項目を見て「まだ開いている」と続けない");
        CloseOpenCalls().Should().BeEmpty();
        BackupFiles().Should().BeEmpty();
    }
}
