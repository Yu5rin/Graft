using System.IO;
using FluentAssertions;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Features;
using Graft.Platform;
using Graft.Tests.TestSupport;
using Graft.UiTests.Vba;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// リボンの書き換え（仕様書_ブックの情報と依頼の観点 16.5・16.6）のうち、<b>Excel で開いているブック</b>の経路（段階 F4）。
/// 閉じて開き直す前後の呼び出しの順序、止めるブック、閉じたあとで止まったときの開き直し、時間切れ、券の解放漏れを、
/// Excel の代役（<see cref="FakeVbaBridge"/>）と一時フォルダの実ファイルで確かめる。
/// <para>
/// どの失敗でも固定するのは (1) <b>ブックのファイルが元のまま（または控えと同じ）</b>、(2) <b>閉じたなら開き直す（E823 を除く）</b>、
/// (3) <b>券（Excel の参照）が残らない</b>の 3 つ。
/// </para>
/// </summary>
public sealed partial class VbaRibbonRewriteServiceTests
{
    /// <summary>ブックを作ってミラーを作り、リボンを取り込んだまま、Excel の代役で<b>開いたまま</b>にする。</summary>
    private async Task<byte[]> ArrangeOpenAsync(string? book14 = null, Action<FakeVbaBridge.Workbook>? configure = null)
    {
        File.WriteAllBytes(_book, RibbonPackageBuilder.ExcelWorkbook(book14));
        var workbook = _bridge.AddWorkbook(_book, FakeVbaBridge.Std("Module1", "Public Sub OnRun(control As IRibbonControl)\r\nEnd Sub"));
        var created = await _service.CreateMirrorAsync(_book, _mirror, rebuild: false);
        created.IsSuccess.Should().BeTrue(string.Join(" / ", created.Issues.Select(i => i.ToDisplayText())));
        var pulled = await _service.PullAsync(Project);
        pulled.IsSuccess.Should().BeTrue(string.Join(" / ", pulled.Issues.Select(i => i.ToDisplayText())));
        configure?.Invoke(workbook);
        return File.ReadAllBytes(_book);
    }

    private FakeVbaBridge.Workbook OpenBook => _bridge.Get(_book)!;

    private string[] CloseOpenCalls()
        => _bridge.Calls.Where(c => c.StartsWith("SaveClose:", StringComparison.Ordinal) || c.StartsWith("Reopen:", StringComparison.Ordinal)
                                    || c.StartsWith("Discard:", StringComparison.Ordinal)).Select(c => c[..c.IndexOf(':', StringComparison.Ordinal)]).ToArray();

    /// <summary>券が残っていない（Excel の参照を持ち越していない）。</summary>
    private void NoTicketLeft() => _bridge.HeldTickets.Should().BeEmpty("券は開き直し・捨てのどちらかで必ず手放す（Excel のプロセスを残さない）");

