using FluentAssertions;
using Graft.Editor;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// インデントガイド（縦線）の位置計算（<see cref="IndentGuideCalculator"/>）のテスト。
/// 検討書の要求どおり、(1) 表示上の列数（タブ幅の扱い）と (2) 線を引く行範囲の算出
/// （開始・終了行を除く、終端の判定）を、括弧言語・インデントベース言語の両方のケースで検証する。
/// </summary>
public class IndentGuideCalculatorTests
{
    // ========== LeadingWhitespaceVisualColumn（表示上の列数） ==========

    [Fact(DisplayName = "スペースのみは文字数どおりの列数になる")]
    public void スペースのみは文字数どおり()
    {
        IndentGuideCalculator.LeadingWhitespaceVisualColumn("    x", tabSize: 4).Should().Be(4);
    }

    [Fact(DisplayName = "タブ1つはタブ幅ぶんの列数になる（文字数の1ではない）")]
    public void タブ1つはタブ幅ぶん()
    {
        IndentGuideCalculator.LeadingWhitespaceVisualColumn("\tx", tabSize: 4).Should().Be(4);
    }

    [Fact(DisplayName = "タブ2つはタブ幅の2倍の列数になる")]
    public void タブ2つはタブ幅の2倍()
    {
        IndentGuideCalculator.LeadingWhitespaceVisualColumn("\t\tx", tabSize: 4).Should().Be(8);
    }

    [Fact(DisplayName = "スペース2つ+タブは次のタブストップまで進む（列数の単純合算ではない）")]
    public void スペースとタブの混在は次のタブストップまで()
    {
        // スペース2つで列2、そこからタブは次のタブストップ(列4)まで進むので合計4（2+4ではない）。
        IndentGuideCalculator.LeadingWhitespaceVisualColumn("  \tx", tabSize: 4).Should().Be(4);
    }

    [Fact(DisplayName = "同じ2文字のインデントでも、タブとスペースでは列数（横位置）が異なる")]
    public void タブとスペースでインデントした行は列数が異なる()
    {
        // 検討書の要件: 文字数（どちらも2文字）ではなく表示上の列数で計算するため、
        // タブ幅4のときタブ2つ（列8）とスペース2つ（列2）は一致しない＝行がずれない。
        var byTabs = IndentGuideCalculator.LeadingWhitespaceVisualColumn("\t\tx", tabSize: 4);
        var bySpaces = IndentGuideCalculator.LeadingWhitespaceVisualColumn("  x", tabSize: 4);
        byTabs.Should().NotBe(bySpaces);
        byTabs.Should().Be(8);
        bySpaces.Should().Be(2);
    }

    [Fact(DisplayName = "空白の後に本文が続くと、その手前で数え終える")]
    public void 本文の手前で数え終える()
    {
        IndentGuideCalculator.LeadingWhitespaceVisualColumn("   var x = 1;", tabSize: 4).Should().Be(3);
    }

    [Fact(DisplayName = "空白のみの行は行全体の列数になる")]
    public void 空白のみの行は全体の列数()
    {
        IndentGuideCalculator.LeadingWhitespaceVisualColumn("    ", tabSize: 4).Should().Be(4);
    }

    [Fact(DisplayName = "tabSizeが0以下でも例外にならず1として扱う（安全側）")]
    public void タブ幅0以下は安全側で1扱い()
    {
        IndentGuideCalculator.LeadingWhitespaceVisualColumn("\t\tx", tabSize: 0).Should().Be(2);
    }

    // ========== ComputeInteriorRange（線を引く行範囲、終端の判定） ==========

    [Fact(DisplayName = "括弧言語: 終了行が閉じ括弧（浅い）なら、その1つ前までが内側")]
    public void 括弧言語は終了行の1つ前まで()
    {
        // public void Foo() {      <- 1行目（ヘッダ、indent 0）
        //     var x = 1;           <- 2行目（indent 4）
        // }                        <- 3行目（終了行、indent 0=ヘッダと同じ＝境界行）
        var result = IndentGuideCalculator.ComputeInteriorRange(
            headerLine: 1, lastLineByOffset: 3, baseIndentColumn: 0, lastLineIndentColumn: 0);

        result.Should().Be((2, 2)); // 2行目だけが内側（3行目の"}"は除外）。
    }

