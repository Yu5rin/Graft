using FluentAssertions;
using Graft.Core.Vba;
using Graft.Features;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// <see cref="VbaRibbonFormatter"/>（リボンの取得結果 → 依頼文の文）と、取得（<see cref="VbaRibbonCollector"/>）の結合のテスト。
/// 仕様書_ブックの情報と依頼の観点 15.4・15.5・15.8。3 状態の出し分け・上限・「最後に保存した内容」の明記。
/// </summary>
public class VbaRibbonFormatterTests
{
    private static async Task<VbaRibbonSnapshot> CollectAsync(
        TempWorkspace ws, string? customUi14 = RibbonPackageBuilder.SampleRibbon, string? customUiOld = null, bool withMirror = true)
    {
        var book = ws.WriteBytes("計画.xlsm", RibbonPackageBuilder.Workbook(customUi14, customUiOld));
        File.SetLastWriteTime(book, new DateTime(2026, 11, 20, 18, 5, 0));
        var mirror = ws.CreateDirectory("mirror");
        if (withMirror)
        {
            ws.WriteText("mirror/Modules/Module1.bas",
                "Public Sub OnRun(control As IRibbonControl)\r\nEnd Sub\r\n"
                + "Private Sub GetEnabled(control As IRibbonControl, ByRef r)\r\nEnd Sub\r\n"
                + "Public Sub OnCopy(control As IRibbonControl)\r\nEnd Sub\r\n");
        }

        return await VbaRibbonCollector.CollectAsync(book, withMirror ? mirror : Path.Combine(ws.RootPath, "無いミラー"));
    }

    [Fact(DisplayName = "取得: 部品・構成・コールバックの照合が結果に入る（Public／Private／無い／モジュール名つき）")]
    public async Task 取得して照合する()
    {
        using var ws = new TempWorkspace();

        var snapshot = await CollectAsync(ws);

        snapshot.Status.Should().Be(VbaRibbonReadStatus.Ok);
        snapshot.HasContent.Should().BeTrue();
        snapshot.WorkbookName.Should().Be("計画.xlsm");
        snapshot.FileModified!.Value.DateTime.Should().Be(new DateTime(2026, 11, 20, 18, 5, 0));
        snapshot.Callbacks.ToDictionary(c => c.Name, c => c.Status).Should().BeEquivalentTo(new Dictionary<string, VbaRibbonCallbackStatus>
        {
            ["RibbonLoaded"] = VbaRibbonCallbackStatus.NotFound,
            ["OnRun"] = VbaRibbonCallbackStatus.Found,
            ["GetEnabled"] = VbaRibbonCallbackStatus.NotPublic,
            ["Module1.OnCopy"] = VbaRibbonCallbackStatus.Found,
            ["OnPick"] = VbaRibbonCallbackStatus.NotFound,
            ["CountItems"] = VbaRibbonCallbackStatus.NotFound,
        });
    }

    [Fact(DisplayName = "ミラーのフォルダが無ければ、リボンは出し、コールバックの照合だけ「できていません」")]
    public async Task ミラーが無い()
    {
        using var ws = new TempWorkspace();

        var snapshot = await CollectAsync(ws, withMirror: false);

        snapshot.HasContent.Should().BeTrue();
        snapshot.Callbacks.Should().OnlyContain(c => c.Status == VbaRibbonCallbackStatus.Unchecked);
        VbaRibbonFormatter.Format(snapshot, VbaWorkbookInfoLevel.Full, 30000).Should().Contain("照合できていません");
    }

    [Fact(DisplayName = "内容も出す: 冒頭に「最後に保存した内容」と取得日時・ファイルの更新日時、構成、コールバックの表、XML の全文が入る")]
    public async Task 内容も出す()
    {
        using var ws = new TempWorkspace();
        var snapshot = await CollectAsync(ws);

        var text = VbaRibbonFormatter.Format(snapshot, VbaWorkbookInfoLevel.Full, 30000);

        text.Should().StartWith("# リボン（customUI）\n");
        text.Should().Contain("最後に保存したときのファイルの内容").And.Contain("まだ保存していない変更は含まれません");
        text.Should().Contain("ファイルの更新: 2026-11-20 18:05").And.Contain("取得: ");
        text.Should().Contain("- ブック: 計画.xlsm").And.Contain("customUI/customUI14.xml（新しい形式）");
        text.Should().Contain("## 構成")
            .And.Contain("- tab tabMain label=\"業務\"")
            .And.Contain("  - group grpA label=\"操作\"")
            .And.Contain("    - button btnRun label=\"実行\" onAction=OnRun getEnabled=GetEnabled");
        text.Should().Contain("## コールバックと VBA の照合")
            .And.Contain("| OnRun | onAction（button btnRun） | あります: Module1 |")
            .And.Contain("Public ではないため、リボンから呼べません")
            .And.Contain("| OnPick | onAction（dropDown ddKind） | VBA に見つかりません |");
        text.Should().Contain("## XML").And.Contain("```xml\n<?xml version=\"1.0\"").And.Contain("onAction=\"OnRun\"");
        text.Should().NotContain("\r", "改行は LF 固定（差し込み側がそろえる）");
        text.Should().EndWith("\n").And.NotEndWith("\n\n");
    }

