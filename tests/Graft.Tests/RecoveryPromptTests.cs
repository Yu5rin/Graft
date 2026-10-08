using System.Linq;
using FluentAssertions;
using Graft.Core;
using Graft.Features;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 失敗ブロックの修正依頼文（<see cref="RecoveryPrompt.Build"/>）と継続依頼文
/// （<see cref="RecoveryPrompt.BuildContinuation"/>）の文面を検証する。
/// 以前の文面には、失敗したSEARCH本文・置換後の内容・出力形式の指示がいずれも無く、
/// 非SRブロックは「現在のコードを取得できませんでした。」と取得失敗に見える文言になっていた。
/// ブロックは実際のパーサ（形式の判別まで含めて）で作り、文面の期待値は緩めず正確に確かめる。
/// </summary>
public class RecoveryPromptTests
{
    private const string GraftPatch =
        "<<<< FILE: src/a.cs\n" +
        "<<<<<<< SEARCH  # 挨拶を変える\n" +
        "Console.WriteLine(\"hello\");\n" +
        "=======\n" +
        "Console.WriteLine(\"world\");\n" +
        ">>>>>>> REPLACE\n";

    private const string StandardPatch =
        "src/a.cs\n" +
        "<<<<<<< SEARCH\n" +
        "Console.WriteLine(\"hello\");\n" +
        "=======\n" +
        "Console.WriteLine(\"world\");\n" +
        ">>>>>>> REPLACE\n";

    private const string UnifiedPatch =
        "--- a/src/a.cs\n" +
        "+++ b/src/a.cs\n" +
        "@@ -1,3 +1,3 @@\n" +
        " using System;\n" +
        "-Console.WriteLine(\"hello\");\n" +
        "+Console.WriteLine(\"world\");\n" +
        " // end\n";

    private const string CurrentFile = "using System;\nConsole.WriteLine(\"HELLO\");\n// end\n";

    private static BlockPlan FailedPlan(string patchText, int blockIndex = 0)
    {
        var patch = new PatchParser().Parse(patchText).Value;
        var block = patch.Blocks[blockIndex];
        var pair = (block as SearchReplaceBlock)?.Pairs[0];
        return new BlockPlan
        {
            Block = block,
            Pair = pair,
            Path = block.Path,
            Operation = EntryOperation.Modify,
            Stage = MatchStage.Failed,
            CanApply = false,
            Description = pair?.Description ?? block.Description,
            Issues = new[] { GraftIssue.Of(ErrorCode.E101, line: pair?.SourceLine) },
        };
    }

    private static string Build(params BlockPlan[] plans)
        => RecoveryPrompt.Build(plans, _ => CurrentFile);

    // ------------------------------------------------------------------
    // 形式の判別（パーサが解析結果へ刻む）
    // ------------------------------------------------------------------

    [Fact(DisplayName = "形式の判別: Graft独自・標準SR・unified diffがブロックとパッチの両方に刻まれる")]
    public void 解析結果に形式が刻まれる()
    {
        var parser = new PatchParser();

        var graft = parser.Parse(GraftPatch).Value;
        graft.Format.Should().Be(PatchFormat.Graft);
        graft.Blocks.Should().OnlyContain(b => b.SourceFormat == PatchFormat.Graft);

        var standard = parser.Parse(StandardPatch).Value;
        standard.Format.Should().Be(PatchFormat.StandardSearchReplace);
        standard.Blocks.Should().OnlyContain(b => b.SourceFormat == PatchFormat.StandardSearchReplace);

        var unified = parser.Parse(UnifiedPatch).Value;
        unified.Format.Should().Be(PatchFormat.UnifiedDiff);
        unified.Blocks.Should().OnlyContain(b => b.SourceFormat == PatchFormat.UnifiedDiff);
    }

    [Fact(DisplayName = "形式の判別: 途中で切れた標準SR形式のパッチも標準SR形式として扱う")]
    public void 切れた標準SRも形式を保つ()
    {
        var truncated = StandardPatch + "src/b.cs\n<<<<<<< SEARCH\nfoo\n=======\nba";
        var patch = new PatchParser().Parse(truncated).Value;

        patch.IsTruncated.Should().BeTrue();
        patch.Format.Should().Be(PatchFormat.StandardSearchReplace);
    }

