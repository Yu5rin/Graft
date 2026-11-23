using System.IO;
using FluentAssertions;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Features;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// 段階 F5（仕様書_ブックの情報と依頼の観点 16.4・16.11）のサービス層: リボンの衝突を「Graft 側で上書き」で反映の対象にすること、
/// ブックの控えの一覧・取り出し・削除、「この控えでブックを置き換える…」（丸ごと戻す操作）。
/// <para>
/// 丸ごと戻す操作で固定するのは、リボンの書き換え（F3・F4）と同じ 3 つ: (1) <b>ブックのファイルが元のまま（または控えと同じ）</b>、
/// (2) <b>閉じたなら開き直す（E823 を除く）</b>、(3) <b>券（Excel の参照）が残らない</b>。加えて、置き換える前のブックを必ず控えに残すこと。
/// </para>
/// </summary>
public sealed partial class VbaRibbonRewriteServiceTests
{
    /// <summary>ブック（リボン A）を作ってミラーを B に直し、書き換えて控え（元のバイト列）を 1 つ作る。書き換え後の位置を返す。</summary>
    private async Task<(byte[] Original, byte[] Rewritten)> RewriteOnceAsync(bool open = false)
    {
        var original = open ? await ArrangeOpenAsync(XmlA) : await ArrangeAsync(book14: XmlA);
        WriteMirror(Ribbon14, XmlB);
        var plan = await PlanAsync();
        var result = await Ribbon.RewriteAsync(Project, plan);
        result.IsSuccess.Should().BeTrue(string.Join(" / ", result.Issues.Select(i => i.ToDisplayText())));
        BackupFiles().Should().ContainSingle();
        return (original, File.ReadAllBytes(_book));
    }

    private async Task<VbaWorkbookRestorePlan> RestorePlanAsync(string? backupFileName = null)
    {
        backupFileName ??= Path.GetFileName(BackupFiles().OrderBy(f => f, StringComparer.Ordinal).First());
        var prepared = await Ribbon.PrepareRestoreFromBackupAsync(Project, backupFileName);
        prepared.IsSuccess.Should().BeTrue(string.Join(" / ", prepared.Issues.Select(i => i.ToDisplayText())));
        return prepared.Value;
    }

    // ------------------------------------------------------------------
    // 衝突 →「Graft 側で上書き」
    // ------------------------------------------------------------------

    [Fact(DisplayName = "リボンの衝突: 準備は反映せず、両側の本文つきの項目（IsRibbon）を返す。上書きと言われた衝突は反映の対象に入る")]
    public async Task 衝突を上書きで反映の対象にする()
    {
        await ArrangeAsync(book14: XmlA);
        WriteMirror(Ribbon14, XmlB);
        var bookZ = XmlA.Replace("業務", "業務Z", StringComparison.Ordinal);
        File.WriteAllBytes(_book, RibbonPackageBuilder.ExcelWorkbook(bookZ));

        var asked = await Ribbon.PrepareRewriteAsync(Project);

        asked.Value.Kind.Should().Be(VbaRibbonRewritePreparationKind.NothingToDo, "上書きと言われていない衝突は書かない");
        var item = asked.Value.Conflicts.Should().ContainSingle().Subject;
        item.IsRibbon.Should().BeTrue();
        item.RelativePath.Should().Be(Ribbon14);
        item.ExcelText.Should().Be(bookZ, "左: ブックの部品");
        item.MirrorText.Should().Be(XmlB, "右: ミラー");
        asked.Value.Notes.Should().ContainSingle(i => i.Code == ErrorCode.E812);

        var overwrite = await Ribbon.PrepareRewriteAsync(Project, overwriteConflicts: new[] { Ribbon14 });

        overwrite.Value.Kind.Should().Be(VbaRibbonRewritePreparationKind.Ready, "上書きと言われた衝突は反映の対象になる（確認ダイアログは省かない）");
        overwrite.Value.Conflicts.Should().BeEmpty();
        overwrite.Value.Notes.Should().NotContain(i => i.Code == ErrorCode.E812);
        var plan = overwrite.Value.Plan!;
        plan.Targets.Should().ContainSingle(t => t.MirrorPath == Ribbon14 && !t.IsNewPart && t.BookXml == bookZ);

        var result = await Ribbon.RewriteAsync(Project, plan);

        result.IsSuccess.Should().BeTrue(string.Join(" / ", result.Issues.Select(i => i.ToDisplayText())));
        PartText(File.ReadAllBytes(_book), "customUI/customUI14.xml").Should().Be(XmlB);
        BackupFiles().Should().ContainSingle("控えはブック全体の控えが兼ねる（モジュールの控えとは別）");
        (await _states.LoadAsync(Project.Id, _book)).Value.Ribbon![Ribbon14].MirrorHash.Should().Be(VbaTextNormalizer.Hash(XmlB));
        (await _service.GetLocalStatusAsync(Project)).RibbonUnpushed.Should().BeFalse();
    }

