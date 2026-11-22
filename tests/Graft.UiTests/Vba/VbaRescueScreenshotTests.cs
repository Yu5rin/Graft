using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FluentAssertions;
using Graft.Core.Vba;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests.Vba;

/// <summary>
/// 反映前の自動控え（段階 Q3）の画面を、<see cref="FakeVbaBridge"/> を使ってヘッドレスで描画し、PNG にして残す
/// （見た目の目視確認用）。<see cref="WorkbookInfoScreenshotTests"/> と同じ作りで、保存先は環境変数
/// <c>GRAFT_Q3_SCREENSHOT_DIR</c>。未設定のときは何も保存せず、描画できることだけを確かめる
/// （通常のテスト実行でファイルを散らかさないため）。
/// </summary>
public class VbaRescueScreenshotTests : IDisposable
{
    private readonly ShownWindowTracker _windows = new();

    public void Dispose() => _windows.Dispose();

    private static void Save(Window window, string fileName)
    {
        window.Measure(new Size(window.Width, window.Height));
        window.Arrange(new Rect(0, 0, window.Width, window.Height));
        for (var i = 0; i < 3; i++) Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame();
        frame.Should().NotBeNull("リソース解決に失敗すると描画そのものができない");

        var dir = Environment.GetEnvironmentVariable("GRAFT_Q3_SCREENSHOT_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        frame!.Save(Path.Combine(dir, fileName));
    }

    private static async Task SettleAsync()
    {
        for (var i = 0; i < 6; i++)
        {
            await Task.Delay(40).ConfigureAwait(true);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private const string ModuleNow =
        "Option Explicit\r\n\r\nSub 集計を更新()\r\n    Dim ws As Worksheet\r\n    Set ws = ThisWorkbook.Worksheets(\"集計\")\r\n" +
        "    ws.Range(\"F2\").Formula = \"=D2*E2\"\r\n    Call 罫線を整える(ws)\r\nEnd Sub\r\n";

    private const string ModuleRescued =
        "Option Explicit\r\n\r\nSub 集計を更新()\r\n    Dim ws As Worksheet\r\n    Set ws = ThisWorkbook.Worksheets(\"集計\")\r\n" +
        "    ws.Range(\"F2\").Formula = \"=D2*E2*1.1\"\r\n    ' 税込みで計算（VBE で直した）\r\nEnd Sub\r\n";

    /// <summary>種別・日時のばらばらな控えを置いて、一覧がそれらしく見える状態にする（本物の掃除・一覧が読む形のファイル名）。</summary>
    private static void SeedRescue(VbaP3Rig r)
    {
        var dir = r.H.AppPaths.GetVbaRescueDirectory(r.Project.Id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Module1-20261121-103015-auto.txt"), ModuleRescued);
        File.WriteAllText(Path.Combine(dir, "Module1-20261121-094502-auto.txt"), ModuleRescued.Replace("1.1", "1.08"));
        File.WriteAllText(Path.Combine(dir, "Module1-20261121-103522-mirror.txt"), ModuleNow.Replace("罫線を整える(ws)", "罫線を整える(ws, True)"));
        File.WriteAllText(Path.Combine(dir, "Module1-20261120-171230.txt"), ModuleRescued.Replace("Option Explicit\r\n\r\n", ""));
        File.WriteAllText(Path.Combine(dir, "Cls1-20261121-103015-auto.txt"), "Option Explicit\r\nPublic Value As Long\r\n");
        File.WriteAllText(MirrorOf(r, "Modules/Module1.bas"), ModuleNow);
    }

    private static string MirrorOf(VbaP3Rig r, string relative) => r.MirrorPath(relative);

    private static async Task<VbaRescueViewModel> OpenAsync(VbaP3Rig r, List<VbaChoiceRequest> captured)
    {
        VbaRescueViewModel? opened = null;
        r.Graft.ProjectPane.ShowRescueAsync = vm =>
        {
            opened = vm;
            return Task.CompletedTask;
        };
        await ShellHarness.ExecuteAsync(r.Graft.ProjectPane.ShowRescueCommand).ConfigureAwait(true);
        opened!.ChooseAsync = request =>
        {
            captured.Add(request);
            return Task.FromResult(request.Default.Id);
        };
        await opened.InitializeAsync().ConfigureAwait(true);
        return opened;
    }

    [AvaloniaFact(DisplayName = "画面の確認: 控えの一覧と差分（モジュールごと、種別・行数・サイズ。右は控えと現在のミラーの差分）")]
    public async Task 控えの一覧と差分()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        SeedRescue(r);
        var vm = await OpenAsync(r, new List<VbaChoiceRequest>()).ConfigureAwait(true);
        vm.SelectedItem = vm.Items.First(i => i.KindText == "反映前の自動控え" && i.ModuleName == "Module1");

        var window = _windows.Track(new VbaRescueWindow(vm));
        window.Show();
        await vm.LoadDiffAsync().ConfigureAwait(true);
        await SettleAsync().ConfigureAwait(true);

        Save(window, "q3-rescue-list-diff.png");
    }

    [AvaloniaFact(DisplayName = "画面の確認: 控えが 1 つも無いとき")]
    public async Task 控えが無いとき()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        var vm = await OpenAsync(r, new List<VbaChoiceRequest>()).ConfigureAwait(true);

        var window = _windows.Track(new VbaRescueWindow(vm));
        window.Show();
        await SettleAsync().ConfigureAwait(true);

        Save(window, "q3-rescue-empty.png");
    }

    [AvaloniaFact(DisplayName = "画面の確認: 「ミラーへ戻す」の確認（実際のダイアログ）と、戻した直後の窓（今すぐ Excel へ反映）")]
    public async Task 戻すの確認と戻した後()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        SeedRescue(r);
        var captured = new List<VbaChoiceRequest>();
        var vm = await OpenAsync(r, captured).ConfigureAwait(true);
        vm.SelectedItem = vm.Items.First(i => i.KindText == "反映前の自動控え" && i.ModuleName == "Module1");
        await vm.LoadDiffAsync().ConfigureAwait(true);

        await vm.RestoreAsync().ConfigureAwait(true); // 既定のボタン（ミラーへ戻す）で進める。

        captured.Should().ContainSingle();
        var dialog = _windows.Track(new VbaChoiceWindow(captured[0]));
        dialog.Show();
        await SettleAsync().ConfigureAwait(true);
        dialog.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        dialog.Width = Math.Max(dialog.MinWidth, dialog.DesiredSize.Width);
        dialog.Height = dialog.DesiredSize.Height;
        Save(dialog, "q3-rescue-restore-confirm.png");

        var window = _windows.Track(new VbaRescueWindow(vm));
        window.Show();
        await SettleAsync().ConfigureAwait(true);
        vm.ShowPushNow.Should().BeTrue();
        Save(window, "q3-rescue-restored.png");
    }

    [AvaloniaFact(DisplayName = "画面の確認: 値が渡る見せ方を選ぶ確認ダイアログの形（動詞のボタン。既定は「やめる」）")]
    public async Task 見本オンの確認ダイアログ()
    {
        using var r = await VbaP3Rig.CreateAsync(arrange: bridge => bridge.Get(VbaP3Rig.Book)!.Info = WorkbookInfoTests.Sample()).ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);

        await context.SetSheetViewModeAsync("Sheet1", Graft.Core.Vba.VbaSheetViewMode.HeaderRows).ConfigureAwait(true); // 既定（やめる）で閉じる。

        var request = r.Choices.Should().ContainSingle().Subject;
        var dialog = _windows.Track(new VbaChoiceWindow(request));
        dialog.Show();
        await SettleAsync().ConfigureAwait(true);
        dialog.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        dialog.Width = Math.Max(dialog.MinWidth, dialog.DesiredSize.Width);
        dialog.Height = dialog.DesiredSize.Height;
        Save(dialog, "q3-sample-confirm-dialog.png");
    }
}
