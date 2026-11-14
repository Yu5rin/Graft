using System.Linq;
using FluentAssertions;
using Graft.Core;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// <see cref="BlockResolver"/> の単体テスト。
/// バグA（同一FILEセクション内の1ペア失敗が、成功しているペアまで連座させる）と
/// バグB（失敗ユニットに BeforeText が設定されずインライン編集が壊れる）の回帰を検証する。
/// </summary>
public class BlockResolverTests
{
    private static SearchReplacePair Pair(string search, string replace, int sourceLine = 1)
        => new() { SearchText = search, ReplaceText = replace, SourceLine = sourceLine };

    private static SearchReplaceBlock Block(params SearchReplacePair[] pairs)
        => new() { Path = "a.txt", Pairs = pairs };

    [Fact(DisplayName = "バグA回帰: 同一ブロック内の1ペア失敗は、成功している別ペアを道連れにしない")]
    public void 失敗ペアと成功ペアが混在しても互いに独立したユニットになる()
    {
        var original = new[] { "line1", "line2", "line3" };
        var failingPair = Pair("存在しない検索対象", "置換後", sourceLine: 2);
        var okPair = Pair("line2", "line2changed", sourceLine: 5);
        var block = Block(failingPair, okPair);

        var resolution = BlockResolver.ResolveFile(original, new PatchBlock[] { block }, new MatchEngine());

        // 1ブロック2ペアは、それぞれ独立した1ユニットとして扱われるはず（ブロック単位の連座をしない）。
        resolution.Units.Should().HaveCount(2, "1ブロック2ペアはそれぞれ独立した1ユニットになるはず");

        var failedUnit = resolution.Units.Single(u => !u.CanApply);
        failedUnit.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E101,
            "SEARCH部が見つからないペアはE101で失敗するはず");
        failedUnit.SourcePair.Should().BeSameAs(failingPair);
        // バグB回帰: 失敗ユニットにも照合の基準テキスト（baseText）がBeforeTextとして残るはず。
        failedUnit.BeforeText.Should().Be("line1\nline2\nline3",
            "失敗ユニットのBeforeTextは、インライン編集パネルが再判定に使う基準テキストと一致するはず");

        var successUnit = resolution.Units.Single(u => u.CanApply);
        successUnit.SourcePair.Should().BeSameAs(okPair);
        successUnit.AfterText.Should().Be("line1\nline2changed\nline3",
            "失敗ペアの影響を受けず、成功ペアだけが正しく適用されるはず");

