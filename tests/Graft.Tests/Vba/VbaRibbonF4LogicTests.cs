using FluentAssertions;
using Graft.Core;
using Graft.Core.Vba;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// 段階 F4 の純ロジック（仕様書_ブックの情報と依頼の観点 16.6・16.9）: 止めるブックの判定、確認ダイアログの文面（開いている・未保存あり／なし・
/// VBA の反映に続けて）、開き直したあとの見た目を戻す手順の決め方、失敗の知らせに出す操作の判定。COM にも画面にも触れない。
/// </summary>
public sealed class VbaRibbonF4LogicTests
{
    private const string Book = @"C:\Work\計画.xlsm";

    private static VbaRibbonRewritePlan Plan(
        VbaWorkbookRewriteState? open = null, bool newPart = false, int preceding = 0, bool both = false)
    {
        var targets = new List<VbaRibbonRewriteTarget>
        {
            new(VbaRibbonFormat.Office2010, "CustomUI/customUI14.xml", "<customUI/>", "h14", newPart ? null : "<customUI/>", newPart),
        };
        if (both) targets.Add(new(VbaRibbonFormat.Office2007, "CustomUI/customUI.xml", "<customUI/>", "h12", "<customUI/>", false));
        return new VbaRibbonRewritePlan(
            "p", Book, "計画.xlsm", targets, Array.Empty<VbaRibbonRewriteWarning>(), @"D:\data\vba-sync\p.workbooks", 1000, DateTime.UtcNow,
            open, preceding);
    }

    // ------------------------------------------------------------------
    // 止めるブック（16.6 の表）
    // ------------------------------------------------------------------

    [Fact(DisplayName = "止めるブック: 開いていなければ止めない（ファイルだけを書き換える経路）")]
    public void 開いていなければ止めない()
        => VbaWorkbookRewriteGuard.Check(VbaWorkbookRewriteState.NotOpen, Book).Should().BeNull();

    [Fact(DisplayName = "止めるブック: 開いていて、止める条件が無ければ止めない（未保存・NOW() による Saved=False は止める理由ではない）")]
    public void 普通のブックは止めない()
    {
        VbaWorkbookRewriteGuard.Check(new VbaWorkbookRewriteState(true), Book).Should().BeNull();
        VbaWorkbookRewriteGuard.Check(new VbaWorkbookRewriteState(true, Saved: false), Book).Should().BeNull("未保存は文面とボタンを変えるだけ。止めるのは確認で行う");
        VbaWorkbookRewriteGuard.Check(new VbaWorkbookRewriteState(true, ProjectMode: VbaProjectState.ModeDesign), Book).Should().BeNull();
    }

    public static IEnumerable<object[]> StopRows()
    {
        yield return new object[] { new VbaWorkbookRewriteState(true, ReadOnly: true), ErrorCode.E819, "読み取り専用" };
        yield return new object[] { new VbaWorkbookRewriteState(true, IsProtectedView: true), ErrorCode.E819, "保護ビュー" };
        yield return new object[] { new VbaWorkbookRewriteState(true, MultiUserEditing: true), ErrorCode.E819, "共有ブック" };
        yield return new object[] { new VbaWorkbookRewriteState(true, AutoSaveOn: true), ErrorCode.E819, "自動保存" };
        yield return new object[] { new VbaWorkbookRewriteState(true, IsUrlLocation: true), ErrorCode.E819, "OneDrive" };
        yield return new object[] { new VbaWorkbookRewriteState(true, IsAddin: true), ErrorCode.E819, "アドイン" };
        yield return new object[] { new VbaWorkbookRewriteState(true, ProjectMode: 0), ErrorCode.E813, "マクロが実行中" };
        yield return new object[] { new VbaWorkbookRewriteState(true, ProjectMode: 1), ErrorCode.E813, "VBProject.Mode=1" };
    }

    [Theory(DisplayName = "止めるブック: 表の 1 行ごとに、コードと案内。どれも「ブックは何も変えていません」で終わる")]
    [MemberData(nameof(StopRows))]
    public void 止める行(VbaWorkbookRewriteState state, ErrorCode code, string word)
    {
        var issue = VbaWorkbookRewriteGuard.Check(state, Book);

        issue.Should().NotBeNull();
        issue!.Code.Should().Be(code);
        issue.Detail.Should().Contain(word).And.Contain("計画.xlsm").And.EndWith("ブックは何も変えていません。");
        issue.Path.Should().Be(Book);
    }

