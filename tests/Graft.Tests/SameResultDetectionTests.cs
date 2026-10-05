using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Core;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// E305（適用後の内容が過去のリビジョンと同じになる）の検知テスト。
/// E302はパッチ本文のハッシュ一致しか見ないため、AIが本文を書き換えて
/// （summaryの文言変更・SR形式からFULL形式への書き直し等）実質同じ変更を出し直すと、
/// 何も警告が出なかった。E305は適用後のファイル内容のハッシュ（manifestのHashAfter）で
/// これを捕まえる。一時フォルダ上の実ファイルと実際のRevisionStoreを使い、
/// 過去の適用はApplyEngineで本当に行って作る（manifestの形式をテスト側で組み立てて
/// 実装と食い違う事故を避けるため）。
/// </summary>
public class SameResultDetectionTests
{
    private static string FullPatch(string summary, string path, string content) =>
        $"<<<< PATCH\nsummary: {summary}\n>>>>\n\n<<<< FILE: {path} MODE=FULL\n{content}\n>>>> END\n";

    private static string SrPatch(string path, string search, string replace) =>
        $"<<<< FILE: {path}\n<<<<<<< SEARCH\n{search}\n=======\n{replace}\n>>>>>>> REPLACE\n";

    /// <summary>パッチを実際に適用してリビジョンを1件作る。</summary>
    private static async Task ApplyAsRevisionAsync(ApplyHarness harness, string patchText, int revision)
    {
        var ctx = harness.MakeContext(revision);
        var plan = await harness.DryRunAsync(patchText, ctx);
        var apply = await harness.ApplyAsync(plan, ctx);
        apply.IsSuccess.Should().BeTrue($"前提: r{revision}の適用が成功している必要がある。issues=" +
            string.Join(", ", apply.Issues.Select(i => i.ToDisplayText())));
    }

    private static async Task<DryRunResult> DryRunAsync(ApplyHarness harness, string patchText, int revision, bool forceReapply = false)
        => await harness.DryRunAsync(patchText, harness.MakeContext(revision, forceReapply: forceReapply));

    /// <summary>DryRunResult自体は問題一覧を持たないため、DryRunPlannerを直接呼んで結果のissuesを得る。</summary>
    private static async Task<GraftResult<DryRunResult>> PlanAsync(ApplyHarness harness, string patchText, int revision, bool forceReapply = false)
    {
        var planner = new DryRunPlanner(harness.Matcher, harness.Revisions);
        return await planner.PlanAsync(ApplyHarness.Parse(patchText), harness.MakeContext(revision, forceReapply: forceReapply), default);
    }

    /// <summary>
    /// 各プランのIssuesに付いたE305を集める。E305は結果レベルのissuesではなくプランへ付く
    /// （結果レベルはMainViewModelが成功時に読まず、画面に出ないため。DryRunPlanner.CheckDuplicateAsync参照）。
    /// </summary>
    private static System.Collections.Generic.List<GraftIssue> E305Of(GraftResult<DryRunResult> result)
        => result.Value.Plans.SelectMany(p => p.Issues).Where(i => i.Code == ErrorCode.E305).ToList();

    /// <summary>E305が無いこと。結果レベルのissuesにも紛れ込んでいないことをあわせて確かめる。</summary>
    private static void NoE305(GraftResult<DryRunResult> result)
    {
        E305Of(result).Should().BeEmpty("プランのIssuesにE305が付いていないはず");
        result.Issues.Should().NotContain(i => i.Code == ErrorCode.E305, "E305は結果レベルには載せない");
    }

    [Fact(DisplayName = "E305は結果レベルではなく、該当パスの最終プランのIssuesにだけ付く")]
    public async Task E305は該当プランのIssuesに付く()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        await ApplyAsRevisionAsync(harness, FullPatch("最初", "a.txt", "same result"), 1);
        harness.WriteProjectText("a.txt", "other\r\n");

        var result = await PlanAsync(harness, FullPatch("言い換え", "a.txt", "same result") + "\n<<<< FILE: b.txt MODE=FULL\nunrelated\n>>>> END\n", 2);

