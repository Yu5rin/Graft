using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Features;
using Graft.Infra;
using Graft.Platform.Null;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;

namespace Graft.UiTests.Vba;

/// <summary>
/// ブックの情報（機能 A。仕様書_ブックの情報と依頼の観点 5章・11.2）のシナリオ。
/// <see cref="FakeVbaBridge"/> のスナップショットが、依頼文の出力とコンテキスト収集の窓の出力に入ること、
/// 全体の 3 状態・シートごとの見せ方・取得失敗の扱いを、実際の起動と同じ依存グラフ（<see cref="ShellHarness"/>）で確かめる。
/// （v0.4 でブック全体の「値の見本」のオン・オフをシートごとの見せ方に一本化した。見せ方ごとの出力・記憶・移行・上限・範囲の検証は
/// <see cref="WorkbookInfoSheetViewTests"/>。）
/// </summary>
public class WorkbookInfoTests
{
    private static readonly DateTimeOffset ReadAt = new(2026, 11, 21, 10, 30, 0, TimeSpan.Zero);

    /// <summary>値（社外秘の想定）を含む、ブックの情報。値の合図に <c>SECRET</c> の文字を入れてある。</summary>
    internal static VbaWorkbookInfoSnapshot Sample(string workbookName = "計画.xlsm") => new(
        workbookName, ReadAt,
        new[]
        {
            new VbaSheetInfo("集計", "Sheet1", VbaSheetVisibility.Visible, "A1:C20",
                new[] { "日付", "顧客", "金額" },
                new IReadOnlyList<string>[] { new[] { "46000", "SECRET-甲社", "=B2*C2 → 2500" }, new[] { "46001", "SECRET-乙社", "300" } }),
            new VbaSheetInfo("設定", "Sheet2", VbaSheetVisibility.Hidden, "A1:B3",
                new[] { "キー", "値" }, Array.Empty<IReadOnlyList<string>>()),
        },
        new[] { new VbaTableInfo("売上", "集計", "A1:C20", new[] { "日付", "顧客", "金額" }) },
        new[] { new VbaNamedRangeInfo("税率", "=設定!$B$3", null) },
        new[] { new VbaReferenceInfo("VBA", "", null, true, false), new VbaReferenceInfo("Scripting", "Microsoft Scripting Runtime", @"C:\Windows\System32\scrrun.dll", false, false) },
        Array.Empty<string>());

    private static async Task<VbaP3Rig> NewRigAsync(Func<FakeVbaBridge.Workbook, VbaWorkbookInfoSnapshot?>? info = null, Settings? settings = null)
    {
        var rig = await VbaP3Rig.CreateAsync(
            settings ?? new Settings { ShowPreview = false },
            arrange: bridge => bridge.Get(VbaP3Rig.Book)!.Info = info is null ? Sample() : info(bridge.Get(VbaP3Rig.Book)!)).ConfigureAwait(true);
        // 値が渡る見せ方を選ぶ確認は、既定（Enter）が「やめる」の選択ダイアログ。値を渡したいテストは、
        // 利用者が「値を渡す」を押したことにする（断るテストだけ上書きする）。
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

    [AvaloniaFact(DisplayName = "収集の窓の出力: 取得したブックの情報が「# 前提」の後・構成の前に入る。既定は全シート構成だけ（値も見出し行も出ない）")]
    public async Task 収集の出力に入る()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);

        var text = await CollectedTextAsync(r).ConfigureAwait(true);

