using FluentAssertions;
using Graft.Editor;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 「すべてのコメントブロックを折りたたむ」コマンドが使う連続区間探索
/// （<see cref="CommentBlockCalculator"/>）のテスト。
/// </summary>
public class CommentBlockCalculatorTests
{
    [Fact(DisplayName = "コメント行が無ければ何も見つからない")]
    public void コメント行が無ければ何も見つからない()
    {
        var lines = new[] { false, false, false };
        CommentBlockCalculator.FindCommentBlocks(lines).Should().BeEmpty();
    }

    [Fact(DisplayName = "1行だけのコメントは対象外（内側行が無いため折りたためない）")]
    public void コメント1行だけは対象外()
    {
        var lines = new[] { false, true, false };
        CommentBlockCalculator.FindCommentBlocks(lines).Should().BeEmpty();
    }

    [Fact(DisplayName = "2行以上連続するコメントは1件の区間になる（1始まり行番号）")]
    public void 連続する複数行コメントは1区間()
    {
        // 0:非コメント, 1-3:コメント(3行), 4:非コメント
        var lines = new[] { false, true, true, true, false };
        CommentBlockCalculator.FindCommentBlocks(lines).Should().Equal((2, 4));
    }

    [Fact(DisplayName = "複数の区間が離れていれば、それぞれ別件として返る")]
    public void 離れた区間はそれぞれ別件()
    {
        var lines = new[] { true, true, false, true, true, true };
        CommentBlockCalculator.FindCommentBlocks(lines).Should().Equal((1, 2), (4, 6));
    }

    [Fact(DisplayName = "ファイル先頭から始まるコメント区間も検出できる")]
    public void 先頭から始まる区間も検出できる()
    {
        var lines = new[] { true, true, true, false };
        CommentBlockCalculator.FindCommentBlocks(lines).Should().Equal((1, 3));
    }

    [Fact(DisplayName = "ファイル末尾まで続くコメント区間も検出できる")]
    public void 末尾まで続く区間も検出できる()
    {
        var lines = new[] { false, true, true };
        CommentBlockCalculator.FindCommentBlocks(lines).Should().Equal((2, 3));
    }

    [Fact(DisplayName = "全行がコメントなら1区間としてまとめて返る")]
    public void 全行コメントなら1区間()
    {
        var lines = new[] { true, true, true, true };
        CommentBlockCalculator.FindCommentBlocks(lines).Should().Equal((1, 4));
    }

    [Fact(DisplayName = "空配列は何も見つからない")]
    public void 空配列は何も見つからない()
    {
        CommentBlockCalculator.FindCommentBlocks(Array.Empty<bool>()).Should().BeEmpty();
    }
}