    [Fact(DisplayName = "衝突のパスの綴り（大文字小文字）が違っても、上書きの指定は効く")]
    public async Task 上書きの指定は大文字小文字を区別しない()
    {
        await ArrangeAsync(book14: XmlA);
        WriteMirror(Ribbon14, XmlB);
        File.WriteAllBytes(_book, RibbonPackageBuilder.ExcelWorkbook(XmlA.Replace("業務", "業務Z", StringComparison.Ordinal)));

        var overwrite = await Ribbon.PrepareRewriteAsync(Project, overwriteConflicts: new[] { "customui/CUSTOMUI14.XML" });

        overwrite.Value.Kind.Should().Be(VbaRibbonRewritePreparationKind.Ready);
    }

    // ------------------------------------------------------------------
    // ブックの控えの一覧・取り出し・削除
    // ------------------------------------------------------------------

    [Fact(DisplayName = "ブックの控えの一覧: 新しい順。形式に合わないファイル（利用者が置いたもの）は載せず、掃除でも消さない")]
    public async Task 控えの一覧()
    {
        await RewriteOnceAsync();
        var directory = BackupDir;
        var stranger = Path.Combine(directory, "メモ.txt");
        File.WriteAllText(stranger, "利用者のメモ");
        File.WriteAllBytes(Path.Combine(directory, "計画-20261122-090000-ribbon.xlsm"), new byte[] { 1, 2, 3 });

        var items = Ribbon.ListWorkbookBackups(Project, prune: true);

        items.Select(i => i.FileName).Should().Equal("計画-20261123-100000-ribbon.xlsm", "計画-20261122-090000-ribbon.xlsm");
        items[0].Size.Should().BeGreaterThan(0);
        items[0].TimeText.Should().Be("2026-11-23 10:00:00");
        File.Exists(stranger).Should().BeTrue("形式に合わないファイルは一覧に載せず、消さない");
        Ribbon.ListWorkbookBackups(new Graft.Features.Project { Id = "none", Name = "x", Root = _mirror, Vba = Project.Vba }).Should().BeEmpty("フォルダが無ければ空");
    }

    [Fact(DisplayName = "ブックの控えの取り出し・削除: 控えのフォルダの中の控えだけ。取り出しは同名があっても上書きしない")]
    public async Task 控えの取り出しと削除()
    {
        var (original, _) = await RewriteOnceAsync();
        var item = Ribbon.ListWorkbookBackups(Project).Single();
        var destination = Path.Combine(_ws.CreateDirectory("export"), item.FileName);

        Ribbon.ExportWorkbookBackup(Project, item, destination).Exported.Should().BeTrue();
        File.ReadAllBytes(destination).Should().Equal(original, "控えのコピーは、書き換える前のブックそのもの");
        File.Exists(item.FullPath).Should().BeTrue("取り出しは控えを残す");
        var again = Ribbon.ExportWorkbookBackup(Project, item, destination);
        again.Exported.Should().BeFalse("同名のファイルは上書きしない");
        again.Failure.Should().NotBeNullOrEmpty();

        var outside = new VbaWorkbookBackupItem("計画-20261123-100000-ribbon.xlsm", _book, item.Name, 1);
        Ribbon.DeleteWorkbookBackup(Project, outside).Deleted.Should().BeFalse("控えのフォルダの外のファイルは削除しない");
        File.Exists(_book).Should().BeTrue();

        Ribbon.DeleteWorkbookBackup(Project, item).Deleted.Should().BeTrue();
        BackupFiles().Should().BeEmpty();
    }

    // ------------------------------------------------------------------
    // この控えでブックを置き換える（丸ごと戻す）
    // ------------------------------------------------------------------

    [Fact(DisplayName = "控えで置き換える（開いていないブック）: いまのブックを控える → 控えの内容で置き換える → リボンの同期基準を捨てる。控えは 2 つになる")]
    public async Task 控えで置き換える()
    {
        var (original, rewritten) = await RewriteOnceAsync();
        var plan = await RestorePlanAsync();
        plan.IsOpen.Should().BeFalse();
        plan.BackupAt.Should().Be(new DateTime(2026, 11, 23, 10, 0, 0));
        VbaWorkbookRestoreText.Steps(plan).Should().HaveCount(2);

        var result = await Ribbon.RestoreFromBackupAsync(Project, plan);

        result.IsSuccess.Should().BeTrue(string.Join(" / ", result.Issues.Select(i => i.ToDisplayText())));
        File.ReadAllBytes(_book).Should().Equal(original, "選んだ控えの内容で置き換わる");
        result.Value.WasOpen.Should().BeFalse();
        result.Value.BackupPath.Should().NotBe(plan.BackupPath);
        File.ReadAllBytes(result.Value.BackupPath).Should().Equal(rewritten, "置き換える前のブックを、新しい控えとして残す（元に戻せる）");
        BackupFiles().Should().HaveCount(2);
        var state = (await _states.LoadAsync(Project.Id, _book)).Value;
        (state.Ribbon is null || state.Ribbon.Count == 0).Should().BeTrue("リボンの同期基準を捨てて「記録なし」に戻す（16.11）");
        NothingLeftBehind();

        // 次の取込: ミラー（B）とブック（A）の両方に記録が無く、本文が違う → 衝突として見つかる（黙って上書きしない）。
        var next = await Ribbon.PrepareRewriteAsync(Project);
        next.Value.Conflicts.Should().ContainSingle(c => c.RelativePath == Ribbon14);
    }