    [Fact(DisplayName = "構成だけ: 構成とコールバックの一覧は出るが、XML の全文は出ない")]
    public async Task 構成だけ()
    {
        using var ws = new TempWorkspace();
        var snapshot = await CollectAsync(ws);

        var text = VbaRibbonFormatter.Format(snapshot, VbaWorkbookInfoLevel.StructureOnly, 30000);

        text.Should().Contain("## 構成").And.Contain("## コールバックと VBA の照合").And.Contain("label=\"業務\"");
        text.Should().NotContain("## XML").And.NotContain("```").And.NotContain("imageMso", "XML の全文にしか無い属性は出ない");
    }

    [Fact(DisplayName = "出さない・リボンが無い・読めない: 空の文字列（見出しだけが残らない）")]
    public async Task 出ないとき()
    {
        using var ws = new TempWorkspace();
        var snapshot = await CollectAsync(ws);
        VbaRibbonFormatter.Format(snapshot, VbaWorkbookInfoLevel.Hidden, 30000).Should().BeEmpty();

        using var none = new TempWorkspace();
        var noRibbon = await CollectAsync(none, customUi14: null);
        noRibbon.Status.Should().Be(VbaRibbonReadStatus.NoRibbon);
        noRibbon.Describe().Should().Be("このブックにリボンの設定はありません");
        VbaRibbonFormatter.Format(noRibbon, VbaWorkbookInfoLevel.Full, 30000).Should().BeEmpty();

        using var broken = new TempWorkspace();
        var brokenXml = await CollectAsync(broken, customUi14: "<customUI><ribbon></customUI>");
        brokenXml.Status.Should().Be(VbaRibbonReadStatus.PartsUnreadable);
        brokenXml.HasContent.Should().BeFalse();
        brokenXml.Reason.Should().Contain("XML として読めません");
        VbaRibbonFormatter.Format(brokenXml, VbaWorkbookInfoLevel.Full, 30000).Should().BeEmpty();
    }

    [Fact(DisplayName = "XML の上限: 超えたら行の切れ目で切り、全体の文字数を添える。構成とコールバックの一覧は上限と別に全部出る")]
    public async Task XMLの上限()
    {
        using var ws = new TempWorkspace();
        var snapshot = await CollectAsync(ws);
        var full = VbaRibbonFormatter.Format(snapshot, VbaWorkbookInfoLevel.Full, 30000);

        var cut = VbaRibbonFormatter.Format(snapshot, VbaWorkbookInfoLevel.Full, 300);

        cut.Length.Should().BeLessThan(full.Length);
        cut.Should().Contain("長いため、先頭の").And.Contain("文字だけを載せています。全体は");
        cut.Should().Contain("- button btnRun").And.Contain("| OnPick |", "構成とコールバックは切られない");
        var xml = cut[(cut.IndexOf("```xml\n", StringComparison.Ordinal) + 7)..];
        xml = xml[..xml.IndexOf("\n```", StringComparison.Ordinal)];
        xml.Length.Should().BeLessThanOrEqualTo(300);
        RibbonPackageBuilder.SampleRibbon.Replace("\r\n", "\n").Should().StartWith(xml);
    }

    [Fact(DisplayName = "部品が 2 つあれば、構成も XML も部品ごとの見出しで並び、XML の上限は合計で数える")]
    public async Task 部品が2つ()
    {
        using var ws = new TempWorkspace();
        var old = "<customUI xmlns=\"http://schemas.microsoft.com/office/2006/01/customui\"><ribbon><tabs><tab id=\"oldTab\" label=\"旧\"/></tabs></ribbon></customUI>";
        var snapshot = await CollectAsync(ws, customUiOld: old);

        var text = VbaRibbonFormatter.Format(snapshot, VbaWorkbookInfoLevel.Full, 30000);

        text.Should().Contain("### customUI/customUI14.xml").And.Contain("### customUI/customUI.xml").And.Contain("（古い形式）");
        text.Should().Contain("tab oldTab label=\"旧\"");

        var tight = VbaRibbonFormatter.Format(snapshot, VbaWorkbookInfoLevel.Full, RibbonPackageBuilder.SampleRibbon.Length - 1);
        tight.Should().Contain("この部品の XML は省略しました", "1 つ目で上限を使い切ったので、2 つ目の XML は省く");
    }

