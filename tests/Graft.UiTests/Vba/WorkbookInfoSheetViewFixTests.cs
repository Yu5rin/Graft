using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Features;
using Graft.Infra;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests.Vba;

/// <summary>
/// 「ブックの情報」のシートごとの見せ方（仕様書_ブックの情報と依頼の観点 5.2。v0.4）で見つかった不具合の回帰テスト（画面・ViewModel まで通すもの）。
/// 各テストの名前に、直した不具合の番号を付けている（不具合 N）。純ロジックの回帰テストは <c>Graft.Tests</c> の <c>VbaSheetViewFixTests</c>。
/// </summary>
public class WorkbookInfoSheetViewFixTests : IDisposable
{
    private const string Other = @"C:\Other\別の会社.xlsm";

    private readonly ShownWindowTracker _windows = new();

    public void Dispose()
    {
        _windows.Dispose();
        GC.SuppressFinalize(this);
    }

    private static async Task<VbaP3Rig> NewRigAsync(
        Func<FakeVbaBridge.Workbook, VbaWorkbookInfoSnapshot?>? info = null, Settings? settings = null)
    {
        var rig = await VbaP3Rig.CreateAsync(
            settings ?? new Settings { ShowPreview = false },
            arrange: bridge => bridge.Get(VbaP3Rig.Book)!.Info = info is null ? WorkbookInfoTests.Sample() : info(bridge.Get(VbaP3Rig.Book)!)).ConfigureAwait(true);
        rig.ChoiceAnswer = _ => "give";
        return rig;
    }

    private static async Task<string> CollectedTextAsync(VbaP3Rig r)
    {
        r.H.Clipboard.Text = null;
        await ShellHarness.ExecuteAsync(r.Graft.ContextCollect!.CopyCommand).ConfigureAwait(true);
        return r.H.Clipboard.Text ?? string.Empty;
    }

    private static async Task<VbaLink> StoredLinkAsync(VbaP3Rig r)
        => (await new ProjectStore(r.H.AppPaths).LoadAsync().ConfigureAwait(true)).Value.First(p => p.Id == r.Project.Id).Vba!;

    private static VbaSheetViewRowViewModel Row(ContextCollectViewModel context, string codeName)
        => context.WorkbookInfo.Sheets.Single(s => s.CodeName == codeName);

    /// <summary>別のブック（同じミラーを使い回せるよう、モジュールは同じ内容）を開く。値の合図は <c>OTHER-BOOK-SECRET</c>。</summary>
    private static void OpenOtherBook(VbaP3Rig r)
    {
        var book = r.Bridge.AddWorkbook(Other,
            FakeVbaBridge.Std("Module1", VbaP3Rig.Module1Original),
            FakeVbaBridge.Cls("Cls1", VbaP3Rig.Cls1Original),
            FakeVbaBridge.Doc("Sheet1", "' sheet", "集計"));
        var sample = WorkbookInfoTests.Sample("別の会社.xlsm");
        book.Info = sample with
        {
            Sheets = new[]
            {
                sample.Sheets[0] with
                {
                    SampleRows = new IReadOnlyList<string>[] { new[] { "1", "OTHER-BOOK-SECRET", "999" } },
                },
                sample.Sheets[1],
            },
        };
    }

    private static VbaWorkbookInfoSnapshot? MemorySnapshot(ContextCollectViewModel context)
        => (VbaWorkbookInfoSnapshot?)typeof(ContextCollectViewModel)
            .GetField("_workbookSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(context);

    // ---------------- 不具合 1: 結び直し ----------------

    [AvaloniaFact(DisplayName = "不具合 1: 同じプロジェクトを選んだまま別のブックへ結び直したら、収集のモデルが作り直され、前のブックの見せ方で新しいブックの値が出ない（実際の追加の経路）")]
    public async Task 不具合1_結び直しで収集を作り直す()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        OpenOtherBook(r);
        var before = r.Graft.ContextCollect!;
        await before.InitializeAsync().ConfigureAwait(true);
        await before.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.All).ConfigureAwait(true);
        (await CollectedTextAsync(r).ConfigureAwait(true)).Should().Contain("SECRET-甲社", "前提: 前のブックでは Sheet1 の値が渡っている");
        var confirms = r.Choices.Count;

