using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Core;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 仕様書6章（適用エンジン）の統合テスト。一時ディレクトリに実ファイルを作成し、
/// ドライラン・本適用・ロールバックを実際のファイルI/Oを通して検証する。
/// </summary>
public class ApplyEngineTests
{
    static ApplyEngineTests()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private static byte[] Utf8Bytes(string text, bool withBom)
    {
        var body = new UTF8Encoding(false).GetBytes(text);
        if (!withBom) return body;
        return new byte[] { 0xEF, 0xBB, 0xBF }.Concat(body).ToArray();
    }

    private static byte[] ShiftJisBytes(string text) => Encoding.GetEncoding(932).GetBytes(text);

    private static string BuildSrPatch(string path, string search, string replace) =>
        $"<<<< FILE: {path}\n<<<<<<< SEARCH\n{search}\n=======\n{replace}\n>>>>>>> REPLACE\n";

    // ------------------------------------------------------------------
    // 根本原因: GraftResult<T>.Value は「成功かつ値がnull」を「失敗」と誤判定し例外を投げる。
    // Core/GraftResult.cs の Value ゲッター（80〜83行目）:
    //     public T Value => IsSuccess && _value is not null
    //         ? _value
    //         : throw new InvalidOperationException("失敗した結果から値を取得しようとしました。");
    // GraftResult<RevisionSummary?> のように「成功だが値はnull」を正当な結果として返す箇所
    // （RevisionStore.FindByPatchHashAsync）で、呼び出し側（DryRunPlanner.CheckDuplicateAsync
    // 253行目）が IsSuccess を確認した直後に .Value を読むと例外が飛ぶ。
    // この結果、一致するリビジョンが存在しない通常のケースを含む、事実上すべてのドライランが
    // 例外で落ちる。以下のテストはこの根本原因を最小再現する。以降の統合テストが軒並み
    // 失敗するのはこの1件が原因であり、個々のシナリオの実装に問題があるわけではない。
    // ------------------------------------------------------------------

    [Fact(DisplayName = "根本原因: GraftResult<T>.Valueは成功かつ値がnullの場合でもnullを返すべき（現状は例外を投げる）")]
    public void GraftResultは成功時に値がnullでも例外を投げるべきではない()
    {
        var ok = GraftResult<string?>.Ok(null);

        var act = () => ok.Value;

        act.Should().NotThrow(
            "IsSuccess=trueの結果は値がnullであっても正当な成功結果であり、Valueアクセスで例外を投げてはならない。" +
            "この不具合によりRevisionStore.FindByPatchHashAsyncが成功かつnullを返す通常のケース" +
            "（一致するリビジョンが存在しない）でDryRunPlanner.CheckDuplicateAsync（253行目）が" +
            "例外を送出し、ドライラン全体が失敗する。");
    }

    // ------------------------------------------------------------------
    // 6.1 ドライランはファイルへ一切書き込まない
    // ------------------------------------------------------------------

    [Fact(DisplayName = "ドライランはファイルを一切変更しない")]
    public async Task ドライランはファイルを変更しない()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        harness.WriteProjectText("a.txt", "line1\nline2\n");
        var before = harness.ReadProjectBytes("a.txt");

        var patchText = BuildSrPatch("a.txt", "line1", "line1changed");
        var ctx = harness.MakeContext(1);
        await harness.DryRunAsync(patchText, ctx);

