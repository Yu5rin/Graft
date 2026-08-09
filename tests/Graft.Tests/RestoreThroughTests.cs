using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Core;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 「ここまで戻す」（<see cref="RevisionRestorer.RestoreThroughAsync"/>・
/// <see cref="RevisionRestorer.BuildRestoreThroughPreview"/>）の統合テスト。
/// ユーザー要望「履歴からそのバージョンまで戻れるようにしたい」に対する追加機能で、
/// 選択したリビジョンより新しいリビジョンをすべて新しい順に取り消し、選択リビジョンを
/// 適用した直後の状態を再現する。単発復元（<see cref="RevisionRestorer.RestoreAsync"/>）を
/// 検証する<see cref="BackupRevisionTests"/>とは別ファイルに分ける。
/// </summary>
public class RestoreThroughTests
{
    /// <summary>SEARCH/REPLACE形式のパッチ本文を組み立てる（仕様書4.1）。</summary>
    private static string BuildSrPatch(string relativePath, string search, string replace)
        => $"<<<< FILE: {relativePath}\n<<<<<<< SEARCH\n{search}\n=======\n{replace}\n>>>>>>> REPLACE\n";

    /// <summary>FULL形式でファイル全体を書き換えるパッチ本文を組み立てる（新規作成にも使う）。</summary>
    private static string BuildFullPatch(string relativePath, string content)
        => $"<<<< FILE: {relativePath} MODE=FULL\n{content}\n>>>> END\n";

    /// <summary>harnessを介して1件のパッチをドライラン→適用まで実際に通す。</summary>
    private static async Task<RevisionManifest> ApplyRealAsync(ApplyHarness harness, int revision, string patchText)
    {
        var ctx = harness.MakeContext(revision);
        var dryRun = await harness.DryRunAsync(patchText, ctx);
        var applied = await harness.ApplyAsync(dryRun, ctx);
        applied.IsSuccess.Should().BeTrue(
            $"r{revision}の適用に失敗: " + string.Join(",", applied.Issues.Select(i => i.ToDisplayText())));
        return applied.Value;
    }

    // ------------------------------------------------------------------
    // 要件1: 逆順（新しい順）で取り消し、同一ファイルへの複数回の変更でも正しい内容になる
    // ------------------------------------------------------------------