        var pane = r.Graft.ProjectPane;
        pane.ShowWorkbookPickerAsync = async picker =>
        {
            await picker.LoadAsync().ConfigureAwait(true);
            picker.SelectedWorkbook = picker.Workbooks.First(w => w.FullName == Other);
            picker.MirrorFolder = r.Project.Root; // 同じミラーを、別のブックで結び直す。
            return await picker.ConfirmAsync().ConfigureAwait(true);
        };
        await ShellHarness.ExecuteAsync(pane.AddFromWorkbookCommand).ConfigureAwait(true);

        var after = r.Graft.ContextCollect!;
        after.Should().NotBeSameAs(before, "結び直したら、古い見せ方と取得結果を持つモデルを作り直す");
        await after.InitializeAsync().ConfigureAwait(true);
        r.Bridge.Calls.Last(c => c.StartsWith("Info:", StringComparison.Ordinal)).Should().Be($"Info:{Other}");
        r.Bridge.InfoValueReads.Last().Should().BeEmpty("新しいブックの値は、確認なしでは読まない");
        after.WorkbookInfo.Sheets.Select(s => s.SelectedMode.Mode).Should().OnlyContain(m => m == VbaSheetViewMode.StructureOnly);
        var text = await CollectedTextAsync(r).ConfigureAwait(true);
        text.Should().NotContain("OTHER-BOOK-SECRET").And.NotContain("SECRET-甲社");
        r.Choices.Count.Should().Be(confirms, "確認をやり直すまで、値は渡らない");
        (await StoredLinkAsync(r).ConfigureAwait(true)).WorkbookInfoSheetViews.Should().BeNullOrEmpty();
    }

    [AvaloniaFact(DisplayName = "不具合 1: 作り直されない古い収集のモデルでも、取得と出力の直前にブックのパスを突き合わせ、前のブックの内容・見せ方を捨てる。古い見せ方を保存へ書き戻さない")]
    public async Task 不具合1_古いモデルでも前のブックの内容を出さない()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        OpenOtherBook(r);
        var stale = r.Graft.ContextCollect!;
        await stale.InitializeAsync().ConfigureAwait(true);
        await stale.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.All).ConfigureAwait(true);
        stale.BuildWorkbookInfoText().Should().Contain("SECRET-甲社", "前提");

        // 保存側だけが別のブックへ結び直された（モデルは作り直されない、という不具合の再現）。
        await r.UpdateLinkAsync(l => l.WithWorkbook(Other)).ConfigureAwait(true);
        r.Graft.ContextCollect.Should().BeSameAs(stale, "前提: このやり方では収集のモデルは作り直されない");

        stale.BuildWorkbookInfoText().Should().BeEmpty("前のブックの取得結果は、出力の直前に捨てる");

        await stale.FetchWorkbookInfoAsync().ConfigureAwait(true);
        r.Bridge.Calls.Last(c => c.StartsWith("Info:", StringComparison.Ordinal)).Should().Be($"Info:{Other}");
        r.Bridge.InfoValueReads.Last().Should().BeEmpty("前のブックの見せ方（Sheet1 は全部）で、新しいブックの値を読まない");
        stale.BuildWorkbookInfoText().Should().NotContain("OTHER-BOOK-SECRET");

        // 次の操作で、古い見せ方（Sheet1 は全部）を保存へ書き戻さない。
        await stale.SetSheetViewModeAsync("Sheet2", VbaSheetViewMode.None).ConfigureAwait(true);
        var stored = (await StoredLinkAsync(r).ConfigureAwait(true)).WorkbookInfoSheetViews!;
        stored.Select(v => (v.CodeName, v.Mode)).Should().Equal(("Sheet2", "None"));
    }

    [AvaloniaFact(DisplayName = "不具合 1: 見せ方の保存は、保存側の最新を読んで対象のシートだけを差し替える（古い全体で上書きしない）")]
    public async Task 不具合1_保存は最新に対して差し替える()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);

        // 別の経路で、Sheet2 の見せ方が保存された（このモデルは知らない）。
        await r.UpdateLinkAsync(l => l.WithSheetViews(new VbaSheetViewSet(VbaSheetView.StructureOnly, new[]
        {
            new KeyValuePair<string, VbaSheetView>("Sheet2", VbaSheetView.Excluded),
        }))).ConfigureAwait(true);

        await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.HeaderRows).ConfigureAwait(true);

        var stored = (await StoredLinkAsync(r).ConfigureAwait(true)).WorkbookInfoSheetViews!;
        stored.Select(v => (v.CodeName, v.Mode)).Should().BeEquivalentTo(new[] { ("Sheet1", "HeaderRows"), ("Sheet2", "None") },
            "Sheet2 の記録を、古い一覧で消さない");
    }

    // ---------------- 不具合 2: 取得結果に載らなかった「出さない」のシート ----------------

    [AvaloniaFact(DisplayName = "不具合 2: 「出さない」のシートが取得結果に載らなくても（時間切れなど）、そのシートの名前を指す名前付き範囲と注記は依頼文に出ない")]
    public async Task 不具合2_載らなかった出さないシートの名前は出ない()
    {
        using var r = await NewRigAsync(book =>
        {
            var sample = WorkbookInfoTests.Sample();
            return sample with
            {
                // 「設定」（Sheet2）は読めなかった（取得結果に載らない）。名前の式と注記には、まだ「設定」が出てくる。
                Sheets = new[] { sample.Sheets[0] },
                Omitted = new[] { "時間がかかったため、シート「設定」以降は読めていません。" },
            };
        }).ConfigureAwait(true);
        await r.UpdateLinkAsync(l => l.WithSheetViews(new VbaSheetViewSet(VbaSheetView.StructureOnly, new[]
        {
            new KeyValuePair<string, VbaSheetView>("Sheet2", VbaSheetView.Excluded),
        }))).ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;

        await context.InitializeAsync().ConfigureAwait(true);

        var text = await CollectedTextAsync(r).ConfigureAwait(true);
        text.Should().Contain("# ブックの情報").And.Contain("| 集計 | Sheet1 |");
        text.Should().NotContain("=設定!").And.NotContain("「設定」", "「出さない」のシートの名前は、名前付き範囲の式にも注記にも出さない");
        text.Should().NotContain("税率");
    }

    // ---------------- 不具合 3: 取得の途中の操作 ----------------

    [AvaloniaFact(DisplayName = "不具合 3: 取得の途中で数式をオフにし、次の取得が失敗しても、古い取得（数式つき）の結果で数式つきの値が戻らない")]
    public async Task 不具合3_古い取得の結果を捨てる()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.HeaderRows).ConfigureAwait(true);
        await context.SetSheetIncludeFormulasAsync("Sheet1", true).ConfigureAwait(true);
        (await CollectedTextAsync(r).ConfigureAwait(true)).Should().Contain("=B2*C2", "前提: 数式つきで出ている");

        var gate = new TaskCompletionSource();
        var slow = r.Bridge.InfoRequests.Count + 1;
        r.Bridge.InfoHook = n => n == slow ? gate.Task : Task.CompletedTask;
        var running = context.FetchWorkbookInfoAsync(); // 数式つきの指示で走り、Excel の応答を待っている取得。
        await ShellHarness.WaitUntilAsync(() => r.Bridge.InfoRequests.Count == slow).ConfigureAwait(true);
        r.Bridge.InfoRequests.Last().Views.For("Sheet1").IncludeFormulas.Should().BeTrue("前提");

        // 待っている間に数式をオフにする。続く取得は失敗する。
        r.Bridge.FailNext("Info", GraftIssue.Of(ErrorCode.E805));
        await context.SetSheetIncludeFormulasAsync("Sheet1", false).ConfigureAwait(true);
        gate.SetResult();
        await running.ConfigureAwait(true);

        var text = context.BuildWorkbookInfoText();
        text.Should().NotContain("=B2*C2", "オフにした数式を、古い取得の結果から戻さない");
        MemorySnapshot(context)!.Sheets.Single(s => s.CodeName == "Sheet1").SampleRows.Should().BeEmpty("メモリにも戻さない");
    }

    [AvaloniaFact(DisplayName = "不具合 3: 取得の途中で構成だけに戻したシートの値は、戻ってきた結果から格納しない（メモリにも残さない）")]
    public async Task 不具合3_構成だけに戻したシートの値は格納しない()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.HeaderRows).ConfigureAwait(true);

        var gate = new TaskCompletionSource();
        var slow = r.Bridge.InfoRequests.Count + 1;
        r.Bridge.InfoHook = n => n == slow ? gate.Task : Task.CompletedTask;
        var running = context.FetchWorkbookInfoAsync();
        await ShellHarness.WaitUntilAsync(() => r.Bridge.InfoRequests.Count == slow).ConfigureAwait(true);

        await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.StructureOnly).ConfigureAwait(true);
        gate.SetResult();
        await running.ConfigureAwait(true);

        MemorySnapshot(context)!.Sheets.Single(s => s.CodeName == "Sheet1").SampleRows.Should().BeEmpty("構成だけに戻したシートの値は、メモリに戻らない");
        context.BuildWorkbookInfoText().Should().NotContain("SECRET");
    }

    // ---------------- 不具合 4: コード名の再利用 ----------------

    [AvaloniaFact(DisplayName = "不具合 4: Excel 側でシートが入れ替わり（同じコード名の別のシート）、値が渡る記録が確認なしで引き継がれない")]
    public async Task 不具合4_別のシートが同じコード名になっても値は渡らない()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.HeaderRows).ConfigureAwait(true);
        (await CollectedTextAsync(r).ConfigureAwait(true)).Should().Contain("SECRET-甲社", "前提");

        // 「集計」を削除し、別の内容の「人事」が、同じコード名 Sheet1 で作られた。
        var book = r.Bridge.Get(VbaP3Rig.Book)!;
        var sample = WorkbookInfoTests.Sample();
        book.Info = sample with
        {
            Sheets = new[]
            {
                new VbaSheetInfo("人事", "Sheet1", VbaSheetVisibility.Visible, "A1:B3",
                    new[] { "氏名", "給与" }, new IReadOnlyList<string>[] { new[] { "HR-SECRET-山田", "900000" } }),
                sample.Sheets[1],
            },
            Tables = Array.Empty<VbaTableInfo>(),
            Names = Array.Empty<VbaNamedRangeInfo>(),
        };
        await context.FetchWorkbookInfoAsync().ConfigureAwait(true);

        var text = await CollectedTextAsync(r).ConfigureAwait(true);
        text.Should().NotContain("HR-SECRET-山田", "別のシートの値を、確認なしで渡さない");
        Row(context, "Sheet1").SelectedMode.Mode.Should().Be(VbaSheetViewMode.StructureOnly);
        (await StoredLinkAsync(r).ConfigureAwait(true)).WorkbookInfoSheetViews.Should().BeNullOrEmpty("値が渡る記録は落とす");
    }

    [AvaloniaFact(DisplayName = "不具合 4: 取得結果に無いコード名の、値が渡る記録は、次の保存で落とす（「出さない」の記録は残す）")]
    public async Task 不具合4_消えたシートの値の記録は保存で落とす()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        await r.UpdateLinkAsync(l => l.WithSheetViews(new VbaSheetViewSet(VbaSheetView.StructureOnly, new[]
        {
            new KeyValuePair<string, VbaSheetView>("Sheet9", new VbaSheetView(VbaSheetViewMode.All)),
            new KeyValuePair<string, VbaSheetView>("Sheet8", VbaSheetView.Excluded),
        }))).ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);

        await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.HeaderRows).ConfigureAwait(true);

        var stored = (await StoredLinkAsync(r).ConfigureAwait(true)).WorkbookInfoSheetViews!;
        stored.Select(v => v.CodeName).Should().BeEquivalentTo(new[] { "Sheet1", "Sheet8" });
    }

    // ---------------- 不具合 5: 範囲 ----------------

    [AvaloniaFact(DisplayName = "不具合 5: 値が渡らない見せ方へ戻すと範囲も消える。「範囲を指定」を選び直しても、以前の範囲が入力なしで読まれない")]
    public async Task 不具合5_戻したら範囲も消える()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.Range).ConfigureAwait(true);
        (await context.SetSheetRangeAsync("Sheet1", "A1:B2").ConfigureAwait(true)).Should().BeNull();
        await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.StructureOnly).ConfigureAwait(true);
        (await StoredLinkAsync(r).ConfigureAwait(true)).WorkbookInfoSheetViews.Should().BeNullOrEmpty("構成だけは既定なので、範囲の記録も残らない");

        r.Choices.Clear();
        var reads = r.Bridge.InfoValueReads.Count;
        await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.Range).ConfigureAwait(true);

        r.Choices.Single().Message.Should().Contain("このあと入力する範囲");
        r.Bridge.InfoValueReads.Skip(reads).Should().NotContain(v => v.ContainsKey("Sheet1"), "範囲の入力が無いうちは、何も読まない");
        context.WorkbookInfo.ValueSheetCount.Should().Be(0);
        var text = await CollectedTextAsync(r).ConfigureAwait(true);
        text.Should().NotContain("範囲 A1:B2").And.NotContain("SECRET");
        Row(context, "Sheet1").RangeText.Should().BeEmpty();
    }

    [AvaloniaFact(DisplayName = "不具合 5: 見出し＋N 行から範囲を指定へ切り替えるとき、指定済みの範囲があれば、確認の文面にその範囲が出る")]
    public async Task 不具合5_指定済みの範囲は確認に出る()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.Range).ConfigureAwait(true);
        await context.SetSheetRangeAsync("Sheet1", "A1:B2").ConfigureAwait(true);
        await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.All).ConfigureAwait(true);
        r.Choices.Clear();

        await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.Range).ConfigureAwait(true);

        r.Choices.Single().Message.Should().Contain("指定済みの範囲 A1:B2 の値が", "渡る範囲を、確認の文面に明示する");
    }

    // ---------------- 不具合 6: コード名が空のシート ----------------

    [AvaloniaFact(DisplayName = "不具合 6: コード名が空のシートが 2 枚あっても、取得を繰り返して例外にならない。見せ方は選べず、値は渡さない")]
    public async Task 不具合6_空のコード名で例外にならない()
    {
        using var r = await NewRigAsync(book =>
        {
            var sample = WorkbookInfoTests.Sample();
            return sample with
            {
                Sheets = new[]
                {
                    sample.Sheets[0] with { Name = "甲", CodeName = string.Empty },
                    sample.Sheets[1] with { Name = "乙", CodeName = string.Empty },
                },
            };
        }).ConfigureAwait(true);
        await r.UpdateLinkAsync(l => l with { WorkbookInfoSampleValues = true }).ConfigureAwait(true); // 移行中: 既定は「見出し＋N 行」。
        var context = r.Graft.ContextCollect!;

        await context.InitializeAsync().ConfigureAwait(true);
        await context.FetchWorkbookInfoAsync().ConfigureAwait(true); // 2 回目で、以前は例外になった。

        context.WorkbookInfo.Sheets.Select(s => s.Name).Should().Equal("甲", "乙");
        context.WorkbookInfo.Sheets.Should().OnlyContain(s => s.NoteText.Contains("見せ方は選べません"));
        (await context.SetSheetViewModeAsync(string.Empty, VbaSheetViewMode.All).ConfigureAwait(true)).Should().BeFalse();
        r.Choices.Should().BeEmpty();
        var text = await CollectedTextAsync(r).ConfigureAwait(true);
        text.Should().NotContain("SECRET", "鍵の無いシートは、移行中の既定（見出し＋N 行）でも値を渡さない");
        r.Bridge.InfoValueReads.Last().Should().BeEmpty();
    }

    // ---------------- 不具合 7: 表示名の変更 ----------------

    [AvaloniaFact(DisplayName = "不具合 7: Excel 側でシート名を変えて取り直したら、一覧の行の表示名（Title）の変更が通知される")]
    public async Task 不具合7_表示名の変更が通知される()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        var row = Row(context, "Sheet1");
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        var book = r.Bridge.Get(VbaP3Rig.Book)!;
        var sample = WorkbookInfoTests.Sample();
        book.Info = sample with { Sheets = new[] { sample.Sheets[0] with { Name = "集計 2027" }, sample.Sheets[1] } };
        await context.FetchWorkbookInfoAsync().ConfigureAwait(true);

        Row(context, "Sheet1").Should().BeSameAs(row, "同じコード名の行は使い回す");
        row.Title.Should().Be("集計 2027（Sheet1）");
        changed.Should().Contain("Title", "画面の表示名が古いまま残らないよう、変更を通知する");
    }

    // ---------------- 不具合 8: 全角の入力 ----------------

    [AvaloniaFact(DisplayName = "不具合 8: 全角で打った範囲・行数が入力欄で受け付けられ、正規化されて保存される")]
    public async Task 不具合8_全角の入力を受け付ける()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.Range).ConfigureAwait(true);

        (await context.SetSheetRangeAsync("Sheet1", "Ａ１：Ｃ３").ConfigureAwait(true)).Should().BeNull();

        (await StoredLinkAsync(r).ConfigureAwait(true)).WorkbookInfoSheetViews!.Single().Range.Should().Be("A1:C3");
        (await context.SetSheetRangeAsync("Sheet1", "A1$").ConfigureAwait(true)).Should().NotBeNull("位置の違う $ は通さない");

        await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.HeaderRows).ConfigureAwait(true);
        (await context.SetSheetHeaderRowsAsync("Sheet1", "１").ConfigureAwait(true)).Should().BeNull();
        (await StoredLinkAsync(r).ConfigureAwait(true)).WorkbookInfoSheetViews!.Single().HeaderRows.Should().Be(1);
    }

    // ---------------- 不具合 10: 空のシートの範囲指定 ----------------

    [AvaloniaFact(DisplayName = "不具合 10: 空のシートを「範囲を指定」にしたとき、何も出ない理由（使用範囲の外）が、依頼文にも画面の注記にも出る")]
    public async Task 不具合10_空のシートの理由が出る()
    {
        using var r = await NewRigAsync(book =>
        {
            var sample = WorkbookInfoTests.Sample();
            return sample with
            {
                Sheets = sample.Sheets.Append(
                    new VbaSheetInfo("空き", "Sheet3", VbaSheetVisibility.Visible, null, Array.Empty<string>(), Array.Empty<IReadOnlyList<string>>())).ToArray(),
            };
        }).ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        await context.SetSheetViewModeAsync("Sheet3", VbaSheetViewMode.Range).ConfigureAwait(true);

        await context.SetSheetRangeAsync("Sheet3", "A1:B2").ConfigureAwait(true);

        var text = await CollectedTextAsync(r).ConfigureAwait(true);
        text.Should().Contain("### 空き（Sheet3）: 範囲 A1:B2").And.Contain("使用範囲 （空） の外で、値はありません");
        Row(context, "Sheet3").NoteText.Should().Contain("使用範囲").And.Contain("外");
    }

    // ---------------- 疑わしい点: 保存の失敗 ----------------

    [AvaloniaFact(DisplayName = "疑わしい点: 見せ方の保存に失敗したら、画面に知らせ、見せ方の表示とメモリの値を保存側（変わっていない）に合わせる")]
    public async Task 保存の失敗を知らせて保存側に合わせる()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.HeaderRows).ConfigureAwait(true);
        (await CollectedTextAsync(r).ConfigureAwait(true)).Should().Contain("SECRET-甲社", "前提");

        // 保存先からプロジェクトが無くなった（保存は「プロジェクトが見つかりません」で失敗する）。
        await new ProjectStore(r.H.AppPaths).RemoveAsync(r.Project.Id, deleteHistory: false).ConfigureAwait(true);

        await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.StructureOnly).ConfigureAwait(true);

        context.WorkbookInfoWarning.Should().Contain("保存できませんでした");
        context.VbaWarning.Should().Contain("保存できませんでした");
        Row(context, "Sheet1").SelectedMode.Mode.Should().Be(VbaSheetViewMode.HeaderRows, "保存してある見せ方（変わっていない）に、表示を合わせる");
        context.BuildWorkbookInfoText().Should().Contain("SECRET-甲社", "メモリの値も、保存してある見せ方に合わせる（表示と出力を食い違わせない）");

        // 全体の出し方の切り替えも同じ。
        var level = context.WorkbookInfo.Level;
        await context.CycleWorkbookInfoLevelAsync().ConfigureAwait(true);
        context.WorkbookInfo.Level.Should().Be(level, "保存できなかったので、出し方も変えない");
    }

    // ---------------- 疑わしい点: 入力欄の確定 ----------------

    private static async Task<(ContextCollectWindow Window, TextBox Box)> OpenRangeBoxAsync(
        WorkbookInfoSheetViewFixTests self, VbaP3Rig r)
    {
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.Range).ConfigureAwait(true);
        var window = self._windows.Track(new ContextCollectWindow(context) { Height = 900 });
        window.Show();
        for (var i = 0; i < 4; i++)
        {
            await Task.Delay(40).ConfigureAwait(true);
            Dispatcher.UIThread.RunJobs();
        }

        // 範囲の入力欄は、行ごとに作られる（見せ方が「範囲を指定」でない行のものは隠れている）。見えているものを取る。
        var box = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Watermark == "例 A1:F30" && t.IsEffectivelyVisible);
        return (window, box);
    }

    [AvaloniaFact(DisplayName = "疑わしい点: 範囲の入力欄に打った直後（フォーカスを外す前）にコピーしても、打った範囲が確定して出力に入る")]
    public async Task 入力直後のコピーでも確定する()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var (_, box) = await OpenRangeBoxAsync(this, r).ConfigureAwait(true);

        box.Text = "B2:B3"; // フォーカスは外さない。

        var text = await CollectedTextAsync(r).ConfigureAwait(true);
        text.Should().Contain("### 集計（Sheet1）: 範囲 B2:B3").And.Contain("SECRET-甲社").And.Contain("SECRET-乙社");
    }

    [AvaloniaFact(DisplayName = "疑わしい点: 範囲の入力欄に打った直後に窓を閉じても、打った範囲が確定して保存される")]
    public async Task 入力直後に窓を閉じても確定する()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var (window, box) = await OpenRangeBoxAsync(this, r).ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;

        box.Text = "B2:B3";
        window.Close();

        await ShellHarness.WaitUntilAsync(() => Row(context, "Sheet1").PassesValues).ConfigureAwait(true);
        Row(context, "Sheet1").PassesValues.Should().BeTrue();
        (await StoredLinkAsync(r).ConfigureAwait(true)).WorkbookInfoSheetViews!.Single().Range.Should().Be("B2:B3");
    }

    // ---------------- 不具合 11: 読み上げの名前 ----------------

    [AvaloniaFact(DisplayName = "不具合 11: シートの行の操作部品（見せ方・行数・範囲・数式）の読み上げの名前に、どのシートのものかが入る")]
    public async Task 不具合11_部品の名前にシート名が入る()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.HeaderRows).ConfigureAwait(true);
        await context.SetSheetViewModeAsync("Sheet2", VbaSheetViewMode.Range).ConfigureAwait(true);
        var window = _windows.Track(new ContextCollectWindow(context) { Height = 900 });
        window.Show();
        for (var i = 0; i < 4; i++)
        {
            await Task.Delay(40).ConfigureAwait(true);
            Dispatcher.UIThread.RunJobs();
        }

        var names = window.GetVisualDescendants().OfType<Control>()
            .Select(c => Avalonia.Automation.AutomationProperties.GetName(c))
            .Where(n => !string.IsNullOrEmpty(n))
            .ToList();

        names.Should().Contain(n => n.Contains("集計") && n.EndsWith("の見せ方", StringComparison.Ordinal));
        names.Should().Contain(n => n.Contains("設定") && n.EndsWith("の見せ方", StringComparison.Ordinal));
        names.Should().Contain(n => n.Contains("集計") && n.Contains("見出しに続く行数"));
        names.Should().Contain(n => n.Contains("設定") && n.Contains("読む範囲"));
        names.Should().Contain(n => n.Contains("集計") && n.Contains("数式も含める"));
        names.Should().NotContain("シートの見せ方", "全行で同じ名前では、どのシートか区別できない");
        names.Should().NotContain("見出しに続く行数").And.NotContain("読む範囲（A1 形式）").And.NotContain("このシートの値に数式も含める");
    }
}