    [Fact(DisplayName = "XML に ``` が含まれていても、囲みが壊れないよう、より長い囲みを使う")]
    public async Task 囲みの長さ()
    {
        using var ws = new TempWorkspace();
        var xml = "<customUI><!-- ```` --><ribbon><tabs><tab id=\"t\" label=\"T\"/></tabs></ribbon></customUI>";
        var snapshot = await CollectAsync(ws, customUi14: xml);

        var text = VbaRibbonFormatter.Format(snapshot, VbaWorkbookInfoLevel.Full, 30000);

        text.Should().Contain("`````xml\n").And.Contain("\n`````\n");
    }

    [Fact(DisplayName = "label の改行・長さ・引用符は 1 行に整える")]
    public async Task ラベルの整形()
    {
        using var ws = new TempWorkspace();
        var xml = "<customUI><ribbon><tabs><tab id=\"t\" label=\"一行目&#10;二行目 &quot;引用&quot; " + new string('あ', 200) + "\"/></tabs></ribbon></customUI>";
        var snapshot = await CollectAsync(ws, customUi14: xml);

        var line = VbaRibbonFormatter.Format(snapshot, VbaWorkbookInfoLevel.StructureOnly, 30000)
            .Split('\n').Single(l => l.TrimStart().StartsWith("- tab t", StringComparison.Ordinal));

        line.Should().Contain("一行目 二行目 '引用' ").And.EndWith("…\"");
    }

    [Fact(DisplayName = "XXE を仕込んだブック: リボンは出さず、ローカルのファイルの中身は取得結果のどこにも入らない")]
    public async Task XXEのブック()
    {
        using var ws = new TempWorkspace();
        var secret = ws.WriteText("secret.txt", "TOP-SECRET-VALUE");
        var xml = $"<?xml version=\"1.0\"?><!DOCTYPE customUI [<!ENTITY leak SYSTEM \"{new Uri(secret).AbsoluteUri}\">]>"
                  + "<customUI><ribbon><tabs><tab id=\"t\" label=\"&leak;\"/></tabs></ribbon></customUI>";

        var snapshot = await CollectAsync(ws, customUi14: xml);

        snapshot.HasContent.Should().BeFalse();
        snapshot.Reason.Should().Contain("DTD");
        VbaRibbonFormatter.Format(snapshot, VbaWorkbookInfoLevel.Full, 30000).Should().NotContain("TOP-SECRET-VALUE");
        snapshot.Describe().Should().NotContain("TOP-SECRET-VALUE");
    }

    [Fact(DisplayName = "一部の部品だけ読めない: 読めた部品は出し、読めなかった部品の理由も依頼文と取得結果に残る")]
    public async Task 一部だけ読めない()
    {
        using var ws = new TempWorkspace();
        var snapshot = await CollectAsync(ws, customUiOld: "<customUI><ribbon></customUI>");

        snapshot.Status.Should().Be(VbaRibbonReadStatus.Ok);
        snapshot.HasContent.Should().BeTrue();
        snapshot.Reason.Should().Contain("customUI/customUI.xml").And.Contain("XML として読めません");
        var text = VbaRibbonFormatter.Format(snapshot, VbaWorkbookInfoLevel.Full, 30000);
        text.Should().Contain("読めなかった部品: customUI/customUI.xml");
        text.Should().Contain("tab tabMain");
    }

    [Fact(DisplayName = "画面の 1 行（Describe）: 取得日時・ファイルの更新日時・「最後に保存した内容」。失敗は理由 1 行")]
    public async Task 画面の1行()
    {
        using var ws = new TempWorkspace();
        var ok = await CollectAsync(ws);
        ok.Describe().Should().Contain("取得: ").And.Contain("ファイルの更新: 2026-11-20 18:05").And.Contain("最後に保存した内容");

        var missing = await VbaRibbonCollector.CollectAsync(Path.Combine(ws.RootPath, "ない.xlsm"), ws.RootPath);
        missing.Status.Should().Be(VbaRibbonReadStatus.FileNotFound);
        missing.Describe().Should().Contain("見つかりません").And.NotContain("\n");
    }
}