    [Fact(DisplayName = "形式の判別: 異なる形式のパッチをキューで結合しても、ブロックごとの形式は保たれる")]
    public void キュー結合でもブロックの形式を保つ()
    {
        var parser = new PatchParser();
        using var ws = new Graft.Tests.TestSupport.TempWorkspace();
        var queue = new PatchQueue(new Graft.Infra.AppPaths(ws.CreateDirectory("app")));
        queue.Add(parser.Parse(GraftPatch).Value);
        queue.Add(parser.Parse(StandardPatch.Replace("src/a.cs", "src/b.cs")).Value);

        var merged = queue.Merge().Value;

        merged.Blocks.Select(b => b.SourceFormat).Should().Equal(PatchFormat.Graft, PatchFormat.StandardSearchReplace);
        merged.Format.Should().Be(PatchFormat.Graft, "混在のときは既定のGraft形式とみなす（依頼文はブロックごとの形式を見る）");
    }

    // ------------------------------------------------------------------
    // 失敗したSEARCH本文・REPLACE・現在のコード
    // ------------------------------------------------------------------

    [Fact(DisplayName = "修正依頼: 失敗したSEARCH本文・置換後・変更の意図・現在のコードが入る")]
    public void 失敗したSEARCH本文が入る()
    {
        var text = Build(FailedPlan(GraftPatch));

        text.Should().Contain("■ src/a.cs — SEARCH部が見つかりません");
        text.Should().Contain("変更の意図: 挨拶を変える");
        text.Should().Contain("失敗したSEARCH部（1行）:\n```\nConsole.WriteLine(\"hello\");\n```");
        text.Should().Contain("置換後（REPLACE・1行。この変更の意図は保ってください）:\n```\nConsole.WriteLine(\"world\");\n```");
        text.Should().Contain("現在のコード（1〜3行目）:\n```\nusing System;\nConsole.WriteLine(\"HELLO\");\n// end\n```");
        text.Should().Contain("失敗したブロックだけを出し直してください");
        text.Should().Contain("適用に成功したブロックは再出力しないでください");
    }

    [Fact(DisplayName = "修正依頼: REPLACEが空（削除）のときは、その旨を書く")]
    public void 削除のREPLACEは空と明記する()
    {
        var plan = FailedPlan(GraftPatch.Replace("Console.WriteLine(\"world\");\n", ""));

        Build(plan).Should().Contain("置換後（REPLACE）: 空（この部分を削除する変更です）");
    }

    [Fact(DisplayName = "修正依頼: 同一ブロックの2個目のペアが失敗しても、そのSEARCH本文が入る（先頭ペアに化けない）")]
    public void 失敗したのは2個目のペア()
    {
        var patch = new PatchParser().Parse(
            "<<<< FILE: src/a.cs\n" +
            "<<<<<<< SEARCH\nfirst\n=======\nFIRST\n>>>>>>> REPLACE\n" +
            "<<<<<<< SEARCH\nsecond\n=======\nSECOND\n>>>>>>> REPLACE\n").Value;
        var block = (SearchReplaceBlock)patch.Blocks[0];
        var plan = new BlockPlan
        {
            Block = block, Pair = block.Pairs[1], Path = "src/a.cs", Operation = EntryOperation.Modify,
            Stage = MatchStage.Failed, CanApply = false,
            Issues = new[] { GraftIssue.Of(ErrorCode.E101, line: block.Pairs[1].SourceLine) },
        };

        var text = Build(plan);

        text.Should().Contain("```\nsecond\n```").And.Contain("```\nSECOND\n```");
        text.Should().NotContain("first", "失敗していない1個目のペアの本文を載せてはならない");
    }

    [Fact(DisplayName = "修正依頼: SEARCH部を持たないブロックは、取得失敗ではなく理由を明記する")]
    public void 非SRブロックの文言()
    {
        var patch = new PatchParser().Parse("<<<< DELETE: old.txt\n").Value;
        var plan = new BlockPlan
        {
            Block = patch.Blocks[0], Path = "old.txt", Operation = EntryOperation.Delete,
            Stage = MatchStage.None, CanApply = false,
            Issues = new[] { GraftIssue.Of(ErrorCode.E210, path: "old.txt") },
        };

        var text = Build(plan);

        text.Should().Contain("このブロックはファイルの削除のため、SEARCH部の引用と現在のコードの抜粋はありません。");
        text.Should().NotContain("現在のコードを取得できませんでした");
    }

