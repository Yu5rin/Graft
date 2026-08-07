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
}