    [Fact(DisplayName = "控えで置き換える（開いているブック）: 保存 → 閉じる →（控え・置き換え）→ 開き直す の順。券は残らない")]
    public async Task 開いているブックを控えで置き換える()
    {
        var (original, rewritten) = await RewriteOnceAsync(open: true);
        var plan = await RestorePlanAsync();
        plan.IsOpen.Should().BeTrue();
        VbaWorkbookRestoreText.Steps(plan).Should().HaveCount(4);
        _bridge.Calls.Clear();
        var progress = new List<string>();
        Ribbon.Progress = progress.Add;

        var result = await Ribbon.RestoreFromBackupAsync(Project, plan);

        result.IsSuccess.Should().BeTrue(string.Join(" / ", result.Issues.Select(i => i.ToDisplayText())));
        CloseOpenCalls().Should().Equal("SaveClose", "Reopen");
        File.ReadAllBytes(_book).Should().Equal(original);
        File.ReadAllBytes(result.Value.BackupPath).Should().Equal(rewritten);
        result.Value.Reopened.Should().BeTrue();
        OpenBook.ReopenCount.Should().Be(2, "最初のリボンの書き換えと、この置き換えで、開き直しは 1 回ずつ");
        progress.Should().Contain(VbaRibbonSyncService.VbaWorkbookRestoreProgress);
        NoTicketLeft();
        NothingLeftBehind();
    }

    [Fact(DisplayName = "控えで置き換える: 置き換えそのものが失敗したら、ブックは元のまま・いま取った控えは消す・開き直す")]
    public async Task 置き換えの失敗は元のまま()
    {
        var (_, rewritten) = await RewriteOnceAsync(open: true);
        var plan = await RestorePlanAsync();
        Ribbon.Io.Replace = (_, _) => throw new IOException("置き換えに失敗");

        var result = await Ribbon.RestoreFromBackupAsync(Project, plan);

        result.Issues.Should().Contain(i => i.Code == ErrorCode.E821);
        File.ReadAllBytes(_book).Should().Equal(rewritten, "置き換えに失敗したら、ブックは置き換える前のまま");
        BackupFiles().Should().ContainSingle("世代だけが進まないよう、いま取った控えは消す");
        OpenBook.ReopenCount.Should().Be(2, "閉じたあとの失敗は、開き直してから知らせる");
        NoTicketLeft();
        NothingLeftBehind();
    }