        resolution.FinalLines.Select(l => l.Text).Should().Equal(
            new[] { "line1", "line2changed", "line3" },
            "失敗ペアに引きずられず、成功ペアの結果だけが最終行配列に反映されるはず");
    }

    [Fact(DisplayName = "全ペア成功時は従来どおり複数ペアが順に適用される（回帰なし）")]
    public void 全ペア成功時は複数ペアが順に適用される()
    {
        // 以前は「1件でも失敗すればブロック全体を失敗にする」実装だったため、全ペア成功時の
        // 挙動（複数ペアの順次適用）自体は変わらないことを確認する回帰テスト。
        var original = new[] { "def a():", "    pass", "def b():", "    pass" };
        var block = Block(
            Pair("def a():\n    pass", "def a():\n    return 1", sourceLine: 1),
            Pair("def b():\n    pass", "def b():\n    return 2", sourceLine: 5));

        var resolution = BlockResolver.ResolveFile(original, new PatchBlock[] { block }, new MatchEngine());

        resolution.Units.Should().HaveCount(2);
        resolution.Units.Should().OnlyContain(u => u.CanApply, "全ペア成功時は道連れ失敗が起きないはず");
        resolution.FinalLines.Select(l => l.Text).Should().Equal(
            "def a():", "    return 1", "def b():", "    return 2");
    }

    [Fact(DisplayName = "全ペア失敗時はいずれのユニットもBeforeTextに元テキストを持つ")]
    public void 全ペア失敗時もBeforeTextが設定される()
    {
        var original = new[] { "keep" };
        var block = Block(
            Pair("見つからない1", "置換1", sourceLine: 1),
            Pair("見つからない2", "置換2", sourceLine: 5));

        var resolution = BlockResolver.ResolveFile(original, new PatchBlock[] { block }, new MatchEngine());

        resolution.Units.Should().HaveCount(2);
        resolution.Units.Should().OnlyContain(u => !u.CanApply && u.BeforeText == "keep");
        resolution.FinalLines.Select(l => l.Text).Should().Equal(
            new[] { "keep" }, "元ファイルは一切変更されないはず");
    }

    // ------------------------------------------------------------------
    // B2: 範囲が重なる SEARCH・重複したペアを「成功」で書き込まない
    // ------------------------------------------------------------------

    private static readonly string[] SixLines = { "l1", "l2", "l3", "l4", "l5", "l6" };

    [Fact(DisplayName = "B2: 範囲が重なるペアは、後のペアが失敗になり、内容は壊れない")]
    public void 範囲が重なるペアは後のペアを失敗にする()
    {
        // 修正前は、ペア2の先頭行(Y4)が消えた内容が「成功」として返った（l4が両方の範囲に含まれる）。
        var first = Pair("l2\nl3\nl4", "X2\nX3\nX4\nX4b", sourceLine: 1);
        var second = Pair("l4\nl5", "Y4\nY5\nY5b\nY5c", sourceLine: 5);

        var resolution = BlockResolver.ResolveFile(SixLines, new PatchBlock[] { Block(first, second) }, new MatchEngine());

        var failed = resolution.Units.Single(u => !u.CanApply);
        failed.SourcePair.Should().BeSameAs(second);
        failed.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E105);
        resolution.Units.Single(u => u.CanApply).SourcePair.Should().BeSameAs(first);
        resolution.FinalLines.Select(l => l.Text).Should().Equal(
            "l1", "X2", "X3", "X4", "X4b", "l5", "l6");
    }

    [Fact(DisplayName = "B2: 範囲が接しているだけ（重ならない）のペアは両方適用される")]
    public void 隣接するペアは両方適用される()
    {
        var resolution = BlockResolver.ResolveFile(SixLines, new PatchBlock[]
        {
            Block(Pair("l2\nl3", "A", sourceLine: 1), Pair("l4\nl5", "B", sourceLine: 5)),
        }, new MatchEngine());

        resolution.Units.Should().OnlyContain(u => u.CanApply);
        resolution.FinalLines.Select(l => l.Text).Should().Equal("l1", "A", "B", "l6");
    }

    [Fact(DisplayName = "B2: まったく同じペアが2つあると、後のものが失敗になり二重に入らない")]
    public void 同じペアの重複は後のものを失敗にする()
    {
        var first = Pair("l3", "Z3", sourceLine: 1);
        var second = Pair("l3", "Z3", sourceLine: 5);

        var resolution = BlockResolver.ResolveFile(SixLines, new PatchBlock[] { Block(first, second) }, new MatchEngine());

        resolution.Units.Single(u => !u.CanApply).Issues.Should().ContainSingle(i =>
            i.Code == ErrorCode.E105 && i.Detail!.Contains("重複"));
        resolution.FinalLines.Select(l => l.Text).Should().Equal("l1", "l2", "Z3", "l4", "l5", "l6");
    }

    [Fact(DisplayName = "B2: 別々のブロック（パッチキューで結合した場合）でも重なりを検出する")]
    public void 別ブロック間でも重なりを検出する()
    {
        var a = Block(Pair("l2\nl3", "A", sourceLine: 1));
        var b = Block(Pair("l3\nl4", "B", sourceLine: 1));

        var resolution = BlockResolver.ResolveFile(SixLines, new PatchBlock[] { a, b }, new MatchEngine());

        resolution.Units.Count(u => !u.CanApply).Should().Be(1);
        resolution.FinalLines.Select(l => l.Text).Should().Equal("l1", "A", "l4", "l5", "l6");
    }

    [Fact(DisplayName = "B2: OCCURRENCE=ALL で1ペアが複数箇所に当たる場合は従来どおり全箇所を置き換える")]
    public void OccurrenceAllは従来どおり()
    {
        var original = new[] { "x", "dup", "y", "dup", "z" };
        var block = new SearchReplaceBlock
        {
            Path = "a.txt",
            Occurrence = new OccurrenceSpec { All = true },
            Pairs = new[] { Pair("dup", "DUP", sourceLine: 1) },
        };

        var resolution = BlockResolver.ResolveFile(original, new PatchBlock[] { block }, new MatchEngine());

        resolution.Units.Should().OnlyContain(u => u.CanApply);
        resolution.FinalLines.Select(l => l.Text).Should().Equal("x", "DUP", "y", "DUP", "z");
    }

    [Fact(DisplayName = "B2: APPEND/PREPEND は SEARCH の範囲と接していても影響しない")]
    public void AppendPrependは影響しない()
    {
        var sr = Block(Pair("l1", "L1", sourceLine: 1), Pair("l6", "L6", sourceLine: 5));
        var append = new AppendBlock { Path = "a.txt", Content = "tail" };
        var prepend = new PrependBlock { Path = "a.txt", Content = "head" };

        var resolution = BlockResolver.ResolveFile(SixLines, new PatchBlock[] { prepend, sr, append }, new MatchEngine());

        resolution.Units.Should().OnlyContain(u => u.CanApply);
        resolution.FinalLines.Select(l => l.Text).Should().Equal(
            "head", "L1", "l2", "l3", "l4", "l5", "L6", "tail");
    }
}
