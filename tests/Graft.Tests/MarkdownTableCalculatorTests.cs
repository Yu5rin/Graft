using FluentAssertions;
using Graft.Editor;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// Markdown編集支援（検討書「Markdownの編集支援」）の表編集（<see cref="MarkdownTableCalculator"/>）
/// のテスト。セル移動(Tab/Shift+Tab)・行追加(Enter)を検証する。
/// </summary>
public class MarkdownTableCalculatorTests
{
    private static readonly string[] SimpleTable =
    {
        "| A | B |",
        "| --- | --- |",
        "| 1 | 2 |",
    };

    [Fact(DisplayName = "表の中の行から表全体を検出できる")]
    public void 表を検出できる()
    {
        var source = new ArrayMarkdownLineSource(SimpleTable);
        var table = MarkdownTableCalculator.TryFindTableAt(source, 2);
        table.Should().NotBeNull();
        table!.StartLine.Should().Be(0);
        table.SeparatorLine.Should().Be(1);
        table.EndLine.Should().Be(2);
        table.Header.Should().Equal("A", "B");
        table.Body.Should().HaveCount(1);
        table.Body[0].Should().Equal("1", "2");
    }

    [Fact(DisplayName = "表でない行では検出されない")]
    public void 表でない行は検出されない()
    {
        var source = new ArrayMarkdownLineSource(new[] { "ただの文章です。" });
        MarkdownTableCalculator.TryFindTableAt(source, 0).Should().BeNull();
    }

    [Fact(DisplayName = "見出し行の直後が区切り行でなければ表として検出しない")]
    public void 区切り行が無ければ表ではない()
    {
        var source = new ArrayMarkdownLineSource(new[] { "| A | B |", "| 1 | 2 |" });
        MarkdownTableCalculator.TryFindTableAt(source, 0).Should().BeNull();
    }

    [Fact(DisplayName = "Tabで次のセルへ移動する")]
    public void Tabで次のセルへ()
    {
        var source = new ArrayMarkdownLineSource(SimpleTable);
        var table = MarkdownTableCalculator.TryFindTableAt(source, 0)!;
        var (moved, row, col) = MarkdownTableCalculator.NextCell(table, rowKind: 0, column: 0, shift: false);
        moved.Should().BeTrue();
        row.Should().Be(0);
        col.Should().Be(1);
    }

    [Fact(DisplayName = "Tabは見出し行の最終セルから区切り行を飛ばして本文1行目へ移動する")]
    public void Tabは区切り行を飛ばす()
    {
        var source = new ArrayMarkdownLineSource(SimpleTable);
        var table = MarkdownTableCalculator.TryFindTableAt(source, 0)!;
        var (moved, row, col) = MarkdownTableCalculator.NextCell(table, rowKind: 0, column: 1, shift: false);
        moved.Should().BeTrue();
        row.Should().Be(2, "区切り行(1)には止まらず本文1行目(2)へ移動するはず");
        col.Should().Be(0);
    }

    [Fact(DisplayName = "Shift+Tabで前のセルへ移動する")]
    public void ShiftTabで前のセルへ()
    {
        var source = new ArrayMarkdownLineSource(SimpleTable);
        var table = MarkdownTableCalculator.TryFindTableAt(source, 0)!;
        var (moved, row, col) = MarkdownTableCalculator.NextCell(table, rowKind: 2, column: 0, shift: true);
        moved.Should().BeTrue();
        row.Should().Be(0, "区切り行を飛ばして見出し行へ戻るはず");
        col.Should().Be(1, "前の行の最終列に位置するはず");
    }

    [Fact(DisplayName = "表の先頭セルでShift+Tabしても移動しない")]
    public void 先頭セルでのShiftTabは移動しない()
    {
        var source = new ArrayMarkdownLineSource(SimpleTable);
        var table = MarkdownTableCalculator.TryFindTableAt(source, 0)!;
        var (moved, _, _) = MarkdownTableCalculator.NextCell(table, rowKind: 0, column: 0, shift: true);
        moved.Should().BeFalse();
    }

