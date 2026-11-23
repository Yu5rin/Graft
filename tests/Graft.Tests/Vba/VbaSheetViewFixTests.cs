using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Graft.Core.Vba;
using Graft.Features;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// 「ブックの情報」のシートごとの見せ方（仕様書_ブックの情報と依頼の観点 5.2。v0.4）で見つかった不具合の回帰テスト（純ロジック）。
/// 各テストの名前に、直した不具合の番号を付けている（不具合 N）。画面まで通す回帰テストは
/// <c>Graft.UiTests</c> の <c>WorkbookInfoSheetViewFixTests</c>。
/// </summary>
public class VbaSheetViewFixTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 22, 10, 30, 0, TimeSpan.Zero);

    private static IReadOnlyList<IReadOnlyList<string>> NoRows => Array.Empty<IReadOnlyList<string>>();

    private static VbaSheetInfo Sheet(string name, string code, string? used = "A1:B3")
        => new(name, code, VbaSheetVisibility.Visible, used, Array.Empty<string>(), NoRows);

    private static VbaSheetViewSet Views(params (string CodeName, VbaSheetView View)[] views)
        => new(VbaSheetView.StructureOnly, views.Select(v => new KeyValuePair<string, VbaSheetView>(v.CodeName, v.View)));

    private static VbaWorkbookInfoSnapshot Snapshot(
        IReadOnlyList<VbaSheetInfo> sheets, IReadOnlyList<VbaNamedRangeInfo>? names = null,
        IReadOnlyList<VbaTableInfo>? tables = null, IReadOnlyList<string>? omitted = null,
        IReadOnlyList<VbaSheetIdentity>? all = null)
        => new("計画.xlsm", At, sheets, tables ?? Array.Empty<VbaTableInfo>(), names ?? Array.Empty<VbaNamedRangeInfo>(),
            Array.Empty<VbaReferenceInfo>(), omitted ?? Array.Empty<string>(), all);

    private static string Format(VbaWorkbookInfoSnapshot s, VbaSheetViewSet views, int max = 20000)
        => VbaWorkbookInfoFormatter.Format(s, VbaWorkbookInfoLevel.Full, views, max);

    // ---------------- 不具合 2・疑わしい点: 「出さない」のシートが取得結果に載らなかったとき ----------------

    [Fact(DisplayName = "不具合 2: 「出さない」のシートが取得結果に載らなくても（全シートの名前だけは分かる）、そのシートの名前・注記・シート限定の名前が出ない")]
    public void 不具合2_載らなかった出さないシートも伏せる()
    {
        // 時間切れで「秘密」以降が読めなかった取得結果。Sheets には「公開」だけが載り、全シートの名前（AllSheets）には「秘密」も載る。
        var snapshot = Snapshot(
            new[] { Sheet("公開", "Sheet1") },
            names: new[]
            {
                new VbaNamedRangeInfo("原価", "=秘密!$B$3", null),
                new VbaNamedRangeInfo("内訳", "=秘密!$A$1:$C$9", "秘密"),
                new VbaNamedRangeInfo("税率", "=公開!$B$3", null),
            },
            omitted: new[] { "時間がかかったため、シート「秘密」以降は読めていません。", "シート「公開」のテーブルは読めませんでした（0x800A03EC）。" },
            all: new[] { new VbaSheetIdentity("公開", "Sheet1"), new VbaSheetIdentity("秘密", "Sheet2") });

        var text = Format(snapshot, Views(("Sheet2", VbaSheetView.Excluded)));

        text.Should().NotContain("秘密").And.NotContain("原価").And.NotContain("内訳");
        text.Should().Contain("税率", "出してよいシートを指す名前は残る").And.Contain("シート「公開」のテーブルは読めませんでした");
    }

    [Fact(DisplayName = "不具合 2: 全シートの名前（AllSheets）が無くても、取得結果に無いシートを指す名前・注記は伏せる側に倒す")]
    public void 不具合2_一覧に無いシートを指す名前は伏せる()
    {
        var snapshot = Snapshot(
            new[] { Sheet("公開", "Sheet1") },
            names: new[]
            {
                new VbaNamedRangeInfo("原価", "=秘密!$B$3", null),
                new VbaNamedRangeInfo("引用", "='売上 2026'!$A$1", null),
                new VbaNamedRangeInfo("範囲外", "=公開!$A$1", "消えたシート"),
                new VbaNamedRangeInfo("税率", "=公開!$B$3", null),
                new VbaNamedRangeInfo("壊れ", "=#REF!", null),
            },
            omitted: new[] { "シート「秘密」は読めませんでした（0x800A03EC）。" });

        var text = Format(snapshot, VbaSheetViewSet.AllStructureOnly);

        text.Should().NotContain("秘密").And.NotContain("売上 2026").And.NotContain("消えたシート");
        text.Should().Contain("税率").And.Contain("壊れ", "#REF! はシート名を指していないので伏せない");
    }

    [Fact(DisplayName = "疑わしい点: 「出さない」のシートのテーブルを指す名前（構造化参照）も伏せる。出してよいテーブルを指す名前は残る")]
    public void 出さないシートのテーブルを指す名前も伏せる()
    {
        var snapshot = Snapshot(
            new[] { Sheet("公開", "Sheet1"), Sheet("秘密", "Sheet2") },
            names: new[]
            {
                new VbaNamedRangeInfo("単価", "=秘密テーブル[単価]", null),
                new VbaNamedRangeInfo("表の別名", "=公開!売上[[#Headers],[日付]]", null),
                new VbaNamedRangeInfo("売上列", "=売上[金額]", null),
            },
            tables: new[]
            {
                new VbaTableInfo("売上", "公開", "A1:B3", new[] { "日付", "金額" }),
                new VbaTableInfo("秘密テーブル", "秘密", "A1:B3", new[] { "単価" }),
            });

        var text = Format(snapshot, Views(("Sheet2", VbaSheetView.Excluded)));

        text.Should().NotContain("秘密テーブル").And.NotContain("単価");
        text.Should().Contain("売上列 = =売上[金額]").And.Contain("表の別名");

        // 「出さない」のシートのテーブルは取得結果に載らない（読まない）ので、一覧に無いテーブルを指す名前も伏せる。
        var withoutTable = Snapshot(
            new[] { Sheet("公開", "Sheet1"), Sheet("秘密", "Sheet2") },
            names: new[] { new VbaNamedRangeInfo("単価", "=秘密テーブル[単価]", null) });
        Format(withoutTable, Views(("Sheet2", VbaSheetView.Excluded))).Should().NotContain("秘密テーブル");
    }

    [Fact(DisplayName = "不具合 2: 名前の式から参照先のシート名を取り出す（引用・3-D・#REF!・別ブックの区別）")]
    public void 不具合2_参照先のシート名()
    {
        VbaWorkbookInfoReadRules.ReferencedSheetNames("=設定!$B$3").Should().Equal("設定");
        VbaWorkbookInfoReadRules.ReferencedSheetNames("='売上 2026'!$A$1:$B$9").Should().Equal("売上 2026");
        VbaWorkbookInfoReadRules.ReferencedSheetNames("='It''s'!A1").Should().Equal("It's");
        VbaWorkbookInfoReadRules.ReferencedSheetNames("=Sheet1:Sheet3!A1").Should().BeEquivalentTo("Sheet1", "Sheet3");
        VbaWorkbookInfoReadRules.ReferencedSheetNames("=#REF!").Should().BeEmpty();
        VbaWorkbookInfoReadRules.ReferencedSheetNames("=0.08").Should().BeEmpty();
        VbaWorkbookInfoReadRules.ReferencedSheetNames("=IF(A!B1>0,C!D1,0)").Should().BeEquivalentTo("A", "C");
        VbaWorkbookInfoReadRules.StructuredReferenceTables("=売上[金額]").Should().Equal("売上");
        VbaWorkbookInfoReadRules.StructuredReferenceTables("=Sheet1!売上[[#Headers],[日付]]").Should().Equal("売上");
        VbaWorkbookInfoReadRules.StructuredReferenceTables("=Sheet1!$A$1").Should().BeEmpty();
    }

    // ---------------- 不具合 4: 消えた・別のシートになったコード名の記録 ----------------

    private static VbaLink LinkWith(params VbaSheetViewSetting[] records) => new() { WorkbookInfoSheetViews = records };

    private static VbaSheetViewSetting Record(string code, string mode, string? name)
        => new() { CodeName = code, Mode = mode, SheetName = name };

    [Fact(DisplayName = "不具合 4: コード名は同じでも表示名が食い違う記録は、値が渡る見せ方なら落とす（構成だけに戻り、再び選ぶときに確認が出る）")]
    public void 不具合4_表示名が食い違えば落とす()
    {
        var link = LinkWith(Record("Sheet3", "All", "旧明細"), Record("Sheet1", "HeaderRows", "集計"));
        var sheets = new[] { new VbaSheetIdentity("別の明細", "Sheet3"), new VbaSheetIdentity("集計", "Sheet1") };

        var result = link.ReconcileSheetViews(sheets);

        result.Changed.Should().BeTrue();
        result.Renamed.Should().Equal("Sheet3");
        result.Link.ResolveSheetViews(5).For("Sheet3").PassesValues.Should().BeFalse("別のシートかもしれないので、確認なしでは渡さない");
        result.Link.ResolveSheetViews(5).For("Sheet1").Mode.Should().Be(VbaSheetViewMode.HeaderRows, "食い違いの無い記録は保つ");
    }

    [Fact(DisplayName = "不具合 4: 取得結果に無いコード名の、値が渡る記録は落とす。「出さない」「構成だけ」の記録は残す")]
    public void 不具合4_消えたシートの値の記録は落とす()
    {
        var link = LinkWith(
            Record("Sheet9", "All", "消えた明細"), Record("Sheet8", "None", "消えた秘密"), Record("Sheet1", "HeaderRows", "集計"));

        var result = link.ReconcileSheetViews(new[] { new VbaSheetIdentity("集計", "Sheet1") });

        result.Removed.Should().Equal("Sheet9");
        result.Link.WorkbookInfoSheetViews!.Select(r => r.CodeName).Should().BeEquivalentTo("Sheet8", "Sheet1");
    }

    [Fact(DisplayName = "不具合 4: 表示名の記録が無い（この項目が無かった版の）記録は、見せ方を変えずに表示名を添える。「出さない」は表示名が違っても残して更新する")]
    public void 不具合4_表示名が無い記録と出さない記録()
    {
        var link = LinkWith(Record("Sheet1", "All", null), Record("Sheet2", "None", "旧設定"));

        var result = link.ReconcileSheetViews(new[] { new VbaSheetIdentity("集計", "Sheet1"), new VbaSheetIdentity("新設定", "Sheet2") });

        result.Changed.Should().BeTrue();
        result.Renamed.Should().BeEmpty();
        result.Link.ResolveSheetViews(5).For("Sheet1").Mode.Should().Be(VbaSheetViewMode.All);
        result.Link.ResolveSheetViews(5).For("Sheet2").Mode.Should().Be(VbaSheetViewMode.None, "出さないは安全側なので、別のシートになっていても残す");
        result.Link.WorkbookInfoSheetViews!.Single(r => r.CodeName == "Sheet1").SheetName.Should().Be("集計");
        result.Link.WorkbookInfoSheetViews!.Single(r => r.CodeName == "Sheet2").SheetName.Should().Be("新設定");

        // 食い違いが無ければ何も変わらない（保存を増やさない）。
        result.Link.ReconcileSheetViews(new[] { new VbaSheetIdentity("集計", "Sheet1"), new VbaSheetIdentity("新設定", "Sheet2") })
            .Changed.Should().BeFalse();
    }

    [Fact(DisplayName = "不具合 4: 記録を書くときに、指定したシートの表示名を添える。指定の無い記録の表示名は、そのまま残す")]
    public void 不具合4_記録に表示名を添える()
    {
        var link = LinkWith(Record("Sheet2", "All", "設定"));
        var views = link.ResolveSheetViews(5).With("Sheet1", new VbaSheetView(VbaSheetViewMode.All));

        var written = link.WithSheetViews(views, new Dictionary<string, string> { ["Sheet1"] = "集計" });

        written.WorkbookInfoSheetViews!.Single(r => r.CodeName == "Sheet1").SheetName.Should().Be("集計");
        written.WorkbookInfoSheetViews!.Single(r => r.CodeName == "Sheet2").SheetName.Should().Be("設定");
    }

    // ---------------- 不具合 6: コード名が空のシート ----------------

    [Fact(DisplayName = "不具合 6: コード名が空のシートの見せ方は、既定が「見出し＋N 行」の移行中でも構成だけ（鍵が無いので値を渡さない側に倒す）")]
    public void 不具合6_空のコード名は構成だけ()
    {
        var legacy = new VbaLink { WorkbookInfoSampleValues = true }.ResolveSheetViews(5);

        legacy.For("Sheet1").Mode.Should().Be(VbaSheetViewMode.HeaderRows, "前提: 記録の無いシートの既定は見出し＋N 行");
        legacy.For(string.Empty).Mode.Should().Be(VbaSheetViewMode.StructureOnly);
        legacy.For(string.Empty).PassesValues.Should().BeFalse();
    }

    // ---------------- 不具合 8: 全角の入力と $ の位置 ----------------

    [Theory(DisplayName = "不具合 8: 全角で入力した範囲も半角へそろえて受け付ける")]
    [InlineData("Ａ１", "A1")]
    [InlineData("Ａ１：Ｆ３０", "A1:F30")]
    [InlineData("A1：F30", "A1:F30")]
    [InlineData("A１", "A1")]
    [InlineData("３:１０", "3:10")]
    [InlineData("Ａ：Ｃ", "A:C")]
    [InlineData("　ｂ２　", "B2")]
    [InlineData("$A$1:$B$2", "A1:B2")]
    [InlineData("A$1", "A1")]
    [InlineData("$A1", "A1")]
    [InlineData("$A:$F", "A:F")]
    [InlineData("$3:$10", "3:10")]
    public void 不具合8_全角を受け付ける(string input, string normalized)
    {
        VbaA1Range.TryParse(input, out var range, out var error).Should().BeTrue($"入力 \"{input}\"（理由: {error}）");
        range.ToA1().Should().Be(normalized);
    }

    [Theory(DisplayName = "不具合 8: $ は列の前と行の前だけ。位置の違う $ と、セルの途中の空白は通さない")]
    [InlineData("A1$")]
    [InlineData("$$A1")]
    [InlineData("A$$1")]
    [InlineData("A 1")]
    [InlineData("A1 B2")]
    [InlineData("A1:$")]
    [InlineData("1$:3")]
    public void 不具合8_位置の違う記号は通さない(string input)
    {
        VbaA1Range.TryParse(input, out _, out var error).Should().BeFalse();
        error.Should().NotBeNullOrEmpty();
    }

    [Fact(DisplayName = "不具合 8: 全角の数字が「名前付き範囲」「行は 1〜…」といった的外れな理由で退けられない")]
    public void 不具合8_理由が的外れでない()
    {
        VbaA1Range.TryParse("A1 B2", out _, out var spaced).Should().BeFalse();
        spaced.Should().NotContain("名前付き範囲", "空白の入った入力は名前ではない");

        VbaA1Range.TryParse("Ａ１０００００００", out _, out var tooBig).Should().BeFalse();
        tooBig.Should().Contain("行は 1〜1,048,576", "全角でも、大きすぎる行は行の範囲の誤りとして知らせる");
    }

    [Theory(DisplayName = "不具合 8: 見出しに続く行数も、全角の数字を受け付ける。整数以外・負・上限超過は退ける")]
    [InlineData("５", true, 5)]
    [InlineData(" １００ ", true, 100)]
    [InlineData("0", true, 0)]
    [InlineData("１０１", false, 0)]
    [InlineData("-1", false, 0)]
    [InlineData("1.5", false, 0)]
    [InlineData("", false, 0)]
    [InlineData("abc", false, 0)]
    public void 不具合8_行数の全角(string input, bool ok, int expected)
    {
        VbaSheetViewLimits.TryParseHeaderRows(input, out var rows).Should().Be(ok);
        rows.Should().Be(expected);
    }

    // ---------------- 不具合 10: 空のシートの「範囲を指定」 ----------------

    [Fact(DisplayName = "不具合 10: 使用範囲の無い（空の）シートを「範囲を指定」にしたら、読まずに「使用範囲の外」の計画になり、依頼文に理由が出る")]
    public void 不具合10_空のシートの範囲指定()
    {
        var view = new VbaSheetView(VbaSheetViewMode.Range, Range: "A1:B2");
        var plan = VbaSheetReadPlanner.Plan(null, view, 30, 500, 50);

        plan.ReadsCells.Should().BeFalse("読む所が無いので、セルは読まない");
        plan.OutsideUsedRange.Should().BeTrue();
        plan.Requested.Should().Be("A1:B2");

        var info = VbaSheetReadPlanner.Assemble(Sheet("空", "Sheet1", used: null), plan, NoRows, null);
        info.Block.Should().NotBeNull();
        var text = Format(Snapshot(new[] { info }), Views(("Sheet1", view)));
        text.Should().Contain("### 空（Sheet1）: 範囲 A1:B2").And.Contain("指定の範囲 A1:B2 は、このシートの使用範囲 （空） の外で、値はありません");
    }

    [Fact(DisplayName = "不具合 10: 空のシートの「全部」「見出し＋N 行」は、見出しだけで中身の無い値の節を作らない（従来どおり）")]
    public void 不具合10_空のシートの全部は何も出さない()
    {
        foreach (var mode in new[] { VbaSheetViewMode.All, VbaSheetViewMode.HeaderRows })
        {
            var view = new VbaSheetView(mode);
            var plan = VbaSheetReadPlanner.Plan(null, view, 30, 500, 50);
            var info = VbaSheetReadPlanner.Assemble(Sheet("空", "Sheet1", used: null), plan, NoRows, null);

            info.Block.Should().BeNull();
            Format(Snapshot(new[] { info }), Views(("Sheet1", view))).Should().NotContain("## シートの値");
        }
    }

    // ---------------- 不具合 9: 行数の上限と文字数の上限の注記 ----------------

    private static VbaSheetInfo BlockSheet(int rows, int cols, int totalRows, int totalCols)
    {
        var grid = Enumerable.Range(0, rows)
            .Select(r => (IReadOnlyList<string>)Enumerable.Range(0, cols).Select(c => $"v{r + 1}-{c + 1}").ToArray())
            .ToArray();
        return new VbaSheetInfo(
            "明細", "Sheet1", VbaSheetVisibility.Visible, $"A1:{VbaWorkbookInfoReadRules.ColumnLetters(totalCols)}{totalRows}",
            Array.Empty<string>(), NoRows, Block: new VbaSheetBlock(VbaSheetViewMode.All, null, 1, 1, grid, totalRows, totalCols));
    }

    [Fact(DisplayName = "不具合 9: 読む行数の上限と文字数の上限が重なっても、注記の「先頭の N 行」と続きの範囲の例は、実際に出した行数に合う")]
    public void 不具合9_注記は出した行数に合う()
    {
        var snapshot = Snapshot(new[] { BlockSheet(rows: 500, cols: 2, totalRows: 2000, totalCols: 2) });
        var views = Views(("Sheet1", new VbaSheetView(VbaSheetViewMode.All)));

        var text = Format(snapshot, views, max: 3000);

        var shownRows = text.Split('\n').Count(l => l.StartsWith("| ", StringComparison.Ordinal) && char.IsDigit(l[2]));
        shownRows.Should().BeInRange(1, 499, "前提: 文字数の上限で、読んだ 500 行より少ない行数まで減っている");
        text.Should().Contain($"先頭の {shownRows} 行・2 列（A1:B{shownRows}）だけを出しています");
        text.Should().Contain($"例 A{shownRows + 1}:B{shownRows * 2}", "続きの例は、出した次の行から");
        text.Should().NotContain("先頭の 500 行", "出していない 500 行を、出したと言わない");
        text.Should().NotContain("文字数の上限のため、500 行のうち", "読んだ行数と出した行数の 2 つの数字を並べない");
    }

    [Fact(DisplayName = "不具合 9: 文字数の上限で値を丸ごと落としたシートは、名前を挙げて伝える")]
    public void 不具合9_落としたシートを列挙する()
    {
        var big = BlockSheet(rows: 400, cols: 6, totalRows: 400, totalCols: 6);
        var small = Sheet("後ろ", "Sheet2") with
        {
            Block = new VbaSheetBlock(VbaSheetViewMode.All, null, 1, 1, new[] { (IReadOnlyList<string>)new[] { "x", "y" } }, 1, 2),
        };
        var snapshot = Snapshot(new[] { big, small });
        var views = Views(("Sheet1", new VbaSheetView(VbaSheetViewMode.All)), ("Sheet2", new VbaSheetView(VbaSheetViewMode.All)));
        var full = Format(snapshot, views, max: 1_000_000);

        // 末尾の「後ろ」の値だけが落ちる大きさに収める（先頭の大きなシートの値は、入るところまで残る）。
        var text = Format(snapshot, views, max: full.Length / 3);

        text.Should().Contain("（長いため一部を省略しました）");
        text.Should().Contain("文字数の上限のため、次のシートの値は出していません: 「後ろ」");
    }
}
