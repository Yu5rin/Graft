using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Core.Vba;
using Graft.Features;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// シートごとの見せ方（仕様書_ブックの情報と依頼の観点 5.2・5.5。v0.4）の純ロジックのテスト:
/// 範囲の検証（<see cref="VbaA1Range"/>）、読む範囲の計画と上限（<see cref="VbaSheetReadPlanner"/>）、
/// 見せ方の文字列の往復（<see cref="VbaSheetViewModes"/>）、<see cref="VbaLink"/> の記録・移行・結び直し。
/// </summary>
public class VbaSheetViewTests
{
    // ---------------- 範囲の検証 ----------------

    [Theory(DisplayName = "範囲の入力: A1・A1:F30・$ と小文字と空白つき・向きが逆・列範囲・行範囲を受け付け、正規化する")]
    [InlineData("A1", "A1")]
    [InlineData("a1:f30", "A1:F30")]
    [InlineData(" $B$2 : $D$9 ", "B2:D9")]
    [InlineData("F30:A1", "A1:F30")]
    [InlineData("A:F", "A:F")]
    [InlineData("c:a", "A:C")]
    [InlineData("3:10", "3:10")]
    [InlineData("A1:XFD1048576", "A1:XFD1048576")]
    public void 範囲を受け付ける(string input, string normalized)
    {
        VbaA1Range.TryParse(input, out var range, out var error).Should().BeTrue();
        error.Should().BeNull();
        range.ToA1().Should().Be(normalized);
        VbaSheetView.NormalizeRange(input).Should().Be(normalized);
    }

    [Theory(DisplayName = "範囲の入力: 空・名前付き範囲・シート名つき・複数領域・不正な形・範囲外は理由つきで不可")]
    [InlineData("", "範囲を入力してください")]
    [InlineData("   ", "範囲を入力してください")]
    [InlineData("売上", "名前付き範囲は指定できません")]
    [InlineData("TaxRate", "名前付き範囲は指定できません")]
    [InlineData("A", "名前付き範囲は指定できません")]
    [InlineData("Sheet1!A1:B2", "シート名は付けずに")]
    [InlineData("A1:B2,D1:E2", "1 つだけ")]
    [InlineData("A1:B2:C3", "A1:F30 の形")]
    [InlineData("A1:", "A1:F30・A:F")]
    [InlineData("1:B", "A1:F30・A:F")]
    [InlineData("A0", "行は 1〜")]
    [InlineData("A1:B1048577", "行は 1〜")]
    [InlineData("XFE1", "列は A〜XFD")]
    [InlineData("0:5", "行は 1〜")]
    public void 範囲を断る(string input, string reasonPart)
    {
        VbaA1Range.TryParse(input, out _, out var error).Should().BeFalse();
        error.Should().Contain(reasonPart);
        VbaSheetView.NormalizeRange(input).Should().BeNull();
    }

    [Fact(DisplayName = "範囲の共通部分と、列範囲・行範囲の正規化（全列・全行は A:F／3:10 のまま戻る）")]
    public void 共通部分()
    {
        var used = VbaA1Range.FromAddress("B2:E20")!.Value;
        VbaA1Range.TryParse("A1:C3", out var requested, out _);

        requested.Intersect(used).Should().Be(new VbaA1Range(2, 2, 3, 3));
        VbaA1Range.FromAddress("Z1:Z9")!.Value.Intersect(used).Should().BeNull();
        VbaA1Range.FromAddress("not an address").Should().BeNull();
    }

    // ---------------- 読む範囲の計画 ----------------

    private static VbaA1Range Used(string address) => VbaA1Range.FromAddress(address)!.Value;

    [Fact(DisplayName = "計画: 構成だけ・出さない・使用範囲なしは、セルを読まない")]
    public void 値を渡さなければ読まない()
    {
        VbaSheetReadPlanner.Plan(Used("A1:F20"), VbaSheetView.StructureOnly, 30, 500, 50).ReadsCells.Should().BeFalse();
        VbaSheetReadPlanner.Plan(Used("A1:F20"), VbaSheetView.Excluded, 30, 500, 50).ReadsCells.Should().BeFalse();
        VbaSheetReadPlanner.Plan(null, new VbaSheetView(VbaSheetViewMode.All), 30, 500, 50).ReadsCells.Should().BeFalse();
        VbaSheetReadPlanner.Plan(Used("A1:F20"), new VbaSheetView(VbaSheetViewMode.Range, Range: null), 30, 500, 50)
            .ReadsCells.Should().BeFalse("範囲が空なら何も読まない");
    }