        result.Issues.Should().NotContain(i => i.Code == ErrorCode.E305, "結果レベルは画面に出ないため、そこへは載せない");
        result.Value.Plans.Single(p => p.Path == "a.txt").Issues.Should().ContainSingle(i => i.Code == ErrorCode.E305);
        result.Value.Plans.Single(p => p.Path == "b.txt").Issues.Should().BeEmpty("無関係なファイルには付かない");
    }

    [Fact(DisplayName = "E305が付いても、そのプランのCanApply・IsSelected・NeedsConfirmation・Diffは付かないときと変わらない")]
    public async Task E305は適用可否や確認の状態を変えない()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        await ApplyAsRevisionAsync(harness, FullPatch("最初", "a.txt", "same result"), 1);
        harness.WriteProjectText("a.txt", "other\r\n");
        var patch = FullPatch("言い換え", "a.txt", "same result");

        // 比較対象: 履歴が空の別ワークスペースで、同じ内容・同じ現在のファイルにドライランしたもの（E305が付かない）
        using var ws2 = new TempWorkspace();
        var plain = new ApplyHarness(ws2);
        plain.WriteProjectText("a.txt", "other\r\n");
        var without = (await PlanAsync(plain, patch, 1)).Value.Plans.Single();

        var withE305Result = await PlanAsync(harness, patch, 2);
        var with = withE305Result.Value.Plans.Single();

        with.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E305);
        without.Issues.Should().BeEmpty("前提: 比較対象にはE305が付かない");
        with.CanApply.Should().Be(without.CanApply).And.BeTrue();
        with.IsSelected.Should().Be(without.IsSelected).And.BeTrue("チェックを外してはならない");
        with.NeedsConfirmation.Should().Be(without.NeedsConfirmation).And.BeFalse("確認を強制してはならない");
        with.AfterText.Should().Be(without.AfterText);
        with.Diff.Should().NotBeNull("差分表示などほかの値は引き継がれる");
        with.Operation.Should().Be(without.Operation);
        with.Stage.Should().Be(without.Stage);
    }

    [Fact(DisplayName = "本文は違うが適用後の内容が過去のリビジョンと同じになるパッチ（summaryだけ違う）はE305の警告になる")]
    public async Task summaryだけ違う再投入はE305になる()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        await ApplyAsRevisionAsync(harness, FullPatch("ログ出力を追加", "a.txt", "same result"), 1);

        // 作業者が手で別の内容へ戻した状態から、summaryだけ書き換えたパッチが来る。
        // r1は新規作成で既定の改行（CRLF）になっているため、ここも同じ形にしておく
        // （改行コードが違えばディスク上の内容も別物なので、「同じ内容」とは見なさない）
        harness.WriteProjectText("a.txt", "something else\r\n");
        var result = await PlanAsync(harness, FullPatch("ログ出力の追加（言い換え）", "a.txt", "same result"), 2);

        result.IsSuccess.Should().BeTrue();
        result.Issues.Should().NotContain(i => i.Code == ErrorCode.E302, "本文は違うのでE302は出ない");
        result.Issues.Should().NotContain(i => i.Code == ErrorCode.E305, "結果レベルには載せない");
        var e305 = E305Of(result).Should().ContainSingle().Subject;
        e305.Severity.Should().Be(Severity.Warning, "巻き戻しの再実行など正当な場合もあるため、止めない");
        e305.Path.Should().Be("a.txt");
        e305.Detail.Should().Contain("r1");
    }

    [Fact(DisplayName = "SR形式で適用した結果と同じ内容になるFULL形式のパッチはE305になる")]
    public async Task SRをFULLに書き直した再投入はE305になる()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        harness.WriteProjectText("b.txt", "a\nb\n");
        await ApplyAsRevisionAsync(harness, SrPatch("b.txt", "b", "c"), 1);

        harness.WriteProjectText("b.txt", "a\nb\n"); // 元へ戻した
        var result = await PlanAsync(harness, FullPatch("書き直し", "b.txt", "a\nc"), 2);

        result.Issues.Should().NotContain(i => i.Code == ErrorCode.E302);
        E305Of(result).Should().ContainSingle(i => i.Path == "b.txt");
    }

    [Fact(DisplayName = "適用後の内容が過去のどのリビジョンとも違えばE305は出ない")]
    public async Task 結果が違えばE305は出ない()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        await ApplyAsRevisionAsync(harness, FullPatch("最初", "a.txt", "first"), 1);

        var result = await PlanAsync(harness, FullPatch("別の内容", "a.txt", "second"), 2);

        NoE305(result);
        result.Issues.Should().NotContain(i => i.Code == ErrorCode.E302);
    }

    [Fact(DisplayName = "同じ内容でもパスが違えばE305は出ない（パスと内容の両方が一致したときだけ）")]
    public async Task 内容が同じでもパスが違えばE305は出ない()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        await ApplyAsRevisionAsync(harness, FullPatch("最初", "a.txt", "same result"), 1);

        var result = await PlanAsync(harness, FullPatch("別ファイル", "other.txt", "same result"), 2);

        NoE305(result);
    }

    [Fact(DisplayName = "パッチ本文まで完全に一致するときはE302だけが出て、E305は出ない（二重に言わない）")]
    public async Task 本文が完全一致ならE302だけでE305は出ない()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        var patch = FullPatch("同じパッチ", "a.txt", "same result");
        await ApplyAsRevisionAsync(harness, patch, 1);

        var rejected = await PlanAsync(harness, patch, 2, forceReapply: false);
        rejected.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E302 && i.Severity == Severity.Error);
        NoE305(rejected);

        // 強制再適用でE302が警告に下がっても、E305は出さない
        var forced = await PlanAsync(harness, patch, 3, forceReapply: true);
        forced.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E302 && i.Severity == Severity.Warning);
        NoE305(forced);
    }

    [Fact(DisplayName = "DELETEだけのパッチではE305は出ない（削除は適用後の内容を持たない）")]
    public async Task DELETEだけならE305は出ない()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        await ApplyAsRevisionAsync(harness, FullPatch("作成", "del.txt", "to be deleted"), 1);

        var result = await PlanAsync(harness, "<<<< DELETE: del.txt\n", 2);

        result.Value.Plans.Should().Contain(p => p.Operation == EntryOperation.Delete);
        NoE305(result);
    }

    [Fact(DisplayName = "過去リビジョンがsuccessでない（rolled_back）ときはE305は出ない")]
    public async Task ロールバック済みのリビジョンはE305の対象外()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        await ApplyAsRevisionAsync(harness, FullPatch("最初", "a.txt", "same result"), 1);

        // 起動時の復旧（StartupCoordinator.MarkRolledBackAsync）と同じ方法でrolled_backにする
        var r1 = (await harness.Revisions.ReadAsync(harness.ProjectId, 1)).Value;
        var manifestPath = Path.Combine(r1.FolderPath, "manifest.json");
        await new JsonFileStore().WriteAsync(manifestPath, r1.Manifest with { Status = RevisionStatus.RolledBack },
            JsonFileStore.DefaultOptions);
        (await harness.Revisions.ReadAsync(harness.ProjectId, 1)).Value.Manifest.Status
            .Should().Be(RevisionStatus.RolledBack, "前提: statusが書き換わっている");

        var result = await PlanAsync(harness, FullPatch("言い換え", "a.txt", "same result"), 2);

        NoE305(result);
    }

    [Fact(DisplayName = "同じ内容のリビジョンが複数あるときは、最も新しいリビジョンを示す")]
    public async Task 該当が複数なら最新のリビジョンを示す()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        await ApplyAsRevisionAsync(harness, FullPatch("r1", "a.txt", "same result"), 1);
        await ApplyAsRevisionAsync(harness, FullPatch("途中", "a.txt", "middle"), 2);
        await ApplyAsRevisionAsync(harness, FullPatch("r3", "a.txt", "same result"), 3);

        var result = await PlanAsync(harness, FullPatch("r4", "a.txt", "same result"), 4);

        E305Of(result).Should().ContainSingle().Which.Detail.Should().Contain("r3");
    }

    [Fact(DisplayName = "複数ファイルが該当したときは、ファイルごとに1件ずつE305が出る")]
    public async Task 複数ファイルが該当すればファイルごとに出る()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        var first = FullPatch("最初", "x.txt", "xx") + "\n<<<< FILE: y.txt MODE=FULL\nyy\n>>>> END\n\n<<<< FILE: z.txt MODE=FULL\nzz\n>>>> END\n";
        await ApplyAsRevisionAsync(harness, first, 1);

        // x・yは同じ結果、zだけ違う結果になる書き直し
        var again = FullPatch("言い換え", "x.txt", "xx") + "\n<<<< FILE: y.txt MODE=FULL\nyy\n>>>> END\n\n<<<< FILE: z.txt MODE=FULL\nzz2\n>>>> END\n";
        var result = await PlanAsync(harness, again, 2);

        E305Of(result).Select(i => i.Path)
            .Should().BeEquivalentTo(new[] { "x.txt", "y.txt" });
    }

    [Fact(DisplayName = "SR形式で同じパスに複数ペアがあるときは、途中の状態ではなく最後のAfterTextを最終状態として比べる")]
    public async Task SR複数ペアは最終状態だけを比べる()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        harness.WriteProjectText("m.txt", "1\n2\n3\n");
        // r1: 1→A だけ。結果は "A\n2\n3\n"
        await ApplyAsRevisionAsync(harness, SrPatch("m.txt", "1", "A"), 1);

        // 今度のパッチは 1→A、2→B の2ペア。1ペア目の直後の状態（A,2,3）はr1の結果と同じだが、
        // 最終状態（A,B,3）は違うので、E305は出てはならない
        harness.WriteProjectText("m.txt", "1\n2\n3\n");
        var twoPairs = SrPatch("m.txt", "1", "A") + "\n" + SrPatch("m.txt", "2", "B");
        var result = await PlanAsync(harness, twoPairs, 2);

        result.Value.Plans.Count(p => p.Path == "m.txt" && p.AfterText is not null)
            .Should().BeGreaterThan(1, "前提: 同じパスに複数のBlockPlanが並んでいる");
        NoE305(result);
    }

    [Fact(DisplayName = "SR形式の複数ペアでも、最終状態が過去のリビジョンと同じならE305になる")]
    public async Task SR複数ペアの最終状態が一致すればE305になる()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        harness.WriteProjectText("m.txt", "1\n2\n3\n");
        var twoPairs = SrPatch("m.txt", "1", "A") + "\n" + SrPatch("m.txt", "2", "B");
        await ApplyAsRevisionAsync(harness, twoPairs, 1);

        // ペアの順序を入れ替えた（本文は違うが最終結果は同じ）パッチ
        harness.WriteProjectText("m.txt", "1\n2\n3\n");
        var swapped = SrPatch("m.txt", "2", "B") + "\n" + SrPatch("m.txt", "1", "A");
        var result = await PlanAsync(harness, swapped, 2);

        result.Issues.Should().NotContain(i => i.Code == ErrorCode.E302);
        E305Of(result).Should().ContainSingle(i => i.Path == "m.txt");
        // 付くのは、そのパスの最終プラン（最後のSEARCH/REPLACEペア）だけ。途中のペアには付かない
        var mPlans = result.Value.Plans.Where(p => p.Path == "m.txt").ToList();
        mPlans.Should().HaveCountGreaterThan(1);
        mPlans.Last().Issues.Should().ContainSingle(i => i.Code == ErrorCode.E305);
        mPlans.Take(mPlans.Count - 1).SelectMany(p => p.Issues).Should().NotContain(i => i.Code == ErrorCode.E305);
    }

    [Fact(DisplayName = "過去のリビジョンが無い（初回）のときはE305もE302も出ない")]
    public async Task 履歴が空ならどちらも出ない()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);

        var result = await PlanAsync(harness, FullPatch("初回", "a.txt", "first"), 1);

        NoE305(result);
        result.Issues.Should().NotContain(i => i.Code == ErrorCode.E302);
    }

    [Theory(DisplayName = "既存ファイルの改行コード・末尾改行・BOMの組み合わせによらず、書き込み結果と同じ内容はE305になる")]
    [InlineData("\r\n", true, false)]
    [InlineData("\n", true, false)]
    [InlineData("\n", false, false)]
    [InlineData("\r\n", false, true)]
    public async Task 改行と末尾改行の組み合わせでもE305になる(string newLine, bool endsWithNewLine, bool bom)
    {
        // 実機での気づき: AfterTextは行を"\n"で連結しただけで、HashAfter（書き込み後に読み戻した本文）
        // とは改行コード・末尾改行が違う。Shapeで復元しないと、既定以外の形のファイルでE305が出ない。
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        var tail = endsWithNewLine ? newLine : string.Empty;
        byte[] Encode(string text) =>
            (bom ? new byte[] { 0xEF, 0xBB, 0xBF } : Array.Empty<byte>())
                .Concat(new System.Text.UTF8Encoding(false).GetBytes(text)).ToArray();

        harness.WriteProjectBytes("s.txt", Encode($"keep1{newLine}old{newLine}keep3{tail}"));
        await ApplyAsRevisionAsync(harness, SrPatch("s.txt", "old", "new"), 1);

        harness.WriteProjectBytes("s.txt", Encode($"keep1{newLine}old{newLine}keep3{tail}")); // 元へ戻した
        var full = FullPatch("書き直し", "s.txt", "keep1\nnew\nkeep3");
        var result = await PlanAsync(harness, full, 2);

        E305Of(result).Should().ContainSingle(i => i.Path == "s.txt");
    }
}