    [Fact(DisplayName = "止めるブック: 拡張子が .xlam なら IsAddin が False でもアドインとして止める。URL のパスも同じ")]
    public void パスからも見分ける()
    {
        VbaWorkbookRewriteGuard.Check(new VbaWorkbookRewriteState(true), @"C:\a\道具.xlam")!.Detail.Should().Contain("アドイン");
        VbaWorkbookRewriteGuard.Check(new VbaWorkbookRewriteState(true), "https://example.sharepoint.com/a/計画.xlsm")!.Detail.Should().Contain("OneDrive");
    }

    [Fact(DisplayName = "止めるブック: 複数当てはまるときは 1 つだけ、利用者がすることの大きい順（URL → 保護ビュー → アドイン → 読み取り専用 → 共有 → 自動保存 → マクロ）")]
    public void 並び()
    {
        var all = new VbaWorkbookRewriteState(true, ReadOnly: true, IsAddin: true, MultiUserEditing: true, AutoSaveOn: true, IsProtectedView: true, IsUrlLocation: true, ProjectMode: 1);
        VbaWorkbookRewriteGuard.Check(all, Book)!.Detail.Should().Contain("OneDrive");
        VbaWorkbookRewriteGuard.Check(all with { IsUrlLocation = false }, Book)!.Detail.Should().Contain("保護ビュー");
        VbaWorkbookRewriteGuard.Check(all with { IsUrlLocation = false, IsProtectedView = false }, Book)!.Detail.Should().Contain("アドイン");
        VbaWorkbookRewriteGuard.Check(new VbaWorkbookRewriteState(true, ReadOnly: true, ProjectMode: 1), Book)!.Code.Should().Be(ErrorCode.E819, "E819 が E813 より先");
    }

    // ------------------------------------------------------------------
    // 確認の文面（16.9 の表の 4 通り）
    // ------------------------------------------------------------------

    [Fact(DisplayName = "確認の文面（開いていて未保存あり）: 手順 4 つ・⚠ の段落・マクロと「元に戻す」の行・ボタン「保存して書き換える」")]
    public void 開いていて未保存あり()
    {
        var plan = Plan(new VbaWorkbookRewriteState(true, Saved: false));
        var text = VbaRibbonRewriteText.FullText(plan);

        VbaRibbonRewriteText.Lead(plan).Should().Contain("Excel で開いているブックは書き換えられないため、Graft が次の順に行います");
        VbaRibbonRewriteText.Steps(plan).Should().Equal(
            "1. ブックを保存して閉じる", "2. 書き換える前のブックを、控えとして丸ごと残す", "3. リボンの XML を差し替える（CustomUI/customUI14.xml）", "4. ブックを開き直す");
        VbaRibbonRewriteText.UnsavedWarning(plan).Should().Be("⚠ このブックには、まだ保存していない変更があります。続けると、その変更も保存されます（再計算だけでも、この表示になります）。");
        VbaRibbonRewriteText.OpenNotices(plan).Should().HaveCount(2);
        text.Should().Contain("Workbook_BeforeClose・Workbook_Open").And.Contain("「元に戻す」の履歴").And.Contain("⚠").And.Contain("控えの場所:");
        VbaRibbonRewriteText.ConfirmButtonText(plan).Should().Be("保存して書き換える");
    }

    [Fact(DisplayName = "確認の文面（開いていて未保存なし）: ⚠ の段落だけ出さない。手順とボタンは同じ（保存は必ず行うので、文面を変えて嘘を書かない）")]
    public void 開いていて未保存なし()
    {
        var withUnsaved = Plan(new VbaWorkbookRewriteState(true, Saved: false));
        var saved = Plan(new VbaWorkbookRewriteState(true, Saved: true));

        VbaRibbonRewriteText.UnsavedWarning(saved).Should().BeNull();
        VbaRibbonRewriteText.FullText(saved).Should().NotContain("⚠").And.Contain("Workbook_BeforeClose");
        VbaRibbonRewriteText.Steps(saved).Should().Equal(VbaRibbonRewriteText.Steps(withUnsaved));
        VbaRibbonRewriteText.ConfirmButtonText(saved).Should().Be(VbaRibbonRewriteText.ConfirmButtonText(withUnsaved));
        VbaRibbonRewriteText.Lead(saved).Should().Be(VbaRibbonRewriteText.Lead(withUnsaved));
    }

