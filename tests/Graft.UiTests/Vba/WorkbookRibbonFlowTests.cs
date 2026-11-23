using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Features;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;

namespace Graft.UiTests.Vba;

/// <summary>
/// 段階 F5: リボンの書き換えを、適用・取り消し・復元・手動の反映の流れへ組み込んだシナリオ（仕様書_ブックの情報と依頼の観点 16.10・16.11・16.4）。
/// 実際の起動と同じ依存グラフに <see cref="FakeVbaBridge"/> をつなぎ、ブックの「ファイル」は一時ファイルの zip で代える。
/// </summary>
public sealed class WorkbookRibbonFlowTests : IDisposable
{
    private const string Ribbon14 = "CustomUI/customUI14.xml";
    private const string Module1 = "Modules/Module1.bas";

    private static readonly string XmlB = RibbonPackageBuilder.SampleRibbon.Replace("label=\"業務\"", "label=\"業務B\"", StringComparison.Ordinal);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "graft-ribbon-flow-ui", Guid.NewGuid().ToString("N"));

    public WorkbookRibbonFlowTests() => Directory.CreateDirectory(_root);

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

    private string BookFile => Path.Combine(_root, "計画.xlsm");

    private static string RibbonPatch() => VbaP3Rig.Edit(Ribbon14, "      <tab id=\"tabMain\" label=\"業務\">", "      <tab id=\"tabMain\" label=\"業務B\">");

    private static string VbaPatch() => VbaP3Rig.Edit(Module1, "Public Sub OnRun(control As IRibbonControl)", "Public Sub OnRun(control As IRibbonControl)\n  MsgBox \"run\"");

    /// <summary>ブック（リボンあり）を取り込み、Excel の代役で<b>開いたまま</b>にする。確認ダイアログには <paramref name="confirm"/> で答え、出たものを <paramref name="shown"/> に残す。</summary>
    private async Task<VbaP3Rig> ArrangeAsync(List<VbaRibbonRewriteViewModel> shown, Func<bool>? confirm = null, bool open = true, Settings? settings = null)
    {
        File.WriteAllBytes(BookFile, RibbonPackageBuilder.ExcelWorkbook(RibbonPackageBuilder.SampleRibbon));
        var r = await VbaP3Rig.CreateAsync(
            settings ?? new Settings { ShowPreview = false },
            arrange: bridge => bridge.Get(VbaP3Rig.Book)!.Find("Module1")!.Code = "Public Sub OnRun(control As IRibbonControl)\r\nEnd Sub").ConfigureAwait(true);
        r.RibbonFile = BookFile;
        r.Graft.VbaRibbonWorkbookFile = _ => BookFile;
        r.Graft.ProjectPane.ShowRibbonRewriteAsync = vm =>
        {
            shown.Add(vm);
            return Task.FromResult(confirm?.Invoke() ?? true);
        };
        await ShellHarness.ExecuteAsync(r.Graft.ProjectPane.PullFromExcelCommand).ConfigureAwait(true);
        if (!open) r.Bridge.CloseWorkbook(VbaP3Rig.Book);
        return r;
    }

    private string? BookRibbon() => VbaRibbonPackageReader.Read(BookFile).Parts.FirstOrDefault(p => p.PartName == "customUI/customUI14.xml")?.Xml;

    private static string[] CloseOpenCalls(VbaP3Rig r)
        => r.Bridge.Calls.Where(c => c.StartsWith("SaveClose:", StringComparison.Ordinal) || c.StartsWith("Reopen:", StringComparison.Ordinal))
            .Select(c => c[..c.IndexOf(':', StringComparison.Ordinal)]).ToArray();

    [AvaloniaFact(DisplayName = "適用のあと: VBA の反映 → リボンの確認（冒頭に「VBA のモジュール 1 件は反映しました」）→ 保存して閉じる → 書き換え → 開き直し → 適用の通知に「リボンも反映済み」")]
    public async Task 適用のあとにリボンも反映する()
    {
        var shown = new List<VbaRibbonRewriteViewModel>();
        using var r = await ArrangeAsync(shown).ConfigureAwait(true);
        r.Bridge.Get(VbaP3Rig.Book)!.Saved = false;
        await r.PasteAsync(VbaPatch(), RibbonPatch()).ConfigureAwait(true);
        r.Bridge.Calls.Clear();

        await r.ApplyAsync().ConfigureAwait(true);

        var vm = shown.Should().ContainSingle("確認は毎回出す").Subject;
        vm.LeadText.Should().StartWith("VBA のモジュール 1 件は Excel へ反映しました。続けてリボンを反映します。");
        vm.UnsavedWarningText.Should().Contain("先ほど Graft が Excel へ反映した VBA の変更も");
        r.Bridge.Calls.Should().ContainInOrder(
            r.Bridge.Calls.First(c => c.StartsWith("Write:", StringComparison.Ordinal)), $"SaveClose:{VbaP3Rig.Book}", $"Reopen:{VbaP3Rig.Book}");
        r.Excel("Module1").Code.Should().Contain("MsgBox \"run\"");
        BookRibbon().Should().Be(XmlB);
        r.Graft.ApplyUndoNoticeText.Should().Contain("Excel へ反映済み").And.Contain("リボンも反映済み");
        (await r.StatusAsync().ConfigureAwait(true)).RibbonUnpushed.Should().BeFalse();
        r.Bridge.HeldTickets.Should().BeEmpty();
    }

    [AvaloniaFact(DisplayName = "AI の回答 → 適用 → VBA の反映 → リボンの反映 → 取り消し → 再反映（一連の操作）。ブックのリボンが元に戻る")]
    public async Task 一連の操作()
    {
        var shown = new List<VbaRibbonRewriteViewModel>();
        using var r = await ArrangeAsync(shown).ConfigureAwait(true);
        await r.PasteAsync(VbaPatch(), RibbonPatch()).ConfigureAwait(true);
        await r.ApplyAsync().ConfigureAwait(true);
        BookRibbon().Should().Be(XmlB);
        shown.Should().HaveCount(1);

        await ShellHarness.ExecuteAsync(r.Graft.UndoCommand).ConfigureAwait(true);

        r.Mirror(Ribbon14).Should().Be(RibbonPackageBuilder.SampleRibbon, "取り消しでミラーが戻る");
        shown.Should().HaveCount(2, "取り消しのあとも、リボンの確認を出す（16.10）");
        shown[1].LeadText.Should().StartWith("VBA のモジュール 1 件は Excel へ反映しました");
        BookRibbon().Should().Be(RibbonPackageBuilder.SampleRibbon, "再反映で、ブックのリボンも元に戻る");
        r.Excel("Module1").Code.Should().NotContain("MsgBox \"run\"");
        (await r.StatusAsync().ConfigureAwait(true)).RibbonUnpushed.Should().BeFalse();
        Directory.GetFiles(r.H.AppPaths.GetVbaWorkbookBackupDirectory(r.Project.Id)).Should().HaveCount(2, "書き換えのたびに、ブック全体の控えが残る");
    }

    [AvaloniaFact(DisplayName = "確認で「中止」: 適用は成功し、リボンは未反映のまま。適用後フックは実行され、取り消しの提案は出ない")]
    public async Task 中止しても適用は成功しフックは実行される()
    {
        var shown = new List<VbaRibbonRewriteViewModel>();
        using var r = await ArrangeAsync(shown, confirm: () => false).ConfigureAwait(true);
        var marker = Path.Combine(r.H.Root, "hook-ran.txt");
        await r.SetHooksAsync(new PostApplyHook { Name = "目印", Command = $"touch \"{marker}\"", OnFailure = HookFailureAction.Warn }).ConfigureAwait(true);
        await r.PasteAsync(RibbonPatch()).ConfigureAwait(true);
        var before = File.ReadAllBytes(BookFile);

        await r.ApplyAsync().ConfigureAwait(true);

        shown.Should().ContainSingle();
        File.ReadAllBytes(BookFile).Should().Equal(before, "中止ならブックは変えない");
        File.Exists(marker).Should().BeTrue("リボンを中止しても、フックは実行する");
        (await r.RevisionsAsync().ConfigureAwait(true)).Should().ContainSingle().Which.Manifest.Status.Should().Be(RevisionStatus.Success);
        r.Choices.Should().BeEmpty("リボンの中止では、適用の取り消しを提案しない");
        r.Graft.ApplyUndoNoticeText.Should().Contain("リボンは未反映");
        (await r.StatusAsync().ConfigureAwait(true)).RibbonUnpushed.Should().BeTrue();
    }

    [AvaloniaFact(DisplayName = "VBA の反映が失敗したら、リボンの確認を出さず「リボンは未反映のまま」と知らせる")]
    public async Task VBAが失敗したらリボンを始めない()
    {
        var shown = new List<VbaRibbonRewriteViewModel>();
        using var r = await ArrangeAsync(shown).ConfigureAwait(true);
        r.Bridge.CorruptOnWrite.Add("Module1");
        r.ChoiceAnswer = _ => "keep";
        await r.PasteAsync(VbaPatch(), RibbonPatch()).ConfigureAwait(true);
        r.Bridge.Calls.Clear();

        await r.ApplyAsync().ConfigureAwait(true);

        shown.Should().BeEmpty();
        CloseOpenCalls(r).Should().BeEmpty("保存も閉じるも呼ばない");
        r.Graft.Notice.Text.Should().Contain("リボンは未反映のままです").And.Contain("Excel へ反映");
        (await r.StatusAsync().ConfigureAwait(true)).RibbonUnpushed.Should().BeTrue();
    }

    [AvaloniaFact(DisplayName = "AutoPushAfterApply がオフ: 適用してもリボンの確認は出ない（バッジの「リボン未反映」だけ）")]
    public async Task 自動反映がオフなら出さない()
    {
        var shown = new List<VbaRibbonRewriteViewModel>();
        using var r = await ArrangeAsync(shown).ConfigureAwait(true);
        await r.UpdateLinkAsync(l => l with { AutoPushAfterApply = false }).ConfigureAwait(true);
        await r.PasteAsync(RibbonPatch()).ConfigureAwait(true);
        r.Bridge.Calls.Clear();

        await r.ApplyAsync().ConfigureAwait(true);

        shown.Should().BeEmpty();
        CloseOpenCalls(r).Should().BeEmpty();
        (await r.StatusAsync().ConfigureAwait(true)).RibbonUnpushed.Should().BeTrue();
    }

    [AvaloniaFact(DisplayName = "手動の「Excel へ反映」: VBA を反映したあと、リボンが未反映なら確認を出す。VBA が失敗すれば出さない")]
    public async Task 手動の反映のあとにリボン()
    {
        var shown = new List<VbaRibbonRewriteViewModel>();
        using var r = await ArrangeAsync(shown).ConfigureAwait(true);
        await r.UpdateLinkAsync(l => l with { AutoPushAfterApply = false }).ConfigureAwait(true);
        await r.PasteAsync(VbaPatch(), RibbonPatch()).ConfigureAwait(true);
        await r.ApplyAsync().ConfigureAwait(true);
        shown.Should().BeEmpty();

        await ShellHarness.ExecuteAsync(r.Graft.ProjectPane.PushToExcelCommand).ConfigureAwait(true);

        shown.Should().ContainSingle().Which.LeadText.Should().StartWith("VBA のモジュール 1 件は Excel へ反映しました");
        BookRibbon().Should().Be(XmlB);

        // VBA が失敗するとき（Excel が閉じている）は、リボンの準備を始めない。
        await r.PasteAsync(VbaP3Rig.Edit(Ribbon14, "      <tab id=\"tabMain\" label=\"業務B\">", "      <tab id=\"tabMain\" label=\"業務C\">"),
            VbaP3Rig.Edit(Module1, "  MsgBox \"run\"", "  MsgBox \"run2\"")).ConfigureAwait(true);
        await r.ApplyAsync().ConfigureAwait(true);
        r.Bridge.CloseWorkbook(VbaP3Rig.Book);
        shown.Clear();
        await ShellHarness.ExecuteAsync(r.Graft.ProjectPane.PushToExcelCommand).ConfigureAwait(true);
        shown.Should().BeEmpty("VBA を反映できなかったので、リボンは始めない");
        r.Graft.Notice.Text.Should().Contain("リボンは未反映のままです");
    }

    [AvaloniaFact(DisplayName = "保存時の自動反映（AutoPushOnSave）はリボンを対象にしない: CustomUI/*.xml を保存しても、確認も書き換えも起きず「リボン未反映」になるだけ")]
    public async Task 保存時の自動反映はリボンを送らない()
    {
        var shown = new List<VbaRibbonRewriteViewModel>();
        using var r = await ArrangeAsync(shown).ConfigureAwait(true);
        await r.UpdateLinkAsync(l => l with { AutoPushOnSave = true }).ConfigureAwait(true);
        var tab = (await r.H.Shell.Editor.OpenFileAsync(r.MirrorPath(Ribbon14)).ConfigureAwait(true)).Value;
        r.Bridge.Calls.Clear();

        tab.Session.Document.Insert(0, "<!-- 編集 -->\n");
        (await r.H.Shell.Editor.SaveActiveAsync().ConfigureAwait(true)).IsSuccess.Should().BeTrue();
        await r.Graft.WhenVbaSaveSyncIdleAsync().ConfigureAwait(true);

        shown.Should().BeEmpty();
        CloseOpenCalls(r).Should().BeEmpty();
        r.Bridge.Calls.Should().NotContain(c => c.StartsWith("Write:", StringComparison.Ordinal));
        (await r.StatusAsync().ConfigureAwait(true)).RibbonUnpushed.Should().BeTrue();
    }

    [AvaloniaFact(DisplayName = "適用前の検査: リボンの XML が XSD に合わないブロックは E818 で止める。警告だけなら適用でき、ブロックに警告が出る")]
    public async Task 適用前のXSD検査()
    {
        var shown = new List<VbaRibbonRewriteViewModel>();
        using var r = await ArrangeAsync(shown).ConfigureAwait(true);
        await r.PasteAsync(VbaP3Rig.Edit(Ribbon14, "          <button id=\"btnRun\" label=\"実行\" size=\"large\" imageMso=\"HappyFace\" onAction=\"OnRun\" getEnabled=\"GetEnabled\"/>",
            "          <button id=\"btnRun\" label=\"実行\" size=\"big\" imageMso=\"HappyFace\" onAction=\"OnRun\" getEnabled=\"GetEnabled\"/>")).ConfigureAwait(true);

        r.H.Dialogs.Messages.Should().Contain(m => m.Message.Contains("E818", StringComparison.Ordinal), "解析の直後に案内する");
        r.H.Dialogs.Messages.Clear();
        await r.ApplyAsync().ConfigureAwait(true);

        r.H.Dialogs.Messages.Should().Contain(m => m.Message.Contains("E818", StringComparison.Ordinal));
        r.Mirror(Ribbon14).Should().Be(RibbonPackageBuilder.SampleRibbon, "止めたので、ミラーは変わらない");
        shown.Should().BeEmpty();
    }

    [AvaloniaFact(DisplayName = "リボンの衝突（ブックとミラーの両方が変更）: 衝突ダイアログに載り、「ブック側を採用」でミラーがブックの内容になる")]
    public async Task 衝突でブック側を採用する()
    {
        var shown = new List<VbaRibbonRewriteViewModel>();
        using var r = await ArrangeAsync(shown).ConfigureAwait(true);
        File.WriteAllText(r.MirrorPath(Ribbon14), XmlB);
        var bookXml = RibbonPackageBuilder.SampleRibbon.Replace("label=\"業務\"", "label=\"業務C\"", StringComparison.Ordinal);
        File.WriteAllBytes(BookFile, RibbonPackageBuilder.ExcelWorkbook(bookXml));
        r.ConflictAnswer = request => VbaConflictDecision.All(request, VbaConflictChoice.AdoptExcel);

        await ShellHarness.ExecuteAsync(r.Graft.ProjectPane.PullFromExcelCommand).ConfigureAwait(true);

        r.Conflicts.Should().ContainSingle().Which.Items.Should().ContainSingle(i => i.IsRibbon);
        r.Mirror(Ribbon14).Should().Be(bookXml);
        shown.Should().BeEmpty();
    }

    [AvaloniaFact(DisplayName = "リボンの衝突で「Graft 側で上書き」: 確認ダイアログを省かず、ブックのリボンをミラーの内容に書き換える")]
    public async Task 衝突でGraft側を上書きする()
    {
        var shown = new List<VbaRibbonRewriteViewModel>();
        using var r = await ArrangeAsync(shown).ConfigureAwait(true);
        File.WriteAllText(r.MirrorPath(Ribbon14), XmlB);
        File.WriteAllBytes(BookFile, RibbonPackageBuilder.ExcelWorkbook(RibbonPackageBuilder.SampleRibbon.Replace("label=\"業務\"", "label=\"業務C\"", StringComparison.Ordinal)));
        r.ConflictAnswer = request => VbaConflictDecision.All(request, VbaConflictChoice.OverwriteExcel);

        await ShellHarness.ExecuteAsync(r.Graft.ProjectPane.PullFromExcelCommand).ConfigureAwait(true);

        shown.Should().ContainSingle("上書きでも、確認ダイアログは省かない");
        BookRibbon().Should().Be(XmlB);
        r.Mirror(Ribbon14).Should().Be(XmlB);
        Directory.GetFiles(r.H.AppPaths.GetVbaWorkbookBackupDirectory(r.Project.Id)).Should().ContainSingle("ブック全体の控えが残る");
    }

    [AvaloniaFact(DisplayName = "控えの窓の「ブックの控え」: 一覧に出て、「この控えでブックを置き換える…」は強い確認（既定は「やめる」）のあと、ブックを元の内容へ戻す")]
    public async Task ブックの控えから置き換える()
    {
        var shown = new List<VbaRibbonRewriteViewModel>();
        using var r = await ArrangeAsync(shown, open: false).ConfigureAwait(true);
        var original = File.ReadAllBytes(BookFile);
        File.WriteAllText(r.MirrorPath(Ribbon14), XmlB);
        await r.Graft.ProjectPane.RefreshVbaBadgesAsync(checkWorkbookOpen: false).ConfigureAwait(true);
        await ShellHarness.ExecuteAsync(r.Graft.ProjectPane.PushRibbonCommand).ConfigureAwait(true);
        BookRibbon().Should().Be(XmlB);

        VbaRescueViewModel? window = null;
        r.Graft.ProjectPane.ShowRescueAsync = async vm =>
        {
            window = vm;
            await vm.InitializeAsync().ConfigureAwait(true);
            vm.CanShowWorkbookBackups.Should().BeTrue();
            vm.ShowWorkbooks = true;
            vm.WorkbookItems.Should().ContainSingle();
            vm.SelectedWorkbook.Should().NotBeNull();
            VbaChoiceRequest? asked = null;
            vm.ChooseAsync = request =>
            {
                asked = request;
                return Task.FromResult("cancel");
            };

            await vm.ReplaceWorkbookAsync().ConfigureAwait(true);
            asked!.Default.Id.Should().Be("cancel", "既定は「やめる」");
            asked.Message.Should().Contain("控えを取ったあとに行った変更は、すべて元に戻ります");
            BookRibbon().Should().Be(XmlB, "やめたら変えない");

            vm.ChooseAsync = request => Task.FromResult("replace");
            await vm.ReplaceWorkbookAsync().ConfigureAwait(true);
        };

        await ShellHarness.ExecuteAsync(r.Graft.ProjectPane.ShowRescueCommand).ConfigureAwait(true);

        window.Should().NotBeNull();
        File.ReadAllBytes(BookFile).Should().Equal(original, "控えの内容で置き換わる");
        Directory.GetFiles(r.H.AppPaths.GetVbaWorkbookBackupDirectory(r.Project.Id)).Should().HaveCount(2, "置き換える前のブックも控えとして残る");
        window!.StatusText.Should().Contain("控え（").And.Contain("置き換えました");
    }
}
