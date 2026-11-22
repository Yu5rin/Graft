using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Graft.Core.Vba;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// <see cref="VbaWorkbookInfoFormatter"/>（ブックの情報 → 依頼文の Markdown）のテスト。
/// 仕様書_ブックの情報と依頼の観点 5.1・5.5・5.7・11.1。
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

    private static string Format(VbaWorkbookInfoSnapshot s, VbaWorkbookInfoLevel level = VbaWorkbookInfoLevel.Full,
        bool samples = false, int max = 20000, bool stale = false)
        => VbaWorkbookInfoFormatter.Format(s, level, samples, max, stale);

    [Fact(DisplayName = "出さない: 空の文字列（見出しも残らない）")]
    public void 出さない()
    {
        Format(Sample(), VbaWorkbookInfoLevel.Hidden).Should().BeEmpty();
    }

    [Fact(DisplayName = "内容も出す: 見出し・取得日時・シート表・テーブル・名前・先頭行の見出し・参照設定がこの順に出る（見本は出ない）")]
    public void 内容も出す()
    {
        var text = Format(Sample());

        text.Should().StartWith("# ブックの情報\n（Excel から取得: ");
        text.Should().Contain("/ 計画.xlsm）");
        var order = new[] { "## シート", "## テーブル", "## 名前付き範囲", "## 先頭行の見出し", "## 参照設定" }
            .Select(h => text.IndexOf(h, StringComparison.Ordinal)).ToArray();
        order.Should().OnlyContain(i => i >= 0);
        order.Should().BeInAscendingOrder();
        text.Should().NotContain("値の見本");
        text.Should().NotContain("46000", "見本をオンにしていなければ、スナップショットに見本があっても出さない");
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

    [Fact(DisplayName = "先頭行の見出し: 列の文字つき。使用範囲が A 列始まりでなければ実際の列文字。空のシートは出ない")]
    public void 先頭行の見出し()
    {
        var shifted = Sample() with
        {
            Sheets = new[]
            {
                new VbaSheetInfo("集計", "Sheet1", VbaSheetVisibility.Visible, "C2:E9",
                    new[] { "日付", "", "金額" }, Array.Empty<IReadOnlyList<string>>(), OmittedColumns: 4),
            },
        };

        var text = Format(shifted);

        text.Should().Contain("- 集計（Sheet1）: C 日付 | D （空） | E 金額 | …（他 4 列）");
        Format(Sample()).Should().Contain("- 集計（Sheet1）: A 日付 | B 顧客 | C 品目 | D 数量 | E 単価 | F 金額");
        Format(Sample()).Should().NotContain("- 空（Sheet3）");
    }

    [Fact(DisplayName = "構成だけ: シート・テーブル・名前付き範囲だけで、見出し・参照設定・見本は無い")]
    public void 構成だけ()
    {
        var text = Format(Sample(), VbaWorkbookInfoLevel.StructureOnly, samples: true);

        text.Should().Contain("## シート").And.Contain("## テーブル").And.Contain("## 名前付き範囲");
        text.Should().NotContain("## 先頭行の見出し");
        text.Should().NotContain("## 参照設定");
        text.Should().NotContain("値の見本");
        text.Should().NotContain("46000");
    }

    [Fact(DisplayName = "値の見本: オンのときだけ出る。見出し・注意・数式つきのセルが表になる")]
    public void 値の見本はオプトイン()
    {
        var on = Format(Sample(), samples: true);

        on.Should().Contain("## 値の見本（各シートの見出し行に続く 1 行。AI に渡ります）");
        on.Should().Contain("### 集計（Sheet1）");
        on.Should().Contain("| 日付 | 顧客 | 品目 | 数量 | 単価 | 金額 |");
        on.Should().Contain("| 46000 | 甲社 | A-100 | 10 | 250 | =D2*E2 → 2500 |");
        Format(Sample(), samples: false).Should().NotContain("値の見本");
        Format(Sample(withSamples: false), samples: true).Should().NotContain("値の見本", "見本を持つシートが無ければ節ごと出ない");
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

        var text = Format(snapshot, samples: true);

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

    [Fact(DisplayName = "全体の上限: 超えたら値の見本から先に落とし、末尾に省略の注記を付ける。構成（シート）は残る")]
    public void 上限は見本から落とす()
    {
        var snapshot = Big(sheets: 10, cols: 8, sampleRows: 5);
        var withSamples = Format(snapshot, samples: true, max: 1_000_000);
        var withoutSamples = Format(snapshot, samples: false, max: 1_000_000);
        withSamples.Length.Should().BeGreaterThan(withoutSamples.Length);

        // 見本を落とせば収まる大きさにする。
        var limit = (withSamples.Length + withoutSamples.Length) / 2;
        var cut = Format(snapshot, samples: true, max: limit);

        cut.Length.Should().BeLessThanOrEqualTo(limit + VbaWorkbookInfoFormatter.TruncatedNote.Length + 2);
        cut.Should().EndWith(VbaWorkbookInfoFormatter.TruncatedNote + "\n");
        cut.Should().Contain("## 先頭行の見出し", "見本より後まで残る");
        cut.Should().Contain("## 参照設定").And.Contain("## 名前付き範囲").And.Contain("## テーブル");
        for (var i = 1; i <= 10; i++) cut.Should().Contain($"| シート{i} | Sheet{i} |");
    }

    [Fact(DisplayName = "全体の上限: さらに小さければ 見出し → 参照設定 → 名前付き範囲 → テーブルの列名 の順に落ち、最後までシートが残る")]
    public void 上限の切る順序()
    {
        var snapshot = Big(sheets: 10, cols: 8, sampleRows: 0);
        var full = Format(snapshot, max: 1_000_000);

        // 見出しを全部落としたあとの大きさの少し上を上限にすると、見出しだけが消え、参照設定は残る。
        var noHeaders = full[..full.IndexOf("## 先頭行の見出し", StringComparison.Ordinal)]
                        + full[full.IndexOf("## 参照設定", StringComparison.Ordinal)..];
        var cutHeaders = Format(snapshot, max: noHeaders.Length + 30);
        cutHeaders.Should().NotContain("## 先頭行の見出し");
        cutHeaders.Should().Contain("## 参照設定").And.Contain("## 名前付き範囲");
        cutHeaders.Should().Contain("| シート10 | Sheet10 |");

        // とても小さい上限: シート以外はすべて落ち、それでも収まらなければシートを末尾から落とす。
        var tiny = Format(snapshot, max: 300);
        tiny.Should().NotContain("## 先頭行の見出し").And.NotContain("## 参照設定").And.NotContain("## 名前付き範囲").And.NotContain("## テーブル");
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
    [Fact(DisplayName = "疑わしい 1: 範囲参照でない名前（定数・数式）は、見本オフでは名前だけ。見本オンのときだけ式も出る")]
    public void 定数の名前は見本オフでは式を出さない()
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

        Format(snapshot, VbaWorkbookInfoLevel.StructureOnly, samples: true).Should().NotContain("SECRET-pass", "構成だけのときは見本の鍵が開かない");
    }
}