    [Fact(DisplayName = "確認の文面（開いていない）: 従来どおり（手順 2 つ・マクロの行なし・ボタン「書き換える」）。開いている計画の NotOpen も同じ")]
    public void 開いていない()
    {
        foreach (var plan in new[] { Plan(), Plan(VbaWorkbookRewriteState.NotOpen) })
        {
            plan.IsOpen.Should().BeFalse();
            VbaRibbonRewriteText.Steps(plan).Should().HaveCount(2);
            VbaRibbonRewriteText.OpenNotices(plan).Should().BeEmpty();
            VbaRibbonRewriteText.UnsavedWarning(plan).Should().BeNull();
            VbaRibbonRewriteText.ConfirmButtonText(plan).Should().Be("書き換える");
            VbaRibbonRewriteText.FullText(plan).Should().NotContain("Workbook_BeforeClose").And.Contain("開いていません");
        }
    }

    [Fact(DisplayName = "確認の文面（VBA の反映に続けて）: 冒頭に件数の一文。0 件なら出さない。開いている・いないのどちらにも付く")]
    public void 続けて出すとき()
    {
        var open = Plan(new VbaWorkbookRewriteState(true), preceding: 3);
        VbaRibbonRewriteText.Lead(open).Should().StartWith("VBA のモジュール 3 件は Excel へ反映しました。続けてリボンを反映します。");
        VbaRibbonRewriteText.Lead(Plan(preceding: 1)).Should().StartWith("VBA のモジュール 1 件は");
        VbaRibbonRewriteText.Lead(Plan(new VbaWorkbookRewriteState(true))).Should().NotContain("VBA のモジュール");
        VbaRibbonRewriteText.PrecedingVbaLine(Plan()).Should().BeNull();
    }

    [Fact(DisplayName = "確認の文面: 新規の追加・新旧 2 形式は、開いているブックでは手順 3 の言い方が変わる")]
    public void 新規と両形式()
    {
        var open = new VbaWorkbookRewriteState(true);
        VbaRibbonRewriteText.Steps(Plan(open, newPart: true))[2].Should().Be("3. リボンの XML を新しく足す（CustomUI/customUI14.xml）");
        VbaRibbonRewriteText.Steps(Plan(open, both: true))[2].Should().Be("3. リボンの XML を差し替える（CustomUI/customUI14.xml・CustomUI/customUI.xml）");
    }

    [Fact(DisplayName = "確認の文面: 「次から確認しない」は無く、実行のボタンは 2 通りだけ")]
    public void 次から確認しないは無い()
    {
        VbaRibbonRewriteText.FullText(Plan(new VbaWorkbookRewriteState(true, Saved: false))).Should().NotContain("次から確認しない");
        VbaRibbonRewriteText.CancelButton.Should().Be("中止");
        new[] { VbaRibbonRewriteText.RewriteButton, VbaRibbonRewriteText.SaveAndRewriteButton }.Should().Equal("書き換える", "保存して書き換える");
    }

    // ------------------------------------------------------------------
    // 失敗の知らせに出す操作
    // ------------------------------------------------------------------

    [Fact(DisplayName = "「もう一度試す」の対象: E805・E813・使用中の E821。開いているときの E819 は外す。そのほかの E821・E820・E822 は出さない")]
    public void もう一度試す()
    {
        VbaRibbonRewriteText.IsRetryable(GraftIssue.Of(ErrorCode.E805)).Should().BeTrue();
        VbaRibbonRewriteText.IsRetryable(GraftIssue.Of(ErrorCode.E813)).Should().BeTrue();
        VbaRibbonRewriteText.IsRetryable(GraftIssue.Of(ErrorCode.E821, "「計画.xlsm」をほかのプログラムが使っているため")).Should().BeTrue();
        VbaRibbonRewriteText.IsRetryable(GraftIssue.Of(ErrorCode.E821, "置き換えに失敗しました")).Should().BeFalse();
        VbaRibbonRewriteText.IsRetryable(GraftIssue.Of(ErrorCode.E819, "「計画.xlsm」は読み取り専用です。閉じて、もう一度")).Should().BeFalse("開いているときの E819 は閉じ開きの経路に置き換わった");
        VbaRibbonRewriteText.IsRetryable(GraftIssue.Of(ErrorCode.E820)).Should().BeFalse();
        VbaRibbonRewriteText.IsRetryable(GraftIssue.Of(ErrorCode.E822)).Should().BeFalse();
    }