    [Fact(DisplayName = "計画: 見出し＋N 行は、先頭の 1+N 行を最大列数まで 1 回で読む（行は使用範囲まで）。切った列数が分かる")]
    public void 見出しとN行の計画()
    {
        var plan = VbaSheetReadPlanner.Plan(Used("C2:BZ200"), new VbaSheetView(VbaSheetViewMode.HeaderRows, 5), 30, 500, 50);

        (plan.StartRow, plan.StartColumn, plan.RowCount, plan.ColumnCount).Should().Be((2, 3, 6, 30));
        plan.TotalColumns.Should().Be(76);

        var small = VbaSheetReadPlanner.Plan(Used("A1:B3"), new VbaSheetView(VbaSheetViewMode.HeaderRows, 50), 30, 500, 50);
        (small.RowCount, small.ColumnCount).Should().Be((3, 2), "使用範囲より多くは読まない");

        VbaSheetReadPlanner.Plan(Used("A1:B3"), new VbaSheetView(VbaSheetViewMode.HeaderRows, 0), 30, 500, 50)
            .RowCount.Should().Be(1, "N が 0 なら見出しの 1 行だけ");
    }

    [Fact(DisplayName = "計画: 全部は使用範囲全体。上限（500 行・50 列）を超えるときは、読む範囲そのものを小さくし、切る前の大きさを持つ")]
    public void 全部の計画と上限()
    {
        var plan = VbaSheetReadPlanner.Plan(Used("A1:BZ1200"), new VbaSheetView(VbaSheetViewMode.All), 30, 500, 50);

        (plan.StartRow, plan.StartColumn, plan.RowCount, plan.ColumnCount).Should().Be((1, 1, 500, 50));
        (plan.TotalRows, plan.TotalColumns).Should().Be((1200, 78));

        var custom = VbaSheetReadPlanner.Plan(Used("A1:BZ1200"), new VbaSheetView(VbaSheetViewMode.All), 30, 1000, 60);
        (custom.RowCount, custom.ColumnCount).Should().Be((1000, 60), "上限は設定で変えられる");

        var within = VbaSheetReadPlanner.Plan(Used("A1:C10"), new VbaSheetView(VbaSheetViewMode.All), 30, 500, 50);
        (within.RowCount, within.ColumnCount, within.TotalRows).Should().Be((10, 3, 10));
    }

    [Fact(DisplayName = "計画: 範囲を指定は使用範囲との共通部分だけを読み、列範囲 A:F でも 100 万行を読まない。外なら読まずに「外」と分かる")]
    public void 範囲の計画()
    {
        var plan = VbaSheetReadPlanner.Plan(Used("A1:H200"), new VbaSheetView(VbaSheetViewMode.Range, Range: "B10:D30"), 30, 500, 50);
        (plan.StartRow, plan.StartColumn, plan.RowCount, plan.ColumnCount).Should().Be((10, 2, 21, 3));
        plan.Requested.Should().Be("B10:D30");

        var columns = VbaSheetReadPlanner.Plan(Used("A1:H800"), new VbaSheetView(VbaSheetViewMode.Range, Range: "A:C"), 30, 500, 50);
        (columns.RowCount, columns.ColumnCount, columns.TotalRows).Should().Be((500, 3, 800), "列全体でも、使用範囲と上限までしか読まない");

        var outside = VbaSheetReadPlanner.Plan(Used("A1:H20"), new VbaSheetView(VbaSheetViewMode.Range, Range: "Z100:Z200"), 30, 500, 50);
        outside.ReadsCells.Should().BeFalse();
        outside.OutsideUsedRange.Should().BeTrue();
    }

