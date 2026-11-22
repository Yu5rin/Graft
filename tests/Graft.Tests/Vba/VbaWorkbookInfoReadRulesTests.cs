using System;
using FluentAssertions;
using Graft.Core.Vba;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// <see cref="VbaWorkbookInfoReadRules"/>（<c>WindowsVbaBridge.Info</c> が使う、COM に触れない変換と判定）のテスト。
/// 仕様書_ブックの情報と依頼の観点 5.5。COM の呼び出し順は Linux では確かめられないので、確かめられる部分を固める。
/// </summary>
public class VbaWorkbookInfoReadRulesTests
{
    /// <summary>Excel の <c>Range.Value2</c> と同じ、下限 1 の 2 次元配列を作る。</summary>
    private static Array OneBased(int rows, int cols)
        => Array.CreateInstance(typeof(object), new[] { rows, cols }, new[] { 1, 1 });

    [Fact(DisplayName = "Value2: 下限 1 の object[,] を、行×列の文字列の表にする")]
    public void 下限1の配列()
    {
        var values = OneBased(2, 3);
        values.SetValue("日付", 1, 1);
        values.SetValue("顧客", 1, 2);
        values.SetValue("数量", 1, 3);
        values.SetValue(46000d, 2, 1);
        values.SetValue("甲社", 2, 2);
        values.SetValue(-2146826246, 2, 3);

        var grid = VbaWorkbookInfoReadRules.ToGrid(values, 60);

        grid.Should().HaveCount(2);
        grid[0].Should().Equal("日付", "顧客", "数量");
        grid[1].Should().Equal("46000", "甲社", "#N/A");
    }

    [Fact(DisplayName = "Value2: 下限 0 の配列でも読める")]
    public void 下限0の配列()
    {
        var values = new object?[,] { { "A", null }, { 1d, true } };

        var grid = VbaWorkbookInfoReadRules.ToGrid(values, 60);

        grid[0].Should().Equal("A", "");
        grid[1].Should().Equal("1", "TRUE");
    }

    [Fact(DisplayName = "Value2: 1 セルの範囲はスカラーが返る。1×1 の表にする（null は空）")]
    public void スカラー()
    {
        VbaWorkbookInfoReadRules.ToGrid("単独", 60).Should().ContainSingle().Which.Should().Equal("単独");
        VbaWorkbookInfoReadRules.ToGrid(12d, 60).Should().ContainSingle().Which.Should().Equal("12");
        VbaWorkbookInfoReadRules.ToGrid(null, 60).Should().ContainSingle().Which.Should().Equal("");
    }

    [Fact(DisplayName = "数式つきの表: = で始まるセルだけが「=式 → 値」になる（定数セルの Formula は値だけ）")]
    public void 数式つきの表()
    {
        var values = OneBased(1, 3);
        values.SetValue(2500d, 1, 1);
        values.SetValue("甲社", 1, 2);
        values.SetValue(3d, 1, 3);
        var formulas = OneBased(1, 3);
        formulas.SetValue("=D2*E2", 1, 1);
        formulas.SetValue("甲社", 1, 2);
        formulas.SetValue("3", 1, 3);

        var grid = VbaWorkbookInfoReadRules.ToGrid(values, formulas, 60);

        grid[0].Should().Equal("=D2*E2 → 2500", "甲社", "3");
    }

    [Fact(DisplayName = "数式つきの表: 1 セルのスカラーでも数式を扱える")]
    public void 数式つきのスカラー()
    {
        VbaWorkbookInfoReadRules.ToGrid(10d, "=A1+9", 60)[0].Should().Equal("=A1+9 → 10");
    }

    [Fact(DisplayName = "列の右側を最大列数で切り、切った数を返す")]
    public void 列を切る()
    {
        var row = new[] { "a", "b", "c", "d", "e" };

        VbaWorkbookInfoReadRules.LimitColumns(row, 3, out var omitted).Should().Equal("a", "b", "c");
        omitted.Should().Be(2);
        VbaWorkbookInfoReadRules.LimitColumns(row, 5, out omitted).Should().Equal(row);
        omitted.Should().Be(0);
    }

    [Theory(DisplayName = "列番号と列の文字の往復")]
    [InlineData(1, "A")]
    [InlineData(26, "Z")]
    [InlineData(27, "AA")]
    [InlineData(52, "AZ")]
    [InlineData(53, "BA")]
    [InlineData(702, "ZZ")]
    [InlineData(703, "AAA")]
    [InlineData(16384, "XFD")]
    public void 列の文字(int number, string letters)
    {
        VbaWorkbookInfoReadRules.ColumnLetters(number).Should().Be(letters);
        VbaWorkbookInfoReadRules.ColumnNumber(letters).Should().Be(number);
    }

    [Theory(DisplayName = "範囲のアドレスの左端の列（A 列始まりでないときの見出しの列文字のため）")]
    [InlineData("A1:F120", 1)]
    [InlineData("C3", 3)]
    [InlineData("$B$2:$D$9", 2)]
    [InlineData("AA10:AC20", 27)]
    [InlineData("", 1)]
    [InlineData(null, 1)]
    public void 左端の列(string? address, int expected)
    {
        VbaWorkbookInfoReadRules.StartColumnOf(address).Should().Be(expected);
    }

