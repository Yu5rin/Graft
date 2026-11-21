using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Infra;
using Graft.Platform;
using Graft.Platform.Null;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests.Vba;

/// <summary>
/// Excel VBA 連携 段階 P4 の画面を、<see cref="FakeVbaBridge"/> を使ってヘッドレスで描画し、PNG にして残す
/// （バッジ・右クリックのチェック項目・設定パネル・E804 のボタンの見た目の目視確認用）。
/// <para>
/// 保存先は環境変数 <c>GRAFT_P4_SCREENSHOT_DIR</c>。未設定のときは何も保存せず、描画できることだけを確かめる
/// （通常のテスト実行でファイルを散らかさないため）。
/// </para>
/// </summary>
public class VbaP4ScreenshotTests : IDisposable
{
    private const string Book2 = @"C:\Work\在庫管理.xlsm";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "graft-vba-p4-shot", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 後片付けの失敗は検証結果に影響しない。
        }

        GC.SuppressFinalize(this);
    }

    private static void Save(Window window, string fileName)
    {
        window.Measure(new Size(window.Width, window.Height));
        window.Arrange(new Rect(0, 0, window.Width, window.Height));
        Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame();
        frame.Should().NotBeNull("リソース解決に失敗すると描画そのものができない");

        var dir = Environment.GetEnvironmentVariable("GRAFT_P4_SCREENSHOT_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        frame!.Save(Path.Combine(dir, fileName));
    }

    [AvaloniaFact(DisplayName = "画面の確認: プロジェクトペインのバッジ（開いている・閉じている・未反映と削除保留）と、右クリックのチェック項目")]
    public async Task プロジェクトペインのバッジとメニュー()
    {
        using var r = await VbaP3Rig.CreateAsync(plainProjectFirst: true, withWindow: true).ConfigureAwait(true);
        var pane = r.Graft.ProjectPane;

        // 2つ目の VBA プロジェクト（ブックは開いていない）を追加してから、ブックを閉じる。
        r.Bridge.AddWorkbook(Book2, FakeVbaBridge.Std("Module1", "Sub B()\r\nEnd Sub"));
        pane.ShowWorkbookPickerAsync = async picker =>
        {
            await picker.LoadAsync().ConfigureAwait(true);
            picker.SelectedWorkbook = picker.Workbooks.First(w => w.FullName == Book2);
            return await picker.ConfirmAsync().ConfigureAwait(true);
        };
        await ShellHarness.ExecuteAsync(pane.AddFromWorkbookCommand).ConfigureAwait(true);
        r.Bridge.CloseWorkbook(Book2);

        // 1つ目の VBA プロジェクトに、未反映 2 件と削除保留 1 件を作る。
        var first = pane.Items.First(i => i.Project.Id == r.Project.Id);
        File.WriteAllText(r.MirrorPath("Modules/Module1.bas"), "Sub A()\r\n  ' edited\r\nEnd Sub\r\n");
        File.WriteAllText(r.MirrorPath("Classes/Cls1.cls"), "Option Explicit\r\nPublic Value As Double\r\n");
        File.Delete(r.MirrorPath("Documents/Sheet1.cls"));
        await pane.SelectAsync(first).ConfigureAwait(true);
        await pane.RefreshVbaBadgesAsync(checkWorkbookOpen: true).ConfigureAwait(true);

        first = pane.Items.First(i => i.Project.Id == r.Project.Id);
        first.VbaStatusText.Should().Be("未反映 2・削除保留 1");
        pane.Items.Single(i => i.Project.Vba is not null && i.Project.Id != r.Project.Id).IsVbaBadgeGray.Should().BeTrue();

        var window = r.H.Window!;
        // 束縛の更新と描画は、ディスパッチャーを何度か回さないと画面に追い付かない（バッジは非同期に更新される）。
        for (var i = 0; i < 5; i++)
        {
            await Task.Delay(40).ConfigureAwait(true);
            Dispatcher.UIThread.RunJobs();
        }

        Save(window, "p4-project-badges.png");

        // 右クリックのメニュー（開いて描画する）。
        var view = window.GetVisualDescendants().OfType<ProjectPane>().First();
        var list = view.GetVisualDescendants().OfType<ListBox>().First(l => l.ContextMenu is not null);
        list.ContextMenu!.Open(list);
        Dispatcher.UIThread.RunJobs();
        Save(window, "p4-project-menu-open.png");
        list.ContextMenu.Close();
    }

    [AvaloniaFact(DisplayName = "画面の確認: 設定の「VBA 連携」タブ（使える環境と、使えない環境）")]
    public async Task 設定パネル()
    {
        var appPaths = new AppPaths(Path.Combine(_root, "app"));
        appPaths.EnsureCoreDirectoriesExist();

        foreach (var (supported, name) in new[] { (true, "p4-settings-vba.png"), (false, "p4-settings-vba-unsupported.png") })
        {
            var vm = new SettingsViewModel(appPaths, new NullDialogService(), new AvaloniaUiServices()) { VbaSupportProbe = () => supported };
            await vm.InitializeAsync().ConfigureAwait(true);
            var window = new SettingsWindow(vm) { Width = 1000, Height = 640 };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                var tabs = window.GetVisualDescendants().OfType<TabControl>().First(t => t.Name == "CategoryTabControl");
                tabs.SelectedItem = tabs.Items.OfType<TabItem>().First(t => Equals(t.Header, "VBA 連携"));
                Dispatcher.UIThread.RunJobs();
                Save(window, name);
            }
            finally
            {
                window.Close();
            }
        }
    }

    [AvaloniaFact(DisplayName = "画面の確認: E804（ブックが開いていない）の選択ダイアログの「Excel で開く」ボタン")]
    public void 接続不可の選択ダイアログ()
    {
        var request = new VbaChoiceRequest(
            "Excel と同期できませんでした",
            "紐付けたブックが開いていません（E804）: C:\\Work\\計画.xlsm\n対処: ブックを Excel で開いてから、もう一度お試しください。\n\n"
            + "「ファイルだけ適用する」を選ぶと、Graft のファイルにだけ適用します。Excel には未反映のまま残り、"
            + "あとからプロジェクトの右クリック「Excel へ反映」で送れます。",
            new[] { new VbaChoiceOption("fileOnly", "ファイルだけ適用する"), new VbaChoiceOption("abort", "中止", IsDefault: true) },
            VbaP3Rig.Book);
        var window = new VbaChoiceWindow(request, _ => { });
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            window.Width = Math.Max(window.Bounds.Width, 520);
            window.Height = Math.Max(window.Bounds.Height, 260);
            Save(window, "p4-choice-e804.png");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "画面の確認: コンテキスト収集で Excel の最新を取り込めなかったときの警告")]
    public async Task コンテキスト収集の警告()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        r.Bridge.CloseWorkbook(VbaP3Rig.Book);

        var window = new ContextCollectWindow(r.Graft.ContextCollect!) { Width = 900, Height = 640 };
        window.Show();
        try
        {
            await ShellHarness.WaitUntilAsync(() => r.Graft.ContextCollect!.StatusMessage is not null && !r.Graft.ContextCollect.IsScanning)
                .ConfigureAwait(true);
            Dispatcher.UIThread.RunJobs();
            r.Graft.ContextCollect!.StatusMessage.Should().Contain("Excel の最新を取り込めていません");
            Save(window, "p4-context-collect-warning.png");
        }
        finally
        {
            window.Close();
        }
    }
}
