using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Graft.Core.Vba;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// <see cref="VbaWorkbookInfoFormatter"/>（ブックの情報 → 依頼文の Markdown）のテスト。
/// 仕様書_ブックの情報と依頼の観点 5.1・5.2・5.5・5.7・11.1。v0.4 でシートごとの見せ方
/// （出さない／構成だけ／見出し＋N 行／全部／範囲を指定）に合わせて書き直した。
/// </summary>
public class VbaWorkbookInfoFormatterTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 21, 10, 30, 0, TimeSpan.Zero);

    private static IReadOnlyList<IReadOnlyList<string>> Rows(params string[][] rows) => rows;

    private static VbaWorkbookInfoSnapshot Sample(bool withSamples = true)
    {
        var sheet1 = new VbaSheetInfo(
            "集計", "Sheet1", VbaSheetVisibility.Visible, "A1:F120",
            new[] { "日付", "顧客", "品目", "数量", "単価", "金額" },
            withSamples
                ? Rows(new[] { "46000", "甲社", "A-100", "10", "250", "=D2*E2 → 2500" })
                : Array.Empty<IReadOnlyList<string>>());
        var sheet2 = new VbaSheetInfo(
            "設定", "Sheet2", VbaSheetVisibility.Hidden, "A1:B12",
            new[] { "キー", "値" },
            withSamples ? Rows(new[] { "税率", "0.1" }) : Array.Empty<IReadOnlyList<string>>());
        var empty = new VbaSheetInfo(
            "空", "Sheet3", VbaSheetVisibility.VeryHidden, null,
            Array.Empty<string>(), Array.Empty<IReadOnlyList<string>>());
        return new VbaWorkbookInfoSnapshot(
            "計画.xlsm", At,
            new[] { sheet1, sheet2, empty },
            new[] { new VbaTableInfo("売上", "集計", "A1:F120", new[] { "日付", "顧客", "品目", "数量", "単価", "金額" }) },
            new[]
            {
                new VbaNamedRangeInfo("税率", "=設定!$B$3", null),
                new VbaNamedRangeInfo("上限", "=集計!$H$1", "集計"),
            },
            new[]
            {
                new VbaReferenceInfo("VBA", "", null, true, false),
                new VbaReferenceInfo("Scripting", "Microsoft Scripting Runtime", @"C:\Windows\System32\scrrun.dll", false, false),
                new VbaReferenceInfo("Broken", "なくなった部品", null, false, true),
            },
            Array.Empty<string>());
    }

    /// <summary>コード名 → 見せ方の一覧（記録の無いシートは構成だけ）。</summary>
    private static VbaSheetViewSet Views(params (string CodeName, VbaSheetView View)[] views)
        => new(VbaSheetView.StructureOnly, views.Select(v => new KeyValuePair<string, VbaSheetView>(v.CodeName, v.View)));

    private static VbaSheetView Header(int rows) => new(VbaSheetViewMode.HeaderRows, rows);

    /// <summary>
    /// 見せ方を指定して整形する。<paramref name="samples"/> が true なら、見本を持つ 2 シート（Sheet1・Sheet2）を
    /// 「見出し＋5 行」にする（v0.4 より前の「値の見本オン」と同じ意味）。何も渡さなければ全シート構成だけ。
    /// </summary>
    private static string Format(VbaWorkbookInfoSnapshot s, VbaWorkbookInfoLevel level = VbaWorkbookInfoLevel.Full,
        bool samples = false, int max = 20000, bool stale = false, VbaSheetViewSet? views = null)
        => VbaWorkbookInfoFormatter.Format(
            s, level,
            views ?? (samples ? Views(("Sheet1", Header(5)), ("Sheet2", Header(5))) : VbaSheetViewSet.AllStructureOnly),
            max, stale);

    [Fact(DisplayName = "出さない: 空の文字列（見出しも残らない）。シートごとの見せ方は無視される")]
    public void 出さない()
    {
        Format(Sample(), VbaWorkbookInfoLevel.Hidden).Should().BeEmpty();
        Format(Sample(), VbaWorkbookInfoLevel.Hidden, samples: true).Should().BeEmpty();
    }

    [Fact(DisplayName = "既定（全シート構成だけ）: 見出し・取得日時・シート表・テーブル・名前・参照設定がこの順に出る。値も見出し行も出ない")]
    public void 既定は構成だけ()
    {
        var text = Format(Sample());

        text.Should().StartWith("# ブックの情報\n（Excel から取得: ");
        text.Should().Contain("/ 計画.xlsm）");
        var order = new[] { "## シート", "## テーブル", "## 名前付き範囲", "## 参照設定" }
            .Select(h => text.IndexOf(h, StringComparison.Ordinal)).ToArray();
        order.Should().OnlyContain(i => i >= 0);
        order.Should().BeInAscendingOrder();
        text.Should().NotContain("## シートの値");
        text.Should().NotContain("46000", "構成だけなら、スナップショットに値があっても出さない");
        text.Should().NotContain("日付 | 顧客", "見出し行も、見せ方が値を渡さないシートでは出ない");
        text.Should().NotContain("\r");
    }

    [Fact(DisplayName = "シートの表: 表示名・コード名・表示状態・使用範囲。空のシートは（空）、隠しシートも表示状態つきで出る")]
    public void シートの表()
    {
        var text = Format(Sample());

        text.Should().Contain("| シート名 | コード名 | 表示 | 使用範囲 |");
        text.Should().Contain("| 集計 | Sheet1 | 表示 | A1:F120 |");
        text.Should().Contain("| 設定 | Sheet2 | 非表示 | A1:B12 |");
        text.Should().Contain("| 空 | Sheet3 | VeryHidden | （空） |");
    }

    [Fact(DisplayName = "テーブル・名前付き範囲・参照設定の書式（スコープ・組み込み・参照切れ）")]
    public void 各節の書式()
    {
        var text = Format(Sample());

        text.Should().Contain("- 売上（シート「集計」 A1:F120）: 日付, 顧客, 品目, 数量, 単価, 金額");
        text.Should().Contain("- 税率 = =設定!$B$3（ブック全体）");
        text.Should().Contain("- 上限 = =集計!$H$1（シート「集計」限定）");
        text.Should().Contain("VBA（組み込み） / Scripting（Microsoft Scripting Runtime、C:\\Windows\\System32\\scrrun.dll） / Broken（なくなった部品）【参照切れ】");
    }

    [Fact(DisplayName = "全体が構成だけ: シート・テーブル・名前付き範囲だけ。参照設定も、シートの値も（見せ方が全部でも）出ない")]
    public void 全体が構成だけ()
    {
        var text = Format(Sample(), VbaWorkbookInfoLevel.StructureOnly, samples: true);

        text.Should().Contain("## シート").And.Contain("## テーブル").And.Contain("## 名前付き範囲");
        text.Should().NotContain("## 参照設定");
        text.Should().NotContain("## シートの値");
        text.Should().NotContain("46000");
        text.Should().NotContain("日付 | 顧客");
    }

    [Fact(DisplayName = "見出し＋N 行: 見出しと続く N 行が表になる。見出しの行は数式なし、続く行は数式つきのセルもそのまま")]
    public void 見出しとN行()
    {
        var text = Format(Sample(), views: Views(("Sheet1", Header(5))));

        text.Should().Contain("## シートの値（AI に渡ります）");
        text.Should().Contain("### 集計（Sheet1）: 見出し＋1 行", "見出しに続く行数は、実際に出した行数（見本が 1 行しか無ければ 1 行）");
        text.Should().Contain("| 日付 | 顧客 | 品目 | 数量 | 単価 | 金額 |");
        text.Should().Contain("| 46000 | 甲社 | A-100 | 10 | 250 | =D2*E2 → 2500 |");
        text.Should().NotContain("税率 | 0.1", "Sheet2 は構成だけなので、値は出ない");
        text.Should().NotContain("### 設定");
    }

    [Fact(DisplayName = "見出し＋N 行: 取得時より少ない N に変えたら、その N までしか出ない（古い取得結果から余分に出さない）")]
    public void 設定のNまでに絞る()
    {
        var snapshot = Sample() with
        {
            Sheets = new[]
            {
                new VbaSheetInfo("集計", "Sheet1", VbaSheetVisibility.Visible, "A1:B9", new[] { "a", "b" },
                    Rows(new[] { "r1", "1" }, new[] { "r2", "2" }, new[] { "r3", "3" })),
            },
        };

        var two = Format(snapshot, views: Views(("Sheet1", Header(2))));
        two.Should().Contain("見出し＋2 行").And.Contain("| r2 | 2 |").And.NotContain("r3");

        var zero = Format(snapshot, views: Views(("Sheet1", Header(0))));
        zero.Should().Contain("見出し＋0 行").And.Contain("| a | b |").And.NotContain("r1", "N が 0 なら見出しだけ");
    }

    [Fact(DisplayName = "見出し＋N 行: 列の文字つき。使用範囲が A 列始まりでなければ実際の列文字。空の見出しは列の文字。切った列数も伝える")]
    public void 見出しの列文字()
    {
        var shifted = Sample() with
        {
            Sheets = new[]
            {
                new VbaSheetInfo("集計", "Sheet1", VbaSheetVisibility.Visible, "C2:E9",
                    new[] { "日付", "", "金額" }, Rows(new[] { "1", "2", "3" }), OmittedColumns: 4),
            },
        };

        var text = Format(shifted, views: Views(("Sheet1", Header(1))));

        text.Should().Contain("| 日付 | D | 金額 |");
        text.Should().Contain("（他 4 列は省略）");
    }

    [Fact(DisplayName = "空のシート（使用範囲なし）は、見出し＋N 行でも値の節が出ない")]
    public void 空のシートは値が出ない()
    {
        var text = Format(Sample(), views: Views(("Sheet3", Header(5))));

        text.Should().NotContain("## シートの値");
    }

    private static VbaSheetInfo BlockSheet(string codeName, string name, int rows, int cols, int totalRows, int totalCols,
        VbaSheetViewMode mode = VbaSheetViewMode.All, string? requested = null, int startRow = 1, int startCol = 1, bool outside = false)
    {
        var grid = Enumerable.Range(0, rows)
            .Select(r => (IReadOnlyList<string>)Enumerable.Range(0, cols).Select(c => $"v{startRow + r}-{c + 1}").ToArray())
            .ToArray();
        return new VbaSheetInfo(
            name, codeName, VbaSheetVisibility.Visible, $"A1:{VbaWorkbookInfoReadRules.ColumnLetters(totalCols)}{totalRows}",
            Array.Empty<string>(), Array.Empty<IReadOnlyList<string>>(),
            Block: new VbaSheetBlock(mode, requested, startRow, startCol, grid, totalRows, totalCols, outside));
    }

    private static VbaWorkbookInfoSnapshot WithSheets(params VbaSheetInfo[] sheets)
        => Sample() with { Sheets = sheets, Tables = Array.Empty<VbaTableInfo>(), Names = Array.Empty<VbaNamedRangeInfo>() };

    [Fact(DisplayName = "全部: 行番号と列の文字つきの表になる。上限に収まるときは、切ったという注記は付かない")]
    public void 全部()
    {
        var snapshot = WithSheets(BlockSheet("Sheet1", "集計", rows: 3, cols: 2, totalRows: 3, totalCols: 2));
        var views = Views(("Sheet1", new VbaSheetView(VbaSheetViewMode.All)));

        var text = Format(snapshot, views: views);

        text.Should().Contain("### 集計（Sheet1）: 全部");
        text.Should().Contain("| 行 | A | B |");
        text.Should().Contain("| 1 | v1-1 | v1-2 |").And.Contain("| 3 | v3-1 | v3-2 |");
        text.Should().NotContain("切りました");
    }

    [Fact(DisplayName = "全部で上限を超えた: 何行・何列のうち何行・何列を出したかを明記し、「範囲を指定」と続きの範囲の例を案内する")]
    public void 全部で切ったら明記する()
    {
        var snapshot = WithSheets(BlockSheet("Sheet1", "明細", rows: 500, cols: 50, totalRows: 1200, totalCols: 60));
        var views = Views(("Sheet1", new VbaSheetView(VbaSheetViewMode.All)));

        var text = Format(snapshot, views: views, max: 1_000_000);

        text.Should().Contain("対象は 1200 行・60 列ありますが、先頭の 500 行・50 列（A1:AX500）だけを出しています");
        text.Should().Contain("「範囲を指定」").And.Contain("例 A501:AX1000");
    }

    [Fact(DisplayName = "全部で列だけ切った: 続きの範囲の例は、切った次の列から同じ行")]
    public void 列だけ切った()
    {
        var snapshot = WithSheets(BlockSheet("Sheet1", "横長", rows: 2, cols: 50, totalRows: 2, totalCols: 80));
        var views = Views(("Sheet1", new VbaSheetView(VbaSheetViewMode.All)));

        var text = Format(snapshot, views: views, max: 1_000_000);

        text.Should().Contain("対象は 2 行・80 列ありますが、先頭の 2 行・50 列");
        text.Should().Contain("例 AY1:CV2");
    }

    [Fact(DisplayName = "範囲を指定: 見出しに範囲が出て、行番号は範囲の先頭から。使用範囲との共通部分で読んだときはそれも伝える")]
    public void 範囲を指定()
    {
        var snapshot = WithSheets(BlockSheet(
            "Sheet1", "集計", rows: 2, cols: 2, totalRows: 2, totalCols: 2,
            mode: VbaSheetViewMode.Range, requested: "B10:D12", startRow: 10, startCol: 2));
        var views = Views(("Sheet1", new VbaSheetView(VbaSheetViewMode.Range, Range: "B10:D12")));

        var text = Format(snapshot, views: views);

        text.Should().Contain("### 集計（Sheet1）: 範囲 B10:D12");
        text.Should().Contain("| 行 | B | C |");
        text.Should().Contain("| 10 | v10-1 | v10-2 |");
        text.Should().Contain("使用範囲との共通部分 B10:C11 を出しています");
    }

    [Fact(DisplayName = "範囲を指定: 範囲を変えた直後の古い取得結果（別の範囲）は出さない。範囲が空・不正のシートも出さない")]
    public void 古い範囲の結果は出さない()
    {
        var snapshot = WithSheets(BlockSheet(
            "Sheet1", "集計", rows: 2, cols: 2, totalRows: 5, totalCols: 5, mode: VbaSheetViewMode.Range, requested: "A1:B2"));

        Format(snapshot, views: Views(("Sheet1", new VbaSheetView(VbaSheetViewMode.Range, Range: "A1:B2"))))
            .Should().Contain("範囲 A1:B2");
        Format(snapshot, views: Views(("Sheet1", new VbaSheetView(VbaSheetViewMode.Range, Range: "A1:C3"))))
            .Should().NotContain("## シートの値", "取得した範囲と、いまの範囲が違う");
        Format(snapshot, views: Views(("Sheet1", new VbaSheetView(VbaSheetViewMode.Range, Range: null))))
            .Should().NotContain("## シートの値", "範囲が空なら何も渡さない");
        Format(snapshot, views: Views(("Sheet1", new VbaSheetView(VbaSheetViewMode.All))))
            .Should().NotContain("## シートの値", "取得したのは範囲指定で、全部ではない");
    }

    [Fact(DisplayName = "範囲を指定: 範囲が使用範囲の外なら、その旨だけを伝える")]
    public void 範囲が使用範囲の外()
    {
        var snapshot = WithSheets(BlockSheet(
            "Sheet1", "集計", rows: 0, cols: 0, totalRows: 3, totalCols: 3, mode: VbaSheetViewMode.Range, requested: "Z100:Z200", outside: true));
        var views = Views(("Sheet1", new VbaSheetView(VbaSheetViewMode.Range, Range: "Z100:Z200")));

        var text = Format(snapshot, views: views);

        text.Should().Contain("### 集計（Sheet1）: 範囲 Z100:Z200");
        text.Should().Contain("指定の範囲 Z100:Z200 は、このシートの使用範囲 A1:C3 の外で、値はありません");
    }

    [Fact(DisplayName = "出さない: そのシートの行・テーブル・シート限定の名前・そのシートを指す名前・読めなかった旨の注記が、すべて出ない")]
    public void 出さないシートは何も出ない()
    {
        var snapshot = Sample() with
        {
            Omitted = new[] { "シート「設定」のテーブルは読めませんでした（0x80020009。シートの見出しなどは読めています）。", "参照設定は読めませんでした。" },
            Tables = new[]
            {
                new VbaTableInfo("売上", "集計", "A1:F120", new[] { "日付" }),
                new VbaTableInfo("設定表", "設定", "A1:B12", new[] { "キー" }),
            },
            Names = new[]
            {
                new VbaNamedRangeInfo("税率", "=設定!$B$3", null),
                new VbaNamedRangeInfo("上限", "=集計!$H$1", "集計"),
                new VbaNamedRangeInfo("内部", "=Sheet9!$A$1", "設定"),
            },
        };
        var views = Views(("Sheet2", VbaSheetView.Excluded));

        var text = Format(snapshot, views: views);

        text.Should().Contain("| 集計 | Sheet1 |").And.Contain("売上");
        text.Should().NotContain("| 設定 |").And.NotContain("「設定」").And.NotContain("設定!").And.NotContain("設定表", "シート名が、どこにも出ない");
        text.Should().NotContain("Sheet2").And.NotContain("税率").And.NotContain("内部");
        text.Should().Contain("上限").And.Contain("参照設定は読めませんでした。");
    }

    [Fact(DisplayName = "出さない: 全体が構成だけでも守られる（全体を切り替えて、出さないにしたシートが現れない）")]
    public void 出さないは構成だけでも守る()
    {
        var text = Format(Sample(), VbaWorkbookInfoLevel.StructureOnly, views: Views(("Sheet2", VbaSheetView.Excluded)));

        text.Should().NotContain("Sheet2").And.NotContain("| 設定 |").And.NotContain("設定!");
    }

    [Fact(DisplayName = "表のセルの縦棒は逃がす（表の区切りと紛れない）")]
    public void 縦棒を逃がす()
    {
        var snapshot = Sample() with
        {
            Sheets = new[]
            {
                new VbaSheetInfo("S", "Sheet1", VbaSheetVisibility.Visible, "A1:A2",
                    new[] { "a|b" }, Rows(new[] { "x|y" })),
            },
        };

        var text = Format(snapshot, views: Views(("Sheet1", Header(1))));

        text.Should().Contain("| a\\|b |").And.Contain("| x\\|y |");
    }

    [Fact(DisplayName = "最新を読めていないとき（前回の結果を使う）は、見出しの次の行に注記が付く")]
    public void 古い結果の注記()
    {
        Format(Sample(), stale: true).Should().Contain($"。{VbaWorkbookInfoFormatter.StaleNote}）");
        Format(Sample(), stale: false).Should().NotContain(VbaWorkbookInfoFormatter.StaleNote);
    }

    [Fact(DisplayName = "読み取れなかったもの（Omitted）は、末尾の節で AI にも伝える")]
    public void 省略の説明()
    {
        var snapshot = Sample() with { Omitted = new[] { "参照設定は読めませんでした。", "シートが多いため、先頭の 100 枚だけを読みました（他 3 枚）。" } };

        var text = Format(snapshot, VbaWorkbookInfoLevel.StructureOnly);

        text.Should().Contain("## 読み取れなかったもの\n- 参照設定は読めませんでした。\n- シートが多いため");
    }

    private static VbaWorkbookInfoSnapshot Big(int sheets, int cols, int sampleRows)
    {
        var infos = Enumerable.Range(1, sheets).Select(i => new VbaSheetInfo(
            $"シート{i}", $"Sheet{i}", VbaSheetVisibility.Visible, $"A1:Z{100 + i}",
            Enumerable.Range(1, cols).Select(c => $"見出し{c}").ToArray(),
            Enumerable.Range(1, sampleRows).Select(r =>
                (IReadOnlyList<string>)Enumerable.Range(1, cols).Select(c => $"値{r}-{c}").ToArray()).ToArray())).ToArray();
        return new VbaWorkbookInfoSnapshot(
            "大きい.xlsm", At, infos,
            new[] { new VbaTableInfo("表1", "シート1", "A1:C9", new[] { "x", "y", "z" }) },
            new[] { new VbaNamedRangeInfo("名前1", "=シート1!$A$1", null) },
            new[] { new VbaReferenceInfo("VBA", "", null, true, false) },
            Array.Empty<string>());
    }

    private static VbaSheetViewSet AllHeader(int sheets, int rows)
        => Views(Enumerable.Range(1, sheets).Select(i => ($"Sheet{i}", Header(rows))).ToArray());

    [Fact(DisplayName = "全体の上限: 超えたら値から先に（末尾のシートから）落とし、末尾に省略の注記を付ける。構成（シート）は残る")]
    public void 上限は値から落とす()
    {
        var snapshot = Big(sheets: 10, cols: 8, sampleRows: 5);
        var withValues = Format(snapshot, max: 1_000_000, views: AllHeader(10, 5));
        var withoutValues = Format(snapshot, max: 1_000_000);
        withValues.Length.Should().BeGreaterThan(withoutValues.Length);

        // 値を全部落とせば収まる大きさにする。
        var limit = (withValues.Length + withoutValues.Length) / 2;
        var cut = Format(snapshot, max: limit, views: AllHeader(10, 5));

        cut.Length.Should().BeLessThanOrEqualTo(limit + VbaWorkbookInfoFormatter.TruncatedNote.Length + 2);
        cut.Should().EndWith(VbaWorkbookInfoFormatter.TruncatedNote + "\n");
        cut.Should().Contain("## 参照設定").And.Contain("## 名前付き範囲").And.Contain("## テーブル", "値より後まで残る");
        cut.Should().Contain("### シート1（Sheet1）", "先頭のシートの値は残り、末尾のシートから落ちる");
        cut.Should().NotContain("### シート10（Sheet10）");
        for (var i = 1; i <= 10; i++) cut.Should().Contain($"| シート{i} | Sheet{i} |");
    }

    [Fact(DisplayName = "全体の上限: 大きな 1 枚（全部の 500 行）は、丸ごと落とす前に、入る行数まで残す。何行のうち何行かも伝える")]
    public void 上限は入る行数まで残す()
    {
        var snapshot = WithSheets(
            new VbaSheetInfo("小さい", "Sheet2", VbaSheetVisibility.Visible, "A1:B2", Array.Empty<string>(), Array.Empty<IReadOnlyList<string>>()),
            BlockSheet("Sheet1", "大きい", rows: 500, cols: 10, totalRows: 500, totalCols: 10));
        var views = Views(("Sheet1", new VbaSheetView(VbaSheetViewMode.All)));
        var full = Format(snapshot, views: views, max: 1_000_000);

        var limit = full.Length / 4;
        var cut = Format(snapshot, views: views, max: limit);

        cut.Length.Should().BeLessThanOrEqualTo(limit + VbaWorkbookInfoFormatter.TruncatedNote.Length + 2);
        cut.Should().Contain("### 大きい（Sheet1）: 全部");
        cut.Should().Contain("| 1 | v1-1 |", "先頭の行は残る");
        cut.Should().NotContain("| 500 | v500-1 |");
        cut.Should().MatchRegex(@"文字数の上限のため、500 行のうち先頭の \d+ 行だけを出しています");
        cut.Should().Contain("| 小さい | Sheet2 |", "構成は残る");
    }

    [Fact(DisplayName = "全体の上限: 値をすべて落としても収まらなければ、参照設定 → 名前付き範囲 → テーブルの列名 の順に落ち、最後までシートが残る")]
    public void 上限の切る順序()
    {
        var snapshot = Big(sheets: 10, cols: 8, sampleRows: 0);
        var full = Format(snapshot, max: 1_000_000);

        // 参照設定を落とした大きさの少し上を上限にすると、参照設定だけが消え、名前付き範囲は残る。
        var noRefs = full[..full.IndexOf("## 参照設定", StringComparison.Ordinal)];
        var cutRefs = Format(snapshot, max: noRefs.Length + 18);
        cutRefs.Should().NotContain("## 参照設定");
        cutRefs.Should().Contain("## 名前付き範囲").And.Contain("## テーブル");
        cutRefs.Should().Contain("| シート10 | Sheet10 |");

        // とても小さい上限: シート以外はすべて落ち、それでも収まらなければシートを末尾から落とす。
        var tiny = Format(snapshot, max: 300);
        tiny.Should().NotContain("## 参照設定").And.NotContain("## 名前付き範囲").And.NotContain("## テーブル");
        tiny.Should().Contain("| シート1 | Sheet1 |");
        tiny.Should().Contain(VbaWorkbookInfoFormatter.TruncatedNote);
    }

    [Fact(DisplayName = "上限に収まるときは、注記を付けない")]
    public void 収まれば注記なし()
    {
        Format(Sample(), max: 20000).Should().NotContain(VbaWorkbookInfoFormatter.TruncatedNote);
    }

    [Fact(DisplayName = "3状態の巡回と文字列の往復: 不明な文字列は Full、巡回は 内容も出す → 構成だけ → 出さない → 内容も出す")]
    public void 三状態()
    {
        VbaWorkbookInfoLevels.Parse(null).Should().Be(VbaWorkbookInfoLevel.Full);
        VbaWorkbookInfoLevels.Parse("").Should().Be(VbaWorkbookInfoLevel.Full);
        VbaWorkbookInfoLevels.Parse("なにか").Should().Be(VbaWorkbookInfoLevel.Full);
        VbaWorkbookInfoLevels.Parse("99").Should().Be(VbaWorkbookInfoLevel.Full);
        VbaWorkbookInfoLevels.Parse("StructureOnly").Should().Be(VbaWorkbookInfoLevel.StructureOnly);
        VbaWorkbookInfoLevels.Parse("Hidden").Should().Be(VbaWorkbookInfoLevel.Hidden);
        VbaWorkbookInfoLevels.Next(VbaWorkbookInfoLevel.Full).Should().Be(VbaWorkbookInfoLevel.StructureOnly);
        VbaWorkbookInfoLevels.Next(VbaWorkbookInfoLevel.StructureOnly).Should().Be(VbaWorkbookInfoLevel.Hidden);
        VbaWorkbookInfoLevels.Next(VbaWorkbookInfoLevel.Hidden).Should().Be(VbaWorkbookInfoLevel.Full);
    }

    [Fact(DisplayName = "疑わしい 1: 範囲参照でない名前（定数・数式）は、値を渡すシートが無ければ名前だけ。値を渡すシートを選んだときだけ式も出る")]
    public void 定数の名前は値を渡さなければ式を出さない()
    {
        var snapshot = Sample() with
        {
            Names = new[]
            {
                new VbaNamedRangeInfo("税率", "=設定!$B$3", null),
                new VbaNamedRangeInfo("合言葉", "=\"SECRET-pass\"", null),
                new VbaNamedRangeInfo("掛率", "=0.08", "集計"),
                new VbaNamedRangeInfo("動的", "=OFFSET(集計!$A$1,0,0,10,1)", null),
            },
        };

        var off = Format(snapshot, samples: false);
        off.Should().Contain("- 税率 = =設定!$B$3（ブック全体）");
        off.Should().Contain("- 合言葉（ブック全体）").And.NotContain("SECRET-pass");
        off.Should().Contain("- 掛率（シート「集計」限定）").And.NotContain("0.08");
        off.Should().Contain("- 動的（ブック全体）").And.NotContain("OFFSET", "関数の引数に定数を持ちうるので、参照とは見ない");

        var on = Format(snapshot, samples: true);
        on.Should().Contain("- 合言葉 = =\"SECRET-pass\"（ブック全体）").And.Contain("- 掛率 = =0.08").And.Contain("OFFSET");

        Format(snapshot, VbaWorkbookInfoLevel.StructureOnly, samples: true).Should().NotContain("SECRET-pass", "構成だけのときは鍵が開かない");
    }
}