    [Theory(DisplayName = "Worksheet.Visible: -1 表示、0 非表示、2 VeryHidden、未知は表示")]
    [InlineData(-1, VbaSheetVisibility.Visible)]
    [InlineData(0, VbaSheetVisibility.Hidden)]
    [InlineData(2, VbaSheetVisibility.VeryHidden)]
    [InlineData(1, VbaSheetVisibility.Visible)]
    public void 表示状態(int value, VbaSheetVisibility expected)
    {
        VbaWorkbookInfoReadRules.VisibilityFromExcel(value).Should().Be(expected);
    }

    [Theory(DisplayName = "名前の分解: ブック全体は ! なし、シート限定は シート名!名前（引用符つきも）")]
    [InlineData("税率", null, "税率")]
    [InlineData("Sheet1!税率", "Sheet1", "税率")]
    [InlineData("'売上 2026'!税率", "売上 2026", "税率")]
    [InlineData("'it''s'!X", "it's", "X")]
    [InlineData("'a!b'!X", "a!b", "X")]
    public void 名前の分解(string raw, string? scope, string name)
    {
        VbaWorkbookInfoReadRules.SplitName(raw).Should().Be((scope, name));
    }

    [Theory(DisplayName = "内部の名前（印刷範囲・フィルター）は一覧から除く。利用者の名前は除かない")]
    [InlineData("_xlnm.Print_Area", true)]
    [InlineData("Sheet1!_xlnm.Print_Titles", true)]
    [InlineData("Sheet1!_FilterDatabase", true)]
    [InlineData("_FilterDatabase", true)]
    [InlineData("税率", false)]
    [InlineData("Sheet1!税率", false)]
    public void 内部の名前(string name, bool expected)
    {
        VbaWorkbookInfoReadRules.IsInternalName(name).Should().Be(expected);
    }

    [Fact(DisplayName = "時間の予算: 超えたら打ち切る。予算 0 以下は打ち切らない")]
    public void 時間の予算()
    {
        var budget = TimeSpan.FromSeconds(20);

        VbaWorkbookInfoReadRules.IsOverBudget(TimeSpan.FromSeconds(19.9), budget).Should().BeFalse();
        VbaWorkbookInfoReadRules.IsOverBudget(TimeSpan.FromSeconds(20), budget).Should().BeTrue();
        VbaWorkbookInfoReadRules.IsOverBudget(TimeSpan.FromHours(1), TimeSpan.Zero).Should().BeFalse();
    }

    [Fact(DisplayName = "省略の説明文は、読めなかった先頭のシート名・件数を含む")]
    public void 省略の文()
    {
        VbaWorkbookInfoReadRules.OmittedByBudgetMessage("集計").Should().Contain("集計").And.Contain("読めていません");
        VbaWorkbookInfoReadRules.OmittedByMaxSheetsMessage(12, 100).Should().Contain("100").And.Contain("12");
    }
    [Theory(DisplayName = "疑わしい 1: 名前の式が範囲参照だけでできているか（定数・数式は false に倒す）")]
    [InlineData("=設定!$B$3", true)]
    [InlineData("=Sheet1!$A$1:$B$9", true)]
    [InlineData("='売上 2026'!$A:$A", true)]
    [InlineData("='売上, 2026'!$A$1,Sheet1!$C$1", true)]
    [InlineData("=(Sheet1!$A$1,Sheet1!$C$1)", true)]
    [InlineData("=Sheet1!$1:$3", true)]
    [InlineData("=売上テーブル[金額]", true)]
    [InlineData("=#REF!", true)]
    [InlineData("=\"パスワード\"", false)]
    [InlineData("=0.08", false)]
    [InlineData("={1,2,3}", false)]
    [InlineData("=TRUE", false)]
    [InlineData("=OFFSET(Sheet1!$A$1,0,0,10,1)", false)]
    [InlineData("=Sheet1!$A$1+1", false)]
    [InlineData("=\"Sheet1!$A$1\"", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void 範囲参照の判定(string? refersTo, bool expected)
    {
        VbaWorkbookInfoReadRules.IsRangeReference(refersTo).Should().Be(expected);
    }

    [Fact(DisplayName = "不具合 10: 読めなかった注記は、実際に読めなかったものだけを言う（シートは読めたがテーブルが読めない・名前が途中まで）")]
    public void 読めなかった注記は正確()
    {
        VbaWorkbookInfoReadRules.OmittedSheetMessage("集計", sheetListed: false, unchecked((int)0x800A03EC))
            .Should().Contain("シート「集計」は読めませんでした");
        var tablesOnly = VbaWorkbookInfoReadRules.OmittedSheetMessage("集計", sheetListed: true, unchecked((int)0x800A03EC));
        tablesOnly.Should().Contain("テーブルは読めませんでした").And.NotContain("シート「集計」は読めませんでした");

        VbaWorkbookInfoReadRules.OmittedNamesMessage(0, unchecked((int)0x800A03EC)).Should().Contain("名前付き範囲は読めませんでした");
        var partial = VbaWorkbookInfoReadRules.OmittedNamesMessage(7, unchecked((int)0x800A03EC));
        partial.Should().Contain("途中までしか").And.Contain("7 件").And.NotContain("名前付き範囲は読めませんでした");
    }
}