    [Fact(DisplayName = "詰め直し: 見出し＋N 行は先頭行を見出しに、続く行を見本に。全部は値のかたまりにして、切る前の大きさを持つ")]
    public void 詰め直し()
    {
        var structure = new VbaSheetInfo("集計", "Sheet1", VbaSheetVisibility.Visible, "A1:C9", Array.Empty<string>(), Array.Empty<IReadOnlyList<string>>());
        IReadOnlyList<IReadOnlyList<string>> cells = new[]
        {
            (IReadOnlyList<string>)new[] { "a", "b" }, new[] { "1", "=A2*2 → 2" },
        };
        var headerPlan = new VbaSheetReadPlan(VbaSheetViewMode.HeaderRows, 1, 1, 2, 2, 9, 3, null, false);

        var header = VbaSheetReadPlanner.Assemble(structure, headerPlan, cells, cells);

        header.HeaderRow.Should().Equal("a", "b");
        header.SampleRows.Should().ContainSingle().Which.Should().Equal("1", "=A2*2 → 2");
        header.OmittedColumns.Should().Be(1);
        header.Block.Should().BeNull();

        var allPlan = new VbaSheetReadPlan(VbaSheetViewMode.All, 1, 1, 2, 2, 9, 3, null, false);
        var all = VbaSheetReadPlanner.Assemble(structure, allPlan, cells, null);

        all.Block.Should().NotBeNull();
        all.Block!.Rows.Should().HaveCount(2);
        (all.Block.TotalRows, all.Block.TotalColumns, all.Block.RowsCut, all.Block.ColumnsCut).Should().Be((9, 3, true, true));
        all.Block.Address.Should().Be("A1:B2");
        all.HeaderRow.Should().BeEmpty();
    }

    // ---------------- 見せ方の文字列・一覧 ----------------

    [Fact(DisplayName = "見せ方の文字列: 往復する。不明・空は構成だけ（値を渡さない側）")]
    public void 見せ方の文字列()
    {
        foreach (var mode in VbaSheetViewModes.InDisplayOrder) VbaSheetViewModes.Parse(mode.ToString()).Should().Be(mode);
        VbaSheetViewModes.Parse(null).Should().Be(VbaSheetViewMode.StructureOnly);
        VbaSheetViewModes.Parse("").Should().Be(VbaSheetViewMode.StructureOnly);
        VbaSheetViewModes.Parse("なにか").Should().Be(VbaSheetViewMode.StructureOnly);
        VbaSheetViewModes.Parse("99").Should().Be(VbaSheetViewMode.StructureOnly);
        VbaSheetViewModes.InDisplayOrder.Select(VbaSheetViewModes.Label)
            .Should().Equal("出さない", "構成だけ", "見出し＋N 行", "全部", "範囲を指定");
    }

    [Fact(DisplayName = "値が渡るのは 見出し＋N 行・全部・有効な範囲の範囲を指定 だけ。全体が構成だけなら値は落ち、出さないは残る")]
    public void 値が渡る見せ方()
    {
        new VbaSheetView(VbaSheetViewMode.HeaderRows, 0).PassesValues.Should().BeTrue();
        new VbaSheetView(VbaSheetViewMode.All).PassesValues.Should().BeTrue();
        new VbaSheetView(VbaSheetViewMode.Range, Range: "A1:B2").PassesValues.Should().BeTrue();
        new VbaSheetView(VbaSheetViewMode.Range, Range: null).PassesValues.Should().BeFalse();
        new VbaSheetView(VbaSheetViewMode.Range, Range: "売上").PassesValues.Should().BeFalse();
        VbaSheetView.StructureOnly.PassesValues.Should().BeFalse();
        VbaSheetView.Excluded.PassesValues.Should().BeFalse();

        new VbaSheetView(VbaSheetViewMode.All).ForLevel(VbaWorkbookInfoLevel.StructureOnly).Should().Be(VbaSheetView.StructureOnly);
        VbaSheetView.Excluded.ForLevel(VbaWorkbookInfoLevel.StructureOnly).Should().Be(VbaSheetView.Excluded);
        new VbaSheetView(VbaSheetViewMode.All).ForLevel(VbaWorkbookInfoLevel.Full).PassesValues.Should().BeTrue();
    }

    [Fact(DisplayName = "見せ方の一覧: コード名の大文字小文字を区別せず引く。記録の無いシートは既定")]
    public void 見せ方の一覧()
    {
        var set = new VbaSheetViewSet(
            VbaSheetView.StructureOnly,
            new[] { new KeyValuePair<string, VbaSheetView>("Sheet1", VbaSheetView.Excluded) });

        set.For("sheet1").Should().Be(VbaSheetView.Excluded);
        set.For("Sheet2").Should().Be(VbaSheetView.StructureOnly);
        set.With("Sheet2", new VbaSheetView(VbaSheetViewMode.All)).For("SHEET2").Mode.Should().Be(VbaSheetViewMode.All);
        set.With("sheet1", VbaSheetView.StructureOnly).For("Sheet1").Should().Be(VbaSheetView.StructureOnly);
    }

    // ---------------- VbaLink: 既定・記録・移行・結び直し ----------------

