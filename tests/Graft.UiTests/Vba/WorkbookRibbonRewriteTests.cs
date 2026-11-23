using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using FluentAssertions;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Features;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests.Vba;

/// <summary>
/// リボンの書き換えの手動の入口（機能 F の段階 F3。開いているブックの閉じ開きは段階 F4 で、分割ファイル <c>WorkbookRibbonRewriteTests.Open.cs</c>。仕様書_ブックの情報と依頼の観点 16.5・16.9・16.10・16.11・16.13）のシナリオ。
/// 実際の起動と同じ依存グラフ（<see cref="ShellHarness"/>）に <see cref="FakeVbaBridge"/> をつなぎ、ブックの「ファイル」は一時ファイルの zip
/// （<see cref="VbaP3Rig.RibbonFile"/>・<c>MainViewModel.VbaRibbonWorkbookFile</c>）で代える。
/// </summary>
public partial class WorkbookRibbonRewriteTests : IDisposable
{
    private const string Ribbon14 = "CustomUI/customUI14.xml";

    private static readonly string XmlB = RibbonPackageBuilder.SampleRibbon.Replace("label=\"業務\"", "label=\"業務B\"", StringComparison.Ordinal);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "graft-ribbon-rewrite-ui", Guid.NewGuid().ToString("N"));
    private readonly ShownWindowTracker _windows = new();

    public WorkbookRibbonRewriteTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _windows.Dispose();
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

    /// <summary>
    /// ブック（リボンあり）を作ってプロジェクトに取り込み、ミラーのリボンを直してから、Excel の代役でブックを閉じる。
    /// 確認ダイアログには <paramref name="confirm"/> で答え、出た ViewModel を <paramref name="shown"/> に残す。
    /// </summary>
    private async Task<VbaP3Rig> ArrangeAsync(List<VbaRibbonRewriteViewModel> shown, bool confirm = true, bool close = true, string? edited = null)
    {
        File.WriteAllBytes(BookFile, RibbonPackageBuilder.ExcelWorkbook(RibbonPackageBuilder.SampleRibbon));
        var r = await VbaP3Rig.CreateAsync(
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
        File.WriteAllText(r.MirrorPath(Ribbon14), edited ?? XmlB);
        if (close) r.Bridge.CloseWorkbook(VbaP3Rig.Book);
        await r.Graft.ProjectPane.RefreshVbaBadgesAsync(checkWorkbookOpen: true).ConfigureAwait(true);
        return r;
    }

    private string? BookRibbon()
        => VbaRibbonPackageReader.Read(BookFile).Parts.FirstOrDefault(p => p.PartName == "customUI/customUI14.xml")?.Xml;

    private static ProjectListItemViewModel Item(VbaP3Rig r) => r.Graft.ProjectPane.Items.First(i => i.Project.Id == r.Project.Id);

    [AvaloniaFact(DisplayName = "手動の「リボンをブックへ反映…」（ブックが開いていない）: 確認 → 書き換え → 通知。「リボン未反映」が消え、Excel には触れない")]
    public async Task 開いていないブックへ反映()
    {
        var shown = new List<VbaRibbonRewriteViewModel>();
        using var r = await ArrangeAsync(shown).ConfigureAwait(true);
        Item(r).VbaStatusText.Should().Contain("リボン未反映");
        r.Graft.ProjectPane.PushRibbonCommand.CanExecute(null).Should().BeTrue();
        // 開いているかを調べる呼び出し（一覧・状態の検査）は読むだけ。閉じる・保存するなどの書き込みの経路は呼ばれない。
        bool IsReadOnlyCall(string c) => c.StartsWith("List", StringComparison.Ordinal) || c.StartsWith("Inspect:", StringComparison.Ordinal);
        var callsBefore = r.Bridge.Calls.Count(c => !IsReadOnlyCall(c));

        await ShellHarness.ExecuteAsync(r.Graft.ProjectPane.PushRibbonCommand).ConfigureAwait(true);

        var vm = shown.Should().ContainSingle("確認は毎回出す").Subject;
        vm.LeadText.Should().Contain("開いていません");
        vm.StepsText.Should().Contain("1. 書き換える前のブックを、控えとして丸ごと残す").And.Contain("2. リボンの XML を差し替える（CustomUI/customUI14.xml）");
        vm.BackupText.Should().Contain(r.H.AppPaths.GetVbaWorkbookBackupDirectory(r.Project.Id)).And.Contain("シートの値も含みます");
        BookRibbon().Should().Be(XmlB);
        r.Graft.Notice.Text.Should().StartWith("リボンをブックへ反映しました（CustomUI/customUI14.xml。控え: 計画-");
        Item(r).VbaStatusText.Should().NotContain("リボン未反映");
        r.Graft.ProjectPane.PushRibbonCommand.CanExecute(null).Should().BeFalse("未反映が無ければ無効（16.13）");
        r.Bridge.Calls.Count(c => !IsReadOnlyCall(c)).Should().Be(callsBefore,
            "開いていないブックの経路は COM でブックに触れない（開いているかを調べる読み取りだけ）");
        r.Graft.Notice.HasAction.Should().BeTrue("開いていないブックは Graft が開かない。完了の通知に「Excel で開く」を添える（16.6）");
        r.Graft.Notice.ActionLabel.Should().Be("Excel で開く");
        r.Graft.VbaSync.IsWritingToExcel.Should().BeFalse();
    }

    [AvaloniaFact(DisplayName = "確認で「中止」: 何も書かず、「リボン未反映」のまま")]
    public async Task 中止なら書かない()
    {
        var shown = new List<VbaRibbonRewriteViewModel>();
        using var r = await ArrangeAsync(shown, confirm: false).ConfigureAwait(true);
        var before = File.ReadAllBytes(BookFile);

        await ShellHarness.ExecuteAsync(r.Graft.ProjectPane.PushRibbonCommand).ConfigureAwait(true);

        shown.Should().ContainSingle();
        File.ReadAllBytes(BookFile).Should().Equal(before);
        Directory.Exists(r.H.AppPaths.GetVbaWorkbookBackupDirectory(r.Project.Id)).Should().BeFalse("中止なら控えも作らない");
        r.Graft.Notice.Text.Should().Contain("中止しました");
        Item(r).VbaStatusText.Should().Contain("リボン未反映");
    }

    [AvaloniaFact(DisplayName = "XML のエラー: 確認を出さず、E818 の行・列を知らせる")]
    public async Task XMLのエラー()
    {
        var shown = new List<VbaRibbonRewriteViewModel>();
        using var r = await ArrangeAsync(shown, edited: XmlB.Replace("size=\"large\"", "size=\"big\"", StringComparison.Ordinal)).ConfigureAwait(true);
        var before = File.ReadAllBytes(BookFile);

        await ShellHarness.ExecuteAsync(r.Graft.ProjectPane.PushRibbonCommand).ConfigureAwait(true);

        shown.Should().BeEmpty();
        var (title, message) = r.H.Dialogs.Messages.Last();
        title.Should().Be("リボンをブックへ反映できません");
        message.Should().Contain("E818").And.Contain("size").And.Contain("big").And.Contain("行");
        File.ReadAllBytes(BookFile).Should().Equal(before);
    }

    [AvaloniaFact(DisplayName = "控えから戻せなかった（E823）: 「控えのフォルダを開く」で控えのフォルダを開く")]
    public async Task 戻せなければ控えのフォルダ()
    {
        var shown = new List<VbaRibbonRewriteViewModel>();
        using var r = await ArrangeAsync(shown).ConfigureAwait(true);
        var calls = 0;
        r.Graft.VbaSync.Ribbon.Io.Replace = (temp, dest) =>
        {
            calls++;
            if (calls > 1) throw new IOException("復元の置き換えに失敗");
            VbaRibbonPackageWriter.ReplaceFile(temp, dest);
        };
        var reader = r.Graft.VbaRibbonPackageReader;
        r.Graft.VbaRibbonPackageReader = path =>
        {
            var read = reader(path);
            return calls > 0 ? read with { Parts = read.Parts.Select(p => p with { Xml = "<customUI/>" }).ToList() } : read;
        };
        var revealed = new List<string>();
        r.Graft.ProjectPane.RevealFolder = revealed.Add;
        r.H.Dialogs.ActionAnswer = (_, _, action) => action == "控えのフォルダを開く";

        await ShellHarness.ExecuteAsync(r.Graft.ProjectPane.PushRibbonCommand).ConfigureAwait(true);

        r.H.Dialogs.Actions.Should().ContainSingle(a => a.Action == "控えのフォルダを開く" && a.Message.Contains("E823", StringComparison.Ordinal));
        revealed.Should().Equal(r.H.AppPaths.GetVbaWorkbookBackupDirectory(r.Project.Id));
        Item(r).VbaStatusText.Should().Contain("要確認");
    }

    [AvaloniaFact(DisplayName = "反映するものが無いとき: 確認を出さず「反映するリボンの変更はありません」")]
    public async Task 反映するものが無い()
    {
        var shown = new List<VbaRibbonRewriteViewModel>();
        using var r = await ArrangeAsync(shown, edited: RibbonPackageBuilder.SampleRibbon).ConfigureAwait(true);
        r.Graft.ProjectPane.PushRibbonCommand.CanExecute(null).Should().BeFalse("未反映が無ければ無効");

        await r.Graft.ProjectPane.PushRibbonAsync(r.Project).ConfigureAwait(true);

        shown.Should().BeEmpty();
        r.Graft.Notice.Text.Should().Be("ブックへ反映するリボンの変更はありません。");
    }

    [AvaloniaFact(DisplayName = "「リボンの XML を検査」: エラー・警告を行・列つきで一覧にするだけ（ブックは変えない）")]
    public async Task XMLの検査()
    {
        var shown = new List<VbaRibbonRewriteViewModel>();
        using var r = await ArrangeAsync(shown, edited: XmlB.Replace("onAction=\"OnPick\"", "onAction=\"OnPickMissing\"", StringComparison.Ordinal)).ConfigureAwait(true);
        var before = File.ReadAllBytes(BookFile);

        await ShellHarness.ExecuteAsync(r.Graft.ProjectPane.InspectRibbonCommand).ConfigureAwait(true);

        var (title, message) = r.H.Dialogs.Messages.Last();
        title.Should().Be("リボンの XML の検査");
        message.Should().StartWith("CustomUI/customUI14.xml: エラー 0 件・警告").And.Contain("［警告］").And.Contain("OnPickMissing").And.Contain("ブックへ書けます");
        File.ReadAllBytes(BookFile).Should().Equal(before);
        shown.Should().BeEmpty();
    }

    [AvaloniaFact(DisplayName = "起動時の後始末: 作業の記録が残っていれば、バッジの調べ直しで片付けて「途中で終わりました」と知らせる（1 回だけ）")]
    public async Task 起動時の後始末()
    {
        var shown = new List<VbaRibbonRewriteViewModel>();
        using var r = await ArrangeAsync(shown).ConfigureAwait(true);
        var job = new VbaRibbonRewriteJob
        {
            ProjectId = r.Project.Id,
            WorkbookPath = BookFile,
            TempPath = Path.Combine(_root, "~graft-計画-0123abcd.tmp"),
            Stage = VbaRibbonRewriteStage.Started,
            StartedAt = DateTimeOffset.Now,
        };
        File.WriteAllText(job.TempPath, "half");
        await new JsonFileStore().WriteAsync(r.H.AppPaths.GetVbaRibbonJobFilePath(r.Project.Id), job).ConfigureAwait(true);

        // 次の起動の代わり（後始末を確かめた記録は、起動ごとのもの）。
        var pane = r.Graft.ProjectPane;
        typeof(ProjectPaneViewModel).GetField("_ribbonRecoveryChecked", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(pane).As<HashSet<string>>().Clear();
        await pane.RefreshVbaBadgesAsync(checkWorkbookOpen: false).ConfigureAwait(true);

        var (title, message) = r.H.Dialogs.Messages.Should().ContainSingle(m => m.Title == "前回のリボンの書き換えについて").Subject;
        message.Should().Contain("途中で終わりました").And.Contain("元のまま");
        File.Exists(job.TempPath).Should().BeFalse();
        File.Exists(r.H.AppPaths.GetVbaRibbonJobFilePath(r.Project.Id)).Should().BeFalse();
        _ = title;

        await pane.RefreshVbaBadgesAsync(checkWorkbookOpen: false).ConfigureAwait(true);
        r.H.Dialogs.Messages.Count(m => m.Title == "前回のリボンの書き換えについて").Should().Be(1, "片付けたら、もう知らせない");
    }

    // ------------------------------------------------------------------
    // 確認ダイアログの窓（16.9）
    // ------------------------------------------------------------------

    private static VbaRibbonRewritePlan Plan(int warnings = 0, bool newPart = false) => new(
        "p", @"C:\Work\計画.xlsm", "計画.xlsm",
        new[] { new VbaRibbonRewriteTarget(VbaRibbonFormat.Office2010, Ribbon14, XmlB, "h", newPart ? null : RibbonPackageBuilder.SampleRibbon, newPart) },
        Enumerable.Range(1, warnings)
            .Select(i => new VbaRibbonRewriteWarning(Ribbon14, new VbaRibbonFinding(Severity.Warning, 12, 10 + i, 5, $"コールバック「OnRun{i}」: VBA に見つかりません。")))
            .ToList(),
        @"C:\Users\me\AppData\Local\Graft\vba-sync\p.workbooks", 1000, DateTime.UtcNow);

    private static VbaRibbonRewriteViewModel NewViewModel(VbaRibbonRewritePlan plan)
        => new(plan, new Settings(), new FakeUiServices(new FakeClipboard()));

    [AvaloniaFact(DisplayName = "確認の窓: 既定（Enter）と Esc は「中止」。「次から確認しない」は無い")]
    public async Task 既定は中止()
    {
        var vm = NewViewModel(Plan());
        var window = _windows.Track(new VbaRibbonRewriteWindow(vm));
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var cancel = window.FindControl<Button>("CancelButton")!;
        cancel.IsDefault.Should().BeTrue();
        cancel.IsCancel.Should().BeTrue();
        window.FindControl<Button>("RewriteButton")!.IsDefault.Should().BeFalse();
        window.GetLogicalDescendantsText().Should().NotContain("次から確認しない");

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        vm.Confirmed.Should().BeFalse();
        window.IsVisible.Should().BeFalse("Esc で閉じる（中止）");
        await Task.CompletedTask.ConfigureAwait(true);
    }

    [AvaloniaFact(DisplayName = "確認の窓: 「書き換える」で Confirmed。警告は 5 件と「ほか N 件」、「すべて見る」で全件。差分を見るで差分が出る")]
    public void 書き換えると警告と差分()
    {
        var vm = NewViewModel(Plan(warnings: 7));
        vm.WarningLines.Should().HaveCount(6).And.EndWith("ほか 2 件");
        vm.HasMoreWarnings.Should().BeTrue();
        vm.ShowAllWarningsCommand.Execute(null);
        vm.WarningLines.Should().HaveCount(7);
        vm.HasMoreWarnings.Should().BeFalse();

        vm.IsDiffVisible.Should().BeFalse();
        vm.ToggleDiffCommand.Execute(null);
        vm.IsDiffVisible.Should().BeTrue();
        vm.DiffButtonText.Should().Be("差分を隠す");
        vm.DiffCaptionText.Should().Contain("ブックのいまのリボン");

        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;
        vm.RewriteCommand.Execute(null);
        vm.Confirmed.Should().BeTrue();
        closed.Should().BeTrue();
    }
}

/// <summary>テスト用の小道具（窓の中の文字をまとめて読む）。</summary>
internal static class RibbonRewriteWindowTestExtensions
{
    public static string GetLogicalDescendantsText(this Window window)
        => string.Join("\n", Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(window)
            .Select(d => d switch
            {
                TextBlock t => t.Text,
                Button { Content: string s } => s,
                _ => null,
            })
            .Where(t => !string.IsNullOrEmpty(t)));
}