    [Fact(DisplayName = "控えで置き換える: 置き換えのあとの照合が合わなければ、いま取った控えから戻す（E821・ブックは置き換える前の内容）")]
    public async Task 照合の不一致は戻す()
    {
        var (_, rewritten) = await RewriteOnceAsync();
        var plan = await RestorePlanAsync();
        var calls = 0;
        Ribbon.Io.Replace = (temp, dest) =>
        {
            calls++;
            // 1 回目（控えの内容での置き換え）だけ、壊れた内容を書く。2 回目（置き換える前の控えからの復元）は正しく置き換える。
            if (calls == 1) File.WriteAllBytes(temp, new byte[] { 9, 9, 9 });
            VbaRibbonPackageWriter.ReplaceFile(temp, dest);
        };

        var result = await Ribbon.RestoreFromBackupAsync(Project, plan);

        result.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E821 && i.Detail!.Contains("置き換える前の内容に戻しました", StringComparison.Ordinal));
        File.ReadAllBytes(_book).Should().Equal(rewritten);
        BackupFiles().Should().HaveCount(2, "戻せたときは、いま取った控えを残す");
        NothingLeftBehind();
    }

    [Fact(DisplayName = "控えで置き換える: 戻せなければ E823。開き直さず券を捨てる。控えは残る")]
    public async Task 戻せなければE823()
    {
        var (_, rewritten) = await RewriteOnceAsync(open: true);
        var plan = await RestorePlanAsync();
        var calls = 0;
        Ribbon.Io.Replace = (temp, dest) =>
        {
            calls++;
            if (calls == 1)
            {
                File.WriteAllBytes(temp, new byte[] { 9, 9, 9 });
                VbaRibbonPackageWriter.ReplaceFile(temp, dest);
                return;
            }

            throw new IOException("復元の置き換えに失敗");
        };

        var result = await Ribbon.RestoreFromBackupAsync(Project, plan);

        var issue = result.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E823).Subject;
        issue.Detail.Should().Contain("閉じたままにしています");
        CloseOpenCalls().Should().Equal("SaveClose", "Reopen", "SaveClose", "Discard");
        OpenBook.Should().BeNull("壊れているかもしれないブックを、Excel で開き直さない");
        NoTicketLeft();
        BackupFiles().Should().HaveCount(2);
        File.ReadAllBytes(BackupFiles().OrderBy(f => f, StringComparer.Ordinal).Last()).Should().Equal(rewritten, "置き換える前のブックの控えが残る");
    }

    [Fact(DisplayName = "別のブックの控え・壊れた控え・消えた控えでは置き換えない（ブックは変えない）")]
    public async Task 置き換えてはいけない控え()
    {
        var (_, rewritten) = await RewriteOnceAsync();
        var other = Path.Combine(BackupDir, "別のブック-20261122-090000-ribbon.xlsm");
        File.WriteAllBytes(other, RibbonPackageBuilder.ExcelWorkbook(XmlA));
        var broken = Path.Combine(BackupDir, "計画-20261122-080000-ribbon.xlsm");
        File.WriteAllBytes(broken, new byte[] { 1, 2, 3, 4 });

        var otherBook = await Ribbon.PrepareRestoreFromBackupAsync(Project, Path.GetFileName(other));
        otherBook.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E821 && i.Detail!.Contains("別のブック", StringComparison.Ordinal));

        var corrupt = await Ribbon.PrepareRestoreFromBackupAsync(Project, Path.GetFileName(broken));
        corrupt.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E821 && i.Detail!.Contains("ブックとして読めません", StringComparison.Ordinal));

        var missing = await Ribbon.PrepareRestoreFromBackupAsync(Project, "計画-20200101-000000-ribbon.xlsm");
        missing.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E821 && i.Detail!.Contains("見つかりません", StringComparison.Ordinal));

        var notBackupName = await Ribbon.PrepareRestoreFromBackupAsync(Project, "計画.xlsm");
        notBackupName.IsSuccess.Should().BeFalse("控えの形でない名前（ブック自身など）では置き換えない");
        File.ReadAllBytes(_book).Should().Equal(rewritten);
    }

    [Fact(DisplayName = "控えで置き換える: 止めるブック（読み取り専用）は E819 で、何も閉じない。確認のあいだに控えが消えたら止める")]
    public async Task 止めるブックと確認のあいだの変化()
    {
        await RewriteOnceAsync(open: true);
        var plan = await RestorePlanAsync();
        OpenBook.IsReadOnly = true;
        _bridge.Calls.Clear();

        var stopped = await Ribbon.PrepareRestoreFromBackupAsync(Project, plan.BackupFileName);
        stopped.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E819);
        CloseOpenCalls().Should().BeEmpty("止めるブックは閉じない");

        OpenBook.IsReadOnly = false;
        File.Delete(plan.BackupPath);
        var result = await Ribbon.RestoreFromBackupAsync(Project, plan);
        result.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E821 && i.Detail!.Contains("消された", StringComparison.Ordinal));
        CloseOpenCalls().Should().BeEmpty();
        NoTicketLeft();
    }

    [Fact(DisplayName = "確認の文面（丸ごと戻す）: 何が戻るか・既定は「やめる」・リボンだけなら履歴の取り消し、と書く。未保存があれば注意を足す")]
    public async Task 確認の文面()
    {
        await RewriteOnceAsync(open: true);
        OpenBook.Saved = false;
        var plan = await RestorePlanAsync();

        var text = VbaWorkbookRestoreText.Message(plan);

        text.Should().Contain("控えを取ったあとに行った変更は、すべて元に戻ります").And.Contain("シートの編集").And.Contain("VBA の変更").And.Contain("リボンの変更");
        text.Should().Contain("置き換える前のブックは、控えとして丸ごと残します").And.Contain("履歴の取り消しを使ってください");
        text.Should().Contain("保存していない変更").And.Contain("Workbook_BeforeClose");
        text.Should().Contain("ブック全体の写しです。シートの値も含みます");
        VbaWorkbookRestoreText.CancelButton.Should().Be("やめる");
        VbaWorkbookRestoreText.Message(plan with { OpenState = null }).Should().NotContain("Workbook_BeforeClose").And.Contain("開いていません");
    }
}