    [Fact(DisplayName = "インデント言語: 終了行がブロック最終行（深い）なら、終了行自身も内側")]
    public void インデント言語は終了行自身も内側()
    {
        // if foo:                  <- 1行目（ヘッダ、indent 0）
        //     x = 1                <- 2行目（indent 4）
        //     y = 2                <- 3行目（終了行、indent 4=ヘッダより深い＝内側そのもの）
        var result = IndentGuideCalculator.ComputeInteriorRange(
            headerLine: 1, lastLineByOffset: 3, baseIndentColumn: 0, lastLineIndentColumn: 4);

        result.Should().Be((2, 3)); // 3行目（ブロック最終行）も含む。単純な「-1」では出ない結果。
    }

    [Fact(DisplayName = "機械的な「終了行-1」ではなく実インデントで判定することの確認（同じ入力形でも結果が変わる）")]
    public void 終端の判定は行番号でなく実インデントで決まる()
    {
        // headerLine/lastLineByOffsetが全く同じでも、lastLineIndentColumnだけで結果が変わる
        // ことを確認する（「機械的に1を引く」実装ではこの違いは絶対に生まれない）。
        var shallow = IndentGuideCalculator.ComputeInteriorRange(1, 5, baseIndentColumn: 0, lastLineIndentColumn: 0);
        var deep = IndentGuideCalculator.ComputeInteriorRange(1, 5, baseIndentColumn: 0, lastLineIndentColumn: 2);

        shallow.Should().Be((2, 4));
        deep.Should().Be((2, 5));
    }

    [Fact(DisplayName = "ヘッダ行の次の行がそのまま終了行（範囲が2行）でも、括弧言語なら内側は0行")]
    public void 括弧範囲2行だけは内側なし()
    {
        // public void Foo() {
        // }
        var result = IndentGuideCalculator.ComputeInteriorRange(
            headerLine: 1, lastLineByOffset: 2, baseIndentColumn: 0, lastLineIndentColumn: 0);

        result.Should().BeNull();
    }

    [Fact(DisplayName = "終了行がヘッダ行と同じ（不正な範囲）ならnull")]
    public void 終了行がヘッダ行と同じならnull()
    {
        IndentGuideCalculator.ComputeInteriorRange(3, 3, 0, 0).Should().BeNull();
    }

    [Fact(DisplayName = "終了行の実インデントが不明（空行等でnull）な場合は境界行として除外する（安全側）")]
    public void 終了行のインデント不明時は境界行として除外()
    {
        var result = IndentGuideCalculator.ComputeInteriorRange(
            headerLine: 1, lastLineByOffset: 3, baseIndentColumn: 0, lastLineIndentColumn: null);

        result.Should().Be((2, 2));
    }

    [Fact(DisplayName = "ネストした階層（基準インデントが0でない）でも同じ規則が成り立つ")]
    public void ネストした階層でも同じ規則が成り立つ()
    {
        //     if bar:              <- 1行目（ヘッダ、indent 4。外側のブロックの内側にある）
        //         z = 1            <- 2行目（indent 8）
        //         w = 2            <- 3行目（終了行、indent 8=ヘッダより深い＝内側）
        var result = IndentGuideCalculator.ComputeInteriorRange(
            headerLine: 1, lastLineByOffset: 3, baseIndentColumn: 4, lastLineIndentColumn: 8);

        result.Should().Be((2, 3));
    }

    // ========== LevelCount（すべてのインデントモード用の階層数） ==========

    [Theory(DisplayName = "階層数はfloor(列数/インデント幅)")]
    [InlineData(0, 4, 0)]
    [InlineData(3, 4, 0)]
    [InlineData(4, 4, 1)]
    [InlineData(7, 4, 1)]
    [InlineData(8, 4, 2)]
    [InlineData(12, 4, 3)]
    public void 階層数はfloor割り算(int column, int indentUnit, int expected)
    {
        IndentGuideCalculator.LevelCount(column, indentUnit).Should().Be(expected);
    }

    [Fact(DisplayName = "インデント幅0以下は階層0（安全側、0除算を避ける）")]
    public void インデント幅0以下は階層0()
    {
        IndentGuideCalculator.LevelCount(8, 0).Should().Be(0);
    }
}
