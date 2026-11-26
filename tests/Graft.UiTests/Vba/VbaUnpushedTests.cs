using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests.Vba;

/// <summary>
/// Excel（ブック）へ未反映のリボン・モジュールの見落とし対策（エクスプローラーの印・閉じるときの確認・ステータスバーの常時表示・
/// ブックが閉じたときの案内・中止しても消えない通知）のシナリオ。実際の起動と同じ依存グラフ（<see cref="ShellHarness"/>）に
/// <see cref="FakeVbaBridge"/> をつなぎ、ブックの「ファイル」は一時ファイルの zip（<see cref="VbaP3Rig.RibbonFile"/>）で代える
/// （<c>WorkbookRibbonRewriteTests</c> と同じ作り）。
/// </summary>
public class VbaUnpushedTests : IDisposable
{
    private const string Ribbon14 = "CustomUI/customUI14.xml";
    private const string Module1 = "Modules/Module1.bas";
    private const string BookName = "計画.xlsm";

    private static readonly string XmlB = RibbonPackageBuilder.SampleRibbon.Replace("label=\"業務\"", "label=\"業務B\"", StringComparison.Ordinal);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "graft-unpushed-ui", Guid.NewGuid().ToString("N"));

    public VbaUnpushedTests() => Directory.CreateDirectory(_root);

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

    private string BookFile => Path.Combine(_root, BookName);

    private string? BookRibbon()
        => VbaRibbonPackageReader.Read(BookFile).Parts.FirstOrDefault(p => p.PartName == "customUI/customUI14.xml")?.Xml;

    /// <summary>
    /// ブック（リボンあり）を作ってプロジェクトに取り込み、取り込んだあとの状態（未反映なし）から、
    /// <paramref name="ribbon"/> ならミラーのリボンを、<paramref name="module"/> なら <c>Module1.bas</c> を直す。
    /// リボンの確認ダイアログは <paramref name="confirm"/> で答え、出た ViewModel の数を <paramref name="shown"/> に残す。
    /// </summary>
    private async Task<VbaP3Rig> ArrangeAsync(
        List<VbaRibbonRewriteViewModel> shown, bool ribbon = true, bool module = false, bool confirm = true, bool withWindow = false,
        Settings? settings = null)
    {
        File.WriteAllBytes(BookFile, RibbonPackageBuilder.ExcelWorkbook(RibbonPackageBuilder.SampleRibbon));
        var r = await VbaP3Rig.CreateAsync(
            settings,
            withWindow: withWindow,
            arrange: bridge => bridge.Get(VbaP3Rig.Book)!.Find("Module1")!.Code =
                "Public Sub OnRun(control As IRibbonControl)\r\nEnd Sub").ConfigureAwait(true);
        r.RibbonFile = BookFile;
        r.Graft.VbaRibbonWorkbookFile = _ => BookFile;
        r.Graft.ProjectPane.ShowRibbonRewriteAsync = vm =>
        {
            shown.Add(vm);
            return Task.FromResult(confirm);
        };

        await ShellHarness.ExecuteAsync(r.Graft.ProjectPane.PullFromExcelCommand).ConfigureAwait(true);
        r.Mirror(Ribbon14).Should().Be(RibbonPackageBuilder.SampleRibbon);
        if (ribbon) File.WriteAllText(r.MirrorPath(Ribbon14), XmlB);
        if (module) File.AppendAllText(r.MirrorPath(Module1), "\r\n' 手で足した行");
        await r.Graft.ProjectPane.RefreshVbaBadgesAsync(checkWorkbookOpen: true).ConfigureAwait(true);
        return r;
    }

    private static async Task WaitForExplorerAsync(VbaP3Rig r)
    {
        await ShellHarness.WaitUntilAsync(() => r.H.Shell.Explorer.RootNodes.Count > 0 && r.H.Shell.Explorer.Project?.Id == r.Project.Id)
            .ConfigureAwait(true);
        // 取り込みでミラーのファイルができたあとの一覧を、確実に読み直す（監視の通知を待たない）。
        await ShellHarness.ExecuteAsync(r.H.Shell.Explorer.RefreshCommand).ConfigureAwait(true);
    }

    /// <summary>フォルダを展開して、中の実列挙が終わるまで待つ。</summary>
    private static async Task<FileNodeViewModel> ExpandAsync(FileNodeViewModel folder)
    {
        folder.IsExpanded = true;
        await ShellHarness.WaitUntilAsync(() => folder.IsLoaded && folder.Children.All(c => !c.IsPlaceholder)).ConfigureAwait(true);
        return folder;
    }

    private static FileNodeViewModel Node(IEnumerable<FileNodeViewModel> nodes, string name)
        => nodes.SingleOrDefault(n => n.Name == name)
           ?? throw new InvalidOperationException($"{name} が無い。あるのは: {string.Join("、", nodes.Select(n => n.Name))}");

    // ------------------------------------------------------------------
    // (a) エクスプローラーの印
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "エクスプローラーの印: 未反映のモジュールに「未反映」、リボンのファイルに「リボン未反映」、親フォルダに「中に未反映あり」。反映すると消える")]
    public async Task エクスプローラーに印が出て反映で消える()
    {
        var shown = new List<VbaRibbonRewriteViewModel>();
        using var r = await ArrangeAsync(shown, ribbon: true, module: true).ConfigureAwait(true);
        await WaitForExplorerAsync(r).ConfigureAwait(true);
        var explorer = r.H.Shell.Explorer;

        var modules = await ExpandAsync(Node(explorer.RootNodes, "Modules")).ConfigureAwait(true);
        var customUi = await ExpandAsync(Node(explorer.RootNodes, "CustomUI")).ConfigureAwait(true);
        var module1 = Node(modules.Children, "Module1.bas");
        var ribbon = Node(customUi.Children, "customUI14.xml");

        module1.UnpushedBadgeText.Should().Be("未反映");
        module1.UnpushedTooltip.Should().Be(VbaBadgeLogic.ModuleUnpushedFileTip, "ツールチップはプロジェクトのバッジと同じ文言");
        ribbon.UnpushedBadgeText.Should().Be("リボン未反映");
        ribbon.UnpushedTooltip.Should().Be(VbaBadgeLogic.RibbonUnpushedTip, "プロジェクトのバッジの「リボン未反映」と同じ文言");
        modules.ContainsUnpushed.Should().BeTrue("親フォルダにも「中に未反映あり」");
        customUi.ContainsUnpushed.Should().BeTrue();
        customUi.UnpushedBadgeText.Should().BeEmpty("フォルダに文字バッジは付けない（小さな印だけ）");
        customUi.UnpushedTooltip.Should().Be(VbaBadgeLogic.FolderContainsUnpushedTip);
        module1.AutomationName.Should().Contain("未反映");
        explorer.RootNodes.Where(n => n.Name is not ("Modules" or "CustomUI")).Should().OnlyContain(n => !n.ContainsUnpushed);

        // 反映（Excel へ反映 → 続けてリボンの確認 → 書き換え）すると、印は消える。
        await ShellHarness.ExecuteAsync(r.Graft.ProjectPane.PushToExcelCommand).ConfigureAwait(true);
        await ShellHarness.WaitUntilAsync(() => !ribbon.HasUnpushedBadge && !module1.HasUnpushedBadge).ConfigureAwait(true);

        shown.Should().ContainSingle("リボンの確認は出る");
        BookRibbon().Should().Be(XmlB);
        module1.HasUnpushedBadge.Should().BeFalse();
        ribbon.HasUnpushedBadge.Should().BeFalse();
        modules.ContainsUnpushed.Should().BeFalse();
        customUi.ContainsUnpushed.Should().BeFalse();
    }

    [AvaloniaFact(DisplayName = "エクスプローラーの印: まだ開いていないフォルダの中も、開いたとき（実列挙のとき）に印が付く。ほかのプロジェクトの印は持ち越さない")]
    public async Task 開いたときに印が付く()
    {
        var shown = new List<VbaRibbonRewriteViewModel>();
        using var r = await ArrangeAsync(shown, ribbon: true).ConfigureAwait(true);
        await WaitForExplorerAsync(r).ConfigureAwait(true);

        var customUi = Node(r.H.Shell.Explorer.RootNodes, "CustomUI");
        customUi.ContainsUnpushed.Should().BeTrue("まだ開いていないフォルダにも、小さな印は付く");
        customUi.IsLoaded.Should().BeFalse();
        await ExpandAsync(customUi).ConfigureAwait(true);
        Node(customUi.Children, "customUI14.xml").UnpushedBadgeText.Should().Be("リボン未反映");

        // 別（普通）のプロジェクトへ切り替えると、印は持ち越さない。
        await r.H.RegisterProjectAsync().ConfigureAwait(true);
        await ShellHarness.WaitUntilAsync(() => r.H.Shell.Explorer.Project?.Id != r.Project.Id).ConfigureAwait(true);
        r.H.Shell.Explorer.RootNodes.Count(n => n.ContainsUnpushed || n.HasUnpushedBadge).Should().Be(0);
    }

    [AvaloniaFact(DisplayName = "エクスプローラーの印: 取込（Excel 側を採用）で未反映でなくなったら消える。VBA 連携を切ると出ない")]
    public async Task 取込で消える()
    {
        var shown = new List<VbaRibbonRewriteViewModel>();
        using var r = await ArrangeAsync(shown, ribbon: false, module: true).ConfigureAwait(true);
        await WaitForExplorerAsync(r).ConfigureAwait(true);
        var modules = await ExpandAsync(Node(r.H.Shell.Explorer.RootNodes, "Modules")).ConfigureAwait(true);
        var module1 = Node(modules.Children, "Module1.bas");
        module1.HasUnpushedBadge.Should().BeTrue();

        // Excel 側も変わっている（衝突）。「Excel 側を採用」でミラーが Excel と同じになり、未反映でなくなる。
        r.Excel("Module1").Code = "Public Sub OnRun(control As IRibbonControl)\r\n  ' Excel 側で変更\r\nEnd Sub";
        r.ConflictAnswer = request => VbaConflictDecision.All(request, VbaConflictChoice.AdoptExcel);
        await ShellHarness.ExecuteAsync(r.Graft.ProjectPane.PullFromExcelCommand).ConfigureAwait(true);
        await ShellHarness.WaitUntilAsync(() => !module1.HasUnpushedBadge).ConfigureAwait(true);

        module1.HasUnpushedBadge.Should().BeFalse();
        modules.ContainsUnpushed.Should().BeFalse();
    }

    // ------------------------------------------------------------------
    // (b) 閉じるときの確認
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "閉じるときの確認: 未反映が無ければ確認しない（数え直しだけ。Excel には触れない）")]
    public async Task 未反映が無ければ確認しない()
    {
        var shown = new List<VbaRibbonRewriteViewModel>();
        using var r = await ArrangeAsync(shown, ribbon: false).ConfigureAwait(true);
        var callsBefore = r.Bridge.Calls.Count;

        (await r.H.Shell.ConfirmVbaUnpushedBeforeExitAsync().ConfigureAwait(true)).Should().BeTrue();

        r.Choices.Should().BeEmpty();
        r.Bridge.Calls.Count.Should().Be(callsBefore, "判定は Excel に触れずファイルを読むだけ");
    }

    [AvaloniaFact(DisplayName = "閉じるときの確認: プロジェクト名と中身（リボン・モジュール N 件）を挙げた 3 択。既定（Enter・Esc）はキャンセルで、閉じない")]
    public async Task 閉じるときの確認の文面と既定()
    {
        var shown = new List<VbaRibbonRewriteViewModel>();
        using var r = await ArrangeAsync(shown, ribbon: true, module: true).ConfigureAwait(true);

        // 台本が無い（選ばずに閉じた）ときは既定のボタン＝キャンセル。
        (await r.H.Shell.ConfirmVbaUnpushedBeforeExitAsync().ConfigureAwait(true)).Should().BeFalse();

        var request = r.Choices.Should().ContainSingle().Subject;
        request.Title.Should().Be("Excel へ未反映の変更があります");
        request.Message.Should().Contain(r.Project.DisplayName).And.Contain(BookName).And.Contain("リボン・モジュール 1 件");
        request.Options.Select(o => o.Label).Should().Equal("反映してから閉じる", "このまま閉じる", "キャンセル");
        request.Default.Id.Should().Be("cancel");
        request.Options.Count(o => o.IsDefault).Should().Be(1);
        r.Excel("Module1").Code.Should().NotContain("手で足した行", "キャンセルでは何も反映しない");
        BookRibbon().Should().Be(RibbonPackageBuilder.SampleRibbon);
        shown.Should().BeEmpty();
    }

    [AvaloniaFact(DisplayName = "閉じるときの確認: 「このまま閉じる」は反映せずに閉じる")]
    public async Task このまま閉じる()
    {
        var shown = new List<VbaRibbonRewriteViewModel>();
        using var r = await ArrangeAsync(shown, ribbon: true, module: true).ConfigureAwait(true);
        r.ChoiceAnswer = _ => "close";

        (await r.H.Shell.ConfirmVbaUnpushedBeforeExitAsync().ConfigureAwait(true)).Should().BeTrue();

        r.WriteCalls.Should().Be(0);
        shown.Should().BeEmpty();
        (await r.StatusAsync().ConfigureAwait(true)).UnpushedCount.Should().Be(1, "未反映のまま残る");
    }

    [AvaloniaFact(DisplayName = "閉じるときの確認: 「反映してから閉じる」はモジュール → リボンの順に既存の反映（リボンの確認窓は出る）を行い、すべて成功したら閉じる")]
    public async Task 反映してから閉じる()
    {
        var shown = new List<VbaRibbonRewriteViewModel>();
        using var r = await ArrangeAsync(shown, ribbon: true, module: true).ConfigureAwait(true);
        r.ChoiceAnswer = _ => "push";

        (await r.H.Shell.ConfirmVbaUnpushedBeforeExitAsync().ConfigureAwait(true)).Should().BeTrue();

        r.Excel("Module1").Code.Should().Contain("手で足した行");
        shown.Should().ContainSingle("リボンの確認窓は出る");
        BookRibbon().Should().Be(XmlB);
        r.Choices.Should().ContainSingle("確認は 3 択の 1 回だけ（反映の途中で聞き直さない）");
        (await r.StatusAsync().ConfigureAwait(true)).UnpushedCount.Should().Be(0);
        (await r.StatusAsync().ConfigureAwait(true)).RibbonUnpushed.Should().BeFalse();
        r.H.Shell.Graft.ProjectPane.GetVbaUnpushed().Should().BeEmpty();
    }

    [AvaloniaFact(DisplayName = "閉じるときの確認: 「反映してから閉じる」でリボンの確認を「中止」したら、閉じずに戻る（リボンは未反映のまま）")]
    public async Task 反映の中止なら閉じない()
    {
        var shown = new List<VbaRibbonRewriteViewModel>();
        using var r = await ArrangeAsync(shown, ribbon: true, module: false, confirm: false).ConfigureAwait(true);
        r.ChoiceAnswer = _ => "push";

        (await r.H.Shell.ConfirmVbaUnpushedBeforeExitAsync().ConfigureAwait(true)).Should().BeFalse();

        shown.Should().ContainSingle();
        BookRibbon().Should().Be(RibbonPackageBuilder.SampleRibbon);
        (await r.StatusAsync().ConfigureAwait(true)).RibbonUnpushed.Should().BeTrue();
    }

    [AvaloniaFact(DisplayName = "閉じるときの確認: 「反映してから閉じる」でモジュールの反映が失敗したら、閉じずに戻る（リボンへは進まない）")]
    public async Task モジュールの反映が失敗したら閉じない()
    {
        var shown = new List<VbaRibbonRewriteViewModel>();
        using var r = await ArrangeAsync(shown, ribbon: true, module: true).ConfigureAwait(true);
        r.ChoiceAnswer = _ => "push";
        r.Bridge.FailNext("Write", GraftIssue.Of(ErrorCode.E805));

        (await r.H.Shell.ConfirmVbaUnpushedBeforeExitAsync().ConfigureAwait(true)).Should().BeFalse();

        shown.Should().BeEmpty("モジュールが済んでいないので、リボンの準備は始めない");
        BookRibbon().Should().Be(RibbonPackageBuilder.SampleRibbon);
    }

    [AvaloniaFact(DisplayName = "閉じるときの確認: ウィンドウを閉じる操作でも同じ確認が出て、キャンセルなら閉じない。「このまま閉じる」なら閉じる")]
    public async Task ウィンドウの閉じる操作()
    {
        var shown = new List<VbaRibbonRewriteViewModel>();
        using var r = await ArrangeAsync(shown, ribbon: true, withWindow: true).ConfigureAwait(true);
        var window = r.H.Window!;
        var closed = false;
        window.Closed += (_, _) => closed = true;

        window.Close(); // 台本が無い → 既定のキャンセル
        await ShellHarness.WaitUntilAsync(() => r.Choices.Count > 0).ConfigureAwait(true);
        await Task.Delay(100).ConfigureAwait(true);
        Dispatcher.UIThread.RunJobs();
        r.Choices.Should().ContainSingle().Which.Title.Should().Be("Excel へ未反映の変更があります");
        closed.Should().BeFalse("キャンセルなら閉じない");
        window.IsVisible.Should().BeTrue();

        r.ChoiceAnswer = _ => "close";
        window.Close();
        await ShellHarness.WaitUntilAsync(() => closed).ConfigureAwait(true);
        closed.Should().BeTrue("「このまま閉じる」なら閉じる");
    }

    // ------------------------------------------------------------------
    // (c) ステータスバーの常時表示
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "ステータスバー: リボンだけ未反映なら「リボン未反映（ブック名）」。押すとそのプロジェクトで「リボンをブックへ反映…」が始まる")]
    public async Task ステータスバーのリボン未反映()
    {
        var shown = new List<VbaRibbonRewriteViewModel>();
        using var r = await ArrangeAsync(shown, ribbon: true, withWindow: true).ConfigureAwait(true);
        var shell = r.H.Shell;

        shell.HasVbaUnpushed.Should().BeTrue();
        shell.VbaUnpushedText.Should().Be($"リボン未反映（{BookName}）");
        shell.VbaUnpushedTooltip.Should().Contain(r.Project.DisplayName).And.Contain("リボン");

        // 画面（ステータスバー）にも、押せるボタンとして出ている。
        Dispatcher.UIThread.RunJobs();
        var button = r.H.Window!.GetVisualDescendants().OfType<Button>()
            .Single(b => b.Content as string == $"リボン未反映（{BookName}）");
        button.IsVisible.Should().BeTrue();
        button.Command.Should().BeSameAs(shell.PushVbaUnpushedCommand);

        await ShellHarness.ExecuteAsync(shell.PushVbaUnpushedCommand).ConfigureAwait(true);

        shown.Should().ContainSingle("リボンは「リボンをブックへ反映…」と同じ確認から始まる");
        BookRibbon().Should().Be(XmlB);
        await ShellHarness.WaitUntilAsync(() => !shell.HasVbaUnpushed).ConfigureAwait(true);
        shell.HasVbaUnpushed.Should().BeFalse("反映すると消える");
        shell.VbaUnpushedText.Should().BeEmpty();
    }

    [AvaloniaFact(DisplayName = "ステータスバー: モジュールが未反映なら「Excel へ未反映（ブック名）」。押すと「Excel へ反映」が実行される。ほかのプロジェクトを選んでいても、その項目を選んで反映する")]
    public async Task ステータスバーのモジュール未反映()
    {
        var shown = new List<VbaRibbonRewriteViewModel>();
        using var r = await ArrangeAsync(shown, ribbon: false, module: true).ConfigureAwait(true);
        var shell = r.H.Shell;
        shell.VbaUnpushedText.Should().Be($"Excel へ未反映（{BookName}）");

        // 別（普通）のプロジェクトを選んでいても、押すと未反映のプロジェクトを選んで反映する。
        await r.H.RegisterProjectAsync().ConfigureAwait(true);
        await ShellHarness.WaitUntilAsync(() => shell.Graft.ProjectPane.SelectedItem?.Project.Id != r.Project.Id).ConfigureAwait(true);
        shell.HasVbaUnpushed.Should().BeTrue();

        await ShellHarness.ExecuteAsync(shell.PushVbaUnpushedCommand).ConfigureAwait(true);

        shell.Graft.ProjectPane.SelectedItem!.Project.Id.Should().Be(r.Project.Id);
        r.Excel("Module1").Code.Should().Contain("手で足した行");
        await ShellHarness.WaitUntilAsync(() => !shell.HasVbaUnpushed).ConfigureAwait(true);
        shell.HasVbaUnpushed.Should().BeFalse();
    }

    [AvaloniaFact(DisplayName = "ステータスバー: 複数のプロジェクトが未反映なら件数（ほか N 件）を出し、押すと先頭から。設定で VBA 連携を切ると出ない")]
    public async Task ステータスバーの複数()
    {
        var shown = new List<VbaRibbonRewriteViewModel>();
        using var r = await ArrangeAsync(shown, ribbon: false, module: true).ConfigureAwait(true);
        var shell = r.H.Shell;
        var firstProject = r.Project;

        // 2 つ目の VBA プロジェクト（別のブック）を追加して、未反映にする。
        r.Bridge.AddWorkbook(@"C:\Work\売上.xlsm", FakeVbaBridge.Std("Module1", "Sub B()\r\nEnd Sub"));
        var pane = shell.Graft.ProjectPane;
        pane.ShowWorkbookPickerAsync = async picker =>
        {
            await picker.LoadAsync().ConfigureAwait(true);
            picker.SelectedWorkbook = picker.Workbooks.First(w => w.FullName == @"C:\Work\売上.xlsm");
            return await picker.ConfirmAsync().ConfigureAwait(true);
        };
        await ShellHarness.ExecuteAsync(pane.AddFromWorkbookCommand).ConfigureAwait(true);
        var second = pane.SelectedItem!.Project;
        second.Id.Should().NotBe(firstProject.Id);
        File.AppendAllText(Path.Combine(second.Root, "Modules", "Module1.bas"), "\r\n' 手で足した行");
        await pane.RefreshVbaBadgesAsync(checkWorkbookOpen: false).ConfigureAwait(true);

        shell.VbaUnpushedText.Should().EndWith("ほか 1 件");
        var order = pane.GetVbaUnpushed().Select(u => u.Project.Id).ToList();
        order.Should().HaveCount(2);

        await ShellHarness.ExecuteAsync(shell.PushVbaUnpushedCommand).ConfigureAwait(true);

        pane.SelectedItem!.Project.Id.Should().Be(order[0], "先頭から反映する");
        await ShellHarness.WaitUntilAsync(() => pane.GetVbaUnpushed().Count == 1).ConfigureAwait(true);
        pane.GetVbaUnpushed().Single().Project.Id.Should().Be(order[1]);
        shell.VbaUnpushedText.Should().NotContain("ほか", "残りが 1 件なら件数は出さない");
    }

    [AvaloniaFact(DisplayName = "ステータスバー: VBA 連携を設定で切っていると、未反映があっても出さない（反映の操作ができないため）")]
    public async Task 連携を切ると出ない()
    {
        var shown = new List<VbaRibbonRewriteViewModel>();
        using var r = await ArrangeAsync(shown, ribbon: true).ConfigureAwait(true);
        r.H.Shell.HasVbaUnpushed.Should().BeTrue();

        r.Graft.UpdateSettings(new Settings { ShowPreview = false, Vba = new VbaSettings { Enabled = false } });
        await r.Graft.ProjectPane.RefreshVbaBadgesAsync(checkWorkbookOpen: false).ConfigureAwait(true);

        r.H.Shell.HasVbaUnpushed.Should().BeFalse();
        (await r.H.Shell.ConfirmVbaUnpushedBeforeExitAsync().ConfigureAwait(true)).Should().BeTrue("切っていれば閉じるときも確認しない");
        r.Choices.Should().BeEmpty();
    }

    // ------------------------------------------------------------------
    // (d) ブックが閉じたら案内
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "ブックが閉じたら案内: リボン未反映のブックが「開 → 閉」に変わったとき、［反映する］つきの通知が残る。押すと開き直さずにリボンを反映する")]
    public async Task ブックが閉じたら案内()
    {
        var shown = new List<VbaRibbonRewriteViewModel>();
        using var r = await ArrangeAsync(shown, ribbon: true).ConfigureAwait(true);
        var notice = r.Graft.Notice;
        notice.Dismiss(); // 取込の通知（数秒で消えるもの）を片付ける。
        notice.IsVisible.Should().BeFalse("開いているあいだは案内しない");

        r.Bridge.CloseWorkbook(VbaP3Rig.Book);
        await r.Graft.ProjectPane.RefreshVbaBadgesAsync(checkWorkbookOpen: true).ConfigureAwait(true);

        notice.IsVisible.Should().BeTrue();
        notice.IsPersistent.Should().BeTrue("数秒で消えずに残る");
        notice.Text.Should().Be($"ブック（{BookName}）が閉じています。今ならリボンを、ブックを開き直さずに反映できます");
        notice.ActionLabel.Should().Be("反映する");
        notice.HasDismiss.Should().BeTrue();

        // 閉じたままの次の数え直しでは、重ねて案内しない（「開 → 閉」に変わった一度だけ）。
        notice.Dismiss();
        await r.Graft.ProjectPane.RefreshVbaBadgesAsync(checkWorkbookOpen: true).ConfigureAwait(true);
        notice.IsVisible.Should().BeFalse();

        // もう一度ブックを開いて閉じると、また案内する。［反映する］で、開いていないブックへ反映する。
        r.Bridge.AddWorkbook(VbaP3Rig.Book, FakeVbaBridge.Std("Module1", "Public Sub OnRun(control As IRibbonControl)\r\nEnd Sub"));
        await r.Graft.ProjectPane.RefreshVbaBadgesAsync(checkWorkbookOpen: true).ConfigureAwait(true);
        r.Bridge.CloseWorkbook(VbaP3Rig.Book);
        await r.Graft.ProjectPane.RefreshVbaBadgesAsync(checkWorkbookOpen: true).ConfigureAwait(true);
        notice.IsVisible.Should().BeTrue();

        await ShellHarness.ExecuteAsync(notice.ActionCommand).ConfigureAwait(true);

        shown.Should().ContainSingle();
        BookRibbon().Should().Be(XmlB);
        r.Bridge.Calls.Should().NotContain(c => c.StartsWith("SaveClose:", StringComparison.Ordinal), "閉じているので、閉じ開きはしない");
    }

    [AvaloniaFact(DisplayName = "ブックが閉じたら案内: リボンが未反映でなければ（モジュールだけ未反映でも）案内しない")]
    public async Task リボンが未反映でなければ案内しない()
    {
        var shown = new List<VbaRibbonRewriteViewModel>();
        using var r = await ArrangeAsync(shown, ribbon: false, module: true).ConfigureAwait(true);
        r.Graft.Notice.Dismiss();

        r.Bridge.CloseWorkbook(VbaP3Rig.Book);
        await r.Graft.ProjectPane.RefreshVbaBadgesAsync(checkWorkbookOpen: true).ConfigureAwait(true);

        r.Graft.Notice.IsVisible.Should().BeFalse();
    }

    // ------------------------------------------------------------------
    // (e) 中止しても消えない通知
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "中止しても消えない: リボンの反映の確認で「中止」すると、数秒で消えない通知（［今すぐ反映］［あとで］）が残り、ステータスバーの常時表示も残る")]
    public async Task 中止後に残る表示()
    {
        var shown = new List<VbaRibbonRewriteViewModel>();
        using var r = await ArrangeAsync(shown, ribbon: true, confirm: false).ConfigureAwait(true);
        var notice = r.Graft.Notice;

        await ShellHarness.ExecuteAsync(r.Graft.ProjectPane.PushRibbonCommand).ConfigureAwait(true);

        shown.Should().ContainSingle();
        notice.IsVisible.Should().BeTrue();
        notice.IsPersistent.Should().BeTrue("数秒で消える通知ではなく、残る種類");
        notice.Text.Should().Be($"リボンの反映を中止しました（{BookName}）。リボンは未反映のままです。");
        notice.ActionLabel.Should().Be("今すぐ反映");
        notice.DismissLabel.Should().Be("あとで");
        r.H.Shell.HasVbaUnpushed.Should().BeTrue("ステータスバーの常時表示も残る");

        // 「あとで」: 通知は消えるが、未反映のあいだはステータスバーの表示が残る。
        notice.DismissCommand.Execute(null);
        notice.IsVisible.Should().BeFalse();
        r.H.Shell.HasVbaUnpushed.Should().BeTrue();

        // もう一度中止して、今度は［今すぐ反映］。確認がもう一度出て、承諾すれば反映される。
        await ShellHarness.ExecuteAsync(r.Graft.ProjectPane.PushRibbonCommand).ConfigureAwait(true);
        notice.IsPersistent.Should().BeTrue();
        r.Graft.ProjectPane.ShowRibbonRewriteAsync = vm =>
        {
            shown.Add(vm);
            return Task.FromResult(true);
        };
        await ShellHarness.ExecuteAsync(notice.ActionCommand).ConfigureAwait(true);

        shown.Should().HaveCount(3);
        BookRibbon().Should().Be(XmlB);
        notice.IsPersistent.Should().BeFalse();
        await ShellHarness.WaitUntilAsync(() => !r.H.Shell.HasVbaUnpushed).ConfigureAwait(true);
        r.H.Shell.HasVbaUnpushed.Should().BeFalse();
    }

    [AvaloniaFact(DisplayName = "中止しても消えない: 自動の流れ（「Excel へ反映」に続くリボンの確認）で「中止」しても、同じ残る通知になる。反映できたら通知は消える")]
    public async Task 自動の流れの中止も残る()
    {
        var shown = new List<VbaRibbonRewriteViewModel>();
        using var r = await ArrangeAsync(shown, ribbon: true, module: true, confirm: false).ConfigureAwait(true);
        var notice = r.Graft.Notice;

        await ShellHarness.ExecuteAsync(r.Graft.ProjectPane.PushToExcelCommand).ConfigureAwait(true);

        r.Excel("Module1").Code.Should().Contain("手で足した行", "モジュールは反映された");
        shown.Should().ContainSingle();
        notice.IsPersistent.Should().BeTrue();
        notice.ActionLabel.Should().Be("今すぐ反映");

        // リボンが別の経路で反映できたら（未反映でなくなったら）、残っていた通知は消える。
        r.Graft.ProjectPane.ShowRibbonRewriteAsync = _ => Task.FromResult(true);
        await ShellHarness.ExecuteAsync(r.Graft.ProjectPane.PushRibbonCommand).ConfigureAwait(true);
        notice.IsPersistent.Should().BeFalse();
    }
}