    private static void TouchLikeExcelSave(string path)
    {
        // 本物の Excel は保存のたびにファイルを書き直す。更新日時だけでも進めて、「控えを取ったときから変わっていない」の検査を現実に近づける。
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(5));
    }

    // ------------------------------------------------------------------
    // 正常: 保存 → 閉じる → 書き換え → 開き直す
    // ------------------------------------------------------------------

    [Fact(DisplayName = "開いているブック（未保存なし）: 準備で止めず、計画に「開いている」を載せる。ファイルには触れない")]
    public async Task 開いているブックの準備()
    {
        var original = await ArrangeOpenAsync(XmlA);
        WriteMirror(Ribbon14, XmlB);

        var plan = await PlanAsync();

        plan.IsOpen.Should().BeTrue();
        plan.HasUnsavedChanges.Should().BeFalse();
        VbaRibbonRewriteText.ConfirmButtonText(plan).Should().Be("保存して書き換える");
        BookShouldBeUnchanged(original, "準備は何も書かない");
        CloseOpenCalls().Should().BeEmpty("準備では閉じない");
    }

    [Fact(DisplayName = "開いているブック: 保存 → 閉じる →（控え・書き換え）→ 開き直す の順。記録の ClosedWorkbook・券の解放・見た目の復元まで")]
    public async Task 開いているブックの正常な流れ()
    {
        await ArrangeOpenAsync(XmlA);
        WriteMirror(Ribbon14, XmlB);
        var plan = await PlanAsync();
        var progress = new List<string>();
        Ribbon.Progress = progress.Add;
        // 閉じた直後に作業の記録が「Graft が閉じた」印つきで存在することも見る。
        var closedFlagSeenWhileClosed = false;
        _bridge.OnSaveAndClose = path =>
        {
            TouchLikeExcelSave(path);
        };
        Ribbon.Io.OpenExclusive = path =>
        {
            closedFlagSeenWhileClosed = File.Exists(JobFile) && System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(JobFile), "\"closedWorkbook\"\\s*:\\s*true", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        };

        var result = await Ribbon.RewriteAsync(Project, plan);

        result.IsSuccess.Should().BeTrue(string.Join(" / ", result.Issues.Select(i => i.ToDisplayText())));
        result.Value.WasOpen.Should().BeTrue();
        result.Value.Reopened.Should().BeTrue();
        CloseOpenCalls().Should().Equal("SaveClose", "Reopen");
        _bridge.Calls.Should().ContainInOrder(
            $"SaveClose:{_book}", $"Reopen:{_book}");
        progress.Should().Equal(
            VbaRibbonRewriteText.ProgressSaveAndClose, VbaRibbonRewriteText.ProgressBackup, VbaRibbonRewriteText.ProgressRewrite, VbaRibbonRewriteText.ProgressReopen);
        closedFlagSeenWhileClosed.Should().BeTrue("閉じたあと、書き換えの前に、記録へ「閉じた」印を書く");
        PartText(File.ReadAllBytes(_book), "customUI/customUI14.xml").Should().Be(XmlB);
        BackupFiles().Should().ContainSingle();
        OpenBook.SaveCount.Should().Be(1);
        OpenBook.CloseCount.Should().Be(1);
        OpenBook.ReopenCount.Should().Be(1);
        OpenBook.RestoredView.Should().Be(OpenBook.View, "閉じる前の見た目を券に載せて、開き直したあとに戻す");
        NoTicketLeft();
        NothingLeftBehind();
        (await _states.LoadAsync(Project.Id, _book)).Value.Ribbon![Ribbon14].MirrorHash.Should().Be(VbaTextNormalizer.Hash(XmlB));
    }

    [Fact(DisplayName = "未保存の変更あり: 計画に載せて文面を変えるが、保存は「Saved」に関係なく必ず 1 回行う（未保存なしでも同じ）")]
    public async Task 保存は必ず行う()
    {
        await ArrangeOpenAsync(XmlA, w => w.Saved = false);
        WriteMirror(Ribbon14, XmlB);
        var plan = await PlanAsync();
        plan.HasUnsavedChanges.Should().BeTrue();
        VbaRibbonRewriteText.UnsavedWarning(plan).Should().Contain("保存していない変更").And.Contain("再計算だけでも");
        (await Ribbon.RewriteAsync(Project, plan)).IsSuccess.Should().BeTrue();
        OpenBook.SaveCount.Should().Be(1);

        // 未保存なし（Saved が True）でも保存する（マクロが Saved = True を書けるため。16.6）。
        WriteMirror(Ribbon14, XmlA);
        var again = await PlanAsync();
        again.HasUnsavedChanges.Should().BeFalse();
        VbaRibbonRewriteText.UnsavedWarning(again).Should().BeNull();
        (await Ribbon.RewriteAsync(Project, again)).IsSuccess.Should().BeTrue();
        OpenBook.SaveCount.Should().Be(2, "Saved が True でも保存してから閉じる");
    }

    [Fact(DisplayName = "閉じるときのブックのイベントは止めない: 代役は閉じる・開く以外を呼ばない（EnableEvents などに触れない）")]
    public async Task イベントに触れない()
    {
        await ArrangeOpenAsync(XmlA);
        WriteMirror(Ribbon14, XmlB);
        var plan = await PlanAsync();
        _bridge.Calls.Clear();

        (await Ribbon.RewriteAsync(Project, plan)).IsSuccess.Should().BeTrue();

        _bridge.Calls.Should().OnlyContain(c => c.StartsWith("Inspect:", StringComparison.Ordinal) || c.StartsWith("SaveClose:", StringComparison.Ordinal)
                                                || c.StartsWith("Reopen:", StringComparison.Ordinal), "書き込みの道具は Inspect・SaveClose・Reopen だけ");
    }

    // ------------------------------------------------------------------
    // 止めるブック（閉じる前に止める。1 文字も書かず、閉じない）
    // ------------------------------------------------------------------

    public static IEnumerable<object[]> StoppedBooks()
    {
        yield return new object[] { "読み取り専用", (Action<FakeVbaBridge.Workbook>)(w => w.IsReadOnly = true), ErrorCode.E819, "読み取り専用" };
        yield return new object[] { "共有ブック", (Action<FakeVbaBridge.Workbook>)(w => w.MultiUserEditing = true), ErrorCode.E819, "共有" };
        yield return new object[] { "自動保存", (Action<FakeVbaBridge.Workbook>)(w => w.AutoSaveOn = true), ErrorCode.E819, "自動保存" };
        yield return new object[] { "保護ビュー", (Action<FakeVbaBridge.Workbook>)(w => w.Availability = VbaWorkbookAvailability.ProtectedView), ErrorCode.E819, "保護ビュー" };
        yield return new object[] { "URL", (Action<FakeVbaBridge.Workbook>)(w => w.Availability = VbaWorkbookAvailability.UrlLocation), ErrorCode.E819, "OneDrive" };
        yield return new object[] { "アドイン", (Action<FakeVbaBridge.Workbook>)(w => w.IsAddin = true), ErrorCode.E819, "アドイン" };
        yield return new object[] { "マクロの実行中", (Action<FakeVbaBridge.Workbook>)(w => w.DesignMode = false), ErrorCode.E813, "マクロが実行中" };
    }

    [Theory(DisplayName = "止めるブック: 準備で止まり、保存も閉じるも呼ばれない。ファイル・控え・記録は何も変わらない")]
    [MemberData(nameof(StoppedBooks))]
    public async Task 止めるブック(string name, Action<FakeVbaBridge.Workbook> configure, ErrorCode code, string word)
    {
        _ = name;
        var original = await ArrangeOpenAsync(XmlA, configure);
        WriteMirror(Ribbon14, XmlB);

        var prepared = await Ribbon.PrepareRewriteAsync(Project);

        prepared.IsSuccess.Should().BeFalse();
        prepared.Issues.Should().ContainSingle(i => i.Code == code && i.Detail!.Contains(word, StringComparison.Ordinal));
        CloseOpenCalls().Should().BeEmpty("止めるブックは閉じない");
        BookShouldBeUnchanged(original, "触れない");
        BackupFiles().Should().BeEmpty();
        NothingLeftBehind();
    }

    [Fact(DisplayName = "Excel が応答しない（状態の検査が E805）: 準備で止める。閉じる方向へは進めない")]
    public async Task 応答しない()
    {
        await ArrangeOpenAsync(XmlA);
        WriteMirror(Ribbon14, XmlB);
        _bridge.FailNext("Inspect", GraftIssue.Of(ErrorCode.E805, "セルの編集中です"));

        var prepared = await Ribbon.PrepareRewriteAsync(Project);

        prepared.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E805);
        CloseOpenCalls().Should().BeEmpty();
    }

    [Fact(DisplayName = "状態を調べられない（E801 など）: 止めず、開いていないものとして進む（排他で開く検査が最後の砦）")]
    public async Task 通信できなければ従来どおり()
    {
        await ArrangeAsync(book14: XmlA);
        WriteMirror(Ribbon14, XmlB);
        _bridge.FailNext("Inspect", GraftIssue.Of(ErrorCode.E801, "通信できません"));

        var prepared = await Ribbon.PrepareRewriteAsync(Project);

        prepared.IsSuccess.Should().BeTrue();
        prepared.Value.Plan!.IsOpen.Should().BeFalse();
    }

    // ------------------------------------------------------------------
    // 確認のあいだの変化
    // ------------------------------------------------------------------

    [Fact(DisplayName = "確認のあいだに、未保存の変更ができた: 確認の文面に無かったので止めて、確認し直す（保存も閉じるも呼ばない）")]
    public async Task 確認のあいだに未保存ができた()
    {
        var original = await ArrangeOpenAsync(XmlA);
        WriteMirror(Ribbon14, XmlB);
        var plan = await PlanAsync();
        OpenBook.Saved = false;

        var result = await Ribbon.RewriteAsync(Project, plan);

        result.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E821 && i.Detail!.Contains("保存していない変更", StringComparison.Ordinal));
        CloseOpenCalls().Should().BeEmpty();
        BookShouldBeUnchanged(original, "触れない");
    }

    [Fact(DisplayName = "確認のあいだに、未保存の変更が無くなった／増えた（未保存あり→あり）は続行できる。保存は行う")]
    public async Task 未保存ありのままなら続ける()
    {
        await ArrangeOpenAsync(XmlA, w => w.Saved = false);
        WriteMirror(Ribbon14, XmlB);
        var plan = await PlanAsync();
        OpenBook.Saved = true; // 利用者が確認のあいだに保存した。文面は「保存されます」のままで嘘にならない。

        (await Ribbon.RewriteAsync(Project, plan)).IsSuccess.Should().BeTrue();
    }

    [Fact(DisplayName = "確認のあいだに、ブックが読み取り専用で開き直された: 書き換えの直前に止める（E819）。閉じない")]
    public async Task 確認のあいだに止めるブックになった()
    {
        var original = await ArrangeOpenAsync(XmlA);
        WriteMirror(Ribbon14, XmlB);
        var plan = await PlanAsync();
        OpenBook.IsReadOnly = true;

        var result = await Ribbon.RewriteAsync(Project, plan);

        result.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E819);
        CloseOpenCalls().Should().BeEmpty();
        BookShouldBeUnchanged(original, "触れない");
    }

    [Fact(DisplayName = "確認のあいだに、利用者が Excel でブックを閉じた: 確認した経路と違うので止める（E821）")]
    public async Task 確認のあいだに閉じられた()
    {
        await ArrangeOpenAsync(XmlA);
        WriteMirror(Ribbon14, XmlB);
        var plan = await PlanAsync();
        _bridge.CloseWorkbook(_book);

        var result = await Ribbon.RewriteAsync(Project, plan);

        result.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E821 && i.Detail!.Contains("閉じられた", StringComparison.Ordinal));
        BackupFiles().Should().BeEmpty();
    }

    // ------------------------------------------------------------------
    // 保存して閉じるの失敗（ファイルは変えていない。ブックは開いたまま）
    // ------------------------------------------------------------------

    [Fact(DisplayName = "Workbook_BeforeClose が閉じるのを取り消した: E820。ブックは開いたまま・ファイルは変えていない・記録を残さない・開き直しは呼ばない")]
    public async Task 閉じるのを取り消された()
    {
        var original = await ArrangeOpenAsync(XmlA);
        WriteMirror(Ribbon14, XmlB);
        var plan = await PlanAsync();
        _bridge.CloseCancelledByMacro = true;

        var result = await Ribbon.RewriteAsync(Project, plan);

        result.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E820);
        CloseOpenCalls().Should().Equal("SaveClose");
        _bridge.Get(_book).Should().NotBeNull("ブックは開いたまま");
        BookShouldBeUnchanged(original, "ファイルは変えていない");
        BackupFiles().Should().BeEmpty();
        NoTicketLeft();
        NothingLeftBehind();
    }

    [Fact(DisplayName = "保存して閉じるが時間切れ、Excel 側では遅れて完了していた: 書き換えず、E820 で「Excel でブックを開いてください」と案内する（［Excel で開く］を出す）")]
    public async Task 時間切れのあとで閉じていた()
    {
        var original = await ArrangeOpenAsync(XmlA);
        WriteMirror(Ribbon14, XmlB);
        var plan = await PlanAsync();
        _bridge.HangOperations.Add("SaveClose");
        _bridge.CompleteLateAfterHang = true;

        var result = await Ribbon.RewriteAsync(Project, plan);

        var issue = result.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E820).Subject;
        issue.Detail.Should().Contain("時間切れ").And.Contain("閉じられていました").And.Contain("リボンの書き換えは行っていません");
        VbaRibbonRewriteText.OffersOpenInExcel(issue).Should().BeTrue();
        BookShouldBeUnchanged(original, "書き換えない（利用者には時間切れと伝わっているので、黙って進めない）");
        BackupFiles().Should().BeEmpty();
        NoTicketLeft();
        NothingLeftBehind();
    }

    [Fact(DisplayName = "保存して閉じるが時間切れ、まだ開いている: E805 に「開いたまま」を添える。ファイルは変えていない")]
    public async Task 時間切れでまだ開いている()
    {
        var original = await ArrangeOpenAsync(XmlA);
        WriteMirror(Ribbon14, XmlB);
        var plan = await PlanAsync();
        _bridge.HangOperations.Add("SaveClose");

        var result = await Ribbon.RewriteAsync(Project, plan);

        result.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E805 && i.Detail!.Contains("開いたまま", StringComparison.Ordinal));
        BookShouldBeUnchanged(original, "触れない");
        NoTicketLeft();
        NothingLeftBehind();
        ProjectPaneRetry(result).Should().BeTrue("E805 は「もう一度試す」を出す");
    }

    private static bool ProjectPaneRetry(GraftResult<VbaRibbonRewriteOutcome> result)
        => result.Issues.Any(VbaRibbonRewriteText.IsRetryable);

    // ------------------------------------------------------------------
    // 閉じたあとで止まったとき: すべて開き直してから知らせる
    // ------------------------------------------------------------------

    [Fact(DisplayName = "閉じたあと、排他で開けない（使用中）: 開き直してから E821 を知らせる。ブックは元のまま。「もう一度試す」の対象")]
    public async Task 閉じたあとの使用中()
    {
        var original = await ArrangeOpenAsync(XmlA);
        WriteMirror(Ribbon14, XmlB);
        var plan = await PlanAsync();
        Ribbon.Io.OpenExclusive = _ => throw new IOException("sharing violation");

        var result = await Ribbon.RewriteAsync(Project, plan);

        var issue = result.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E821).Subject;
        issue.Detail.Should().Contain("ほかのプログラムが使っている").And.Contain(VbaRibbonRewriteText.ReopenedNote);
        CloseOpenCalls().Should().Equal("SaveClose", "Reopen");
        OpenBook.ReopenCount.Should().Be(1, "閉じたなら、失敗しても開き直す");
        BookShouldBeUnchanged(original, "元のまま");
        BackupFiles().Should().BeEmpty();
        NoTicketLeft();
        NothingLeftBehind();
        ProjectPaneRetry(result).Should().BeTrue();
    }

    [Fact(DisplayName = "閉じたあと、控えを書けない（E816）: 書き換えず、開き直してから知らせる")]
    public async Task 閉じたあとの控え失敗()
    {
        var original = await ArrangeOpenAsync(XmlA);
        WriteMirror(Ribbon14, XmlB);
        var plan = await PlanAsync();
        var defaultCreate = Ribbon.Io.CreateNew;
        Ribbon.Io.CreateNew = path => path.StartsWith(BackupDir, StringComparison.Ordinal) ? throw new IOException("denied") : defaultCreate(path);

        var result = await Ribbon.RewriteAsync(Project, plan);

        result.Issues.Should().Contain(i => i.Code == ErrorCode.E816);
        result.Issues.First().Detail.Should().Contain(VbaRibbonRewriteText.ReopenedNote);
        OpenBook.ReopenCount.Should().Be(1);
        BookShouldBeUnchanged(original, "元のまま");
        NoTicketLeft();
        NothingLeftBehind();
    }

    [Fact(DisplayName = "閉じたあと、置き換えの失敗: ブックは元のまま・控えは消す・開き直す")]
    public async Task 閉じたあとの置き換え失敗()
    {
        var original = await ArrangeOpenAsync(XmlA);
        WriteMirror(Ribbon14, XmlB);
        var plan = await PlanAsync();
        Ribbon.Io.Replace = (_, _) => throw new IOException("The process cannot access the file.");

        var result = await Ribbon.RewriteAsync(Project, plan);

        result.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E821);
        OpenBook.ReopenCount.Should().Be(1);
        BookShouldBeUnchanged(original, "File.Replace の失敗は元のまま");
        BackupFiles().Should().BeEmpty();
        NoTicketLeft();
        NothingLeftBehind();
    }

    [Fact(DisplayName = "閉じたあと、読み直しの不一致で控えから戻せた: 開き直す（ブックは元の内容）")]
    public async Task 閉じたあとの戻せた()
    {
        var original = await ArrangeOpenAsync(XmlA);
        WriteMirror(Ribbon14, XmlB);
        var plan = await PlanAsync();
        var replaced = false;
        Ribbon.Io.Replace = (temp, dest) =>
        {
            VbaRibbonPackageWriter.ReplaceFile(temp, dest);
            replaced = true;
        };
        Ribbon.PackageReader = path =>
        {
            var read = VbaRibbonPackageReader.Read(path);
            return replaced && PartText(File.ReadAllBytes(path), "customUI/customUI14.xml") == XmlB
                ? read with { Parts = read.Parts.Select(p => p with { Xml = p.Xml + "<!-- x -->" }).ToList() }
                : read;
        };

        var result = await Ribbon.RewriteAsync(Project, plan);

        result.Issues.Should().Contain(i => i.Code == ErrorCode.E821 && i.Detail!.Contains("控えから元の内容に戻しました", StringComparison.Ordinal));
        OpenBook.ReopenCount.Should().Be(1, "戻せたので、元の内容のブックを開き直す");
        BookShouldBeUnchanged(original, "控えから戻したので、元のバイト列");
        NoTicketLeft();
    }

    [Fact(DisplayName = "閉じたあと、控えからも戻せない（E823）: 開き直さず券を捨てる。「Excel では閉じたまま」を添える。控えは残る")]
    public async Task 閉じたあとの戻せない()
    {
        var original = await ArrangeOpenAsync(XmlA);
        WriteMirror(Ribbon14, XmlB);
        var plan = await PlanAsync();
        var calls = 0;
        Ribbon.Io.Replace = (temp, dest) =>
        {
            calls++;
            if (calls > 1) throw new IOException("復元の置き換えに失敗");
            VbaRibbonPackageWriter.ReplaceFile(temp, dest);
        };
        Ribbon.PackageReader = path =>
        {
            var read = VbaRibbonPackageReader.Read(path);
            return calls > 0 ? read with { Parts = read.Parts.Select(p => p with { Xml = "<customUI/>" }).ToList() } : read;
        };

        var result = await Ribbon.RewriteAsync(Project, plan);

        var issue = result.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E823).Subject;
        issue.Detail.Should().Contain("閉じたままにしています");
        CloseOpenCalls().Should().Equal("SaveClose", "Discard");
        OpenBook.Should().BeNull("Excel では閉じたまま（壊れているかもしれないブックでマクロを動かさない）");
        NoTicketLeft();
        BackupFiles().Should().ContainSingle();
        File.ReadAllBytes(BackupFiles()[0]).Should().Equal(original);
    }

    // ------------------------------------------------------------------
    // 開き直しの失敗・時間切れ（書き換えは済んでいる）
    // ------------------------------------------------------------------

    [Fact(DisplayName = "開き直しの失敗（Excel が終了していた）: 書き換えは済んだ成功に E822（警告）を添える。ブックは新しい内容のまま")]
    public async Task 開き直しの失敗()
    {
        await ArrangeOpenAsync(XmlA);
        WriteMirror(Ribbon14, XmlB);
        var plan = await PlanAsync();
        _bridge.ExcelQuitBeforeReopen = true;

        var result = await Ribbon.RewriteAsync(Project, plan);

        result.IsSuccess.Should().BeTrue("書き換えは済んでいる");
        result.Value.Reopened.Should().BeFalse();
        var issue = result.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E822).Subject;
        issue.Severity.Should().Be(Severity.Warning);
        VbaRibbonRewriteText.OffersOpenInExcel(issue).Should().BeTrue();
        PartText(File.ReadAllBytes(_book), "customUI/customUI14.xml").Should().Be(XmlB);
        (await _states.LoadAsync(Project.Id, _book)).Value.Ribbon![Ribbon14].MirrorHash.Should().Be(VbaTextNormalizer.Hash(XmlB), "基準は更新済み");
        NoTicketLeft();
        NothingLeftBehind();
    }

    [Fact(DisplayName = "開き直しが時間切れ、Excel 側では開いていた（利用者がダイアログに答えた）: 状態を取り直して成功として扱う（E822 を出さない）")]
    public async Task 時間切れのあとで開いていた()
    {
        await ArrangeOpenAsync(XmlA);
        WriteMirror(Ribbon14, XmlB);
        var plan = await PlanAsync();
        _bridge.HangOperations.Add("Reopen");
        _bridge.CompleteLateAfterHang = true;

        var result = await Ribbon.RewriteAsync(Project, plan);

        result.IsSuccess.Should().BeTrue();
        result.Issues.Should().NotContain(i => i.Code == ErrorCode.E822);
        result.Value.Reopened.Should().BeTrue();
        NoTicketLeft();
    }

    [Fact(DisplayName = "開き直しが時間切れ、まだ開いていない: E822（警告）。券は捨てられ、ブックは新しい内容")]
    public async Task 開き直しの時間切れ()
    {
        await ArrangeOpenAsync(XmlA);
        WriteMirror(Ribbon14, XmlB);
        var plan = await PlanAsync();
        _bridge.HangOperations.Add("Reopen");

        var result = await Ribbon.RewriteAsync(Project, plan);

        result.IsSuccess.Should().BeTrue();
        result.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E822);
        NoTicketLeft();
        PartText(File.ReadAllBytes(_book), "customUI/customUI14.xml").Should().Be(XmlB);
    }

    [Fact(DisplayName = "取り消し（CancellationToken）: 閉じたあとの開き直しは取り消しを受け付けず、最後まで行う。券は残らない")]
    public async Task 取り消しでも開き直す()
    {
        await ArrangeOpenAsync(XmlA);
        WriteMirror(Ribbon14, XmlB);
        var plan = await PlanAsync();
        using var cts = new CancellationTokenSource();
        _bridge.OnSaveAndClose = _ => cts.Cancel(); // 閉じた直後に取り消す。

        GraftResult<VbaRibbonRewriteOutcome>? result = null;
        try
        {
            result = await Ribbon.RewriteAsync(Project, plan, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // どちらでもよい（書き換えの本体は取り消しを見ない）。大事なのは、閉じたブックを開き直し、券を残さないこと。
        }

        OpenBook.Should().NotBeNull("閉じたまま放置しない");
        NoTicketLeft();
        _ = result;
    }

    // ------------------------------------------------------------------
    // 後始末の知らせ
    // ------------------------------------------------------------------

    [Fact(DisplayName = "途中で落ちた後始末（閉じたあと）: 知らせに「Excel では閉じたままです。Excel でブックを開いてください」を添える")]
    public async Task 閉じたあとで落ちた()
    {
        var original = await ArrangeOpenAsync(XmlA);
        WriteMirror(Ribbon14, XmlB);
        var plan = await PlanAsync();
        // 置き換えのあとの読み直しで「落ちる」（どの catch にも捕まらない例外）。このとき記録は ClosedWorkbook つきで残る。
        Ribbon.PackageReader = path => PartText(File.ReadAllBytes(path), "customUI/customUI14.xml") == XmlB
            ? throw new SimulatedCrash()
            : VbaRibbonPackageReader.Read(path);

        await FluentActions.Awaiting(() => Ribbon.RewriteAsync(Project, plan)).Should().ThrowAsync<SimulatedCrash>();
        NoTicketLeft();
        File.Exists(JobFile).Should().BeTrue();

        Ribbon.PackageReader = path => VbaRibbonPackageReader.Read(path);
        var recovery = await Ribbon.RecoverInterruptedAsync(Project);

        recovery.Should().NotBeNull();
        recovery!.State.Should().Be(VbaRibbonInterruptedState.Rewritten);
        recovery.Message.Should().Contain("Excel では閉じたままです").And.Contain("Excel でブックを開いてください");
        original.Should().NotBeEmpty();
    }
}