    [Fact(DisplayName = "修正依頼: 現在のファイルを読めないときは従来どおり取得できなかった旨を書く")]
    public void 現在のコードが読めない()
    {
        var text = RecoveryPrompt.Build(new[] { FailedPlan(GraftPatch) }, _ => null);

        text.Should().Contain("失敗したSEARCH部").And.Contain("現在のコードを取得できませんでした。");
    }

    // ------------------------------------------------------------------
    // 形式ごとの指示
    // ------------------------------------------------------------------

    [Fact(DisplayName = "修正依頼: Graft独自形式で受け取ったときは、同じGraft形式の骨組みで出し直すよう指示する")]
    public void Graft形式の指示()
    {
        var text = Build(FailedPlan(GraftPatch));

        text.Should().Contain("【出力形式】\n受け取ったパッチと同じGraft形式で出力してください。");
        text.Should().Contain("<<<< FILE: 相対パス\n<<<<<<< SEARCH  # このペアの変更内容を1行で");
        text.Should().NotContain("標準SEARCH/REPLACE形式").And.NotContain("unified diff");
        text.Should().NotContain("失敗したハンク");
    }

    [Fact(DisplayName = "修正依頼: 標準SR形式で受け取ったときは、同じ標準SR形式（パスだけの行から始まる骨組み）で出し直すよう指示する")]
    public void 標準SR形式の指示()
    {
        var text = Build(FailedPlan(StandardPatch));

        text.Should().Contain("【出力形式】\n受け取ったパッチと同じ標準SEARCH/REPLACE形式で出力してください。");
        text.Should().Contain("```\n相対パス\n<<<<<<< SEARCH\n（現在のコードに一致する修正前のコード）\n=======\n（修正後のコード）\n>>>>>>> REPLACE\n```");
        text.Should().NotContain("<<<< FILE:", "標準SR形式の依頼にGraft独自ヘッダを混ぜてはならない");
        text.Should().NotContain("Graft形式").And.NotContain("unified diff");
    }

    [Fact(DisplayName = "修正依頼: unified diffで受け取ったときは、同じunified diffで失敗したハンクだけを出し直すよう指示する")]
    public void UnifiedDiffの指示()
    {
        var text = Build(FailedPlan(UnifiedPatch));

        text.Should().Contain("【出力形式】\n受け取ったパッチと同じunified diff形式で出力してください。失敗したハンクだけを出し直し");
        text.Should().Contain("--- a/相対パス\n+++ b/相対パス\n@@ -開始行,行数 +開始行,行数 @@");
        text.Should().Contain("失敗したハンクの変更前のコード（SEARCH相当・3行）:\n```\nusing System;\nConsole.WriteLine(\"hello\");\n// end\n```");
        text.Should().NotContain("<<<<<<< SEARCH").And.NotContain("Graft形式");
    }

    [Fact(DisplayName = "修正依頼: 形式が混在する失敗ブロックでは、形式ごとに対象ファイルを添えて指示し、各ブロックに形式を付す")]
    public void 混在形式の指示()
    {
        var graft = FailedPlan(GraftPatch);
        var standard = FailedPlan(StandardPatch.Replace("src/a.cs", "src/b.cs"));

        var text = Build(graft, standard);

        text.Should().Contain("【出力形式: Graft形式】（対象: src/a.cs）");
        text.Should().Contain("【出力形式: 標準SEARCH/REPLACE形式】（対象: src/b.cs）");
        text.Should().Contain("■ src/a.cs — SEARCH部が見つかりません（Graft形式）");
        text.Should().Contain("■ src/b.cs — SEARCH部が見つかりません（標準SEARCH/REPLACE形式）");
    }

    [Theory(DisplayName = "修正依頼: 生成した文面そのものをクリップボード監視がパッチと誤検知しない")]
    [InlineData(GraftPatch)]
    [InlineData(StandardPatch)]
    [InlineData(UnifiedPatch)]
    public void 依頼文はパッチと誤検知されない(string patchText)
    {
        var text = Build(FailedPlan(patchText));

        PatchTextDetector.LooksLikePatch(text).Should().BeFalse(
            "骨組み・引用はすべて閉じたコードフェンスの内側にあるため、貼り戻しの自動検知に反応してはならない");
    }

    // ------------------------------------------------------------------
    // 中略
    // ------------------------------------------------------------------

