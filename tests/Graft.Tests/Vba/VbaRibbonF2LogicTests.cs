using System.IO;
using FluentAssertions;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Features;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// リボンのミラーと取込（段階 F2。仕様書_ブックの情報と依頼の観点 16.3・16.4・16.11・16.14）の純ロジックと保存先のテスト:
/// 同期状態の <c>ribbon</c> の往復、<c>.workbooks</c> の付け替え（<c>VbaSyncStateStore.Move</c>・<c>VbaDataRelinker.Relink</c>）、
/// 三方比較の Skip・SkippedReason の警告への写し方、ブックを読まない状態（バッジ）、バッジの文言。
/// </summary>
public class VbaRibbonF2LogicTests
{
    private const string Book = @"C:\Work\計画.xlsm";
    private const string Ribbon14 = "CustomUI/customUI14.xml";
    private static readonly DateTimeOffset At = new(2026, 11, 23, 10, 0, 0, TimeSpan.FromHours(9));

    private static VbaRibbonSyncRecord Record(string hash = "h1", bool deletePending = false, bool needsReview = false)
        => new("customUI/customUI14.xml", hash, hash, deletePending, needsReview, At);

    // ------------------------------------------------------------------
    // 同期状態の ribbon
    // ------------------------------------------------------------------

    [Fact(DisplayName = "同期状態の ribbon: 保存して読むと全項目が往復し、JSON は仕様の形（ribbon・partName・mirrorHash・fileHash…）")]
    public async Task ribbonの往復()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var store = new VbaSyncStateStore(paths);
        var state = new VbaSyncState { WorkbookFullName = Book }.WithRibbon(Ribbon14, Record(needsReview: true));

        await store.SaveAsync("p_a", state);
        var loaded = (await store.LoadAsync("p_a", Book)).Value;

