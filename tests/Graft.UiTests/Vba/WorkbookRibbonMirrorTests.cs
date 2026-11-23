using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Core.Vba;
using Graft.Features;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;

namespace Graft.UiTests.Vba;

/// <summary>
/// リボンのミラーと取込（機能 F の段階 F2。仕様書_ブックの情報と依頼の観点 16.3・16.4・16.10・16.14）のシナリオ。実際の起動と同じ
/// 依存グラフ（<see cref="ShellHarness"/>）に <see cref="FakeVbaBridge"/> をつなぎ、ブックの「ファイル」は一時ファイルの zip
/// （<see cref="VbaP3Rig.RibbonFile"/>）で代える。ブックへは書かない（書き換えは F3）。
/// </summary>
public class WorkbookRibbonMirrorTests : IDisposable
{
    private const string Ribbon14 = "CustomUI/customUI14.xml";

    private static readonly string XmlB = RibbonPackageBuilder.SampleRibbon.Replace("label=\"業務\"", "label=\"業務B\"", StringComparison.Ordinal);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "graft-ribbon-mirror-ui", Guid.NewGuid().ToString("N"));

    public WorkbookRibbonMirrorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 後片付けの失敗は検証結果に影響しない。
        }

        GC.SuppressFinalize(this);
    }

    private string Book(string? customUi14 = RibbonPackageBuilder.SampleRibbon)
    {
        var path = Path.Combine(_root, "計画.xlsm");
        File.WriteAllBytes(path, RibbonPackageBuilder.Workbook(customUi14));
        return path;
    }

    private async Task<VbaP3Rig> NewRigAsync(string? ribbonFile, Settings? settings = null)
    {
        var rig = await VbaP3Rig.CreateAsync(
            settings ?? new Settings { ShowPreview = false },
            arrange: bridge => bridge.Get(VbaP3Rig.Book)!.Find("Module1")!.Code =
                "Public Sub OnRun(control As IRibbonControl)\r\nEnd Sub").ConfigureAwait(true);
        rig.RibbonFile = ribbonFile;
        return rig;
    }

    private static async Task<string> CollectedTextAsync(VbaP3Rig r)
    {
        await ShellHarness.ExecuteAsync(r.Graft.ContextCollect!.CopyCommand).ConfigureAwait(true);
        return r.H.Clipboard.Text ?? string.Empty;
    }

    private static int Count(string text, string needle)
    {
        var count = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    /// <summary>出力の冒頭の生成日時（実行のたびに変わる）を除く。</summary>
    private static string Normalize(string text)
        => string.Join("\n", text.Split('\n').Where(l => !l.Contains("生成日時", StringComparison.Ordinal)));

    [AvaloniaFact(DisplayName = "コンテキスト収集を開くと、最初の取込で CustomUI/ ができ、リボンの行はミラーのファイルを読む（出どころの表示つき）")]
    public async Task 収集の前の取込でCustomUIができる()
    {
        using var r = await NewRigAsync(Book()).ConfigureAwait(true);
        File.Exists(r.MirrorPath(Ribbon14)).Should().BeFalse("プロジェクトを作った時点では、まだ取り込んでいない");
        var context = r.Graft.ContextCollect!;

        await context.InitializeAsync().ConfigureAwait(true);

        r.Mirror(Ribbon14).Should().Be(RibbonPackageBuilder.SampleRibbon);
        context.Ribbon.IsFromMirror.Should().BeTrue();
        context.Ribbon.NoteText.Should().Be(VbaRibbonRowViewModel.MirrorContentNote);
        context.Ribbon.StatusText.Should().Contain("Graft のミラーのファイル CustomUI/customUI14.xml").And.Contain("ブックへは反映済みです");
        context.RibbonWarning.Should().BeNull();

        var text = await CollectedTextAsync(r).ConfigureAwait(true);
        text.Should().Contain("Graft のミラーのファイル（CustomUI/customUI14.xml）の内容**です。ブックへは反映済みです。");
        text.Should().NotContain("最後に保存したときのファイルの内容");
        Count(text, "onAction=\"OnRun\"").Should().Be(1, "XML の全文は依頼文に 1 回だけ出す（16.14）");
        text.Should().Contain("| OnRun | onAction（button btnRun） | あります: Module1 |");
    }

    [AvaloniaFact(DisplayName = "XML は片方にだけ: CustomUI/customUI14.xml を対象ファイルから外せばリボンの文に全文（見出しはミラーのパス）、入れれば場所の 1 行だけ")]
    public async Task XMLは片方にだけ出す()
    {
        using var r = await NewRigAsync(Book()).ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        var node = context.Files.First(f => f.RelativePath == Ribbon14);

        node.State = ContextFileState.Full;
        var selected = await CollectedTextAsync(r).ConfigureAwait(true);
        selected.Should().Contain("XML の全文は、対象ファイルの `CustomUI/customUI14.xml` にあります。");
        Count(selected, "onAction=\"OnRun\"").Should().Be(1);

        node.State = ContextFileState.StructureOnly;
        var notSelected = await CollectedTextAsync(r).ConfigureAwait(true);
        notSelected.Should().Contain("### CustomUI/customUI14.xml\n\n```xml");
        notSelected.Should().NotContain("にあります。");
        Count(notSelected, "onAction=\"OnRun\"").Should().Be(1);
    }

    [AvaloniaFact(DisplayName = "ミラーを直すと「まだ反映していません」と「リボン未反映」のバッジ。ブックのファイルは変えない")]
    public async Task 未反映の表示()
    {
        var book = Book();
        using var r = await NewRigAsync(book).ConfigureAwait(true);
        await ShellHarness.ExecuteAsync(r.Graft.ProjectPane.PullFromExcelCommand).ConfigureAwait(true);
        var bytes = File.ReadAllBytes(book);
        File.WriteAllText(r.MirrorPath(Ribbon14), XmlB);

        var pane = r.Graft.ProjectPane;
        await pane.RefreshVbaBadgesAsync(checkWorkbookOpen: false).ConfigureAwait(true);
        var item = pane.Items.Single(i => i.Project.Vba is not null);
        item.VbaStatusText.Should().Be(VbaBadgeLogic.RibbonUnpushedText);
        item.VbaTooltip.Should().Contain("ブックを閉じて開き直して反映します");

        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        context.Ribbon.StatusText.Should().Contain("まだ反映していません");
        (await CollectedTextAsync(r).ConfigureAwait(true)).Should().Contain("業務B").And.Contain("ブックへは**まだ反映していません**。");
        File.ReadAllBytes(book).Should().Equal(bytes, "F2 ではブックのファイルへ書かない");
    }

    [AvaloniaFact(DisplayName = "手動の「Excel から取込」: Excel が閉じていても、リボンはブックのファイルから取り込む（モジュールの失敗は従来どおり伝える）")]
    public async Task 手動の取込はExcelが閉じていてもリボンを取り込む()
    {
        using var r = await NewRigAsync(Book()).ConfigureAwait(true);
        r.Bridge.CloseWorkbook(VbaP3Rig.Book);

        await ShellHarness.ExecuteAsync(r.Graft.ProjectPane.PullFromExcelCommand).ConfigureAwait(true);

        r.Mirror(Ribbon14).Should().Be(RibbonPackageBuilder.SampleRibbon);
        r.H.Dialogs.Messages.Should().Contain(m => m.Title.Contains("取り込めませんでした"), "モジュールは取り込めていない");
    }

    [AvaloniaFact(DisplayName = "手動の「Excel から取込」: リボンの衝突は衝突ダイアログに載せる（F5）。選ばずに閉じる（中止）なら、ミラーは変わらず、衝突を伝える")]
    public async Task 手動の取込でリボンの衝突を知らせる()
    {
        using var r = await NewRigAsync(Book()).ConfigureAwait(true);
        await ShellHarness.ExecuteAsync(r.Graft.ProjectPane.PullFromExcelCommand).ConfigureAwait(true);
        File.WriteAllText(r.MirrorPath(Ribbon14), XmlB);
        var bookXml = RibbonPackageBuilder.SampleRibbon.Replace("label=\"業務\"", "label=\"業務C\"", StringComparison.Ordinal);
        Book(bookXml);
        r.H.Dialogs.Messages.Clear();

        await ShellHarness.ExecuteAsync(r.Graft.ProjectPane.PullFromExcelCommand).ConfigureAwait(true);

        r.Mirror(Ribbon14).Should().Be(XmlB, "衝突は黙って上書きしない");
        var item = r.Conflicts.Should().ContainSingle("リボンの衝突も衝突ダイアログに載せる").Subject.Items.Should().ContainSingle().Subject;
        item.IsRibbon.Should().BeTrue();
        item.RelativePath.Should().Be(Ribbon14);
        item.ExcelText.Should().Be(bookXml, "左はブックのファイルの部品");
        item.MirrorText.Should().Be(XmlB, "右は Graft 側（ミラー）");
        r.H.Dialogs.Messages.Should().Contain(m => m.Message.Contains("E812") && m.Message.Contains(Ribbon14), "選ばずに閉じたので、解決されていないことを伝える");
    }

    [AvaloniaFact(DisplayName = "解析の前の取込: パッチが CustomUI/ だけを含むとき、Excel が閉じていても、解析の前にブックのリボンを取り込む")]
    public async Task 解析の前の取込()
    {
        using var r = await NewRigAsync(Book()).ConfigureAwait(true);
        await ShellHarness.ExecuteAsync(r.Graft.ProjectPane.PullFromExcelCommand).ConfigureAwait(true);
        Book(XmlB); // ブック側だけが変わった（Excel でリボンを別のツールで直して保存した、など）。
        r.Bridge.CloseWorkbook(VbaP3Rig.Book);
        var reads = r.Bridge.ReadNameRequests.Count;

        // search/replace は行単位で照合するので、タブの行を丸ごと書く。
        await r.PasteAsync(VbaP3Rig.Edit(
            Ribbon14, "      <tab id=\"tabMain\" label=\"業務B\">", "      <tab id=\"tabMain\" label=\"業務D\">")).ConfigureAwait(true);

        r.Mirror(Ribbon14).Should().Be(XmlB, "解析の前に取り込んだので、パッチはブックの最新に当たる");
        var plan = r.Block(Ribbon14).Plan;
        plan.CanApply.Should().BeTrue("取り込んだ内容に対して search が見つかる: " + string.Join(" / ", plan.Issues.Select(i => i.ToDisplayText())));
        r.Bridge.ReadNameRequests.Count.Should().Be(reads, "範囲がリボンだけなら Excel（COM）を読まない");
        r.Choices.Should().BeEmpty("Excel が閉じていても「ファイルだけ適用」の確認を出さない");
    }

    [AvaloniaFact(DisplayName = "保存時の自動反映はリボンを拾わない: CustomUI/customUI14.xml を保存しても Excel を呼ばない（OnEditorTabSavedForVba）")]
    public async Task 保存時の自動反映はリボンを拾わない()
    {
        using var r = await NewRigAsync(Book()).ConfigureAwait(true);
        await r.UpdateLinkAsync(l => l with { AutoPushOnSave = true }).ConfigureAwait(true);
        await ShellHarness.ExecuteAsync(r.Graft.ProjectPane.PullFromExcelCommand).ConfigureAwait(true);
        var tab = (await r.H.Shell.Editor.OpenFileAsync(r.MirrorPath(Ribbon14)).ConfigureAwait(true)).Value;
        var calls = r.Bridge.Calls.Count;
        var reads = r.RibbonPackageReads;

        tab.Session.Document.Insert(0, "<!-- 編集 -->\n");
        (await r.H.Shell.Editor.SaveActiveAsync().ConfigureAwait(true)).IsSuccess.Should().BeTrue();
        await r.Graft.WhenVbaSaveSyncIdleAsync().ConfigureAwait(true);
        await Task.Delay(700).ConfigureAwait(true); // まとめの時間（500ms）を超えて待っても、何も起きない。

        r.Bridge.Calls.Count.Should().Be(calls, "Excel への呼び出しが 1 回も起きない");
        r.RibbonPackageReads.Should().Be(reads, "ブックのファイルも読まない");
        r.Mirror(Ribbon14).Should().StartWith("<!-- 編集 -->");
        (await r.StatusAsync().ConfigureAwait(true)).RibbonUnpushed.Should().BeTrue("バッジが「リボン未反映」になるだけ");
    }

    [AvaloniaFact(DisplayName = "customUI の無いプロジェクト: CustomUI/ を作らず、収集の出力も依頼文も、リボンの書き換えをオフにしたときと 1 バイトも変わらない")]
    public async Task customUIが無ければ出力は変わらない()
    {
        using var r = await NewRigAsync(Book(customUi14: null)).ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        var copy = r.Graft.PromptCopy!;

        await context.InitializeAsync().ConfigureAwait(true);
        var collectedOn = await CollectedTextAsync(r).ConfigureAwait(true);
        await copy.QuickCopyAsync().ConfigureAwait(true);
        var promptOn = r.H.Clipboard.Text!;

        Directory.Exists(r.MirrorPath("CustomUI")).Should().BeFalse();
        context.BuildRibbonText().Should().BeEmpty();
        context.BuildRibbonTextFor().Should().BeNull();
        File.ReadAllText(r.H.AppPaths.GetVbaSyncFilePath(r.Project.Id)).Should().NotContain("ribbon");

        r.Graft.UpdateSettings(new Settings { ShowPreview = false, Vba = new VbaSettings { RibbonEditEnabled = false } });
        await context.InitializeAsync().ConfigureAwait(true);
        var collectedOff = await CollectedTextAsync(r).ConfigureAwait(true);
        await copy.QuickCopyAsync().ConfigureAwait(true);
        var promptOff = r.H.Clipboard.Text!;

        Normalize(collectedOn).Should().Be(Normalize(collectedOff));
        promptOn.Should().Be(promptOff);
    }
}