    [Fact(DisplayName = "表の最終セルでのTabは移動しない(行追加はEnterの役目)")]
    public void 最終セルでのTabは移動しない()
    {
        var source = new ArrayMarkdownLineSource(SimpleTable);
        var table = MarkdownTableCalculator.TryFindTableAt(source, 0)!;
        var (moved, _, _) = MarkdownTableCalculator.NextCell(table, rowKind: 2, column: 1, shift: false);
        moved.Should().BeFalse();
    }

    [Fact(DisplayName = "最終行の最終セルでEnterすると空の行が1つ追加される")]
    public void Enterで行が追加される()
    {
        var source = new ArrayMarkdownLineSource(SimpleTable);
        var table = MarkdownTableCalculator.TryFindTableAt(source, 0)!;
        var updated = MarkdownTableCalculator.AppendEmptyRow(table);
        updated.Body.Should().HaveCount(2);
        updated.Body[1].Should().Equal(string.Empty, string.Empty);
        updated.EndLine.Should().Be(table.EndLine + 1);
    }

    [Fact(DisplayName = "整形すると列幅が最も長いセルに揃う")]
    public void 整形で列幅が揃う()
    {
        var source = new ArrayMarkdownLineSource(new[]
        {
            "| A | longcolumn |",
            "| --- | --- |",
            "| 1 | x |",
        });
        var table = MarkdownTableCalculator.TryFindTableAt(source, 0)!;
        var text = MarkdownTableCalculator.FormatTableText(table);
        var lines = text.Split('\n');
        lines.Should().HaveCount(3);
        // すべての行が同じ幅(同じ文字数)に揃っているはず(列幅の統一)。
        lines[0].Length.Should().Be(lines[1].Length);
        lines[0].Length.Should().Be(lines[2].Length);
        lines[0].Should().Contain("longcolumn");
    }

    [Fact(DisplayName = "列揃え(右揃え)を区切り行から読み取り、整形時も引き継ぐ")]
    public void 列揃えを読み取り整形に反映する()
    {
        var source = new ArrayMarkdownLineSource(new[]
        {
            "| A | B |",
            "| --- | ---: |",
            "| 1 | 2 |",
        });
        var table = MarkdownTableCalculator.TryFindTableAt(source, 0)!;
        table.Aligns[1].Should().Be(MarkdownTableAlign.Right);
        var text = MarkdownTableCalculator.FormatTableText(table);
        text.Should().Contain("--:");
    }

    [Fact(DisplayName = "セルの文字範囲(トリム済み)を正しく求める")]
    public void セルの文字範囲を求める()
    {
        var (start, end) = MarkdownTableCalculator.CellSpanInLine("| 1 | 2 |", column: 1);
        "| 1 | 2 |"[start..end].Should().Be("2");
    }

    [Fact(DisplayName = "セルの前後の空白はトリムされた範囲になる")]
    public void セルの前後の空白はトリムされる()
    {
        var (start, end) = MarkdownTableCalculator.CellSpanInLine("|  padded  | b |", column: 0);
        "|  padded  | b |"[start..end].Should().Be("padded");
    }

    [Fact(DisplayName = "★空セル(空白のみ)でも範囲が負にならない(行追加直後の空セル選択で実際にクラッシュした不具合の再発防止)")]
    public void 空セルでも範囲は負にならない()
    {
        // 整形直後の追加行はすべて空白ぶんのパディングのみのセルになる(例: "|     |     |")。
        var (start, end) = MarkdownTableCalculator.CellSpanInLine("|     |     |", column: 0);
        (end - start).Should().BeGreaterThanOrEqualTo(0);
        start.Should().BeLessThanOrEqualTo(end);
    }

    [Fact(DisplayName = "行追加後に整形した表の空セルの範囲を求めても例外にならない")]
    public void 行追加後の整形結果で空セルの範囲を求めても例外にならない()
    {
        var source = new ArrayMarkdownLineSource(SimpleTable);
        var table = MarkdownTableCalculator.TryFindTableAt(source, 0)!;
        var appended = MarkdownTableCalculator.AppendEmptyRow(table);
        var text = MarkdownTableCalculator.FormatTableText(appended);
        var newRowLine = text.Split('\n')[^1];

        var act = () => MarkdownTableCalculator.CellSpanInLine(newRowLine, column: 0);
        act.Should().NotThrow();
    }
}