    [Fact(DisplayName = "既定は全シート構成だけ（記録が無く、旧項目もオフ）")]
    public void 既定は構成だけ()
    {
        var views = new VbaLink().ResolveSheetViews(5);

        views.Default.Should().Be(VbaSheetView.StructureOnly);
        views.For("Sheet1").PassesValues.Should().BeFalse();
        new VbaLink().WorkbookInfoSheetViews.Should().BeNull("触っていない既存の projects.json は null");
    }

    [Fact(DisplayName = "移行: 値の見本がオンだった既存のプロジェクトは、全シート「見出し＋既存の N 行」（数式の選択も引き継ぐ）として読まれる")]
    public void 値の見本からの移行()
    {
        var link = new VbaLink { WorkbookInfoSampleValues = true, WorkbookInfoIncludeFormulas = true };

        var views = link.ResolveSheetViews(legacySampleRows: 8);

        views.For("Sheet1").Should().Be(new VbaSheetView(VbaSheetViewMode.HeaderRows, 8, true));
        views.For("どのシートでも").Mode.Should().Be(VbaSheetViewMode.HeaderRows);
        new VbaLink { WorkbookInfoSampleValues = true }.ResolveSheetViews(5).For("Sheet9").IncludeFormulas.Should().BeFalse();
    }

    [Fact(DisplayName = "移行: 見本がオフの既存のプロジェクトは構成だけのまま（数式だけオンでも値は渡らない）")]
    public void 見本オフなら移行しない()
    {
        new VbaLink { WorkbookInfoIncludeFormulas = true }.ResolveSheetViews(5).For("Sheet1").PassesValues.Should().BeFalse();
    }

    [Fact(DisplayName = "移行の確定: シートごとの記録を書くと旧項目はオフに戻り、以後は新しい記録だけを見る（全シートを構成だけに戻しても再移行しない）")]
    public void 記録を書くと旧項目はオフ()
    {
        var link = new VbaLink { WorkbookInfoSampleValues = true, WorkbookInfoIncludeFormulas = true };
        var resolved = link.ResolveSheetViews(5);

        var written = link.WithSheetViews(new VbaSheetViewSet(
            VbaSheetView.StructureOnly,
            new[]
            {
                new KeyValuePair<string, VbaSheetView>("Sheet1", resolved.Default),
                new KeyValuePair<string, VbaSheetView>("Sheet2", VbaSheetView.StructureOnly),
            }));

        written.WorkbookInfoSampleValues.Should().BeFalse();
        written.WorkbookInfoIncludeFormulas.Should().BeFalse();
        written.WorkbookInfoSheetViews.Should().ContainSingle("既定（構成だけ）と同じ記録は書かない").Which.CodeName.Should().Be("Sheet1");
        written.ResolveSheetViews(5).For("Sheet1").Mode.Should().Be(VbaSheetViewMode.HeaderRows);
        written.ResolveSheetViews(5).For("Sheet2").Mode.Should().Be(VbaSheetViewMode.StructureOnly);

        var allBack = written.WithSheetViews(VbaSheetViewSet.AllStructureOnly);
        allBack.WorkbookInfoSheetViews.Should().BeEmpty("空の一覧は「触ったあと、全シート構成だけ」");
        allBack.ResolveSheetViews(5).For("Sheet1").PassesValues.Should().BeFalse();
    }

    [Fact(DisplayName = "記録はコード名に結び付く: 表示名には依らず、一覧に無い（消えた）シートの記録も保たれる")]
    public void 記録はコード名に結び付く()
    {
        var link = new VbaLink().WithSheetViews(new VbaSheetViewSet(
            VbaSheetView.StructureOnly,
            new[]
            {
                new KeyValuePair<string, VbaSheetView>("Sheet1", new VbaSheetView(VbaSheetViewMode.Range, 3, true, "A1:F30")),
                new KeyValuePair<string, VbaSheetView>("Sheet9", VbaSheetView.Excluded),
            }));

        var views = link.ResolveSheetViews(5);

        views.For("Sheet1").Should().Be(new VbaSheetView(VbaSheetViewMode.Range, 3, true, "A1:F30"));
        views.For("Sheet9").Mode.Should().Be(VbaSheetViewMode.None, "シートが消えても、記録は残る");
        link.WorkbookInfoSheetViews!.Select(r => r.CodeName).Should().Equal("Sheet1", "Sheet9");
    }