    [Fact(DisplayName = "［Excel で開く］の対象: 開き直せなかった E822、時間切れのあとで閉じられていた E820。保存・閉じるに失敗した E820 は出さない（ブックは開いたまま）")]
    public void Excelで開く()
    {
        VbaRibbonRewriteText.OffersOpenInExcel(GraftIssue.Of(ErrorCode.E822, "開き直せませんでした")).Should().BeTrue();
        VbaRibbonRewriteText.OffersOpenInExcel(GraftIssue.Of(ErrorCode.E820, "閉じられていました。" + VbaRibbonRewriteText.OpenInExcelSentence)).Should().BeTrue();
        VbaRibbonRewriteText.OffersOpenInExcel(GraftIssue.Of(ErrorCode.E820, "保存できませんでした。ブックは Excel で開いたままです。")).Should().BeFalse();
        VbaRibbonRewriteText.OffersOpenInExcel(GraftIssue.Of(ErrorCode.E821)).Should().BeFalse();
    }

    // ------------------------------------------------------------------
    // 見た目を戻す手順（16.6「開き直す」の 2）
    // ------------------------------------------------------------------

    [Fact(DisplayName = "見た目を戻す: 位置と大きさは「通常」のときだけ。最大化・最小化は WindowState だけ（Left などを書くと「通常」に戻って崩れるため）")]
    public void 位置は通常のときだけ()
    {
        var normal = new VbaWorkbookViewState("Sheet1", VbaWorkbookViewRestore.Normal, 10, 20, 800, 600);
        VbaWorkbookViewRestore.RestoresWindowState(normal).Should().BeTrue();
        VbaWorkbookViewRestore.RestoresBounds(normal).Should().BeTrue();

        var maximized = normal with { WindowState = VbaWorkbookViewRestore.Maximized };
        VbaWorkbookViewRestore.RestoresWindowState(maximized).Should().BeTrue();
        VbaWorkbookViewRestore.RestoresBounds(maximized).Should().BeFalse();
        VbaWorkbookViewRestore.RestoresBounds(normal with { WindowState = VbaWorkbookViewRestore.Minimized }).Should().BeFalse();

        VbaWorkbookViewRestore.RestoresWindowState(normal with { WindowState = null }).Should().BeFalse("取れなかった項目は戻さない");
        VbaWorkbookViewRestore.RestoresWindowState(normal with { WindowState = 99 }).Should().BeFalse("知らない値は書かない");
        VbaWorkbookViewRestore.RestoresBounds(normal with { Width = 0 }).Should().BeFalse("大きさが正でなければ戻さない");
        VbaWorkbookViewRestore.RestoresBounds(normal with { Left = null }).Should().BeFalse();
    }

    [Fact(DisplayName = "見た目を戻す: 閉じる前にアクティブでなかったブックだけ、前にアクティブだったブックを前面へ戻す（同じパスは戻さない）")]
    public void 前面へ戻す()
    {
        var inactive = new VbaWorkbookViewState(WasActive: false, PreviouslyActiveWorkbook: @"C:\Work\別.xlsx");
        VbaWorkbookViewRestore.ReactivatesPrevious(inactive, Book).Should().BeTrue();
        VbaWorkbookViewRestore.ReactivatesPrevious(inactive with { WasActive = true }, Book).Should().BeFalse();
        VbaWorkbookViewRestore.ReactivatesPrevious(inactive with { PreviouslyActiveWorkbook = null }, Book).Should().BeFalse("相手が分からなければ動かさない");
        VbaWorkbookViewRestore.ReactivatesPrevious(inactive with { PreviouslyActiveWorkbook = @"c:\work\計画.XLSM" }, Book).Should().BeFalse("同じブック自身は前面へ戻す相手ではない");
    }

    // ------------------------------------------------------------------
    // 後始末の知らせ・待ち時間の置き場
    // ------------------------------------------------------------------

    [Fact(DisplayName = "排他で開けるまで待つ時間は 1 か所の定数（実機検証 12 で見直す）。保存・開き直しの時間切れの既定は設定と同じ 120 秒")]
    public void 待ち時間の置き場()
    {
        VbaRibbonRewriteTimings.ExclusiveTimeout.Should().Be(TimeSpan.FromSeconds(10));
        VbaRibbonRewriteTimings.ExclusiveInterval.Should().Be(TimeSpan.FromMilliseconds(200));
        VbaRibbonRewriteTimings.ExclusiveTimeoutAfterExcelClose.Should().BeGreaterThanOrEqualTo(VbaRibbonRewriteTimings.ExclusiveTimeout);
        new Graft.Infra.VbaSettings().WorkbookSaveOpenTimeoutMs.Should().Be(120000);
    }
}
