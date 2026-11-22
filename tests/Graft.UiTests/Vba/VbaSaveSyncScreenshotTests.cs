using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Graft.UiTests.TestSupport;
using Graft.Views;

namespace Graft.UiTests.Vba;

/// <summary>
/// 保存時の自動反映（段階 Q4）の画面を、<see cref="FakeVbaBridge"/> を使ってヘッドレスで描画し、PNG にして残す
/// （右クリックのチェック項目・「衝突 N」のバッジ・通知の見た目の目視確認用）。<see cref="VbaRescueScreenshotTests"/> と同じ作りで、
/// 保存先は環境変数 <c>GRAFT_Q4_SCREENSHOT_DIR</c>。未設定のときは何も保存せず、描画できることだけを確かめる
/// （通常のテスト実行でファイルを散らかさないため）。
/// </summary>
public class VbaSaveSyncScreenshotTests
{
    private static void Save(Window window, string fileName)
    {
        window.Measure(new Size(window.Width, window.Height));
        window.Arrange(new Rect(0, 0, window.Width, window.Height));
        for (var i = 0; i < 3; i++) Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame();
        frame.Should().NotBeNull("リソース解決に失敗すると描画そのものができない");

        var dir = Environment.GetEnvironmentVariable("GRAFT_Q4_SCREENSHOT_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        frame!.Save(Path.Combine(dir, fileName));
    }

    private static async Task SettleUiAsync()
    {
        for (var i = 0; i < 6; i++)
        {
            await Task.Delay(40).ConfigureAwait(true);
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact(DisplayName = "画面の確認: 右クリックのチェック項目・衝突バッジ・自動反映をしなかった通知")]
    public async Task 保存時の自動反映の画面()
    {
        using var r = await VbaP3Rig.CreateAsync(withWindow: true).ConfigureAwait(true);
        await r.UpdateLinkAsync(l => l with { AutoPushOnSave = true }).ConfigureAwait(true);
        var pane = r.Graft.ProjectPane;
        var window = r.H.Window!;

        // 1. 右クリックのチェック項目（オン）。
        await SettleUiAsync().ConfigureAwait(true);
        var view = window.GetVisualDescendants().OfType<ProjectPane>().First();
        var list = view.GetVisualDescendants().OfType<ListBox>().First(l => l.ContextMenu is not null);
        list.ContextMenu!.Open(list);
        Dispatcher.UIThread.RunJobs();
        Save(window, "q4-project-menu-autopush-on-save.png");
        list.ContextMenu.Close();

        // 2. 衝突: Excel 側を VBE で変えたあとに、ミラーを保存する。送らずに、バッジに「衝突 1」が出る。
        var tab = (await r.H.Shell.Editor.OpenFileAsync(r.MirrorPath("Modules/Module1.bas")).ConfigureAwait(true)).Value;
        r.Excel("Module1").Code = "Sub A()\r\n  ' VBE で編集\r\nEnd Sub";
        tab.Session.Document.Insert(0, "' Graft で編集\r\n");
        (await r.H.Shell.Editor.SaveActiveAsync().ConfigureAwait(true)).IsSuccess.Should().BeTrue();
        await r.Graft.WhenVbaSaveSyncIdleAsync().ConfigureAwait(true);
        await pane.RefreshVbaBadgesAsync(checkWorkbookOpen: true).ConfigureAwait(true);
        await SettleUiAsync().ConfigureAwait(true);

        pane.Items.First(i => i.Project.Id == r.Project.Id).VbaStatusText.Should().Contain("衝突 1");
        Save(window, "q4-conflict-badge.png");

        // 3. マクロの実行中: 通知だけ（ダイアログなし）。ステータスバーに出る。
        r.Bridge.Get(VbaP3Rig.Book)!.DesignMode = false;
        var cls = (await r.H.Shell.Editor.OpenFileAsync(r.MirrorPath("Classes/Cls1.cls")).ConfigureAwait(true)).Value;
        cls.Session.Document.Insert(0, "' 編集\r\n");
        (await r.H.Shell.Editor.SaveActiveAsync().ConfigureAwait(true)).IsSuccess.Should().BeTrue();
        await r.Graft.WhenVbaSaveSyncIdleAsync().ConfigureAwait(true);
        await SettleUiAsync().ConfigureAwait(true);

        r.Graft.Notice.IsVisible.Should().BeTrue();
        // ヘッドレスでは、通知の文言が長いものに差し替わった直後の 1 枚目は、前の文言の幅のまま省略記号で描かれることがある
        // （画面の見た目の確認用の撮り方の都合で、アプリの挙動ではない）。いったん消して出し直してから撮る。
        var text = r.Graft.Notice.Text;
        r.Graft.Notice.Dismiss();
        await SettleUiAsync().ConfigureAwait(true);
        r.Graft.Notice.Show(text);
        await SettleUiAsync().ConfigureAwait(true);
        var tb = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == r.Graft.Notice.Text);
        tb.InvalidateMeasure();
        tb.InvalidateVisual();
        await SettleUiAsync().ConfigureAwait(true);
        Save(window, "q4-notice-not-pushed.png");
    }
}