    [Fact(DisplayName = "記録の読み込み: 壊れた見せ方・範囲・行数は、値を渡さない側（構成だけ・範囲なし・範囲内）へ寄る")]
    public void 壊れた記録は安全側()
    {
        var broken = new VbaSheetViewSetting { CodeName = "Sheet1", Mode = "なにか", HeaderRows = 9999, Range = "売上" }.ToView();
        broken.Mode.Should().Be(VbaSheetViewMode.StructureOnly);
        broken.HeaderRows.Should().Be(VbaSheetViewLimits.MaxHeaderRows);
        broken.Range.Should().BeNull();

        new VbaSheetViewSetting { CodeName = "S", Mode = "Range", Range = "x" }.ToView().PassesValues.Should().BeFalse();
    }

    [Fact(DisplayName = "別のブックへ結び直したら、値が渡る見せ方も「出さない」もすべて戻す（確認をやり直す）。同じブック（大文字小文字違い）なら保つ")]
    public void 結び直しで見せ方を戻す()
    {
        var link = new VbaLink { WorkbookFullName = @"C:\Work\計画.xlsm", AutoPushOnSave = true }.WithSheetViews(new VbaSheetViewSet(
            VbaSheetView.StructureOnly,
            new[]
            {
                new KeyValuePair<string, VbaSheetView>("Sheet1", new VbaSheetView(VbaSheetViewMode.All)),
                new KeyValuePair<string, VbaSheetView>("Sheet2", VbaSheetView.Excluded),
            }));

        var other = link.WithWorkbook(@"C:\Other\別の本.xlsm");
        other.ResolveSheetViews(5).For("Sheet1").PassesValues.Should().BeFalse("新しいブックの Sheet1 の値は、確認なしに渡さない");
        other.WorkbookInfoSheetViews.Should().BeNull();
        other.AutoPushOnSave.Should().BeTrue("ほかの設定は引き継ぐ");

        link.WithWorkbook(@"c:\work\計画.xlsm").ResolveSheetViews(5).For("Sheet1").Mode.Should().Be(VbaSheetViewMode.All);

        var legacy = new VbaLink { WorkbookFullName = @"C:\Work\計画.xlsm", WorkbookInfoSampleValues = true };
        legacy.WithWorkbook(@"C:\Other\別の本.xlsm").ResolveSheetViews(5).For("Sheet1").PassesValues.Should().BeFalse();
        legacy.WithWorkbook(@"C:\Work\計画.xlsm").ResolveSheetViews(5).For("Sheet1").PassesValues.Should().BeTrue();
    }

    [Fact(DisplayName = "projects.json: シートごとの見せ方を持たない既存の vba は null で読める。書いて読み直すと往復する（camelCase）")]
    public async Task ProjectsJsonの往復()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var root = ws.CreateDirectory("proj");
        await File.WriteAllTextAsync(paths.ProjectsFilePath, $$"""
            {
              "projects": [
                { "id": "p_000009", "name": "旧VBA", "root": "{{root.Replace("\\", "\\\\")}}",
                  "vba": { "workbookFullName": "C:\\Work\\計画.xlsm", "workbookInfoSampleValues": true } }
              ]
            }
            """);
        var store = new ProjectStore(paths);

        var old = (await store.LoadAsync()).Value.Should().ContainSingle().Subject;
        old.Vba!.WorkbookInfoSheetViews.Should().BeNull();
        old.Vba.ResolveSheetViews(5).For("Sheet1").Mode.Should().Be(VbaSheetViewMode.HeaderRows, "値の見本がオンの旧形式は、移行して読まれる");

        var written = old.Vba.WithSheetViews(new VbaSheetViewSet(
            VbaSheetView.StructureOnly,
            new[] { new KeyValuePair<string, VbaSheetView>("Sheet1", new VbaSheetView(VbaSheetViewMode.Range, 5, false, "A1:F30")) }));
        await store.SaveAsync(new[] { old with { Vba = written } });
        var json = await File.ReadAllTextAsync(paths.ProjectsFilePath);
        var reloaded = (await new ProjectStore(paths).LoadAsync()).Value.Should().ContainSingle().Subject.Vba!;

        json.Should().Contain("\"workbookInfoSheetViews\"").And.Contain("\"codeName\": \"Sheet1\"").And.Contain("\"mode\": \"Range\"")
            .And.Contain("\"range\": \"A1:F30\"");
        reloaded.ResolveSheetViews(5).For("Sheet1").Should().Be(new VbaSheetView(VbaSheetViewMode.Range, 5, false, "A1:F30"));
        reloaded.WorkbookInfoSampleValues.Should().BeFalse("移行が済んだので、旧項目はオフ");
        reloaded.ResolveSheetViews(5).For("Sheet2").PassesValues.Should().BeFalse();
    }
}
