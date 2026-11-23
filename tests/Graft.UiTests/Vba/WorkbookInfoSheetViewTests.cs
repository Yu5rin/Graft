using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Features;
using Graft.Infra;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;

namespace Graft.UiTests.Vba;

/// <summary>
/// ブックの情報のシートごとの見せ方（仕様書_ブックの情報と依頼の観点 5.2。v0.4）のシナリオ。
/// 見せ方ごとの出力、既定、確認ダイアログ、コード名での記憶、旧「値の見本」からの移行、上限と切り詰めの明記、範囲の検証、
/// 値が要るシートだけを読むこと、オフにしたら捨てること、設定オフで入らないことを、
/// 実際の起動と同じ依存グラフ（<see cref="ShellHarness"/>）と <see cref="FakeVbaBridge"/> で確かめる。
/// </summary>
public class WorkbookInfoSheetViewTests
{
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
        await ShellHarness.ExecuteAsync(r.Graft.ContextCollect!.CopyCommand).ConfigureAwait(true);
        return r.H.Clipboard.Text ?? string.Empty;
    }

    private static int InfoCalls(VbaP3Rig r) => r.Bridge.Calls.Count(c => c.StartsWith("Info:", StringComparison.Ordinal));

    private static async Task<VbaLink> StoredLinkAsync(VbaP3Rig r)
        => (await new ProjectStore(r.H.AppPaths).LoadAsync().ConfigureAwait(true)).Value.First(p => p.Id == r.Project.Id).Vba!;

    private static VbaSheetViewRowViewModel Row(ContextCollectViewModel context, string codeName)
        => context.WorkbookInfo.Sheets.Single(s => s.CodeName == codeName);

    /// <summary>1 枚目のシート（Sheet1）が、行 × 列の大きな表を持つブックの情報。値は <c>r行-列</c>。</summary>
    private static VbaWorkbookInfoSnapshot Big(FakeVbaBridge.Workbook workbook, int rows, int columns)
    {
        workbook.SheetGrids["Sheet1"] = Enumerable.Range(1, rows)
            .Select(r => Enumerable.Range(1, columns).Select(c => $"r{r}-{c}").ToArray())
            .ToArray();
        var sample = WorkbookInfoTests.Sample();
        var sheet1 = sample.Sheets[0] with
        {
            UsedRange = $"A1:{VbaWorkbookInfoReadRules.ColumnLetters(columns)}{rows}",
            HeaderRow = Array.Empty<string>(),
            SampleRows = Array.Empty<IReadOnlyList<string>>(),
        };
        return sample with { Sheets = new[] { sheet1, sample.Sheets[1] } };
    }

    private static Settings Wide(int allRows = 500, int allColumns = 50) => new()
    {
        ShowPreview = false,
        // 出力全体の文字数の上限は、切り詰めの確認を邪魔しないよう設定で許される最大（100000）にする
        // （これを超える値は、設定ファイルの読み込みで既定値へ戻される）。
        Vba = new VbaSettings { WorkbookInfoMaxChars = 100_000, WorkbookInfoAllMaxRows = allRows, WorkbookInfoAllMaxColumns = allColumns },
    };

    // ---------------- 既定と一覧 ----------------

    [AvaloniaFact(DisplayName = "既定: 取得したシートが一覧に並び、全シート「構成だけ」。何も保存されていない（触るまで記録を作らない）")]
    public async Task 既定は全シート構成だけ()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;

        await context.InitializeAsync().ConfigureAwait(true);

        context.WorkbookInfo.Sheets.Select(s => (s.CodeName, s.Title, s.SelectedMode.Mode))
            .Should().Equal(("Sheet1", "集計（Sheet1）", VbaSheetViewMode.StructureOnly), ("Sheet2", "設定（Sheet2）", VbaSheetViewMode.StructureOnly));
        context.WorkbookInfo.ShowSheetViews.Should().BeTrue();
        context.WorkbookInfo.ValueSheetCount.Should().Be(0);
        context.WorkbookInfo.IsValueNoticeVisible.Should().BeFalse("値が渡るシートが無いので、注意も出ない");
        (await StoredLinkAsync(r).ConfigureAwait(true)).WorkbookInfoSheetViews.Should().BeNull();
        Row(context, "Sheet1").ModeOptions.Select(o => o.Label).Should().Equal("出さない", "構成だけ", "見出し＋N 行", "全部", "範囲を指定");
    }

    [AvaloniaFact(DisplayName = "全体が構成だけ・出さないのとき、シートごとの一覧は出ない（シートごとの見せ方は効かない）")]
    public async Task 全体が構成だけなら一覧は出ない()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);

        await context.CycleWorkbookInfoLevelAsync().ConfigureAwait(true);
        context.WorkbookInfo.ShowSheetViews.Should().BeFalse();
        await context.CycleWorkbookInfoLevelAsync().ConfigureAwait(true);
        context.WorkbookInfo.ShowSheetViews.Should().BeFalse();
        await context.CycleWorkbookInfoLevelAsync().ConfigureAwait(true);
        context.WorkbookInfo.ShowSheetViews.Should().BeTrue();
    }

    // ---------------- 確認ダイアログ ----------------

    [AvaloniaFact(DisplayName = "値が渡る見せ方を選ぶと確認が出る（ボタンは動詞、既定は「やめる」）。シート名と渡る範囲が文面に出る")]
    public async Task 確認ダイアログの形()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        r.ChoiceAnswer = null; // 何も選ばずに閉じた（Enter・Esc・×）のと同じ。

        var applied = await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.HeaderRows).ConfigureAwait(true);

        applied.Should().BeFalse("既定のまま閉じたので、何も変えない");
        var request = r.Choices.Should().ContainSingle().Subject;
        request.Title.Should().Contain("集計");
        request.Message.Should().Contain("見出し行（1 行目）と、続く 5 行").And.Contain("AI に渡ります").And.Contain("社外秘");
        request.Options.Select(o => o.Label).Should().Equal("値を渡す", "やめる");
        request.Default.Label.Should().Be("やめる");
        request.Options.Count(o => o.IsDefault).Should().Be(1);
        r.H.Dialogs.Confirms.Should().BeEmpty("OK／キャンセルの確認ではなく、動詞のボタンの選択ダイアログを使う");
        Row(context, "Sheet1").SelectedMode.Mode.Should().Be(VbaSheetViewMode.StructureOnly);
        (await StoredLinkAsync(r).ConfigureAwait(true)).WorkbookInfoSheetViews.Should().BeNull();
    }

    [AvaloniaFact(DisplayName = "確認の文面は見せ方で変わる: 全部は上限（行・列）、範囲を指定は範囲の入力")]
    public async Task 確認の文面は見せ方で変わる()
    {
        using var r = await NewRigAsync(settings: Wide(300, 40)).ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        r.ChoiceAnswer = _ => "cancel";

        await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.All).ConfigureAwait(true);
        await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.Range).ConfigureAwait(true);

        r.Choices.Should().HaveCount(2);
        r.Choices[0].Message.Should().Contain("使用範囲の値すべて（最大 300 行・40 列）");
        r.Choices[1].Message.Should().Contain("範囲の値が");
    }

    [AvaloniaFact(DisplayName = "確認を断ると、見せ方も保存も取得も変わらず、一覧の選択も元に戻る")]
    public async Task 確認を断れば変わらない()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        r.ChoiceAnswer = _ => "cancel";
        var calls = InfoCalls(r);
        var row = Row(context, "Sheet1");

        // 画面の操作（ComboBox の選択）と同じ経路: 行の SelectedMode を変える。
        row.SelectedMode = VbaSheetModeOption.Of(VbaSheetViewMode.All);
        await ShellHarness.WaitUntilAsync(() => r.Choices.Count == 1 && row.SelectedMode.Mode == VbaSheetViewMode.StructureOnly).ConfigureAwait(true);

        row.SelectedMode.Mode.Should().Be(VbaSheetViewMode.StructureOnly, "断られたので、画面の選択も元に戻る");
        InfoCalls(r).Should().Be(calls, "断ったので取り直さない");
        (await StoredLinkAsync(r).ConfigureAwait(true)).WorkbookInfoSheetViews.Should().BeNull();
        context.WorkbookInfo.IsValueNoticeVisible.Should().BeFalse();
    }

    [AvaloniaFact(DisplayName = "確認: 選択ダイアログが配線されていない環境でも、確認を省略せず、値を渡す見せ方にしない")]
    public async Task 確認が配線されていなければ値を渡さない()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        r.Graft.ChooseVbaAsync = null;

        (await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.All).ConfigureAwait(true)).Should().BeFalse();

        Row(context, "Sheet1").SelectedMode.Mode.Should().Be(VbaSheetViewMode.StructureOnly);
    }

    [AvaloniaFact(DisplayName = "確認: 値が渡らない見せ方（出さない・構成だけ）を選ぶときは、確認を出さない")]
    public async Task 値が渡らない見せ方は確認なし()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);

        await context.SetSheetViewModeAsync("Sheet2", VbaSheetViewMode.None).ConfigureAwait(true);
        await context.SetSheetViewModeAsync("Sheet2", VbaSheetViewMode.StructureOnly).ConfigureAwait(true);

        r.Choices.Should().BeEmpty();
    }

    [AvaloniaFact(DisplayName = "確認: 実際の窓で、Enter の既定のボタンは「やめる」（primary）になる")]
    public async Task 確認の窓の既定のボタン()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        r.ChoiceAnswer = null;
        await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.HeaderRows).ConfigureAwait(true);
        var request = r.Choices.Should().ContainSingle().Subject;

        var window = new Graft.Views.VbaChoiceWindow(request);
        try
        {
            window.Show();
            var buttons = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Avalonia.Controls.Button>().ToList();

            buttons.Select(b => b.Content).Should().Equal("値を渡す", "やめる");
            buttons.Single(b => b.IsDefault).Content.Should().Be("やめる");
        }
        finally
        {
            window.Close();
        }
    }

    // ---------------- 見せ方ごとの出力 ----------------

    [AvaloniaFact(DisplayName = "見出し＋N 行: そのシートだけ値が出る。注意が常時出て、Excel のセルを読んだのもそのシートだけ")]
    public async Task 見出しとN行()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);

        (await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.HeaderRows).ConfigureAwait(true)).Should().BeTrue();

        var text = await CollectedTextAsync(r).ConfigureAwait(true);
        text.Should().Contain("## シートの値（AI に渡ります）").And.Contain("### 集計（Sheet1）: 見出し＋2 行");
        text.Should().Contain("| 日付 | 顧客 | 金額 |").And.Contain("| 46000 | SECRET-甲社 | 2500 |");
        text.Should().NotContain("キー | 値", "Sheet2 は構成だけ");
        r.Bridge.InfoValueReads.Last().Should().BeEquivalentTo(new Dictionary<string, VbaSheetViewMode> { ["Sheet1"] = VbaSheetViewMode.HeaderRows });
        context.WorkbookInfo.ValueNoticeText.Should().Contain("1 枚").And.Contain("集計");
        context.WorkbookInfo.IsValueNoticeVisible.Should().BeTrue();
        var stored = (await StoredLinkAsync(r).ConfigureAwait(true)).WorkbookInfoSheetViews!.Should().ContainSingle().Subject;
        (stored.CodeName, stored.Mode, stored.HeaderRows).Should().Be(("Sheet1", "HeaderRows", 5));
    }

    [AvaloniaFact(DisplayName = "見出し＋N 行の N は、シートごとに選べる。変えると取り直され、設定の N の既定とは別に保存される")]
    public async Task NはシートごとにきめられるN()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.HeaderRows).ConfigureAwait(true);
        var calls = InfoCalls(r);

        (await context.SetSheetHeaderRowsAsync("Sheet1", "1").ConfigureAwait(true)).Should().BeNull();

        InfoCalls(r).Should().Be(calls + 1);
        r.Bridge.InfoRequests.Last().Views.For("Sheet1").HeaderRows.Should().Be(1);
        var text = await CollectedTextAsync(r).ConfigureAwait(true);
        text.Should().Contain("見出し＋1 行").And.Contain("SECRET-甲社").And.NotContain("SECRET-乙社");
        (await StoredLinkAsync(r).ConfigureAwait(true)).WorkbookInfoSheetViews!.Single().HeaderRows.Should().Be(1);

        (await context.SetSheetHeaderRowsAsync("Sheet1", "0").ConfigureAwait(true)).Should().BeNull();
        (await CollectedTextAsync(r).ConfigureAwait(true)).Should().Contain("見出し＋0 行").And.NotContain("SECRET");
    }

    [AvaloniaFact(DisplayName = "見出し＋N 行の N の入力: 整数以外・負・上限超過は入力欄で知らせ、保存も取り直しもしない")]
    public async Task Nの入力を検証する()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.HeaderRows).ConfigureAwait(true);
        var calls = InfoCalls(r);

        foreach (var bad in new[] { "", "abc", "-1", "1.5", "101", "99999999999" })
        {
            (await context.SetSheetHeaderRowsAsync("Sheet1", bad).ConfigureAwait(true)).Should().Contain("0〜100", $"入力 \"{bad}\" は誤り");
        }

        InfoCalls(r).Should().Be(calls);
        (await StoredLinkAsync(r).ConfigureAwait(true)).WorkbookInfoSheetViews!.Single().HeaderRows.Should().Be(5);

        // 画面の入力欄（行の RowsText）からも、同じ検証が働く。
        var row = Row(context, "Sheet1");
        row.RowsText = "abc";
        await ShellHarness.WaitUntilAsync(() => row.HasRowsError).ConfigureAwait(true);
        row.RowsError.Should().Contain("0〜100");
    }

    [AvaloniaFact(DisplayName = "全部: 使用範囲の値が、行番号と列の文字つきの表で出る。読んだのはそのシートだけ")]
    public async Task 全部()
    {
        using var r = await NewRigAsync(w => Big(w, rows: 4, columns: 3), Wide()).ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);

        await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.All).ConfigureAwait(true);

        var text = await CollectedTextAsync(r).ConfigureAwait(true);
        text.Should().Contain("### 集計（Sheet1）: 全部").And.Contain("| 行 | A | B | C |");
        text.Should().Contain("| 1 | r1-1 | r1-2 | r1-3 |").And.Contain("| 4 | r4-1 | r4-2 | r4-3 |");
        text.Should().NotContain("切りました");
        r.Bridge.InfoValueReads.Last().Should().BeEquivalentTo(new Dictionary<string, VbaSheetViewMode> { ["Sheet1"] = VbaSheetViewMode.All });
    }

    [AvaloniaFact(DisplayName = "全部の上限: 既定の 1 シート 500 行・50 列で切り、何行・何列のうち何行・何列かと「範囲を指定」の案内を依頼文に明記する。画面にも同じ事実が出る")]
    public async Task 全部の上限で切る()
    {
        using var r = await NewRigAsync(w => Big(w, rows: 1200, columns: 60), Wide()).ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);

        await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.All).ConfigureAwait(true);

        r.Bridge.InfoRequests.Last().AllMaxRows.Should().Be(500);
        r.Bridge.InfoRequests.Last().AllMaxColumns.Should().Be(50);
        var text = await CollectedTextAsync(r).ConfigureAwait(true);
        // 読む行数の上限（500 行）と、依頼文の文字数の上限が重なっても、注記は実際に出した行数とアドレスで言う（不具合 9）。
        var shown = System.Text.RegularExpressions.Regex.Match(text, @"対象は 1200 行・60 列ありますが、先頭の (\d+) 行・50 列（A1:AX(\d+)）だけを出しています");
        shown.Success.Should().BeTrue("読んだ行数ではなく、出した行数で書く");
        shown.Groups[1].Value.Should().Be(shown.Groups[2].Value);
        var shownRows = int.Parse(shown.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        shownRows.Should().BeInRange(1, 500);
        text.Should().Contain("「範囲を指定」").And.Contain($"例 A{shownRows + 1}:AX{shownRows * 2}");
        text.Should().NotContain($"| {shownRows + 1} | r{shownRows + 1}-1 |").And.NotContain("r1-51 ", "上限を超える行・列は読まない");
        Row(context, "Sheet1").NoteText.Should().Contain("1200 行・60 列のうち、500 行・50 列だけを渡しています").And.Contain("範囲を指定");
    }

    [AvaloniaFact(DisplayName = "全部の上限は設定で変えられる（行・列）。変えた値が読み取りの指示にも出力にも効く")]
    public async Task 全部の上限は設定で変えられる()
    {
        using var r = await NewRigAsync(w => Big(w, rows: 300, columns: 30), Wide(100, 20)).ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);

        await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.All).ConfigureAwait(true);

        r.Bridge.InfoRequests.Last().AllMaxRows.Should().Be(100);
        var text = await CollectedTextAsync(r).ConfigureAwait(true);
        text.Should().Contain("対象は 300 行・30 列ありますが、先頭の 100 行・20 列");
        text.Should().Contain("| 100 | r100-1 |").And.NotContain("| 101 | r101-1 |");
    }

    [AvaloniaFact(DisplayName = "範囲を指定: 入力した範囲の値だけが、範囲の行番号・列の文字で出る")]
    public async Task 範囲を指定()
    {
        using var r = await NewRigAsync(w => Big(w, rows: 50, columns: 8), Wide()).ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.Range).ConfigureAwait(true);

        (await context.SetSheetRangeAsync("Sheet1", " c10 : $E12 ").ConfigureAwait(true)).Should().BeNull();

        var text = await CollectedTextAsync(r).ConfigureAwait(true);
        text.Should().Contain("### 集計（Sheet1）: 範囲 C10:E12").And.Contain("| 行 | C | D | E |");
        text.Should().Contain("| 10 | r10-3 | r10-4 | r10-5 |").And.Contain("| 12 | r12-3 | r12-4 | r12-5 |");
        text.Should().NotContain("r9-3").And.NotContain("r13-3").And.NotContain("r10-2 ");
        (await StoredLinkAsync(r).ConfigureAwait(true)).WorkbookInfoSheetViews!.Single().Range.Should().Be("C10:E12", "正規化して保存する");
    }

    [AvaloniaFact(DisplayName = "範囲を指定: 列範囲 A:C は使用範囲の行まで。範囲が使用範囲の外なら、その旨だけが出る")]
    public async Task 範囲の列指定と範囲外()
    {
        using var r = await NewRigAsync(w => Big(w, rows: 5, columns: 8), Wide()).ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.Range).ConfigureAwait(true);

        await context.SetSheetRangeAsync("Sheet1", "A:B").ConfigureAwait(true);
        var columns = await CollectedTextAsync(r).ConfigureAwait(true);
        columns.Should().Contain("範囲 A:B").And.Contain("| 5 | r5-1 | r5-2 |").And.NotContain("| 6 |");

        await context.SetSheetRangeAsync("Sheet1", "Z100:Z200").ConfigureAwait(true);
        var outside = await CollectedTextAsync(r).ConfigureAwait(true);
        outside.Should().Contain("範囲 Z100:Z200").And.Contain("使用範囲 A1:H5 の外で、値はありません");
        Row(context, "Sheet1").NoteText.Should().Contain("使用範囲").And.Contain("外");
    }

    [AvaloniaFact(DisplayName = "構成だけ: シート名・コード名・使用範囲・テーブル名と列名は出て、値は出ない")]
    public async Task 構成だけ()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);

        var text = await CollectedTextAsync(r).ConfigureAwait(true);

        text.Should().Contain("| 集計 | Sheet1 | 表示 | A1:C20 |").And.Contain("- 売上（シート「集計」 A1:C20）: 日付, 顧客, 金額");
        text.Should().NotContain("SECRET").And.NotContain("## シートの値");
    }

    [AvaloniaFact(DisplayName = "出さない: そのシートは何も渡らない（シート行・テーブル・そのシートを指す名前も）。そのシートのテーブルは Excel から読まない")]
    public async Task 出さない()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);

        await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.None).ConfigureAwait(true);
        await context.FetchWorkbookInfoAsync().ConfigureAwait(true); // 取り直して、テーブルを読んだシートを確かめる。

        var text = await CollectedTextAsync(r).ConfigureAwait(true);
        text.Should().Contain("| 設定 | Sheet2 |");
        text.Should().NotContain("| 集計 |").And.NotContain("売上").And.NotContain("| Sheet1 |");
        r.Bridge.InfoTableReads.Last().Should().BeEquivalentTo(new[] { "設定" }, "「出さない」のシートのテーブルは読まない");
        Row(context, "Sheet1").SelectedMode.Mode.Should().Be(VbaSheetViewMode.None, "一覧には残る（戻せるように）");

        await context.SetSheetViewModeAsync("Sheet2", VbaSheetViewMode.None).ConfigureAwait(true);
        var names = await CollectedTextAsync(r).ConfigureAwait(true);
        names.Should().NotContain("税率", "出さないシートを指す名前は出ない");
    }

    [AvaloniaFact(DisplayName = "出さないから戻すと、テーブルを取り直して出る")]
    public async Task 出さないから戻す()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.None).ConfigureAwait(true);
        var calls = InfoCalls(r);

        await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.StructureOnly).ConfigureAwait(true);

        InfoCalls(r).Should().Be(calls + 1);
        (await CollectedTextAsync(r).ConfigureAwait(true)).Should().Contain("売上");
    }

    // ---------------- 数式 ----------------

    [AvaloniaFact(DisplayName = "数式: シートごとに選べる。選んだシートだけ数式つきで読み、オフにしたら数式入りの値を直ちに捨てる（取得に失敗しても出ない）")]
    public async Task 数式はシートごと()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.HeaderRows).ConfigureAwait(true);
        (await CollectedTextAsync(r).ConfigureAwait(true)).Should().NotContain("=B2*C2", "既定は数式なし（値だけ）").And.Contain("| 46000 | SECRET-甲社 | 2500 |");

        await context.SetSheetIncludeFormulasAsync("Sheet1", true).ConfigureAwait(true);

        r.Bridge.InfoRequests.Last().Views.For("Sheet1").IncludeFormulas.Should().BeTrue();
        r.Bridge.InfoRequests.Last().Views.For("Sheet2").IncludeFormulas.Should().BeFalse();
        (await CollectedTextAsync(r).ConfigureAwait(true)).Should().Contain("=B2*C2 → 2500");

        // Excel を閉じて取り直しが失敗する状態にしてから、数式をオフにする。
        r.Bridge.CloseWorkbook(VbaP3Rig.Book);
        await context.SetSheetIncludeFormulasAsync("Sheet1", false).ConfigureAwait(true);

        context.WorkbookInfoWarning.Should().Contain("E804", "取り直しは失敗している");
        var text = await CollectedTextAsync(r).ConfigureAwait(true);
        text.Should().NotContain("=B2*C2", "オフにした数式が、前回の成功分から出続けてはならない").And.NotContain("SECRET");
        text.Should().Contain("| 集計 | Sheet1 |", "構成は前回の成功分を使い続ける");
    }

    // ---------------- 範囲の検証 ----------------

    [AvaloniaFact(DisplayName = "範囲の検証: 不正な入力は理由を返し、依頼文に入らない。名前付き範囲・シート名つき・空は不可。以前の正しい範囲も取り消す")]
    public async Task 範囲の検証()
    {
        using var r = await NewRigAsync(w => Big(w, rows: 20, columns: 5), Wide()).ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.Range).ConfigureAwait(true);
        await context.SetSheetRangeAsync("Sheet1", "A1:B2").ConfigureAwait(true);
        (await CollectedTextAsync(r).ConfigureAwait(true)).Should().Contain("範囲 A1:B2", "前提: 正しい範囲は入る");

        (await context.SetSheetRangeAsync("Sheet1", "売上").ConfigureAwait(true)).Should().Contain("名前付き範囲は指定できません");
        var afterBad = await CollectedTextAsync(r).ConfigureAwait(true);
        afterBad.Should().NotContain("## シートの値", "不正な入力は依頼文に入れない。画面と、実際に渡るものを食い違わせない");
        afterBad.Should().NotContain("r1-1", "それまでの範囲の値も出ない");
        (await StoredLinkAsync(r).ConfigureAwait(true)).WorkbookInfoSheetViews!.Single().Range.Should().BeNull();

        (await context.SetSheetRangeAsync("Sheet1", "Sheet1!A1:B2").ConfigureAwait(true)).Should().Contain("シート名は付けずに");
        (await context.SetSheetRangeAsync("Sheet1", "").ConfigureAwait(true)).Should().Contain("範囲を入力してください");
        (await context.SetSheetRangeAsync("Sheet1", "A0:B2").ConfigureAwait(true)).Should().Contain("行は 1〜");
        (await context.SetSheetRangeAsync("Sheet1", "A1:B3").ConfigureAwait(true)).Should().BeNull();
        (await CollectedTextAsync(r).ConfigureAwait(true)).Should().Contain("範囲 A1:B3");
    }

    [AvaloniaFact(DisplayName = "範囲の入力欄: 行の RangeText に不正な値を入れると誤りが出て、打った文字は残る。正しく入れると誤りが消える")]
    public async Task 範囲の入力欄()
    {
        using var r = await NewRigAsync(w => Big(w, rows: 20, columns: 5), Wide()).ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.Range).ConfigureAwait(true);
        var row = Row(context, "Sheet1");
        row.HasRangeError.Should().BeTrue("範囲が空のあいだは、渡らないことを入力欄で知らせる");

        row.RangeText = "売上";
        await ShellHarness.WaitUntilAsync(() => row.RangeError.Contains("名前付き範囲")).ConfigureAwait(true);
        row.RangeText.Should().Be("売上", "打った文字は消さない");
        row.PassesValues.Should().BeFalse();

        row.RangeText = "b2:c4";
        await ShellHarness.WaitUntilAsync(() => !row.HasRangeError).ConfigureAwait(true);
        row.HasRangeError.Should().BeFalse();
        (await CollectedTextAsync(r).ConfigureAwait(true)).Should().Contain("範囲 B2:C4");
    }

    // ---------------- 読み取り ----------------

    [AvaloniaFact(DisplayName = "値が要るシートだけ読む: 見せ方を選んだシート以外は、セルを読まない（Fake が読んだ要求を記録）。構成だけに戻すと読まなくなる")]
    public async Task 値が要るシートだけ読む()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        r.Bridge.InfoValueReads.Should().OnlyContain(reads => reads.Count == 0);

        await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.HeaderRows).ConfigureAwait(true);
        r.Bridge.InfoValueReads.Last().Keys.Should().Equal("Sheet1");

        await context.SetSheetViewModeAsync("Sheet2", VbaSheetViewMode.All).ConfigureAwait(true);
        r.Bridge.InfoValueReads.Last().Keys.Should().BeEquivalentTo(new[] { "Sheet1", "Sheet2" });

        var before = InfoCalls(r);
        await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.StructureOnly).ConfigureAwait(true);
        InfoCalls(r).Should().Be(before, "値が渡らなくなる変更では、取り直さない（メモリの値を捨てるだけ）");
        await context.FetchWorkbookInfoAsync().ConfigureAwait(true);
        r.Bridge.InfoValueReads.Last().Keys.Should().Equal("Sheet2");
    }

    [AvaloniaFact(DisplayName = "全体を構成だけにすると、値が渡る見せ方でもセルを読まず、依頼文にも値が出ない（見せ方の記録は残る）")]
    public async Task 全体が構成だけなら値を読まない()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.HeaderRows).ConfigureAwait(true);

        await context.CycleWorkbookInfoLevelAsync().ConfigureAwait(true); // 構成だけ
        await context.FetchWorkbookInfoAsync().ConfigureAwait(true);

        r.Bridge.InfoValueReads.Last().Should().BeEmpty();
        (await CollectedTextAsync(r).ConfigureAwait(true)).Should().NotContain("SECRET");
        (await StoredLinkAsync(r).ConfigureAwait(true)).WorkbookInfoSheetViews!.Single().Mode.Should().Be("HeaderRows");
    }

    // ---------------- オフにしたら捨てる ----------------

    [AvaloniaFact(DisplayName = "値が渡らない見せ方へ戻すと、メモリの値は直ちに消え（取得に失敗しても出ず）、プレビュー欄も空になる")]
    public async Task 戻したら値もプレビューも消える()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.HeaderRows).ConfigureAwait(true);
        await ShellHarness.ExecuteAsync(context.PreviewCommand).ConfigureAwait(true);
        context.PreviewLines.Any(l => l.Text.Contains("SECRET")).Should().BeTrue("前提: 渡している間はプレビューに値が出る");

        r.Bridge.CloseWorkbook(VbaP3Rig.Book); // 取り直しが失敗する状態でも、捨てる。
        await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.StructureOnly).ConfigureAwait(true);

        context.PreviewLines.Should().BeEmpty("戻したら直ちに捨てる、と案内している");
        context.BuildWorkbookInfoText().Should().NotContain("SECRET");
        context.WorkbookInfo.IsValueNoticeVisible.Should().BeFalse();

        context.Dispose();
        context.PreviewLines.Should().BeEmpty();
    }

    [AvaloniaFact(DisplayName = "全体を構成だけ・出さないへ戻すと、メモリの値も消える（Excel を閉じて取り直せなくても、内容も出すに戻した出力に値が出ない）")]
    public async Task 全体を戻したら値が消える()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.HeaderRows).ConfigureAwait(true);
        await ShellHarness.ExecuteAsync(context.PreviewCommand).ConfigureAwait(true);

        await context.CycleWorkbookInfoLevelAsync().ConfigureAwait(true); // 構成だけ

        context.PreviewLines.Should().BeEmpty();
        r.Bridge.CloseWorkbook(VbaP3Rig.Book);
        await context.CycleWorkbookInfoLevelAsync().ConfigureAwait(true); // 出さない
        await context.CycleWorkbookInfoLevelAsync().ConfigureAwait(true); // 内容も出す（取り直しは失敗する）
        context.BuildWorkbookInfoText().Should().NotContain("SECRET", "メモリに値を持ち続けない");
    }

    // ---------------- 設定で入らない ----------------

    [AvaloniaFact(DisplayName = "設定で「ブックの情報の取得」をオフにした直後のクイックコピーに、値が渡るシートの内容は出ない")]
    public async Task 取得オフの直後のクイックコピー()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        await r.Graft.ContextCollect!.InitializeAsync().ConfigureAwait(true);
        await r.Graft.ContextCollect!.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.All).ConfigureAwait(true);
        await r.Graft.PromptCopy!.QuickCopyAsync().ConfigureAwait(true);
        r.H.Clipboard.Text.Should().Contain("SECRET", "前提: オフにする前は値が入る");

        r.Graft.UpdateSettings(r.Graft.CurrentSettings with { Vba = r.Graft.CurrentSettings.Vba with { CollectWorkbookInfo = false } });
        await r.Graft.PromptCopy!.QuickCopyAsync().ConfigureAwait(true);

        r.H.Clipboard.Text!.Should().NotContain("# ブックの情報").And.NotContain("SECRET");
    }

    [AvaloniaFact(DisplayName = "設定で VBA 連携そのものをオフにした直後のクイックコピーにも、値が渡るシートの内容は出ない")]
    public async Task VBA連携オフの直後のクイックコピー()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        await r.Graft.ContextCollect!.InitializeAsync().ConfigureAwait(true);
        await r.Graft.ContextCollect!.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.HeaderRows).ConfigureAwait(true);
        await r.Graft.PromptCopy!.QuickCopyAsync().ConfigureAwait(true);
        r.H.Clipboard.Text.Should().Contain("SECRET");

        r.Graft.UpdateSettings(r.Graft.CurrentSettings with { Vba = r.Graft.CurrentSettings.Vba with { Enabled = false } });
        await r.Graft.PromptCopy!.QuickCopyAsync().ConfigureAwait(true);

        r.H.Clipboard.Text!.Should().NotContain("# ブックの情報").And.NotContain("SECRET");
    }

    [AvaloniaFact(DisplayName = "観点を何も選ばないとき、ブックの情報が入らない状態（出さない／取得オフ）の依頼文は同一で、ブックの情報の痕跡が無い")]
    public async Task 観点なしの依頼文は同一()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.All).ConfigureAwait(true);
        await context.CycleWorkbookInfoLevelAsync().ConfigureAwait(true);
        await context.CycleWorkbookInfoLevelAsync().ConfigureAwait(true); // 出さない
        await r.Graft.PromptCopy!.QuickCopyAsync().ConfigureAwait(true);
        var hidden = r.H.Clipboard.Text!;

        r.Graft.UpdateSettings(r.Graft.CurrentSettings with { Vba = r.Graft.CurrentSettings.Vba with { CollectWorkbookInfo = false } });
        await r.Graft.PromptCopy!.QuickCopyAsync().ConfigureAwait(true);
        var off = r.H.Clipboard.Text!;

        hidden.Should().Be(off, "ブックの情報が入らなければ、見せ方を選んでいても依頼文は変わらない");
        hidden.Should().NotContain("ブックの情報").And.NotContain("# 依頼の観点").And.NotContain("SECRET");
    }

    // ---------------- 記憶 ----------------

    /// <summary>保存済みのプロジェクトから、コンテキスト収集を新しく作る（プロジェクトを開き直したのと同じ）。</summary>
    private static async Task<ContextCollectViewModel> ReopenAsync(VbaP3Rig r, int sampleRows = 5)
    {
        var project = (await new ProjectStore(r.H.AppPaths).LoadAsync().ConfigureAwait(true)).Value.First(p => p.Id == r.Project.Id);
        var source = new VbaWorkbookInfoSource
        {
            IsAvailable = () => true,
            SampleRows = () => sampleRows,
            FetchAsync = async (views, ct) => await r.Bridge.ReadWorkbookInfoAsync(
                VbaP3Rig.Book, new VbaWorkbookInfoOptions(true, views, 30, 60, 100, TimeSpan.FromSeconds(20)), ct).ConfigureAwait(true),
            ChooseAsync = request => Task.FromResult("give"),
        };
        return new ContextCollectViewModel(
            r.H.AppPaths, new ProjectStore(r.H.AppPaths), project, new Settings(), new FakeUiServices(r.H.Clipboard), r.H.Dialogs,
            vbaWorkbookInfo: source);
    }

    [AvaloniaFact(DisplayName = "記憶: プロジェクトごとに保存され、開き直しても同じ見せ方で読まれる。新しい取得でも、値が渡るシートだけ読む")]
    public async Task 開き直しても覚えている()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.HeaderRows).ConfigureAwait(true);
        await context.SetSheetHeaderRowsAsync("Sheet1", 1).ConfigureAwait(true);
        await context.SetSheetViewModeAsync("Sheet2", VbaSheetViewMode.None).ConfigureAwait(true);

        var reopened = await ReopenAsync(r).ConfigureAwait(true);
        r.Bridge.InfoValueReads.Clear();
        await reopened.FetchWorkbookInfoAsync().ConfigureAwait(true);

        Row(reopened, "Sheet1").SelectedMode.Mode.Should().Be(VbaSheetViewMode.HeaderRows);
        Row(reopened, "Sheet1").RowsText.Should().Be("1");
        Row(reopened, "Sheet2").SelectedMode.Mode.Should().Be(VbaSheetViewMode.None);
        r.Bridge.InfoValueReads.Last().Keys.Should().Equal("Sheet1");
        var text = reopened.BuildWorkbookInfoText();
        text.Should().Contain("SECRET-甲社").And.NotContain("SECRET-乙社").And.NotContain("| 設定 |");
    }

    [AvaloniaFact(DisplayName = "記憶はシートのコード名に結び付く: 表示名を変えると、値が渡る見せ方は構成だけに戻って再確認を求める（不具合 4）。「出さない」の記録は、消えたシートの分も残るが画面には出ない")]
    public async Task 表示名を変えたら値の見せ方は戻る()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.HeaderRows).ConfigureAwait(true);
        await context.SetSheetViewModeAsync("Sheet2", VbaSheetViewMode.None).ConfigureAwait(true);

        // Excel 側で「集計」を「集計 2027」に改名し、「設定」シートを削除した。
        var book = r.Bridge.Get(VbaP3Rig.Book)!;
        var renamed = WorkbookInfoTests.Sample();
        book.Info = renamed with
        {
            Sheets = new[] { renamed.Sheets[0] with { Name = "集計 2027" } },
            Tables = Array.Empty<VbaTableInfo>(),
            Names = Array.Empty<VbaNamedRangeInfo>(),
        };
        await context.FetchWorkbookInfoAsync().ConfigureAwait(true);

        // 表示名を変えただけなのか、別のシートが同じコード名になったのかは、記録からは区別できない。安全側（再確認）に倒す。
        Row(context, "Sheet1").Name.Should().Be("集計 2027");
        Row(context, "Sheet1").SelectedMode.Mode.Should().Be(VbaSheetViewMode.StructureOnly, "表示名が食い違うので、値は確認なしでは渡さない");
        context.BuildWorkbookInfoText().Should().NotContain("SECRET-甲社").And.NotContain("見出し＋");
        context.WorkbookInfoWarning.Should().Contain("集計 2027").And.Contain("選び直してください");
        context.WorkbookInfo.Sheets.Select(s => s.CodeName).Should().Equal("Sheet1");
        (await StoredLinkAsync(r).ConfigureAwait(true)).WorkbookInfoSheetViews!.Select(v => v.CodeName)
            .Should().Equal(new[] { "Sheet2" }, "値が渡る記録（Sheet1）は落とし、消えたシートの「出さない」の記録は残す");

        // 選び直せば（確認のうえ）また渡せる。記録には新しい表示名が添えられる。
        (await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.HeaderRows).ConfigureAwait(true)).Should().BeTrue();
        r.Choices.Should().HaveCount(2, "最初の選択と、選び直しの 2 回、確認が出た");
        context.BuildWorkbookInfoText().Should().Contain("### 集計 2027（Sheet1）: 見出し＋2 行");
        (await StoredLinkAsync(r).ConfigureAwait(true)).WorkbookInfoSheetViews!.Single(v => v.CodeName == "Sheet1").SheetName.Should().Be("集計 2027");
    }

    [AvaloniaFact(DisplayName = "別のブックへ結び直したとき（実際の追加の経路）、値が渡る見せ方も出さないも戻り、確認をやり直す")]
    public async Task 結び直しで戻る()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var pane = r.Graft.ProjectPane;
        await r.UpdateLinkAsync(l => l
            .WithSheetViews(new VbaSheetViewSet(VbaSheetView.StructureOnly, new[]
            {
                new KeyValuePair<string, VbaSheetView>("Sheet1", new VbaSheetView(VbaSheetViewMode.All)),
                new KeyValuePair<string, VbaSheetView>("Sheet2", VbaSheetView.Excluded),
            })) with
        {
            WorkbookFullName = @"C:\Old\計画.xlsm",
            AutoPushOnSave = true,
        }).ConfigureAwait(true);
        pane.Items.First(i => i.Project.Id == r.Project.Id).Project.Vba!.WorkbookInfoSheetViews.Should().HaveCount(2, "前提");

        await ShellHarness.ExecuteAsync(pane.AddFromWorkbookCommand).ConfigureAwait(true);

        var vba = pane.Items.First(i => i.Project.Id == r.Project.Id).Project.Vba!;
        vba.WorkbookFullName.Should().Be(VbaP3Rig.Book);
        vba.ResolveSheetViews(5).For("Sheet1").PassesValues.Should().BeFalse("新しいブックでは、値を渡す確認をやり直す");
        vba.ResolveSheetViews(5).For("Sheet2").Mode.Should().Be(VbaSheetViewMode.StructureOnly);
        vba.AutoPushOnSave.Should().BeTrue("ほかの設定は引き継ぐ");
    }

    // ---------------- 旧「値の見本」からの移行 ----------------

    [AvaloniaFact(DisplayName = "移行: 値の見本がオンだった既存のプロジェクトは、全シート「見出し＋既存の N 行」として扱われ、確認なしで値が渡り続ける")]
    public async Task 値の見本からの移行()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        await r.UpdateLinkAsync(l => l with { WorkbookInfoSampleValues = true }).ConfigureAwait(true);
        var context = await ReopenAsync(r, sampleRows: 1).ConfigureAwait(true); // 設定の行数は 1

        await context.FetchWorkbookInfoAsync().ConfigureAwait(true);

        var views = r.Bridge.InfoRequests.Last().Views;
        views.For("Sheet1").Should().Be(new VbaSheetView(VbaSheetViewMode.HeaderRows, 1), "既存の N（設定の行数）で、全シート");
        views.For("Sheet2").Mode.Should().Be(VbaSheetViewMode.HeaderRows);
        Row(context, "Sheet1").SelectedMode.Mode.Should().Be(VbaSheetViewMode.HeaderRows);
        Row(context, "Sheet1").RowsText.Should().Be("1");
        context.WorkbookInfo.ValueSheetCount.Should().Be(2);
        r.Choices.Should().BeEmpty("以前に確認を済ませているので、移行では確認を出さない");
        var text = context.BuildWorkbookInfoText();
        text.Should().Contain("SECRET-甲社").And.NotContain("SECRET-乙社").And.Contain("| キー | 値 |");
    }

    [AvaloniaFact(DisplayName = "移行: 数式を含める選択も全シートで引き継ぐ。1 枚だけ変えると、残りの移行結果が確定して保存され、旧項目はオフに戻る")]
    public async Task 移行の確定()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        await r.UpdateLinkAsync(l => l with { WorkbookInfoSampleValues = true, WorkbookInfoIncludeFormulas = true }).ConfigureAwait(true);
        var context = await ReopenAsync(r).ConfigureAwait(true);
        await context.FetchWorkbookInfoAsync().ConfigureAwait(true);
        r.Bridge.InfoRequests.Last().Views.For("Sheet1").IncludeFormulas.Should().BeTrue();

        await context.SetSheetViewModeAsync("Sheet2", VbaSheetViewMode.StructureOnly).ConfigureAwait(true);

        var stored = await StoredLinkAsync(r).ConfigureAwait(true);
        stored.WorkbookInfoSampleValues.Should().BeFalse();
        stored.WorkbookInfoIncludeFormulas.Should().BeFalse();
        stored.WorkbookInfoSheetViews!.Should().ContainSingle("Sheet2 は構成だけ（既定）に戻したので記録なし。Sheet1 は移行結果のまま")
            .Which.Should().Match<VbaSheetViewSetting>(v => v.CodeName == "Sheet1" && v.Mode == "HeaderRows" && v.IncludeFormulas);
        Row(context, "Sheet1").SelectedMode.Mode.Should().Be(VbaSheetViewMode.HeaderRows);
        Row(context, "Sheet2").SelectedMode.Mode.Should().Be(VbaSheetViewMode.StructureOnly);

        // 全シートを構成だけに戻しても、旧項目から再び移行されない。
        await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.StructureOnly).ConfigureAwait(true);
        var reopened = await ReopenAsync(r).ConfigureAwait(true);
        await reopened.FetchWorkbookInfoAsync().ConfigureAwait(true);
        reopened.WorkbookInfo.ValueSheetCount.Should().Be(0);
        reopened.BuildWorkbookInfoText().Should().NotContain("SECRET");
    }

    [AvaloniaFact(DisplayName = "移行: 値の見本がオフだった既存のプロジェクトは、構成だけのまま（以前は渡っていた見出し行も渡らない）")]
    public async Task 見本オフは構成だけのまま()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        await r.UpdateLinkAsync(l => l with { WorkbookInfoSampleValues = false, WorkbookInfoIncludeFormulas = true }).ConfigureAwait(true);
        var context = await ReopenAsync(r).ConfigureAwait(true);

        await context.FetchWorkbookInfoAsync().ConfigureAwait(true);

        context.WorkbookInfo.ValueSheetCount.Should().Be(0);
        context.BuildWorkbookInfoText().Should().NotContain("日付 | 顧客").And.NotContain("SECRET");
    }
}
