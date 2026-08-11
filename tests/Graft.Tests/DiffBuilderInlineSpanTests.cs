using FluentAssertions;
using Graft.Core;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 機能改善（単語レベルの差分強調）: <see cref="DiffBuilder"/>が変更前後の行をペアにして
/// 文字単位のハイライト範囲（<see cref="DiffLine.InlineSpans"/>）を計算することの検証と、
/// 極端に長い行では計算を打ち切ること（性能対策）の回帰テスト。
/// </summary>
public class DiffBuilderInlineSpanTests
{
    [Fact(DisplayName = "変更前後で対応する行には、実際に変わった部分だけのInlineSpansが付く")]
    public void 対応する行に変更部分だけのInlineSpansが付く()
    {
        var before = "foo bar baz\n";
        var after = "foo qux baz\n";

        var diff = DiffBuilder.Build("sample.txt", before, after, contextLines: 3);
        var lines = diff.Hunks.SelectMany(h => h.Lines).ToList();

        var removed = lines.Single(l => l.Kind == DiffLineKind.Removed);
        var added = lines.Single(l => l.Kind == DiffLineKind.Added);

        // 行全体ではなく「bar」「qux」の位置だけがハイライト対象になっているはず。
        removed.InlineSpans.Should().ContainSingle();
        removed.InlineSpans[0].Start.Should().Be(removed.Text.IndexOf("bar", StringComparison.Ordinal));
        removed.InlineSpans[0].Length.Should().Be("bar".Length);

        added.InlineSpans.Should().ContainSingle();
        added.InlineSpans[0].Start.Should().Be(added.Text.IndexOf("qux", StringComparison.Ordinal));
        added.InlineSpans[0].Length.Should().Be("qux".Length);
    }

    [Fact(DisplayName = "変更なし行にはInlineSpansが付かない")]
    public void 変更なし行にはInlineSpansが付かない()
    {
        var before = "同じ行\n変更前\n";
        var after = "同じ行\n変更後\n";

        var diff = DiffBuilder.Build("sample.txt", before, after, contextLines: 3);
        var unchanged = diff.Hunks.SelectMany(h => h.Lines).Single(l => l.Kind == DiffLineKind.Unchanged);

        unchanged.InlineSpans.Should().BeEmpty();
    }

    [Fact(DisplayName = "複数行の連続した削除・追加は、同じ位置同士がペアになる")]
    public void 複数行の削除追加は位置ごとにペアになる()
    {
        var before = "行1: abc\n行2: def\n";
        var after = "行1: xbc\n行2: dxf\n";

        var diff = DiffBuilder.Build("sample.txt", before, after, contextLines: 3);
        var lines = diff.Hunks.SelectMany(h => h.Lines).ToList();

        var removedLines = lines.Where(l => l.Kind == DiffLineKind.Removed).ToList();
        var addedLines = lines.Where(l => l.Kind == DiffLineKind.Added).ToList();
        removedLines.Should().HaveCount(2);
        addedLines.Should().HaveCount(2);

        // 1行目ペア（abc→xbc）はaの位置、2行目ペア（def→dxf）はeの位置が変わっただけ。
        removedLines[0].InlineSpans.Should().ContainSingle(s => s.Start == removedLines[0].Text.IndexOf('a'));
        addedLines[0].InlineSpans.Should().ContainSingle(s => s.Start == addedLines[0].Text.IndexOf('x'));
        removedLines[1].InlineSpans.Should().ContainSingle(s => s.Start == removedLines[1].Text.IndexOf('e'));
        addedLines[1].InlineSpans.Should().ContainSingle(s => s.Start == addedLines[1].Text.IndexOf('x'));
    }

    [Fact(DisplayName = "1行が2000文字を超える変更行は、文字単位ハイライトの計算を打ち切りInlineSpansが空になる")]
    public void 極端に長い行はInlineSpansの計算を打ち切る()
    {
        var longBefore = new string('a', 2001) + "END";
        var longAfter = new string('a', 2001) + "ZZZ";
        var before = longBefore + "\n";
        var after = longAfter + "\n";

        var diff = DiffBuilder.Build("sample.txt", before, after, contextLines: 3);
        var lines = diff.Hunks.SelectMany(h => h.Lines).ToList();

        var removed = lines.Single(l => l.Kind == DiffLineKind.Removed);
        var added = lines.Single(l => l.Kind == DiffLineKind.Added);

        // 行の種別（削除・追加）自体は通常どおり判定されるが、行内ハイライトだけが打ち切られる。
        removed.Text.Should().Be(longBefore);
        added.Text.Should().Be(longAfter);
        removed.InlineSpans.Should().BeEmpty("2000文字を超える行は文字単位diffの計算コストを避けるため打ち切る");
        added.InlineSpans.Should().BeEmpty();
    }

    [Fact(DisplayName = "ちょうど2000文字の行は打ち切らず、2001文字からしきい値超過として扱う")]
    public void しきい値ちょうどでは打ち切らない()
    {
        // ちょうど2000文字同士（末尾1文字だけ違う）は通常どおり計算される。
        var before2000 = new string('a', 1999) + "X\n";
        var after2000 = new string('a', 1999) + "Y\n";
        var diffAt2000 = DiffBuilder.Build("sample.txt", before2000, after2000, contextLines: 3);
        var linesAt2000 = diffAt2000.Hunks.SelectMany(h => h.Lines).ToList();
        linesAt2000.Single(l => l.Kind == DiffLineKind.Removed).InlineSpans.Should().NotBeEmpty();
        linesAt2000.Single(l => l.Kind == DiffLineKind.Added).InlineSpans.Should().NotBeEmpty();

        // 2001文字になった瞬間から打ち切られる。
        var before2001 = new string('a', 2000) + "X\n";
        var after2001 = new string('a', 2000) + "Y\n";
        var diffAt2001 = DiffBuilder.Build("sample.txt", before2001, after2001, contextLines: 3);
        var linesAt2001 = diffAt2001.Hunks.SelectMany(h => h.Lines).ToList();
        linesAt2001.Single(l => l.Kind == DiffLineKind.Removed).InlineSpans.Should().BeEmpty();
        linesAt2001.Single(l => l.Kind == DiffLineKind.Added).InlineSpans.Should().BeEmpty();
    }
}