    [Fact(DisplayName = "中略: 上限（40行）ちょうどの本文は省略せず全行を載せる")]
    public void 上限ちょうどは中略しない()
    {
        var lines = Enumerable.Range(1, RecoveryPrompt.MaxQuotedLines).Select(i => $"行{i}").ToList();

        var excerpt = RecoveryPrompt.Excerpt(lines);

        excerpt.Should().Be(string.Join("\n", lines));
        excerpt.Should().NotContain("中略");
    }

    [Fact(DisplayName = "中略: 100行のSEARCHは先頭20行と末尾20行を残し、中間60行の省略を明記する")]
    public void 長いSEARCHは中略される()
    {
        var search = string.Join("\n", Enumerable.Range(1, 100).Select(i => $"search{i:000}"));
        var patch = "<<<< FILE: src/a.cs\n<<<<<<< SEARCH\n" + search + "\n=======\nnew\n>>>>>>> REPLACE\n";

        var text = Build(FailedPlan(patch));

        text.Should().Contain("失敗したSEARCH部（100行）:");
        text.Should().Contain("search001").And.Contain("search020");
        text.Should().Contain("（中略: 中間の60行を省略。全体は100行）");
        text.Should().Contain("search081").And.Contain("search100");
        text.Should().NotContain("search021").And.NotContain("search080", "中間の行は載せない");
    }

    [Fact(DisplayName = "中略: 極端に長い1行は打ち切って、省略した旨と全体の文字数を明記する")]
    public void 長い1行は打ち切られる()
    {
        var longLine = new string('x', RecoveryPrompt.MaxQuotedLineChars + 500);

        var excerpt = RecoveryPrompt.Excerpt(new[] { longLine });

        excerpt.Should().StartWith(new string('x', RecoveryPrompt.MaxQuotedLineChars) + "…（この行は長いため");
        excerpt.Should().Contain($"全体は{longLine.Length}文字");
        excerpt.Length.Should().BeLessThan(longLine.Length);
    }

    [Fact(DisplayName = "引用: 本文に ``` が含まれていても、囲みのフェンスが途中で閉じない")]
    public void 本文のバッククォートに負けないフェンス()
    {
        var patch = "<<<< FILE: README.md\n<<<<<<< SEARCH\n```cs\nvar a = 1;\n```\n=======\nx\n>>>>>>> REPLACE\n";

        var text = Build(FailedPlan(patch));

        text.Should().Contain("````\n```cs\nvar a = 1;\n```\n````");
    }

    // ------------------------------------------------------------------
    // 継続依頼
    // ------------------------------------------------------------------

    [Theory(DisplayName = "継続依頼: 受け取った形式に合わせて「同じ◯◯で」と頼む")]
    [InlineData(PatchFormat.Graft, "同じGraft形式で出力してください。")]
    [InlineData(PatchFormat.StandardSearchReplace, "同じ標準SEARCH/REPLACE形式で出力してください。")]
    [InlineData(PatchFormat.UnifiedDiff, "同じunified diff形式で出力してください。")]
    public void 継続依頼は形式に合わせる(PatchFormat format, string expected)
    {
        var text = RecoveryPrompt.BuildContinuation(new[] { "a", "b", "c", "d" }, format);

        text.Should().Contain("出力が途中で切れています。以下の続きから、" + expected);
        text.Should().EndWith("最後に受け取った行:\nb\nc\nd", "末尾3行だけを含める（従来どおり）");
    }

    [Fact(DisplayName = "継続依頼: 形式を省略した呼び出しは従来どおりGraft形式")]
    public void 継続依頼の既定はGraft形式()
    {
        RecoveryPrompt.BuildContinuation(new[] { "x" }).Should().Contain("同じGraft形式で出力してください。");
    }

    [Theory(DisplayName = "継続依頼: 他の形式の名前を混ぜない")]
    [InlineData(PatchFormat.StandardSearchReplace, "Graft形式", "unified diff")]
    [InlineData(PatchFormat.UnifiedDiff, "Graft形式", "標準SEARCH/REPLACE")]
    [InlineData(PatchFormat.Graft, "標準SEARCH/REPLACE", "unified diff")]
    public void 継続依頼に他形式が混ざらない(PatchFormat format, string other1, string other2)
    {
        var text = RecoveryPrompt.BuildContinuation(new[] { "x" }, format);

        text.Should().NotContain(other1).And.NotContain(other2);
    }
}