    [Fact(DisplayName = "ここまで戻す: 同一ファイルを複数リビジョンで変更していても新しい順に取り消し、選択リビジョン直後の内容と厳密に一致する")]
    public async Task 同一ファイルの複数変更でも逆順で正しく戻る()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);

        // r1〜r4はすべて同じファイルを書き換える（逆順検証の要）。
        await ApplyRealAsync(harness, 1, BuildFullPatch("target.txt", "v1"));
        await ApplyRealAsync(harness, 2, BuildFullPatch("target.txt", "v2"));
        await ApplyRealAsync(harness, 3, BuildFullPatch("target.txt", "v3"));
        await ApplyRealAsync(harness, 4, BuildFullPatch("target.txt", "v4"));

        var list = await harness.Revisions.ListAsync(harness.ProjectId);
        list.IsSuccess.Should().BeTrue();

        var preview = RevisionRestorer.BuildRestoreThroughPreview(list.Value, targetRevision: 2);
        preview.CanExecute.Should().BeTrue();
        preview.RevisionsToUndo.Select(r => r.Manifest.Revision).Should().Equal(new[] { 4, 3 },
            "新しい順（r4→r3）でなければならない。この順序を誤ると同一ファイルへの複数回の変更が壊れる");
        preview.AffectedPaths.Should().Equal("target.txt");

        var restorer = new RevisionRestorer(harness.Paths);
        var result = await restorer.RestoreThroughAsync(
            harness.ProjectId, harness.ProjectRoot, targetRevision: 2, preview.RevisionsToUndo, newRevisionNumber: 5, force: false);

        result.IsSuccess.Should().BeTrue(string.Join(",", result.Issues.Select(i => i.ToDisplayText())));
        var content = System.Text.Encoding.UTF8.GetString(harness.ReadProjectBytes("target.txt"));
        content.Should().Contain("v2").And.NotContain("v3").And.NotContain("v4");

        // ハッシュによる厳密一致確認（要件: ハッシュまたは内容比較で厳密に確認）。
        var r2Result = await harness.Revisions.ReadAsync(harness.ProjectId, 2);
        r2Result.IsSuccess.Should().BeTrue();
        var hashNow = FileTextIO.ComputeHash(content);
        var r2EntryHash = r2Result.Value.Manifest.Entries.Single(e => e.Path == "target.txt").HashAfter;
        hashNow.Should().Be(r2EntryHash, "r2を適用した直後のhashAfterと完全に一致するはず");

        // まとめ戻し自体が新規リビジョンとして記録されていること。
        result.Value.Revision.Should().Be(5);
        result.Value.Status.Should().Be(RevisionStatus.Success);
        result.Value.Type.Should().Be("revert");
        var recorded = await harness.Revisions.ReadAsync(harness.ProjectId, 5);
        recorded.IsSuccess.Should().BeTrue();
        recorded.Value.Manifest.Status.Should().Be(RevisionStatus.Success);
        recorded.Value.IsRestorable.Should().BeTrue();
    }

    [Fact(DisplayName = "ここまで戻す: 異なるファイルにまたがる複数リビジョンでも、それぞれ正しい状態まで戻る")]
    public async Task 複数ファイルにまたがる場合も正しく戻る()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);

        await ApplyRealAsync(harness, 1, BuildFullPatch("a.txt", "a1") + "\n" + BuildFullPatch("b.txt", "b1"));
        await ApplyRealAsync(harness, 2, BuildFullPatch("a.txt", "a2")); // aだけ変更
        await ApplyRealAsync(harness, 3, BuildFullPatch("b.txt", "b2")); // bだけ変更（このリビジョンを取り消し対象にする）

        var list = await harness.Revisions.ListAsync(harness.ProjectId);
        var preview = RevisionRestorer.BuildRestoreThroughPreview(list.Value, targetRevision: 2);
        preview.RevisionsToUndo.Select(r => r.Manifest.Revision).Should().Equal(3);
        preview.AffectedPaths.Should().Equal("b.txt");

        var restorer = new RevisionRestorer(harness.Paths);
        var result = await restorer.RestoreThroughAsync(
            harness.ProjectId, harness.ProjectRoot, targetRevision: 2, preview.RevisionsToUndo, newRevisionNumber: 4, force: false);

        result.IsSuccess.Should().BeTrue();
        System.Text.Encoding.UTF8.GetString(harness.ReadProjectBytes("a.txt")).Should().Contain("a2", "r3はaに影響しないので変わらないはず");
        System.Text.Encoding.UTF8.GetString(harness.ReadProjectBytes("b.txt")).Should().Contain("b1").And.NotContain("b2");
        result.Value.Entries.Should().ContainSingle(e => e.Path == "b.txt", "変化したのはbだけのはず");
    }

    // ------------------------------------------------------------------
    // 要件4: 復元不可のリビジョンが含まれる場合は中止し、原因を明示する
    // ------------------------------------------------------------------

    [Fact(DisplayName = "ここまで戻す: 取り消し対象に復元不可のリビジョンが含まれる場合は実行前に中止し、原因のリビジョンを示す")]
    public async Task 復元不可のリビジョンを含む場合は中止する()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);

        await ApplyRealAsync(harness, 1, BuildFullPatch("x.txt", "x1"));
        await ApplyRealAsync(harness, 2, BuildFullPatch("x.txt", "x2"));
        await ApplyRealAsync(harness, 3, BuildFullPatch("x.txt", "x3"));

        // r2のバックアップフォルダの実体を外部から削除された状態を再現する（13.1）。
        var r2Before = await harness.Revisions.ReadAsync(harness.ProjectId, 2);
        Directory.Delete(r2Before.Value.FolderPath, recursive: true);

        var list = await harness.Revisions.ListAsync(harness.ProjectId);
        var preview = RevisionRestorer.BuildRestoreThroughPreview(list.Value, targetRevision: 1);
        preview.NotRestorable.Should().ContainSingle(r => r.Manifest.Revision == 2);
        preview.CanExecute.Should().BeFalse();

        var beforeContent = harness.ReadProjectBytes("x.txt");
        var restorer = new RevisionRestorer(harness.Paths);
        var result = await restorer.RestoreThroughAsync(
            harness.ProjectId, harness.ProjectRoot, targetRevision: 1, preview.RevisionsToUndo, newRevisionNumber: 4, force: false);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(i => i.Code == ErrorCode.E405 && i.Detail != null && i.Detail.Contains("r2"),
            "どのリビジョンが原因で復元不可なのかをメッセージに含めるはず");
        harness.ReadProjectBytes("x.txt").Should().Equal(beforeContent, "中止した場合はファイルへ一切書き込んではならない");

        // 新規リビジョン用フォルダも作られていない（バックアップ開始前に中止したはず）。
        var backupDir = harness.Paths.GetProjectBackupDirectory(harness.ProjectId);
        Directory.EnumerateDirectories(backupDir).Should().HaveCount(2, "r1・r3の実体のみで、r4用フォルダは作られないはず（r2は削除済み）");
    }

    // ------------------------------------------------------------------
    // 要件5: まとめ戻し自体を取り消せる
    // ------------------------------------------------------------------

    [Fact(DisplayName = "ここまで戻す: 操作自体を新規リビジョンとして記録し、単発復元で元の状態へ戻せる")]
    public async Task まとめ戻し自体を単発復元で取り消せる()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);

        await ApplyRealAsync(harness, 1, BuildFullPatch("y.txt", "y1"));
        await ApplyRealAsync(harness, 2, BuildFullPatch("y.txt", "y2"));
        await ApplyRealAsync(harness, 3, BuildFullPatch("y.txt", "y3"));
        var beforeThroughContent = harness.ReadProjectBytes("y.txt"); // "y3\n"相当（戻しすぎを取り消した後に期待する内容）

        var list = await harness.Revisions.ListAsync(harness.ProjectId);
        var preview = RevisionRestorer.BuildRestoreThroughPreview(list.Value, targetRevision: 1);

        var restorer = new RevisionRestorer(harness.Paths);
        var throughResult = await restorer.RestoreThroughAsync(
            harness.ProjectId, harness.ProjectRoot, targetRevision: 1, preview.RevisionsToUndo, newRevisionNumber: 4, force: false);
        throughResult.IsSuccess.Should().BeTrue();
        System.Text.Encoding.UTF8.GetString(harness.ReadProjectBytes("y.txt")).Should().Contain("y1").And.NotContain("y3",
            "ここまでで「戻しすぎた」状態（r1直後）になっているはず");

        // 「戻しすぎた」ので、まとめ戻し自体（r4）を単発復元で取り消す。
        var r4 = await harness.Revisions.ReadAsync(harness.ProjectId, 4);
        r4.IsSuccess.Should().BeTrue();
        r4.Value.IsRestorable.Should().BeTrue();

        var undone = await restorer.RestoreAsync(harness.ProjectId, harness.ProjectRoot, r4.Value, force: false);
        undone.IsSuccess.Should().BeTrue(string.Join(",", undone.Issues.Select(i => i.ToDisplayText())));
        harness.ReadProjectBytes("y.txt").Should().Equal(beforeThroughContent,
            "まとめ戻し（r4）を取り消すと、まとめ戻しを行う直前（r3適用直後）の内容へ完全に戻るはず");
    }

    // ------------------------------------------------------------------
    // 要件6: 最新リビジョンを選んだ場合は取り消す対象が無い
    // ------------------------------------------------------------------

    [Fact(DisplayName = "ここまで戻す: 最新リビジョンを選ぶと取り消し対象が無く実行不可になる")]
    public async Task 最新リビジョン選択時は対象なし()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        await ApplyRealAsync(harness, 1, BuildFullPatch("z.txt", "z1"));
        await ApplyRealAsync(harness, 2, BuildFullPatch("z.txt", "z2"));

        var list = await harness.Revisions.ListAsync(harness.ProjectId);
        var preview = RevisionRestorer.BuildRestoreThroughPreview(list.Value, targetRevision: 2);

        preview.RevisionsToUndo.Should().BeEmpty();
        preview.CanExecute.Should().BeFalse();
    }

    // ------------------------------------------------------------------
    // 要件7: 途中で失敗した場合は成功したと報告せず、どこまで戻せたかを伝える
    // ------------------------------------------------------------------

    [Fact(DisplayName = "ここまで戻す: 途中のリビジョンの取り消しに失敗すると、それ以降は処理せず中断し、成功したと報告しない")]
    public async Task 途中失敗時は成功と偽らず中断する()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);

        await ApplyRealAsync(harness, 1, BuildFullPatch("w.txt", "w1"));
        await ApplyRealAsync(harness, 2, BuildFullPatch("w.txt", "w2"));
        await ApplyRealAsync(harness, 3, BuildFullPatch("w.txt", "w3"));

        // r2の退避ファイル（w.txtの実体）だけを外部から個別に破損させ、IsRestorable判定
        // （フォルダ・manifestの整合性チェック）はすり抜けるが、実際の取り消し処理
        // （UndoContentAsyncの読み取り）は失敗する状況を再現する。SafeFileWriterの検証失敗等、
        // 実際に想定される「フォルダはあるのに個別の書き戻しだけ失敗する」ケースの代わりに使う
        // 決定的な再現手段（chmodは本テスト実行環境がrootで無効化されるため使えない）。
        var r2Summary = await harness.Revisions.ReadAsync(harness.ProjectId, 2);
        File.Delete(Path.Combine(r2Summary.Value.FolderPath, "w.txt"));

        var list = await harness.Revisions.ListAsync(harness.ProjectId);
        var preview = RevisionRestorer.BuildRestoreThroughPreview(list.Value, targetRevision: 1);
        // AreBackupFilesPresentは実際に退避ファイルを見て判定するため、r2は自動的に
        // IsRestorable=falseへ変わる。ここでは「事前チェックをすり抜けた場合」を検証したいので、
        // IsRestorable=trueへ手動で上書きしたRevisionSummaryへ差し替える
        // （事前チェック自体は別テスト「復元不可のリビジョンを含む場合は中止する」で検証済み）。
        var patchedRevisions = preview.RevisionsToUndo
            .Select(r => r.Manifest.Revision == 2 ? r with { IsRestorable = true } : r)
            .ToList();

        var restorer = new RevisionRestorer(harness.Paths);
        var result = await restorer.RestoreThroughAsync(
            harness.ProjectId, harness.ProjectRoot, targetRevision: 1, patchedRevisions, newRevisionNumber: 4, force: false);

        result.IsSuccess.Should().BeFalse("途中で失敗したので成功したと報告してはならない");
        result.Errors.Should().Contain(i => i.Code == ErrorCode.E403 && i.Detail != null && i.Detail.Contains("r3") && i.Detail.Contains("r2"),
            "r3までは取り消せたがr2で失敗し中断したことがメッセージから分かるはず");

        // 中断してもr4（このまとめ戻し自体）はin_progressとして記録され、次回起動時の
        // 「未完了の適用」検出（6.3）に委ねられる。成功（success）にはならない。
        var recorded = await harness.Revisions.ReadAsync(harness.ProjectId, 4);
        recorded.IsSuccess.Should().BeTrue();
        recorded.Value.Manifest.Status.Should().Be(RevisionStatus.InProgress,
            "途中失敗のため success を名乗ってはならない（6.3の中断復帰と同じ扱い）");

        // r3の取り消しは実際に成功しているはず（w.txtはr2適用直後の内容="w2"になっている）。
        System.Text.Encoding.UTF8.GetString(harness.ReadProjectBytes("w.txt")).Should().Contain("w2");
    }
}
