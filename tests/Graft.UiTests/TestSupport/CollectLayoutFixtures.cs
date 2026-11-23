using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Graft.Core.Vba;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Graft.UiTests.Vba;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests.TestSupport;

/// <summary>
/// 「AIに渡すファイルを選ぶ」画面（コンテキスト収集の窓）の配置のテスト・スクリーンショットが共通で使う下拵え。
/// 通常のプロジェクト（ファイル 18 個）と、VBA プロジェクト（モジュール 11 個・シート 5 枚・リボンの行つき）の 2 通り。
/// 設計案の試作と同じ条件（同じデータ・同じ選択）で撮り比べられるようにしてある。
/// </summary>
internal static class CollectLayoutFixtures
{
    /// <summary>走査・取得が終わり、描画が落ち着くまで待つ。</summary>
    public static async Task SettleAsync(ContextCollectViewModel context)
    {
        await Task.Delay(120).ConfigureAwait(true);
        await ShellHarness.WaitUntilAsync(() => !context.IsScanning).ConfigureAwait(true);
        for (var i = 0; i < 4; i++)
        {
            await Task.Delay(40).ConfigureAwait(true);
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>依頼の観点のチップを、Id で選ぶ。</summary>
    public static void Select(PromptFocusSelectionViewModel focus, params string[] ids)
    {
        foreach (var chip in focus.Groups.SelectMany(g => g.Chips).Where(c => ids.Contains(c.Id))) chip.IsSelected = true;
    }

    /// <summary>窓を開き（大きさを変えられる）、走査が終わるまで待つ。</summary>
    public static async Task<ContextCollectWindow> OpenAsync(
        ShownWindowTracker windows, ContextCollectViewModel context, double? width = null, double? height = null)
    {
        var window = new ContextCollectWindow(context);
        if (width is { } w) window.Width = w;
        if (height is { } h) window.Height = h;
        windows.Track(window);
        window.Show();
        await SettleAsync(context).ConfigureAwait(true);
        return window;
    }

    /// <summary>通常のプロジェクト: ファイル 18 個を作って登録し、観点を 3 つ選んだ収集の ViewModel を返す。</summary>
    public static async Task<ContextCollectViewModel> NormalAsync(ShellHarness h)
    {
        var body = string.Concat(Enumerable.Range(1, 60).Select(i => $"def handler_{i}(request):\n    return render(request, 'page_{i}.html')\n"));
        foreach (var rel in new[]
                 {
                     "README.md", "pyproject.toml", "main.py",
                     "src/app.py", "src/config.py", "src/models/user.py", "src/models/order.py", "src/models/invoice.py",
                     "src/views/home.py", "src/views/order_list.py", "src/views/settings.py",
                     "src/services/billing.py", "src/services/mailer.py",
                     "tests/test_user.py", "tests/test_order.py", "tests/test_billing.py",
                     "lib/vendor/big_table.py", "lib/vendor/helpers.py",
                 })
        {
            var path = h.ProjectPath(rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, body).ConfigureAwait(true);
        }

        await h.RegisterProjectAsync().ConfigureAwait(true);
        var context = h.Shell.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        Select(context.Focus, "focus-readable", "focus-bugfix", "focus-speed");
        return context;
    }

    /// <summary>5 枚のシート（集計・設定・明細・マスタ_顧客・月次レポート）。</summary>
    public static VbaWorkbookInfoSnapshot FiveSheets()
    {
        var sample = WorkbookInfoTests.Sample();
        VbaSheetInfo Sheet(string name, string code, string range) => new(
            name, code, VbaSheetVisibility.Visible, range, new[] { "日付", "担当", "金額" },
            new IReadOnlyList<string>[] { new[] { "46000", "甲", "100" }, new[] { "46001", "乙", "300" } });
        return sample with
        {
            Sheets = sample.Sheets
                .Append(Sheet("明細", "Sheet3", "A1:H480"))
                .Append(Sheet("マスタ_顧客", "Sheet4", "A1:F120"))
                .Append(Sheet("月次レポート", "Sheet5", "A1:M40"))
                .ToArray(),
        };
    }

    /// <summary>
    /// VBA プロジェクト: シート 5 枚、リボンの行（<paramref name="rootDir"/> に一時の xlsm を作る）、モジュール 11 個。
    /// 値が渡るシートの選択（集計＝見出し＋N 行、明細＝範囲を指定）は、呼び出し側で行う。
    /// </summary>
    public static async Task<VbaP3Rig> VbaRigAsync(string rootDir)
    {
        Directory.CreateDirectory(rootDir);
        var path = Path.Combine(rootDir, "計画.xlsm");
        await File.WriteAllBytesAsync(path, RibbonPackageBuilder.Workbook(RibbonPackageBuilder.SampleRibbon)).ConfigureAwait(true);
        var rig = await VbaP3Rig.CreateAsync(
            new Settings { ShowPreview = false },
            arrange: bridge =>
            {
                var book = bridge.Get(VbaP3Rig.Book)!;
                book.Info = FiveSheets();
                book.Find("Module1")!.Code =
                    "Public Sub OnRun(control As IRibbonControl)\r\nEnd Sub\r\nPrivate Sub GetEnabled(control As IRibbonControl, ByRef r)\r\nEnd Sub";
                foreach (var name in new[] { "Module2", "ModReport", "ModUtil" }) book.Modules.Add(FakeVbaBridge.Std(name, "Sub A()\nEnd Sub\n"));
                foreach (var name in new[] { "clsProgressBar", "clsOrder" }) book.Modules.Add(FakeVbaBridge.Cls(name, "Option Explicit\n"));
                book.Modules.Add(FakeVbaBridge.Doc("Sheet2", "' sheet", "設定"));
                book.Modules.Add(FakeVbaBridge.Doc("Sheet3", "' sheet", "明細"));
                book.Modules.Add(FakeVbaBridge.Doc("ThisWorkbook", "' book"));
            }).ConfigureAwait(true);
        rig.RibbonFile = path;
        rig.ChoiceAnswer = _ => "give";
        return rig;
    }

    /// <summary>ファイル一覧に見えている行数（一覧の見える高さ ÷ 1 行の高さ）。設計案の表と同じ測り方。</summary>
    public static double VisibleFileRows(ContextCollectWindow window)
    {
        var list = window.FindControl<ListBox>("FileListBox")!;
        var viewer = list.GetVisualDescendants().OfType<ScrollViewer>().First();
        var item = list.GetVisualDescendants().OfType<ListBoxItem>().First();
        return viewer.Viewport.Height / item.Bounds.Height;
    }
}