        harness.ReadProjectBytes("a.txt").Should().Equal(before, "ドライランはファイルへ一切書き込まないはず（仕様書6.1）");
    }

    // ------------------------------------------------------------------
    // 6.1 allOrNothing / 部分適用モードとバックアップ
    // ------------------------------------------------------------------

    [Fact(DisplayName = "allOrNothingでは1件でも失敗すれば全体を中止し、いずれのファイルも変更されない")]
    public async Task allOrNothingで1件失敗すると全体を中止する()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        harness.WriteProjectText("ok.txt", "hello\n");
        harness.WriteProjectText("bad.txt", "存在しない検索対象は含まれていません\n");

        var patchText = BuildSrPatch("ok.txt", "hello", "world")
            + "\n" + BuildSrPatch("bad.txt", "見つからない文字列", "置換後");
        var ctx = harness.MakeContext(1);
        var dryRun = await harness.DryRunAsync(patchText, ctx);
        dryRun.FailedCount.Should().BeGreaterThan(0, "bad.txt側はSEARCH不一致で失敗するはず");

        var apply = await harness.ApplyAsync(dryRun, ctx);

        apply.IsSuccess.Should().BeFalse();
        harness.ReadProjectBytes("ok.txt").Should().Equal(Encoding.UTF8.GetBytes("hello\n"),
            "allOrNothingで中止された場合、成功側のファイルも変更されてはならない");
        Directory.Exists(harness.Paths.GetProjectBackupDirectory(harness.ProjectId)).Should().BeFalse(
            "致命的失敗はバックアップ開始前に検出されるため、バックアップフォルダ自体が作られないはず");
    }

    [Fact(DisplayName = "部分適用モードでは失敗したブロックの対象ファイルも含め全件バックアップする")]
    public async Task 部分適用モードでも失敗ファイルを含め全件バックアップする()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        harness.WriteProjectText("ok.txt", "hello\n");
        harness.WriteProjectText("bad.txt", "存在しない検索対象は含まれていません\n");

        var patchText = BuildSrPatch("ok.txt", "hello", "world")
            + "\n" + BuildSrPatch("bad.txt", "見つからない文字列", "置換後");
        var settings = new Graft.Infra.Settings { ApplyMode = "partial" };
        var ctx = harness.MakeContext(1, settings);
        var dryRun = await harness.DryRunAsync(patchText, ctx);

        var apply = await harness.ApplyAsync(dryRun, ctx);

        apply.IsSuccess.Should().BeTrue("部分適用モードでは成功ブロックのみ適用され全体は成功するはず");
        var revisionDir = harness.Paths.GetRevisionDirectory(harness.ProjectId, apply.Value.Revision, apply.Value.AppliedAt);
        File.Exists(Path.Combine(revisionDir, "ok.txt")).Should().BeTrue("適用対象のファイルはバックアップされるはず");
        File.Exists(Path.Combine(revisionDir, "bad.txt")).Should().BeTrue(
            "失敗したブロックの対象ファイルも部分適用モードでは全件バックアップの対象になるはず（仕様書6.1）");
    }

    // ------------------------------------------------------------------
    // 6.6 適用順序
    // ------------------------------------------------------------------

    [Fact(DisplayName = "MKDIR→RENAME→FULL/SR→DELETEの順に適用され、リネーム後のパスへのSRが正しく解決される")]
    public async Task 適用順序どおりに処理される()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        harness.WriteProjectText("old.py", "def foo():\n    return 1\n");
        harness.WriteProjectText("obsolete.txt", "temp\n");

        var patchText = """
            <<<< MKDIR: newdir

            <<<< RENAME: old.py -> newdir/moved.py

            <<<< FILE: newdir/moved.py
            <<<<<<< SEARCH
                return 1
            =======
                return 2
            >>>>>>> REPLACE

            <<<< DELETE: obsolete.txt
            """;
        var ctx = harness.MakeContext(1);
        var dryRun = await harness.DryRunAsync(patchText, ctx);
        var apply = await harness.ApplyAsync(dryRun, ctx);

        apply.IsSuccess.Should().BeTrue(string.Join(", ", apply.Issues.Select(i => i.ToDisplayText())));
        Directory.Exists(Path.Combine(harness.ProjectRoot, "newdir")).Should().BeTrue();
        harness.ProjectFileExists("old.py").Should().BeFalse("リネーム元は残らないはず");
        harness.ProjectFileExists("obsolete.txt").Should().BeFalse("DELETEされたファイルは残らないはず");
        var content = Encoding.UTF8.GetString(harness.ReadProjectBytes("newdir/moved.py"));
        content.Should().Contain("return 2", "リネーム後のパスに対するSRが正しく解決されているはず");
    }

    [Fact(DisplayName = "同一パッチ内でリネーム前の旧パスを参照するブロックはE207になる")]
    public async Task 旧パス参照はE207になる()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        harness.WriteProjectText("a.py", "original\n");

        var patchText = """
            <<<< RENAME: a.py -> b.py

            <<<< FILE: a.py
            <<<<<<< SEARCH
            original
            =======
            changed
            >>>>>>> REPLACE
            """;
        var ctx = harness.MakeContext(1);
        var dryRun = await harness.DryRunAsync(patchText, ctx);

        var aPlan = dryRun.Plans.Single(p => p.Path == "a.py" && p.Operation != EntryOperation.Rename);
        aPlan.CanApply.Should().BeFalse();
        aPlan.Issues.Should().Contain(i => i.Code == ErrorCode.E207);
    }

    // ------------------------------------------------------------------
    // FULL / SR 混在（E208） / 二重適用検知（E302）
    // ------------------------------------------------------------------

    [Fact(DisplayName = "同一ファイルへのFULLとSRの混在はE208警告になり、FULL適用後の内容にSRが解決される")]
    public async Task FULLとSRの混在はE208警告付きで正しく解決される()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);

        var patchText = """
            <<<< FILE: mix.txt MODE=FULL
            line_a
            line_b
            >>>> END

            <<<< FILE: mix.txt
            <<<<<<< SEARCH
            line_b
            =======
            line_c
            >>>>>>> REPLACE
            """;
        var ctx = harness.MakeContext(1);
        var dryRun = await harness.DryRunAsync(patchText, ctx);

        dryRun.Plans.Should().Contain(p => p.Issues.Any(i => i.Code == ErrorCode.E208 && i.Severity == Severity.Warning));

        var apply = await harness.ApplyAsync(dryRun, ctx);
        apply.IsSuccess.Should().BeTrue();
        var content = Encoding.UTF8.GetString(harness.ReadProjectBytes("mix.txt"));
        content.Should().Contain("line_a").And.Contain("line_c").And.NotContain("line_b",
            "FULLが先に適用され、その結果に対してSRが解決されているはず");
    }

    [Fact(DisplayName = "適用済みパッチの再投入はE302で中止され、強制再適用なら警告付きで成功する")]
    public async Task 二重適用はE302で検知され強制再適用は警告付きで成功する()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);

        var patchText = """
            <<<< FILE: dup.txt MODE=FULL
            duplicate content
            >>>> END
            """;
        var firstCtx = harness.MakeContext(1);
        var dryRun = await harness.DryRunAsync(patchText, firstCtx);
        var first = await harness.ApplyAsync(dryRun, firstCtx);
        first.IsSuccess.Should().BeTrue();

        var secondCtx = harness.MakeContext(2, forceReapply: false);
        var second = await harness.ApplyAsync(dryRun, secondCtx);
        second.IsSuccess.Should().BeFalse();
        second.Errors.Should().Contain(i => i.Code == ErrorCode.E302);

        var thirdCtx = harness.MakeContext(3, forceReapply: true);
        var third = await harness.ApplyAsync(dryRun, thirdCtx);
        third.IsSuccess.Should().BeTrue("ForceReapply指定時は警告付きで再適用できるはず");
        third.Issues.Should().Contain(i => i.Code == ErrorCode.E302 && i.Severity == Severity.Warning);
    }

    // ------------------------------------------------------------------
    // 6.4 エンコーディング・改行・末尾改行の保持
    // ------------------------------------------------------------------

    [Theory(DisplayName = "SR適用後もエンコーディング・改行・末尾改行の組み合わせを保持する")]
    [InlineData(true, "\r\n", true)]
    [InlineData(false, "\n", true)]
    [InlineData(false, "\r\n", false)]
    public async Task エンコーディングと改行を保持する(bool bom, string newLine, bool endsWithNewLine)
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        var tail = endsWithNewLine ? newLine : string.Empty;
        var original = Utf8Bytes($"keep1{newLine}ターゲット行{newLine}keep3{tail}", bom);
        harness.WriteProjectBytes("sample.txt", original);

        var patchText = BuildSrPatch("sample.txt", "ターゲット行", "置換後の行");
        var ctx = harness.MakeContext(1);
        var dryRun = await harness.DryRunAsync(patchText, ctx);
        var apply = await harness.ApplyAsync(dryRun, ctx);

        apply.IsSuccess.Should().BeTrue(string.Join(", ", apply.Issues.Select(i => i.ToDisplayText())));
        var bytes = harness.ReadProjectBytes("sample.txt");
        if (bom) bytes.Take(3).Should().Equal(0xEF, 0xBB, 0xBF);
        var bodyStart = bom ? 3 : 0;
        var text = new UTF8Encoding(false).GetString(bytes, bodyStart, bytes.Length - bodyStart);
        text.Should().Be($"keep1{newLine}置換後の行{newLine}keep3{tail}",
            "未変更行の改行・末尾改行・BOMの有無がすべて維持されているはず");
    }

    [Fact(DisplayName = "Shift_JISファイルへのSR適用後もエンコーディングと改行を保持する")]
    public async Task ShiftJISファイルの保持()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        var original = ShiftJisBytes("keep1\r\nターゲット行\r\nkeep3\r\n");
        harness.WriteProjectBytes("sjis.txt", original);

        var patchText = BuildSrPatch("sjis.txt", "ターゲット行", "置換後の行");
        var ctx = harness.MakeContext(1);
        var dryRun = await harness.DryRunAsync(patchText, ctx);
        var apply = await harness.ApplyAsync(dryRun, ctx);

        apply.IsSuccess.Should().BeTrue();
        var bytes = harness.ReadProjectBytes("sjis.txt");
        var text = Encoding.GetEncoding(932).GetString(bytes);
        text.Should().Be("keep1\r\n置換後の行\r\nkeep3\r\n");
    }

    [Fact(DisplayName = "改行混在ファイルでは未変更行それぞれの改行コードが個別に維持される")]
    public async Task 改行混在ファイルの未変更行は個別に維持される()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        // 優勢な改行はLF（2対1）。CRLFの「ターゲット行」は変更せず、先頭行だけを置き換える。
        harness.WriteProjectBytes("mixed.txt", Utf8Bytes("先頭行\nターゲット行\r\n末尾行\n", withBom: false));

        var patchText = BuildSrPatch("mixed.txt", "先頭行", "置換後の行");
        var ctx = harness.MakeContext(1);
        var dryRun = await harness.DryRunAsync(patchText, ctx);
        var apply = await harness.ApplyAsync(dryRun, ctx);

        apply.IsSuccess.Should().BeTrue();
        var text = Encoding.UTF8.GetString(harness.ReadProjectBytes("mixed.txt"));
        // 6.4: 主たる規則は「優勢な改行コードに統一する」で、「混在は可能な限り維持」は
        // 未変更行を書き換えないという意味。置換で生成された行は優勢な改行（LF）を使い、
        // 未変更のCRLF行はCRLFのまま残るのが正しい。
        text.Should().Be("置換後の行\nターゲット行\r\n末尾行\n",
            "未変更行は優勢な改行と異なっていても元の改行コードのまま維持されるはず");
    }

    // ------------------------------------------------------------------
    // 失敗時のロールバック
    // ------------------------------------------------------------------

    [Fact(DisplayName = "書き込み失敗時は例外を投げずロールバックされ、成功済みの変更も取り消される")]
    public async Task 書き込み失敗時は例外を投げずロールバックされる()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        harness.WriteProjectText("keep.txt", "original\n");
        // "blocked" をフォルダではなく通常ファイルとして作り、FULL形式が親フォルダ作成に
        // 失敗するケースを再現する（keep.txt側は先に書き込みが成功する順序で並べる）。
        harness.WriteProjectFileAsBlocker("blocked", "私はフォルダではなく通常のファイルです");

        var patchText = """
            <<<< FILE: keep.txt
            <<<<<<< SEARCH
            original
            =======
            changed
            >>>>>>> REPLACE

            <<<< FILE: blocked/file.txt MODE=FULL
            new content
            >>>> END
            """;
        var ctx = harness.MakeContext(1);
        var dryRun = await harness.DryRunAsync(patchText, ctx);

        Func<Task<GraftResult<RevisionManifest>>> act = () => harness.ApplyAsync(dryRun, ctx);
        var apply = await act.Should().NotThrowAsync(
            "適用エンジンはユーザー操作起因の失敗を例外ではなくGraftResultで返す設計であるはず（附録A・仕様書6.4）");

        apply.Subject.IsSuccess.Should().BeFalse("書き込みに失敗した場合は失敗として返るはず");
        var keepContent = Encoding.UTF8.GetString(harness.ReadProjectBytes("keep.txt"));
        keepContent.Should().Be("original\n",
            "失敗時は同一適用内で先に成功していたkeep.txtへの変更もロールバックされているはず（仕様書6.1・6.3）");
    }
}
