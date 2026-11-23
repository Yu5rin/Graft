using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Core.Vba;
using Graft.Features;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Xunit;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;

namespace Graft.UiTests.Vba;

/// <summary>
/// リボン（customUI。機能 E。仕様書_ブックの情報と依頼の観点 15章・15.8）のシナリオ。
/// 一時ファイルに組み立てた小さな .xlsm 相当の zip を、実際の起動と同じ依存グラフ（<see cref="ShellHarness"/>）で読み、
/// 依頼文とコンテキスト収集の出力への差し込み・3 状態・記憶・取得の契機・読めないときの扱い・同一性を確かめる。
/// Excel（COM）は <see cref="FakeVbaBridge"/>。ブックの「ファイル」だけは <see cref="VbaP3Rig.RibbonFile"/> で一時ファイルを指す。
/// <para>
/// 【リボンの書き換え（機能 F）はオフにして確かめる】 機能 F の段階 F2 から、取込でミラーに <c>CustomUI/*.xml</c> ができ、行はミラーの
/// ファイルを読む（16.14）。この章（15 章）の動き（ブックのファイルを読む）は、ミラーにリボンのファイルが無いとき
/// （customUI の無いブック・<c>Vba.RibbonEditEnabled</c> がオフ・まだ取り込んでいない）にそのまま残る。ここでは設定をオフにして、
/// 15 章の動きが変わらないことを固定する。ミラーから読む動きは <see cref="WorkbookRibbonMirrorTests"/>。
/// </para>
/// </summary>
public class WorkbookRibbonTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "graft-ribbon-ui", Guid.NewGuid().ToString("N"));

    public WorkbookRibbonTests() => Directory.CreateDirectory(_root);

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

    /// <summary>一時フォルダにファイルを書く（ブックの代役の zip など）。</summary>
    private string WriteBytes(string name, byte[] data)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    private string Book(string name = "計画.xlsm", string? customUi14 = RibbonPackageBuilder.SampleRibbon, string? customUiOld = null)
        => WriteBytes(name, RibbonPackageBuilder.Workbook(customUi14, customUiOld));

    /// <summary>Module1 に、リボンのコールバックの一部（OnRun は Public、GetEnabled は Private）を持つミラーを用意したリグ。</summary>
    private async Task<VbaP3Rig> NewRigAsync(string? ribbonFile, Settings? settings = null)
    {
        settings ??= new Settings { ShowPreview = false };
        var rig = await VbaP3Rig.CreateAsync(
            settings with { Vba = settings.Vba with { RibbonEditEnabled = false } },
            arrange: bridge =>
            {
                var book = bridge.Get(VbaP3Rig.Book)!;
                book.Info = WorkbookInfoTests.Sample();
                book.Find("Module1")!.Code =
                    "Public Sub OnRun(control As IRibbonControl)\r\nEnd Sub\r\nPrivate Sub GetEnabled(control As IRibbonControl, ByRef r)\r\nEnd Sub";
            }).ConfigureAwait(true);
        rig.RibbonFile = ribbonFile;
        return rig;
    }

    private static async Task<string> CollectedTextAsync(VbaP3Rig r)
    {
        await ShellHarness.ExecuteAsync(r.Graft.ContextCollect!.CopyCommand).ConfigureAwait(true);
        return r.H.Clipboard.Text ?? string.Empty;
    }

    private static async Task<VbaLink> StoredLinkAsync(VbaP3Rig r)
        => (await new ProjectStore(r.H.AppPaths).LoadAsync().ConfigureAwait(true)).Value.First(p => p.Id == r.Project.Id).Vba!;

    private static int Index(string text, string needle) => text.IndexOf(needle, StringComparison.Ordinal);

    [AvaloniaFact(DisplayName = "収集の窓の出力: 既定は内容も出す。ブックの情報の後・構成の前に、構成・コールバックの照合・XML の全文が入る")]
    public async Task 収集の出力に入る()
    {
        using var r = await NewRigAsync(Book()).ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);

        var text = await CollectedTextAsync(r).ConfigureAwait(true);

        context.Ribbon.IsVisible.Should().BeTrue();
        context.Ribbon.Level.Should().Be(VbaWorkbookInfoLevel.Full);
        context.Ribbon.StatusText.Should().Contain("取得: ").And.Contain("最後に保存した内容");
        text.Should().Contain("# リボン（customUI）").And.Contain("最後に保存したときのファイルの内容");
        text.Should().Contain("- tab tabMain label=\"業務\"");
        text.Should().Contain("| OnRun | onAction（button btnRun） | あります: Module1 |", "ミラーの Public Sub と照合される");
        text.Should().Contain("Public ではないため", "GetEnabled は Private");
        text.Should().Contain("| OnPick | onAction（dropDown ddKind） | VBA に見つかりません |");
        text.Should().Contain("```xml\n").And.Contain("onAction=\"OnRun\"");
        new[] { "# 前提", "# ブックの情報", "# リボン（customUI）", "# プロジェクト構成" }.Select(n => Index(text, n))
            .Should().BeInAscendingOrder().And.OnlyContain(i => i >= 0);
        context.Ribbon.CallbackText.Should().Contain("コールバック 6 件");
        context.Ribbon.MissingText.Should().Contain("OnPick").And.Contain("GetEnabled");
    }

    [AvaloniaFact(DisplayName = "依頼文コピー: 窓を開かない経路でも、リボンが依頼文に入る（ブックの情報の後・観点の前）")]
    public async Task 依頼文に入る()
    {
        using var r = await NewRigAsync(Book()).ConfigureAwait(true);
        var copy = r.Graft.PromptCopy!;
        copy.Context.Focus.Groups.SelectMany(g => g.Chips).First(c => c.Id == "focus-readable").IsSelected = true;

        await copy.QuickCopyAsync().ConfigureAwait(true);

        var text = r.H.Clipboard.Text!;
        new[] { "# ブックの情報", "# リボン（customUI）", "# 依頼の観点" }.Select(n => Index(text, n))
            .Should().BeInAscendingOrder().And.OnlyContain(i => i >= 0);
        text.Should().Contain("ファイルの更新: ");
    }

    [AvaloniaFact(DisplayName = "3 状態: 内容も出す → 構成だけ（XML が消える）→ 出さない（何も出ず、ファイルも読まない）→ 内容も出す（読み直す）。選んだ状態は保存される")]
    public async Task 三状態の巡回()
    {
        using var r = await NewRigAsync(Book()).ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        var reads = r.RibbonReads;

        await context.CycleRibbonLevelAsync().ConfigureAwait(true);
        context.Ribbon.Level.Should().Be(VbaWorkbookInfoLevel.StructureOnly);
        var structure = await CollectedTextAsync(r).ConfigureAwait(true);
        structure.Should().Contain("# リボン（customUI）").And.Contain("## 構成").And.Contain("## コールバックと VBA の照合");
        structure.Should().NotContain("## XML").And.NotContain("```xml");
        (await StoredLinkAsync(r).ConfigureAwait(true)).RibbonState.Should().Be("StructureOnly");
        r.RibbonReads.Should().Be(reads, "出し方を変えただけでは、ファイルを読み直さない");

        await context.CycleRibbonLevelAsync().ConfigureAwait(true);
        context.Ribbon.Level.Should().Be(VbaWorkbookInfoLevel.Hidden);
        (await CollectedTextAsync(r).ConfigureAwait(true)).Should().NotContain("リボン");
        (await StoredLinkAsync(r).ConfigureAwait(true)).RibbonState.Should().Be("Hidden");
        await context.FetchWorkbookInfoAsync().ConfigureAwait(true);
        r.RibbonReads.Should().Be(reads, "出さない間は、ブックのファイルを読まない");
        context.Ribbon.StatusText.Should().Contain("出さない");

        await context.CycleRibbonLevelAsync().ConfigureAwait(true);
        context.Ribbon.Level.Should().Be(VbaWorkbookInfoLevel.Full);
        r.RibbonReads.Should().Be(reads + 1, "出さないから戻したときは、読み直す");
        (await CollectedTextAsync(r).ConfigureAwait(true)).Should().Contain("```xml");
        (await StoredLinkAsync(r).ConfigureAwait(true)).RibbonState.Should().Be("Full");
    }

    [AvaloniaFact(DisplayName = "ブックの情報の出し方とは独立: ブックの情報を出さなくても、リボンは出る（逆も同じ）")]
    public async Task ブックの情報とは独立()
    {
        using var r = await NewRigAsync(Book()).ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        await context.CycleWorkbookInfoLevelAsync().ConfigureAwait(true);
        await context.CycleWorkbookInfoLevelAsync().ConfigureAwait(true); // ブックの情報: 出さない

        var text = await CollectedTextAsync(r).ConfigureAwait(true);

        text.Should().NotContain("# ブックの情報").And.Contain("# リボン（customUI）");
        (await StoredLinkAsync(r).ConfigureAwait(true)).WorkbookInfoState.Should().Be("Hidden");
        (await StoredLinkAsync(r).ConfigureAwait(true)).RibbonState.Should().Be("Full");
    }

    [AvaloniaFact(DisplayName = "記憶: プロジェクトごとに保存され、開き直しても同じ出し方で読まれる")]
    public async Task 開き直しても覚えている()
    {
        using var r = await NewRigAsync(Book()).ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        await context.CycleRibbonLevelAsync().ConfigureAwait(true); // 構成だけ

        var project = (await new ProjectStore(r.H.AppPaths).LoadAsync().ConfigureAwait(true)).Value.First(p => p.Id == r.Project.Id);
        var reopened = new ContextCollectViewModel(
            r.H.AppPaths, new ProjectStore(r.H.AppPaths), project, new Settings(), new FakeUiServices(r.H.Clipboard), r.H.Dialogs,
            vbaWorkbookInfo: new VbaWorkbookInfoSource
            {
                IsAvailable = () => true,
                SampleRows = () => 5,
                FetchAsync = (views, ct) => Task.FromResult<Graft.Core.GraftResult<VbaWorkbookInfoSnapshot>?>(null),
                // 開き直したモデルが読むのは、同じ（紐付いている）ブックのファイル。一時ファイルを読み、ブックのパスは紐付けにそろえる。
                FetchRibbonAsync = async ct => (await VbaRibbonCollector.CollectAsync(Book("別名.xlsm"), project.Root, null, ct).ConfigureAwait(false))
                    with { WorkbookPath = VbaP3Rig.Book },
            });
        await reopened.FetchRibbonAsync().ConfigureAwait(true);

        reopened.Ribbon.Level.Should().Be(VbaWorkbookInfoLevel.StructureOnly);
        reopened.BuildRibbonText().Should().Contain("## 構成").And.NotContain("## XML");
    }

    [AvaloniaFact(DisplayName = "リボンの設定が無いブック: 項目に「このブックにリボンの設定はありません」と出し、依頼文には何も入らない（警告にもしない）")]
    public async Task リボンが無いブック()
    {
        using var r = await NewRigAsync(Book(customUi14: null)).ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);

        var text = await CollectedTextAsync(r).ConfigureAwait(true);

        context.Ribbon.IsVisible.Should().BeTrue();
        context.Ribbon.StatusText.Should().Be("このブックにリボンの設定はありません");
        context.RibbonWarning.Should().BeNull();
        text.Should().NotContain("リボン");
    }

    [AvaloniaTheory(DisplayName = "読めないとき（ファイルが無い・壊れた zip・壊れた XML・DTD 付き）は、収集を止めず、理由を 1 行で知らせて、リボンなしで続ける")]
    [InlineData("missing")]
    [InlineData("zip")]
    [InlineData("xml")]
    [InlineData("dtd")]
    public async Task 読めないとき(string kind)
    {
        var file = kind switch
        {
            "missing" => Path.Combine(_root, "ない.xlsm"),
            "zip" => WriteBytes("壊れた.xlsm", Enumerable.Range(0, 200).Select(i => (byte)i).ToArray()),
            "xml" => Book(customUi14: "<customUI><ribbon></customUI>"),
            _ => Book(customUi14: "<?xml version=\"1.0\"?><!DOCTYPE c [<!ENTITY x \"y\">]><customUI/>"),
        };
        using var r = await NewRigAsync(file).ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);

        var text = await CollectedTextAsync(r).ConfigureAwait(true);

        context.Ribbon.HasStatus.Should().BeTrue();
        context.Ribbon.StatusText.Should().NotContain("\n").And.NotContain("取得: ");
        context.RibbonWarning.Should().Contain("リボン（customUI）を読めませんでした");
        context.VbaWarning.Should().Contain("リボン（customUI）");
        text.Should().NotContain("# リボン（customUI）");
        text.Should().Contain("# プロジェクト構成", "収集そのものは止まらない");
    }

    [AvaloniaFact(DisplayName = "ブックのファイルが排他で開かれているときは、ロックと知らせて続ける")]
    public async Task ロックされている()
    {
        var file = Book();
        using var holder = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var r = await NewRigAsync(file).ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;

        await context.InitializeAsync().ConfigureAwait(true);

        context.Ribbon.StatusText.Should().Contain("使用中");
        context.BuildRibbonText().Should().BeEmpty();
    }

    [AvaloniaFact(DisplayName = "古い形式（.xls）は対象外と知らせ、警告にはしない")]
    public async Task xlsは対象外()
    {
        using var r = await NewRigAsync(WriteBytes("古い.xls", new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0 })).ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;

        await context.InitializeAsync().ConfigureAwait(true);

        context.Ribbon.StatusText.Should().Contain(".xls").And.Contain("対象外");
        context.RibbonWarning.Should().BeNull();
        context.BuildRibbonText().Should().BeEmpty();
    }

    [AvaloniaFact(DisplayName = "取得の契機はブックの情報と同じ: 窓を開いたときと依頼文コピーのとき。シートの見せ方を変えるだけでは読み直さない")]
    public async Task 取得の契機()
    {
        using var r = await NewRigAsync(Book()).ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;

        await context.InitializeAsync().ConfigureAwait(true);
        var opened = r.RibbonReads;
        opened.Should().BeGreaterThan(0);

        r.ChoiceAnswer = _ => "give";
        await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.HeaderRows).ConfigureAwait(true);
        r.RibbonReads.Should().Be(opened, "シートの見せ方の変更では、ブックのファイルは変わらないので読み直さない");

        await r.Graft.PromptCopy!.QuickCopyAsync().ConfigureAwait(true);
        r.RibbonReads.Should().BeGreaterThan(opened, "依頼文コピーのときに読む");
    }

    [AvaloniaFact(DisplayName = "ブックの情報の取得をオフ（設定）にすると、リボンも読まず、依頼文にも入らない")]
    public async Task 設定でオフ()
    {
        using var r = await NewRigAsync(Book()).ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        (await CollectedTextAsync(r).ConfigureAwait(true)).Should().Contain("# リボン（customUI）");

        r.Graft.UpdateSettings(r.Graft.CurrentSettings with { Vba = r.Graft.CurrentSettings.Vba with { CollectWorkbookInfo = false } });

        (await CollectedTextAsync(r).ConfigureAwait(true)).Should().NotContain("リボン", "設定をオフにした直後は、取得済みの内容も出さない");
        await r.Graft.PromptCopy!.QuickCopyAsync().ConfigureAwait(true);
        r.H.Clipboard.Text!.Should().NotContain("リボン");
        context.Ribbon.StatusText.Should().Contain("オフ");
    }

    [AvaloniaFact(DisplayName = "XML の上限（設定 Vba.RibbonMaxChars）を超えたら、切って全体の文字数を添える。構成は切らない")]
    public async Task XMLの上限()
    {
        using var r = await NewRigAsync(Book(), new Settings { ShowPreview = false, Vba = new VbaSettings { RibbonMaxChars = 2000 } }).ConfigureAwait(true);
        var big = RibbonPackageBuilder.SampleRibbon.Replace("</group>", string.Concat(Enumerable.Range(0, 80).Select(i => $"<button id=\"b{i}\" label=\"ボタン{i}\" onAction=\"OnB{i}\"/>")) + "</group>");
        r.RibbonFile = Book("大きい.xlsm", big);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);

        var text = await CollectedTextAsync(r).ConfigureAwait(true);

        text.Should().Contain("文字だけを載せています。全体は");
        text.Should().Contain("button b79", "構成は上限で切らない");
    }

    [AvaloniaFact(DisplayName = "同一性: リボンを出さない（出さない・リボンなしのブック）なら、観点を選ばない依頼文は、リボンを足す前と同じ（痕跡が無い）")]
    public async Task リボンなしの依頼文は同一()
    {
        using var hidden = await NewRigAsync(Book()).ConfigureAwait(true);
        var hiddenContext = hidden.Graft.ContextCollect!;
        await hiddenContext.InitializeAsync().ConfigureAwait(true);
        await hiddenContext.CycleRibbonLevelAsync().ConfigureAwait(true);
        await hiddenContext.CycleRibbonLevelAsync().ConfigureAwait(true); // 出さない
        await hidden.Graft.PromptCopy!.QuickCopyAsync().ConfigureAwait(true);
        var hiddenText = hidden.H.Clipboard.Text!;

        using var none = await NewRigAsync(Book("なし.xlsm", customUi14: null)).ConfigureAwait(true);
        await none.Graft.PromptCopy!.QuickCopyAsync().ConfigureAwait(true);
        var noneText = none.H.Clipboard.Text!;

        using var shown = await NewRigAsync(Book("あり.xlsm")).ConfigureAwait(true);
        await shown.Graft.PromptCopy!.QuickCopyAsync().ConfigureAwait(true);
        var shownText = shown.H.Clipboard.Text!;

        hiddenText.Should().NotContain("リボン").And.NotContain("# 依頼の観点");
        noneText.Should().NotContain("リボン").And.NotContain("# 依頼の観点");
        Normalize(hiddenText).Should().Be(Normalize(noneText), "出さない・リボンなしは、どちらも何も差し込まない");
        shownText.Should().Contain("# リボン（customUI）");
        // リボンの節を取り除けば、リボンを出したときの依頼文も、出さなかったときと同じ（差し込み以外を変えていない）。
        var start = shownText.IndexOf("# リボン（customUI）", StringComparison.Ordinal);
        var next = shownText.IndexOf("\n# ", start + 1, StringComparison.Ordinal);
        var end = next < 0 ? shownText.Length : next + 1;
        Normalize(shownText.Remove(start, end - start)).Trim().Should().Be(Normalize(noneText).Trim());
    }

    /// <summary>実行ごとに変わる部分（生成日時・一時フォルダ名）をそろえる。</summary>
    private static string Normalize(string text)
        => System.Text.RegularExpressions.Regex.Replace(
            System.Text.RegularExpressions.Regex.Replace(text, @"- 生成日時: .*", "- 生成日時: X"),
            @"取得: \d{4}-\d{2}-\d{2} \d{2}:\d{2}", "取得: T");

    [AvaloniaFact(DisplayName = "別のブックへ結び直されたら、前のブックのリボンの内容は捨てる")]
    public async Task 結び直しで捨てる()
    {
        using var r = await NewRigAsync(Book()).ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        context.BuildRibbonText().Should().Contain("# リボン（customUI）");

        await r.UpdateLinkAsync(link => link with { WorkbookFullName = @"C:\Work\別のブック.xlsm" }).ConfigureAwait(true);

        context.BuildRibbonText().Should().BeEmpty("前のブックの label などを、別のブックの依頼文に混ぜない");
        context.Ribbon.StatusText.Should().Contain("結び直された");
    }

    [AvaloniaFact(DisplayName = "VBA でないプロジェクトには、リボンの行は出ない（通常のプロジェクトの挙動は変わらない）")]
    public async Task 通常のプロジェクトでは出ない()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        await h.RegisterProjectAsync().ConfigureAwait(true);
        var context = h.Shell.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);

        context.Ribbon.IsVisible.Should().BeFalse();
        context.BuildRibbonText().Should().BeEmpty();
        context.RibbonWarning.Should().BeNull();
    }

    // ------------------------------------------------------------------
    // 1.2.2 の不具合の回帰テスト（ViewModel を通して確かめるもの）
    // ------------------------------------------------------------------

    /// <summary>リボンの読み取りを、外から開けるまで待たせる。開けるまで読み取りは終わらない（取り消しには応える）。</summary>
    private static TaskCompletionSource GateReader(VbaP3Rig r)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = r.Graft.VbaRibbonReader;
        r.Graft.VbaRibbonReader = async (path, root, ct) =>
        {
            await gate.Task.WaitAsync(ct).ConfigureAwait(false);
            return await inner(path, root, ct).ConfigureAwait(false);
        };
        return gate;
    }

    [AvaloniaFact(DisplayName = "不具合 1: 25 万段の入れ子の customUI を持つブックでも、収集の窓の取得で落ちず、「入れ子が深すぎるため読みません」と知らせて、リボンなしで続ける")]
    public async Task 不具合1_深い入れ子でも落ちない()
    {
        var deep = string.Concat(Enumerable.Repeat("<a>", 250_000)) + string.Concat(Enumerable.Repeat("</a>", 250_000));
        using var r = await NewRigAsync(Book(customUi14: deep)).ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;

        await context.InitializeAsync().ConfigureAwait(true);
        var text = await CollectedTextAsync(r).ConfigureAwait(true);

        context.Ribbon.StatusText.Should().Contain("入れ子が深すぎるため読みません");
        context.RibbonWarning.Should().Contain("リボン（customUI）を読めませんでした");
        text.Should().NotContain("# リボン（customUI）").And.Contain("# プロジェクト構成");
    }

    [AvaloniaFact(DisplayName = "不具合 2: id が 10 万文字・コールバック 200 個の button を持つブックでも、収集の出力は元の XML の数倍以内")]
    public async Task 不具合2_巨大なidでも依頼文は膨らまない()
    {
        var sb = new System.Text.StringBuilder("<customUI><ribbon><tabs><tab id=\"t\"><group id=\"g\"><button id=\"");
        sb.Append('x', 100_000).Append('"');
        for (var i = 0; i < 200; i++) sb.Append(" getX").Append(i).Append("=\"Cb").Append(i).Append('"');
        sb.Append("/></group></tab></tabs></ribbon></customUI>");
        using var r = await NewRigAsync(Book(customUi14: sb.ToString())).ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        await context.CycleRibbonLevelAsync().ConfigureAwait(true); // 構成だけ

        var ribbon = context.BuildRibbonText();

        ribbon.Should().NotBeEmpty();
        ribbon.Length.Should().BeLessThan(150_000, "修正前は約 2000 万文字になった");
    }

    [AvaloniaFact(DisplayName = "不具合 2: 依頼文の文は取得結果ごとに作って使い回す（3 状態のクリックのたびに作り直さない）。出し方か取得結果が変わったときだけ作り直す")]
    public async Task 不具合2_整形結果を使い回す()
    {
        using var r = await NewRigAsync(Book()).ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);

        var first = context.BuildRibbonText();
        var second = context.BuildRibbonText();

        first.Should().NotBeEmpty();
        ReferenceEquals(first, second).Should().BeTrue("同じ取得結果・同じ出し方なら、同じ文字列をそのまま返す");

        await context.CycleRibbonLevelAsync().ConfigureAwait(true); // 構成だけ
        var structure = context.BuildRibbonText();
        structure.Should().NotBe(first).And.NotContain("## XML");
        ReferenceEquals(structure, context.BuildRibbonText()).Should().BeTrue();

        await context.FetchRibbonAsync().ConfigureAwait(true); // 読み直し（新しい取得結果）
        context.BuildRibbonText().Should().Be(structure, "内容は同じ。取得結果が替わっても、文は正しく作り直される");
    }

    [AvaloniaFact(DisplayName = "不具合 6: 取得の最中に「出さない」へ切り替えたら、取得が戻っても、行に取得済みの表示・コールバック名・構成を残さない")]
    public async Task 不具合6_取得中に出さないへ()
    {
        using var r = await NewRigAsync(Book()).ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        var gate = GateReader(r);

        var fetching = context.FetchRibbonAsync();
        await context.CycleRibbonLevelAsync().ConfigureAwait(true); // 構成だけ
        await context.CycleRibbonLevelAsync().ConfigureAwait(true); // 出さない
        context.Ribbon.Level.Should().Be(VbaWorkbookInfoLevel.Hidden);
        gate.SetResult();
        await fetching.ConfigureAwait(true);

        context.Ribbon.Level.Should().Be(VbaWorkbookInfoLevel.Hidden);
        context.Ribbon.StatusText.Should().Contain("出さない").And.NotContain("取得: ");
        context.Ribbon.CallbackText.Should().BeEmpty("「出さない」の行に、コールバック名を出さない");
        context.Ribbon.MissingText.Should().BeEmpty();
        context.BuildRibbonText().Should().BeEmpty();
        (await CollectedTextAsync(r).ConfigureAwait(true)).Should().NotContain("リボン");
    }

    [AvaloniaFact(DisplayName = "疑わしい点: 取り消された取得のあとに、前回の取得結果を残さない（取り消した操作が古い内容を出力へ運ばない）")]
    public async Task 取り消しで取得結果を残さない()
    {
        using var r = await NewRigAsync(Book()).ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        context.BuildRibbonText().Should().NotBeEmpty("前提: 前回の取得結果がある");
        GateReader(r);
        using var cts = new CancellationTokenSource();

        var fetching = context.FetchRibbonAsync(cts.Token);
        cts.Cancel();
        var act = async () => await fetching.ConfigureAwait(true);
        await act.Should().ThrowAsync<OperationCanceledException>();

        context.BuildRibbonText().Should().BeEmpty("取り消されたあとに、古い構成・XML の全文を残さない");
        context.Ribbon.StatusText.Should().NotContain("取得: ");
    }

    [AvaloniaFact(DisplayName = "疑わしい点: 破棄（Dispose）のあとに終わった取得は、結果（XML の全文を含む）を書き戻さない")]
    public async Task 破棄後に終わった取得は残さない()
    {
        using var r = await NewRigAsync(Book()).ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        var gate = GateReader(r);

        var fetching = context.FetchRibbonAsync();
        context.Dispose();
        gate.SetResult();
        await fetching.ConfigureAwait(true);

        context.BuildRibbonText().Should().BeEmpty("破棄したあとに、取得結果が戻ってきて残らない");
    }

    [AvaloniaFact(DisplayName = "不具合 8: パスワードで暗号化されたブック（拡張子 .xlsm で OLE のしるし）は、「パスワードで暗号化されている（または古い形式の）ブックのため読めません」と知らせ、警告にする")]
    public async Task 不具合8_暗号化されたブック()
    {
        var file = WriteBytes("暗号化.xlsm", new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0 });
        using var r = await NewRigAsync(file).ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;

        await context.InitializeAsync().ConfigureAwait(true);

        context.Ribbon.StatusText.Should().Contain("パスワードで暗号化されている（または古い形式の）ブックのため読めません").And.NotContain(".xls は");
        context.RibbonWarning.Should().Contain("リボン（customUI）を読めませんでした");
        context.BuildRibbonText().Should().BeEmpty();
    }
}