        loaded.Ribbon.Should().ContainSingle();
        loaded.Ribbon![Ribbon14].Should().Be(Record(needsReview: true));
        loaded.Ribbon.Should().ContainKey("customui/CUSTOMUI14.XML", "キーは大文字小文字を区別しない");
        var json = File.ReadAllText(paths.GetVbaSyncFilePath("p_a"));
        json.Should().Contain("\"ribbon\": {").And.Contain("\"CustomUI/customUI14.xml\"")
            .And.Contain("\"partName\": \"customUI/customUI14.xml\"").And.Contain("\"mirrorHash\"").And.Contain("\"fileHash\"")
            .And.Contain("\"deletePending\"").And.Contain("\"needsReview\": true").And.Contain("\"syncedAt\"");
        json.Should().Contain("\"version\": 1", "版は 1 のまま");
    }

    [Fact(DisplayName = "同期状態: ribbon の無い古いファイルは Ribbon が null。リボンの記録が無ければ JSON に ribbon を書かない（従来と同じ形）")]
    public async Task ribbonの無い古いファイル()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var store = new VbaSyncStateStore(paths);
        ws.WriteText("app/vba-sync/p_a.json", "{\"version\":1,\"workbookFullName\":\"C:\\\\Work\\\\計画.xlsm\",\"modules\":{}}");

        var loaded = (await store.LoadAsync("p_a", Book)).Value;
        loaded.Ribbon.Should().BeNull();

        await store.SaveAsync("p_a", loaded);
        File.ReadAllText(paths.GetVbaSyncFilePath("p_a")).Should().NotContain("ribbon");

        // 最後の 1 件を取り除くと null に戻る。
        var once = loaded.WithRibbon(Ribbon14, Record()).WithoutRibbon("customui/customui14.xml");
        once.Ribbon.Should().BeNull();
    }

    [Fact(DisplayName = "同期状態: ribbon のキーは正規のパスに直し、リボンのパスでないキー・欠けた記録（手で編集した JSON）は捨てる")]
    public async Task ribbonの正規化()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        ws.WriteText("app/vba-sync/p_a.json",
            "{\"version\":1,\"workbookFullName\":\"C:\\\\Work\\\\計画.xlsm\",\"modules\":{},\"ribbon\":{" +
            "\"customui\\\\CUSTOMUI14.XML\":{\"partName\":\"customUI/ribbon.xml\",\"mirrorHash\":\"a\",\"fileHash\":\"b\"}," +
            "\"CustomUI/foo.xml\":{\"partName\":\"x\",\"mirrorHash\":\"a\",\"fileHash\":\"b\"}," +
            "\"CustomUI/customUI.xml\":{\"mirrorHash\":\"a\"}}}");

        var loaded = (await new VbaSyncStateStore(paths).LoadAsync("p_a", Book)).Value;

        loaded.Ribbon.Should().ContainSingle();
        loaded.Ribbon!.Keys.Single().Should().Be(Ribbon14);
        loaded.Ribbon[Ribbon14].PartName.Should().Be("customUI/ribbon.xml");
        loaded.RibbonRecordsByFormat().Keys.Should().Equal(VbaRibbonFormat.Office2010);
    }

    [Fact(DisplayName = "Move: Id が変わったとき、ブックの丸ごとの控え（.workbooks）も .rescue と同じく新しい Id へ付け替わる")]
    public async Task Moveでworkbooksも付け替わる()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var store = new VbaSyncStateStore(paths);
        await store.SaveAsync("p_old000", new VbaSyncState { WorkbookFullName = Book }.WithRibbon(Ribbon14, Record()));
        ws.WriteText("app/vba-sync/p_old000.workbooks/計画-20261123-100000-ribbon.xlsm", "控え");

        store.Move("p_old000", "p_new000");

        paths.GetVbaWorkbookBackupDirectory("p_new000").Should().EndWith(Path.Combine("vba-sync", "p_new000.workbooks"));
        Directory.Exists(paths.GetVbaWorkbookBackupDirectory("p_old000")).Should().BeFalse();
        File.ReadAllText(Path.Combine(paths.GetVbaWorkbookBackupDirectory("p_new000"), "計画-20261123-100000-ribbon.xlsm"))
            .Should().Be("控え");
        (await store.LoadAsync("p_new000", Book)).Value.Ribbon.Should().ContainKey(Ribbon14, "リボンの記録も同じファイルで移る");
    }

    [Fact(DisplayName = "Move: 新しい Id に .workbooks が既にあれば、旧 Id のもので消さない（.rescue と同じ扱い）")]
    public void Moveは既存のworkbooksを消さない()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        ws.WriteText("app/vba-sync/p_old000.workbooks/a.xlsm", "旧");
        ws.WriteText("app/vba-sync/p_new000.workbooks/b.xlsm", "新");

        new VbaSyncStateStore(paths).Move("p_old000", "p_new000");

        File.Exists(Path.Combine(paths.GetVbaWorkbookBackupDirectory("p_new000"), "b.xlsm")).Should().BeTrue();
        File.Exists(Path.Combine(paths.GetVbaWorkbookBackupDirectory("p_old000"), "a.xlsm")).Should().BeTrue();
    }

    [Fact(DisplayName = "Relink（データ保存先の移行）: .workbooks も新しい Id へ付け替わる")]
    public async Task Relinkでworkbooksも付け替わる()
    {
        using var ws = new TempWorkspace();
        var oldPaths = new AppPaths(ws.CreateDirectory("old"));
        var newPaths = new AppPaths(ws.Combine("new"));
        var mirror = Path.Combine(oldPaths.VbaMirrorsDirectory, "計画-1a2b3c4d");
        Directory.CreateDirectory(Path.Combine(mirror, "Modules"));
        File.WriteAllText(Path.Combine(mirror, "Modules", "Module1.bas"), "Sub A()\r\nEnd Sub\r\n");
        var store = new ProjectStore(oldPaths);
        var registered = (await store.RegisterAsync(mirror, "計画")).Value;
        var oldProject = (await store.UpdateAsync(registered.Id, p => p with { Vba = new VbaLink { WorkbookFullName = Book } })).Value;
        await new VbaSyncStateStore(oldPaths).SaveAsync(oldProject.Id, new VbaSyncState { WorkbookFullName = Book });
        Directory.CreateDirectory(oldPaths.GetVbaWorkbookBackupDirectory(oldProject.Id));
        File.WriteAllText(Path.Combine(oldPaths.GetVbaWorkbookBackupDirectory(oldProject.Id), "計画-20261123-100000-ribbon.xlsm"), "控え");

        var result = DataDirectoryMigrator.MigrateAndSwitchPointer(ws.RootPath, oldPaths.BaseDirectory, newPaths.BaseDirectory, switchToPortable: false);

        result.IsSuccess.Should().BeTrue(string.Join(" / ", result.Issues.Select(i => i.ToDisplayText())));
        var loaded = (await new ProjectStore(newPaths).LoadAsync()).Value.Single();
        loaded.Id.Should().NotBe(oldProject.Id);
        Directory.Exists(newPaths.GetVbaWorkbookBackupDirectory(oldProject.Id)).Should().BeFalse("旧 Id の控えが置き去りにならない");
        File.ReadAllText(Path.Combine(newPaths.GetVbaWorkbookBackupDirectory(loaded.Id), "計画-20261123-100000-ribbon.xlsm"))
            .Should().Be("控え");
    }

    // ------------------------------------------------------------------
    // 三方比較の Skip・SkippedReason を警告へ
    // ------------------------------------------------------------------

    [Fact(DisplayName = "警告への写し方: 見送り（SkippedReason）・Skip・部品が無くなった は E810、衝突は E812。すべて警告")]
    public void 警告への写し方()
    {
        var plan = new VbaRibbonSyncPlan(
            new[]
            {
                new VbaRibbonSyncItem(VbaRibbonFormat.Office2010, Ribbon14, "customUI/customUI14.xml", VbaRibbonSyncAction.Skip,
                    Warning: "CustomUI/customUI14.xml を読めないため、リボンの同期を見送りました（ロック中）"),
                new VbaRibbonSyncItem(VbaRibbonFormat.Office2007, "CustomUI/customUI.xml", "customUI/customUI.xml", VbaRibbonSyncAction.Conflict),
            },
            SkippedReason: null);
        var issues = VbaRibbonSyncIssues.Collect(plan);

        issues.Should().HaveCount(2);
        issues.Should().OnlyContain(i => i.Severity == Severity.Warning);
        issues[0].Code.Should().Be(ErrorCode.E810);
        issues[0].Path.Should().Be(Ribbon14);
        issues[0].Detail.Should().Contain("ロック中");
        issues[1].Code.Should().Be(ErrorCode.E812);
        issues[1].Detail.Should().Contain("ブックのリボンと、このファイルの両方が変更されている").And.Contain("取り込みませんでした");

        var skipped = VbaRibbonSyncIssues.Collect(new VbaRibbonSyncPlan(Array.Empty<VbaRibbonSyncItem>(), "ブックのファイルを読めないため、リボンの同期を見送りました（壊れています）"));
        skipped.Should().ContainSingle().Which.Should().Match<GraftIssue>(i =>
            i.Code == ErrorCode.E810 && i.Path == "CustomUI/" && i.Detail!.Contains("同期の対象から外します"));
    }

    [Fact(DisplayName = "警告への写し方: 解決済み（ブック側を採用）の衝突は警告にしない。要確認の衝突は専用の文")]
    public void 解決済みの衝突()
    {
        var plan = new VbaRibbonSyncPlan(new[]
        {
            new VbaRibbonSyncItem(VbaRibbonFormat.Office2010, Ribbon14, "customUI/customUI14.xml", VbaRibbonSyncAction.Conflict,
                ConflictReason: VbaConflictReason.NeedsReview),
        });

        VbaRibbonSyncIssues.Collect(plan, unresolvedConflicts: Array.Empty<string>()).Should().BeEmpty();
        VbaRibbonSyncIssues.Collect(plan, unresolvedConflicts: new[] { "customui/customui14.xml" })
            .Single().Detail.Should().Contain("要確認");
    }

    [Fact(DisplayName = "三方比較の入力: 読めない部品（DTD・壊れた XML）は Problem つきに直す（取込でミラーへ書かない）")]
    public void 壊れた部品は読めない部品にする()
    {
        var read = new VbaRibbonReadResult(VbaRibbonReadStatus.Ok, null, new[]
        {
            new VbaRibbonRawPart("customUI/customUI14.xml", true, "<?xml version=\"1.0\"?><!DOCTYPE a [<!ENTITY x \"y\">]><customUI/>", null),
            new VbaRibbonRawPart("customUI/customUI.xml", false, "<customUI", null),
        });

        var checkedRead = VbaRibbonSyncService.CheckParts(read);

        checkedRead.Parts.Should().OnlyContain(p => !p.IsReadable && p.Problem != null);
        checkedRead.Parts[0].Problem.Should().Contain("DTD");
        var plan = VbaRibbonSyncPlanner.Plan(checkedRead, new Dictionary<VbaRibbonFormat, VbaRibbonMirrorFile>(), null, At);
        plan.Items.Should().OnlyContain(i => i.Action == VbaRibbonSyncAction.Skip);
    }

    // ------------------------------------------------------------------
    // ブックを読まない状態（バッジ）
    // ------------------------------------------------------------------

    private static VbaRibbonMirrorEntry Entry(string? text, string? problem = null)
        => new(VbaRibbonFormat.Office2010, Ribbon14, "/m/CustomUI/customUI14.xml", new VbaRibbonMirrorFile(text, problem), null, null);

    [Fact(DisplayName = "ブックを読まない状態: 未反映（基準と違う・記録なし）、削除保留、要確認、読めないものは数えない")]
    public void ブックを読まない状態()
    {
        var text = RibbonPackageBuilder.SampleRibbon;
        var synced = new VbaSyncState().WithRibbon(Ribbon14, Record(VbaTextNormalizer.Hash(text)));
        var mirror = new Dictionary<VbaRibbonFormat, VbaRibbonMirrorEntry> { [VbaRibbonFormat.Office2010] = Entry(text) };

        VbaRibbonSyncService.ComputeLocalStatus(mirror, synced).Should().BeEquivalentTo(new VbaRibbonLocalStatus(false, Array.Empty<string>(), 0));

        var changed = new Dictionary<VbaRibbonFormat, VbaRibbonMirrorEntry> { [VbaRibbonFormat.Office2010] = Entry(text + "<!-- x -->") };
        VbaRibbonSyncService.ComputeLocalStatus(changed, synced).Unpushed.Should().BeTrue();
        VbaRibbonSyncService.ComputeLocalStatus(mirror, new VbaSyncState()).Unpushed.Should().BeTrue("記録が無く、ミラーにだけある＝新規の追加の反映待ち");

        var empty = new Dictionary<VbaRibbonFormat, VbaRibbonMirrorEntry>();
        VbaRibbonSyncService.ComputeLocalStatus(empty, synced).DeletePending.Should().Equal(Ribbon14);
        VbaRibbonSyncService.ComputeLocalStatus(empty, new VbaSyncState()).Should().BeEquivalentTo(VbaRibbonLocalStatus.None);

        var review = new VbaSyncState().WithRibbon(Ribbon14, Record(VbaTextNormalizer.Hash(text), needsReview: true));
        VbaRibbonSyncService.ComputeLocalStatus(mirror, review).NeedsReviewCount.Should().Be(1);

        var unreadable = new Dictionary<VbaRibbonFormat, VbaRibbonMirrorEntry> { [VbaRibbonFormat.Office2010] = Entry(null, "ロック中") };
        VbaRibbonSyncService.ComputeLocalStatus(unreadable, synced).Should().BeEquivalentTo(VbaRibbonLocalStatus.None);
    }

    [Fact(DisplayName = "バッジ: リボン未反映は補足行に「リボン未反映」（件数なし）、ツールチップに閉じ開きで反映する旨。既定では表示は変わらない")]
    public void バッジのリボン未反映()
    {
        var badge = VbaBadgeLogic.Compute(true, true, true, 2, 1, ribbonUnpushed: true);
        badge.StatusText.Should().Be("未反映 2・リボン未反映・削除保留 1");
        badge.Tooltip.Should().Contain("ブックを閉じて開き直して反映します");

        VbaBadgeLogic.Compute(true, true, true, 0, 0, ribbonUnpushed: true).StatusText.Should().Be("リボン未反映");
        VbaBadgeLogic.Compute(true, true, true, 2, 1).Should().Be(VbaBadgeLogic.Compute(true, true, true, 2, 1, 0, 0, false));
        VbaBadgeLogic.Compute(true, true, true, 0, 0).StatusText.Should().BeEmpty();
        VbaBadgeLogic.Compute(false, true, true, 0, 0, ribbonUnpushed: true).Should().Be(VbaBadgeState.Hidden);
    }

    [Fact(DisplayName = "VbaLocalStatus: RibbonUnpushed は等価の比較に含めない（件数だけを比べる呼び出し側のため）")]
    public void LocalStatusの等価()
        => new VbaLocalStatus(0, 0) { RibbonUnpushed = true }.Should().Be(new VbaLocalStatus(0, 0));
}
