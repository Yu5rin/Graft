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
/// 3 状態・値の見本のオプトイン・取得失敗の扱いを、実際の起動と同じ依存グラフ（<see cref="ShellHarness"/>）で確かめる。
/// </summary>
public class WorkbookInfoTests
{
    private static readonly DateTimeOffset ReadAt = new(2026, 11, 21, 10, 30, 0, TimeSpan.Zero);

    /// <summary>値の見本（社外秘の想定）を含む、ブックの情報。見本の合図に <c>SECRET</c> の文字を入れてある。</summary>
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
        // 値の見本をオンにする確認は、既定（Enter）が「やめる」の選択ダイアログ。見本をオンにしたいテストは、
        // 利用者が「値の見本を渡す」を押したことにする（断るテストだけ上書きする）。
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

    [AvaloniaFact(DisplayName = "収集の窓の出力: 取得したブックの情報が「# 前提」の後・構成の前に入る。既定は内容も出す（見本は出ない）")]
    public async Task 収集の出力に入る()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);

        var text = await CollectedTextAsync(r).ConfigureAwait(true);

        context.WorkbookInfo.IsVisible.Should().BeTrue();
        context.WorkbookInfo.Level.Should().Be(VbaWorkbookInfoLevel.Full);
        text.Should().Contain("# ブックの情報").And.Contain("| 集計 | Sheet1 | 表示 | A1:C20 |").And.Contain("## 先頭行の見出し").And.Contain("## 参照設定");
        text.Should().NotContain("SECRET", "値の見本は既定オフ");
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

    [AvaloniaFact(DisplayName = "3 状態: 内容も出す → 構成だけ（見出し・参照設定が消える）→ 出さない（何も出ず Excel も呼ばない）→ 内容も出す。選んだ状態は保存される")]
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
        structure.Should().NotContain("## 先頭行の見出し").And.NotContain("## 参照設定");
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
        (await CollectedTextAsync(r).ConfigureAwait(true)).Should().Contain("## 先頭行の見出し");
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
            FetchAsync = async (sample, formulas, ct) => await r.Bridge.ReadWorkbookInfoAsync(
                VbaP3Rig.Book, new VbaWorkbookInfoOptions(true, sample ? 5 : 0, formulas, 30, 60, 100, TimeSpan.FromSeconds(20)), ct).ConfigureAwait(true),
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

    [AvaloniaFact(DisplayName = "値の見本がオフなら、options.SampleRows が 0 で渡る（見本を読まない）。出力にも見本は無い")]
    public async Task 見本オフなら0行で呼ぶ()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;

        await context.InitializeAsync().ConfigureAwait(true);

        r.Bridge.InfoRequests.Should().NotBeEmpty();
        r.Bridge.InfoRequests.Should().OnlyContain(o => o.SampleRows == 0 && !o.IncludeFormulas);
        r.Bridge.InfoRequests[0].IncludeReferences.Should().BeTrue();
        r.Bridge.InfoRequests[0].MaxColumns.Should().Be(30);
        r.Bridge.InfoRequests[0].MaxCellChars.Should().Be(60);
        r.Bridge.InfoRequests[0].Budget.Should().Be(TimeSpan.FromSeconds(20), "呼び出し全体の時間切れ 30 秒の 2/3");
        (await CollectedTextAsync(r).ConfigureAwait(true)).Should().NotContain("SECRET");
    }

    [AvaloniaFact(DisplayName = "値の見本をオンにすると確認が出る。確認を断ると何も変わらない")]
    public async Task 見本は確認を断れば変わらない()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        r.ChoiceAnswer = _ => "cancel";
        var calls = InfoCalls(r);

        await context.ToggleWorkbookSampleValuesAsync().ConfigureAwait(true);

        var confirm = r.Choices.Should().ContainSingle().Subject;
        confirm.Message.Should().Contain("先頭 5 行").And.Contain("AI に渡ります").And.Contain("社外秘");
        r.H.Dialogs.Confirms.Should().BeEmpty("OK／キャンセルの確認ではなく、動詞のボタンの選択ダイアログを使う");
        context.WorkbookInfo.SampleValues.Should().BeFalse();
        context.WorkbookInfo.SampleNoticeText.Should().BeEmpty();
        InfoCalls(r).Should().Be(calls, "断ったので取り直さない");
        (await StoredLinkAsync(r).ConfigureAwait(true)).WorkbookInfoSampleValues.Should().BeFalse();
    }

    [AvaloniaFact(DisplayName = "値の見本の確認: ボタンは動詞（値の見本を渡す／やめる）で、既定（Enter）は「やめる」。既定のまま閉じると何も変わらない")]
    public async Task 見本の確認は既定がやめる()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        r.ChoiceAnswer = null; // 何も選ばずに閉じた（Enter・Esc・×）のと同じ。

        await context.ToggleWorkbookSampleValuesAsync().ConfigureAwait(true);

        var request = r.Choices.Should().ContainSingle().Subject;
        request.Options.Select(o => o.Label).Should().Equal("値の見本を渡す", "やめる");
        request.Default.Label.Should().Be("やめる");
        request.Options.Count(o => o.IsDefault).Should().Be(1);
        context.WorkbookInfo.SampleValues.Should().BeFalse("社外秘が AI に渡る操作なので、既定では何も変えない");
        (await StoredLinkAsync(r).ConfigureAwait(true)).WorkbookInfoSampleValues.Should().BeFalse();
        r.H.Dialogs.Confirms.Should().BeEmpty();
    }

    [AvaloniaFact(DisplayName = "値の見本の確認: 選択ダイアログが配線されていない環境でも、確認を省略せず、オンにしない")]
    public async Task 見本の確認が配線されていなければオンにしない()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        r.Graft.ChooseVbaAsync = null;

        await context.ToggleWorkbookSampleValuesAsync().ConfigureAwait(true);

        context.WorkbookInfo.SampleValues.Should().BeFalse();
    }

    [AvaloniaFact(DisplayName = "値の見本の確認: 実際の窓で、Enter の既定のボタンは「やめる」（primary）になる")]
    public async Task 見本の確認の窓の既定のボタン()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        await context.ToggleWorkbookSampleValuesAsync().ConfigureAwait(true);
        var request = r.Choices.Should().ContainSingle().Subject;

        var window = new Graft.Views.VbaChoiceWindow(request);
        try
        {
            window.Show();
            var buttons = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Avalonia.Controls.Button>().ToList();

            buttons.Select(b => b.Content).Should().Equal("値の見本を渡す", "やめる");
            buttons.Single(b => b.IsDefault).Content.Should().Be("やめる");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "値の見本をオンにすると、見本が読まれて出力に入り、注意が常時出る。オフに戻すと出力から消える")]
    public async Task 見本をオンにすると入る()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);

        await context.ToggleWorkbookSampleValuesAsync().ConfigureAwait(true);

        context.WorkbookInfo.SampleValues.Should().BeTrue();
        context.WorkbookInfo.SampleNoticeText.Should().Be("値の見本: オン（各シートの先頭 5 行が AI へ渡ります）");
        context.WorkbookInfo.IsSampleNoticeVisible.Should().BeTrue();
        r.Bridge.InfoRequests.Last().SampleRows.Should().Be(5);
        r.Bridge.InfoRequests.Last().IncludeFormulas.Should().BeFalse("数式は別の選択で既定オフ");
        var on = await CollectedTextAsync(r).ConfigureAwait(true);
        on.Should().Contain("## 値の見本").And.Contain("SECRET-甲社").And.Contain("=B2*C2 → 2500");
        (await StoredLinkAsync(r).ConfigureAwait(true)).WorkbookInfoSampleValues.Should().BeTrue();

        await context.ToggleWorkbookSampleValuesAsync().ConfigureAwait(true);

        context.WorkbookInfo.SampleValues.Should().BeFalse();
        context.WorkbookInfo.IsSampleNoticeVisible.Should().BeFalse();
        (await CollectedTextAsync(r).ConfigureAwait(true)).Should().NotContain("SECRET", "オフに戻した瞬間に、出力からもメモリからも消える");
        (await StoredLinkAsync(r).ConfigureAwait(true)).WorkbookInfoSampleValues.Should().BeFalse();
    }

    [AvaloniaFact(DisplayName = "値の見本の数式: 見本をオンにしているときだけ options.IncludeFormulas が true で渡る")]
    public async Task 数式は見本がオンのときだけ()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);

        await context.SetWorkbookIncludeFormulasAsync(true).ConfigureAwait(true);
        r.Bridge.InfoRequests.Last().IncludeFormulas.Should().BeFalse("見本がオフなら、数式を選んでいても読まない");
        (await StoredLinkAsync(r).ConfigureAwait(true)).WorkbookInfoIncludeFormulas.Should().BeTrue();

        await context.ToggleWorkbookSampleValuesAsync().ConfigureAwait(true);
        r.Bridge.InfoRequests.Last().Should().Match<VbaWorkbookInfoOptions>(o => o.SampleRows == 5 && o.IncludeFormulas);
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

    [AvaloniaFact(DisplayName = "ブックの情報はディスクに書かれない: 値の見本をオンにして取得しても、データ保存先のどのファイルにも見本の値が残らない")]
    public async Task 見本がディスクに残らない()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        await context.ToggleWorkbookSampleValuesAsync().ConfigureAwait(true);
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

        leaked.Should().BeEmpty("見本の値が社外秘を含みうるため、メモリにだけ持つ");
    }
}
