using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Platform;
using Graft.UiTests.TestSupport;

namespace Graft.UiTests.Vba;

/// <summary>
/// 「疑わしい点」の修正: 未保存確認ダイアログの親を、コンテキスト収集の窓が開いているときはその窓にする。
/// 修正前は、確認の親を「アクティブな窓」の推測に任せていたので、モーダルの窓が開いている間に取り込みの確認が
/// メインウィンドウを親に出ると、モーダルの裏に回って操作できなかった（衝突ダイアログだけは窓を親にしていた）。
/// </summary>
public class VbaFixDialogOwnerTests : IDisposable
{
    private readonly ShownWindowTracker _windows = new();

    public void Dispose()
    {
        AvaloniaDialogService.PreferredOwner = null;
        _windows.Dispose();
        GC.SuppressFinalize(this);
    }

    private Window ShownWindow()
    {
        var window = _windows.Track(new Window { Width = 200, Height = 100 });
        window.Show();
        return window;
    }

    [AvaloniaFact(DisplayName = "ダイアログの親: 優先する窓（コンテキスト収集）が表示中なら、メインウィンドウやアクティブな窓より優先してそれを親にする")]
    public void 優先する窓が親になる()
    {
        var main = ShownWindow();
        var context = ShownWindow();

        AvaloniaDialogService.ChooseOwner(context, new[] { main, context }, main).Should().BeSameAs(context);
        AvaloniaDialogService.ChooseOwner(null, Array.Empty<Window>(), main).Should().BeSameAs(main, "優先する窓が無ければ従来どおりメインウィンドウ");
    }

    [AvaloniaFact(DisplayName = "ダイアログの親: 優先する窓が閉じられて表示中でなければ使わない。どの窓も表示中でなければ親は無し")]
    public void 閉じた窓は親にならない()
    {
        var main = ShownWindow();
        var closed = ShownWindow();
        closed.Hide();

        AvaloniaDialogService.ChooseOwner(closed, new[] { main }, main).Should().BeSameAs(main);

        main.Hide();
        AvaloniaDialogService.ChooseOwner(closed, new[] { main, closed }, main).Should().BeNull();
    }
}
