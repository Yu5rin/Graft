using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using Graft.Infra;
using Graft.Platform;
using Graft.Platform.Null;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests;

/// <summary>
/// 不具合3（実機検証）: タブの閉じるボタン（<c>Button.tabClose</c>）が既定で
/// 一切見えず、閉じる手段が画面上に無いという指摘への対応。
///
/// 調査の過程で、指摘された「選択中でも常時表示にすべき」以前に、既存の
/// 「ホバー中だけ表示」というコメント・意図された挙動自体がそもそも機能していなかった
/// （副次的に見つかった不具合）ことも判明した。Button要素へ直接 <c>IsVisible="False"</c> を
/// 指定していたため、Avaloniaの値の優先順位（Local値 &gt; Style）により
/// <c>:pointerover</c>/<c>:selected</c> のStyleセレクタが一致してもIsVisible="True"へ
/// 上書きできなかった（下の「非選択タブもホバーすると表示される」テストで、修正前は
/// ホバーさせても非表示のままになることを確認済み）。既定値もStyleセレクタ側
/// （<c>Button.tabClose</c>）に揃えることで両方を直した。
/// </summary>
public class TabCloseButtonVisibilityTests : IDisposable
{
    // BuildTwoTabWindowAsyncが載せるEditorPaneはAvaloniaEditのTextView(タブごとの
    // TextEditor)を内包する。以前はここで開いたWindowをClose()もShownWindowTrackerへの
    // 登録もしないまま終わっていた（閉じ忘れの実例）。他のシナリオテストと同じ後始末に揃える。
    private readonly ShownWindowTracker _windows = new();

    public void Dispose()
    {
        _windows.Dispose();
        GC.SuppressFinalize(this);
    }

    [AvaloniaFact(DisplayName = "不具合3: 選択中のタブは、ホバーしていなくても閉じるボタンが常時表示される")]
    public async Task 選択中タブの閉じるボタンは常時表示される()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"graft-tabclose-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var (window, selectedButton, unselectedButton) = await BuildTwoTabWindowAsync(dir).ConfigureAwait(true);
            _ = window;

            selectedButton.IsVisible.Should().BeTrue(
                "選択中のタブでは、ホバーしていなくても閉じるボタンが常に見えている必要がある（初見でも閉じ方が分かるように）");
            unselectedButton.IsVisible.Should().BeFalse(
                "非選択タブは従来どおりホバー時のみの表示（見た目の変化を最小限にする）");
        }
        finally
        {
            await TestSupport.TempDirectoryCleanup.TryDeleteRecursiveAsync(dir).ConfigureAwait(true);
        }
    }

    [AvaloniaFact(DisplayName = "不具合3（副次的に発覚した不具合）: 非選択タブもホバーすると閉じるボタンが表示される")]
    public async Task 非選択タブもホバーすると表示される()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"graft-tabclose-hover-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var (window, _, unselectedButton) = await BuildTwoTabWindowAsync(dir).ConfigureAwait(true);

            var unselectedItem = unselectedButton.GetVisualAncestors().OfType<ListBoxItem>().First();
            var pos = unselectedItem.TranslatePoint(new Point(5, 5), window)!.Value;
            window.MouseMove(pos);
            window.CaptureRenderedFrame().Should().NotBeNull();

            unselectedButton.IsVisible.Should().BeTrue(
                "既存のホバー表示（v2.0のWPF版から踏襲した挙動）自体が壊れていないことも確認する");
        }
        finally
        {
            await TestSupport.TempDirectoryCleanup.TryDeleteRecursiveAsync(dir).ConfigureAwait(true);
        }
    }

    private async Task<(Window Window, Button Selected, Button Unselected)> BuildTwoTabWindowAsync(string dir)
    {
        var aPath = Path.Combine(dir, "a.txt");
        var bPath = Path.Combine(dir, "b.txt");
        await File.WriteAllTextAsync(aPath, "a").ConfigureAwait(true);
        await File.WriteAllTextAsync(bPath, "b").ConfigureAwait(true);

        var vm = new EditorPaneViewModel(new Settings(), new NullDialogService(), new AvaloniaUiServices());
        vm.SetProject(dir);
        (await vm.OpenFileAsync(aPath).ConfigureAwait(true)).IsSuccess.Should().BeTrue();
        (await vm.OpenFileAsync(bPath).ConfigureAwait(true)).IsSuccess.Should().BeTrue();
        // 直後に開いたb.txtが選択中のタブになる（EditorPaneViewModel.OpenFileAsync）。
        vm.ActiveTab!.Session.FullPath.Should().Be(bPath);

        var pane = new EditorPane { DataContext = vm };
        var window = _windows.Track(new Window { Width = 800, Height = 600, Content = pane });
        window.Show();
        window.CaptureRenderedFrame().Should().NotBeNull();

        var items = window.GetVisualDescendants().OfType<ListBoxItem>()
            .Where(i => i.DataContext is EditorTabViewModel)
            .ToList();
        items.Should().HaveCount(2);

        var selectedItem = items.Single(i => ((EditorTabViewModel)i.DataContext!).Session.FullPath == bPath);
        var unselectedItem = items.Single(i => ((EditorTabViewModel)i.DataContext!).Session.FullPath == aPath);
        selectedItem.IsSelected.Should().BeTrue("前提: ListBoxItem自体が選択状態になっているはず");

        var selectedButton = selectedItem.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("tabClose"));
        var unselectedButton = unselectedItem.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("tabClose"));

        return (window, selectedButton, unselectedButton);
    }
}