        context.WorkbookInfo.IsVisible.Should().BeTrue();
        context.WorkbookInfo.Level.Should().Be(VbaWorkbookInfoLevel.Full);
        text.Should().Contain("# ブックの情報").And.Contain("| 集計 | Sheet1 | 表示 | A1:C20 |").And.Contain("## 参照設定");
        text.Should().NotContain("## シートの値").And.NotContain("SECRET", "既定は全シート構成だけ。値は渡らない");
        text.Should().NotContain("日付 | 顧客 | 金額 |", "見出し行も、見せ方が値を渡すシートだけ");
        text.IndexOf("# ブックの情報", StringComparison.Ordinal).Should().BeLessThan(text.IndexOf("# プロジェクト構成", StringComparison.Ordinal));
    }

    [AvaloniaFact(DisplayName = "依頼文コピー: 窓を開かない経路でも、ブックの情報が依頼文に入る（観点の前）")]
    public async Task 依頼文に入る()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var copy = r.Graft.PromptCopy!;
        copy.Context.Focus.Groups.SelectMany(g => g.Chips).First(c => c.Id == "focus-readable").IsSelected = true;

        await copy.QuickCopyAsync().ConfigureAwait(true);

        var text = r.H.Clipboard.Text!;
        text.Should().Contain("# ブックの情報").And.Contain("# 依頼の観点");
        text.IndexOf("# ブックの情報", StringComparison.Ordinal).Should().BeLessThan(text.IndexOf("# 依頼の観点", StringComparison.Ordinal));
        text.Should().Contain("「ブックの情報」に載っているものだけ", "ブックの情報が入ったので、VBA の前提の最終行が切り替わる");
        text.Should().NotContain("確かめられないものを推測で使わず、質問してください。");
    }

    [AvaloniaFact(DisplayName = "3 状態: 内容も出す → 構成だけ（参照設定が消える）→ 出さない（何も出ず Excel も呼ばない）→ 内容も出す。選んだ状態は保存される")]
    public async Task 三状態の巡回()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        var calls = InfoCalls(r);

        await context.CycleWorkbookInfoLevelAsync().ConfigureAwait(true);
        context.WorkbookInfo.Level.Should().Be(VbaWorkbookInfoLevel.StructureOnly);
        var structure = await CollectedTextAsync(r).ConfigureAwait(true);
        structure.Should().Contain("## シート").And.Contain("## テーブル").And.Contain("## 名前付き範囲");
        structure.Should().NotContain("## 参照設定");
        (await StoredLinkAsync(r).ConfigureAwait(true)).WorkbookInfoState.Should().Be("StructureOnly");

        await context.CycleWorkbookInfoLevelAsync().ConfigureAwait(true);
        context.WorkbookInfo.Level.Should().Be(VbaWorkbookInfoLevel.Hidden);
        var hidden = await CollectedTextAsync(r).ConfigureAwait(true);
        hidden.Should().NotContain("ブックの情報");
        (await StoredLinkAsync(r).ConfigureAwait(true)).WorkbookInfoState.Should().Be("Hidden");
        await context.FetchWorkbookInfoAsync().ConfigureAwait(true);
        InfoCalls(r).Should().Be(calls, "出さない間は Excel に問い合わせない");

        await context.CycleWorkbookInfoLevelAsync().ConfigureAwait(true);
        context.WorkbookInfo.Level.Should().Be(VbaWorkbookInfoLevel.Full);
        InfoCalls(r).Should().Be(calls + 1, "出さないから戻したときは、取り直す");
        (await CollectedTextAsync(r).ConfigureAwait(true)).Should().Contain("## 参照設定");
    }

    [AvaloniaFact(DisplayName = "保存済みの状態（出さない）でプロジェクトを開くと、Excel に問い合わせず、何も出ない")]
    public async Task 保存済みの出さないでは呼ばない()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        // 保存済みの VbaLink（出さない）から、コンテキスト収集を新しく作る（プロジェクトを開き直したのと同じ）。
        var project = r.Project with { Vba = r.Project.Vba! with { WorkbookInfoState = "Hidden" } };
        var source = new VbaWorkbookInfoSource
        {
            IsAvailable = () => true,
            SampleRows = () => 5,
            FetchAsync = async (views, ct) => await r.Bridge.ReadWorkbookInfoAsync(
                VbaP3Rig.Book, new VbaWorkbookInfoOptions(true, views, 30, 60, 100, TimeSpan.FromSeconds(20)), ct).ConfigureAwait(true),
        };
        var context = new ContextCollectViewModel(
            r.H.AppPaths, new ProjectStore(r.H.AppPaths), project, new Settings(), new FakeUiServices(r.H.Clipboard), r.H.Dialogs,
            vbaWorkbookInfo: source);
        r.Bridge.Calls.Clear();

        await context.InitializeAsync().ConfigureAwait(true);

        context.WorkbookInfo.Level.Should().Be(VbaWorkbookInfoLevel.Hidden);
        InfoCalls(r).Should().Be(0);
        context.BuildWorkbookInfoText().Should().BeEmpty();
        context.WorkbookInfo.StatusText.Should().Contain("出さない");
    }

    [AvaloniaFact(DisplayName = "読み取りの指示: 既定では全シート構成だけで渡り、セルの値を読んだシートは 1 つも無い。列・文字数・時間の予算・全部の上限は設定どおり")]
    public async Task 既定ではセルを読まない()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;

        await context.InitializeAsync().ConfigureAwait(true);

        r.Bridge.InfoRequests.Should().NotBeEmpty();
        r.Bridge.InfoRequests.Should().OnlyContain(o => !o.Views.For("Sheet1").PassesValues && !o.Views.For("Sheet2").PassesValues);
        r.Bridge.InfoValueReads.Should().OnlyContain(reads => reads.Count == 0, "構成だけのシートのセルは読まない");
        r.Bridge.InfoRequests[0].IncludeReferences.Should().BeTrue();
        r.Bridge.InfoRequests[0].MaxColumns.Should().Be(30);
        r.Bridge.InfoRequests[0].MaxCellChars.Should().Be(60);
        r.Bridge.InfoRequests[0].AllMaxRows.Should().Be(500);
        r.Bridge.InfoRequests[0].AllMaxColumns.Should().Be(50);
        r.Bridge.InfoRequests[0].Budget.Should().Be(TimeSpan.FromSeconds(20), "呼び出し全体の時間切れ 30 秒の 2/3");
        (await CollectedTextAsync(r).ConfigureAwait(true)).Should().NotContain("SECRET");
    }

    [AvaloniaFact(DisplayName = "取得失敗: 収集は止まらず警告が出る。前回の成功分があれば、取得日時つきで使う")]
    public async Task 失敗しても止まらず前回を使う()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        context.WorkbookInfoWarning.Should().BeNull();

        r.Bridge.FailNext("Info", GraftIssue.Of(ErrorCode.E805, "応答なし"));
        await context.FetchWorkbookInfoAsync().ConfigureAwait(true);

        context.WorkbookInfoWarning.Should().Contain("E805").And.Contain("2026-11-21");
        context.StatusMessage.Should().Contain("E805");
        var text = await CollectedTextAsync(r).ConfigureAwait(true);
        text.Should().Contain(VbaWorkbookInfoFormatter.StaleNote, "最新を読めていない注記つきで、前回の内容を使う");
        text.Should().Contain("| 集計 | Sheet1 |");
        context.WorkbookInfo.StatusText.Should().Contain("最新を読めていません");
    }

    [AvaloniaFact(DisplayName = "取得失敗で前回の成功分も無ければ、何も出さない（見出しだけが残らない）。収集は止まらない")]
    public async Task 失敗して前回も無ければ何も出ない()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        r.Bridge.FailNext("Info", GraftIssue.Of(ErrorCode.E804, "開いていない"));
        var context = r.Graft.ContextCollect!;

        await context.InitializeAsync().ConfigureAwait(true);

        context.Files.Should().NotBeEmpty();
        context.WorkbookInfoWarning.Should().Contain("E804").And.Contain("ブックの情報なしで続けます");
        (await CollectedTextAsync(r).ConfigureAwait(true)).Should().NotContain("ブックの情報");
        context.BuildFocusText().Should().BeEmpty();
        context.WorkbookInfo.Sheets.Should().BeEmpty("取得できていないので、シートの一覧も出ない");
        context.WorkbookInfo.ShowSheetViewsPlaceholder.Should().BeTrue();
    }

    [AvaloniaFact(DisplayName = "一部だけ読めた（参照設定が読めない）: 読めた分を使い、E815 の警告が出る")]
    public async Task 一部だけ読めたらE815()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        r.Bridge.Get(VbaP3Rig.Book)!.Locked = true; // 参照設定だけ読めない（E803 相当）。
        var context = r.Graft.ContextCollect!;

        await context.InitializeAsync().ConfigureAwait(true);

        context.WorkbookInfoWarning.Should().Contain("E815").And.Contain("参照設定は読めませんでした");
        var text = await CollectedTextAsync(r).ConfigureAwait(true);
        text.Should().Contain("| 集計 | Sheet1 |").And.NotContain("## 参照設定");
        text.Should().Contain("## 読み取れなかったもの");
    }

    [AvaloniaFact(DisplayName = "取り込みの警告とブックの情報の警告は、1 行に連結される")]
    public async Task 警告は1行に連結される()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        r.Bridge.FailNext("Read", GraftIssue.Of(ErrorCode.E805, "応答なし"));
        r.Bridge.FailNext("Info", GraftIssue.Of(ErrorCode.E805, "応答なし"));
        var context = r.Graft.ContextCollect!;

        await context.InitializeAsync().ConfigureAwait(true);

        context.StatusMessage.Should().Contain("Excel の最新を取り込めていません").And.Contain("ブックの情報を取得できていません");
        context.VbaWarning.Should().Be(context.StatusMessage);
    }

    [AvaloniaFact(DisplayName = "Vba.CollectWorkbookInfo が false なら、Excel のブックの情報は呼ばれず、何も出ない")]
    public async Task 設定でオフなら呼ばれない()
    {
        using var r = await NewRigAsync(settings: new Settings { ShowPreview = false, Vba = new VbaSettings { CollectWorkbookInfo = false } }).ConfigureAwait(true);
        r.Bridge.Calls.Clear();
        var context = r.Graft.ContextCollect!;

        await context.InitializeAsync().ConfigureAwait(true);

        InfoCalls(r).Should().Be(0);
        (await CollectedTextAsync(r).ConfigureAwait(true)).Should().NotContain("ブックの情報");
        context.WorkbookInfo.StatusText.Should().Contain("オフ");
    }

    [AvaloniaFact(DisplayName = "Vba.Enabled が false なら、行は出ず Excel も呼ばない")]
    public async Task 機能が無効なら行が出ない()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        r.Graft.UpdateSettings(r.Graft.CurrentSettings with { Vba = new VbaSettings { Enabled = false } });
        r.Bridge.Calls.Clear();
        var context = r.Graft.ContextCollect!;

        await context.InitializeAsync().ConfigureAwait(true);

        context.WorkbookInfo.IsVisible.Should().BeFalse();
        InfoCalls(r).Should().Be(0);
    }

    [AvaloniaFact(DisplayName = "VBA でない通常のプロジェクトでは、行が出ず、ブックの情報の出力も増えない")]
    public async Task 通常のプロジェクトでは出ない()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        var bridge = new FakeVbaBridge();
        h.Shell.Graft.VbaBridge = bridge;
        await File.WriteAllTextAsync(Path.Combine(h.ProjectDirectory, "main.py"), "print(1)\n").ConfigureAwait(true);
        await h.RegisterProjectAsync().ConfigureAwait(true);
        var context = h.Shell.Graft.ContextCollect!;

        await context.InitializeAsync().ConfigureAwait(true);
        await ShellHarness.ExecuteAsync(context.CopyCommand).ConfigureAwait(true);

        context.WorkbookInfo.IsVisible.Should().BeFalse();
        context.WorkbookInfo.Sheets.Should().BeEmpty();
        bridge.Calls.Should().BeEmpty();
        h.Clipboard.Text.Should().NotContain("ブックの情報");
    }

    [AvaloniaFact(DisplayName = "Null のプラットフォーム（Excel を使えない環境）では、行が出ない")]
    public async Task Nullでは行が出ない()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        r.Graft.VbaBridge = new NullVbaBridge();
        var context = r.Graft.ContextCollect!;

        await context.InitializeAsync().ConfigureAwait(true);

        context.WorkbookInfo.IsVisible.Should().BeFalse();
        (await CollectedTextAsync(r).ConfigureAwait(true)).Should().NotContain("ブックの情報");
    }

    [AvaloniaFact(DisplayName = "ブックの情報が入らないときの観点: VBA の前提の最終行は「質問してください」のまま")]
    public async Task 情報が入らなければ前提の最終行は質問()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        await context.CycleWorkbookInfoLevelAsync().ConfigureAwait(true);
        await context.CycleWorkbookInfoLevelAsync().ConfigureAwait(true);
        context.WorkbookInfo.Level.Should().Be(VbaWorkbookInfoLevel.Hidden);
        context.Focus.Groups.SelectMany(g => g.Chips).First(c => c.Id == "focus-readable").IsSelected = true;

        var focus = context.BuildFocusText();

        focus.Should().Contain("確かめられないものを推測で使わず、質問してください。");
        focus.Should().NotContain("「ブックの情報」に載っているものだけ");
    }

    [AvaloniaFact(DisplayName = "トークン推定: ブックの情報を出すと推定が増え、出さないと戻る")]
    public async Task トークン推定に含まれる()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        var withInfo = context.EstimatedTokens;

        await context.CycleWorkbookInfoLevelAsync().ConfigureAwait(true);
        await context.CycleWorkbookInfoLevelAsync().ConfigureAwait(true);
        var hidden = context.EstimatedTokens;

        withInfo.Should().BeGreaterThan(hidden);
    }

    [AvaloniaFact(DisplayName = "ブックの情報はディスクに書かれない: 値が渡る見せ方にして取得しても、データ保存先のどのファイルにも値が残らない")]
    public async Task 値がディスクに残らない()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.HeaderRows).ConfigureAwait(true);
        (await CollectedTextAsync(r).ConfigureAwait(true)).Should().Contain("SECRET-甲社", "前提: 値は依頼文に入る");
        await context.CycleWorkbookInfoLevelAsync().ConfigureAwait(true);
        await context.CycleWorkbookInfoLevelAsync().ConfigureAwait(true);
        await context.CycleWorkbookInfoLevelAsync().ConfigureAwait(true);
        await ShellHarness.ExecuteAsync(context.PreviewCommand).ConfigureAwait(true);
        await Task.Delay(700).ConfigureAwait(true); // 保存のデバウンスが済むまで待つ。

        var leaked = Directory.EnumerateFiles(r.H.Root, "*", SearchOption.AllDirectories)
            .Where(path =>
            {
                try { return File.ReadAllText(path).Contains("SECRET", StringComparison.Ordinal); }
                catch (IOException) { return false; }
            })
            .ToArray();

        leaked.Should().BeEmpty("値が社外秘を含みうるため、メモリにだけ持つ（保存するのは見せ方の選択だけ）");
        (await StoredLinkAsync(r).ConfigureAwait(true)).WorkbookInfoSheetViews.Should().ContainSingle().Which.Mode.Should().Be("HeaderRows");
    }
}
