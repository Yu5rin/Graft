using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Editor;
using Graft.Infra;
using Graft.Platform;
using Graft.Platform.Null;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests;

/// <summary>
/// カラープレビュー機能（検討書「コード中のカラープレビュー」）の回帰テスト。
/// <see cref="ColorPreviewElementGenerator"/>が<c>#RRGGBB</c>・<c>rgb()</c>・<c>hsl()</c>それぞれで
/// スウォッチ（<see cref="ColorSwatchElement"/>）を可視行へ差し込むこと、設定でオフにできることを
/// 検証する（<c>EditorMarkdownDecorationTests</c>と同じ構成）。
/// </summary>
public class EditorColorPreviewTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-color-preview", Guid.NewGuid().ToString("N"));
    private readonly ShownWindowTracker _windows = new();

    public EditorColorPreviewTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _windows.Dispose();
        TempDirectoryCleanup.TryDeleteRecursive(_root);
        GC.SuppressFinalize(this);
    }

    [AvaloniaFact(DisplayName = "#RRGGBBにスウォッチが表示される")]
    public async Task 十六進のスウォッチが表示される()
    {
        var (window, pane, vm) = await OpenPaneAsync().ConfigureAwait(true);
        await OpenFileAsync(vm, "a.css", "color: #ff6600;\n").ConfigureAwait(true);

        var swatches = FindSwatches(window, pane);
        swatches.Should().ContainSingle();
        swatches[0].Color.Should().Be(new RgbaColor(255, 0xff, 0x66, 0x00));
    }

    [AvaloniaFact(DisplayName = "rgb()にスウォッチが表示される")]
    public async Task rgbのスウォッチが表示される()
    {
        var (window, pane, vm) = await OpenPaneAsync().ConfigureAwait(true);
        await OpenFileAsync(vm, "a.js", "const c = 'rgb(255, 102, 0)';\n").ConfigureAwait(true);

        var swatches = FindSwatches(window, pane);
        swatches.Should().ContainSingle();
        swatches[0].Color.Should().Be(new RgbaColor(255, 255, 102, 0));
    }

    [AvaloniaFact(DisplayName = "hsl()にスウォッチが表示される")]
    public async Task hslのスウォッチが表示される()
    {
        var (window, pane, vm) = await OpenPaneAsync().ConfigureAwait(true);
        await OpenFileAsync(vm, "a.css", "color: hsl(24, 100%, 50%);\n").ConfigureAwait(true);

        var swatches = FindSwatches(window, pane);
        swatches.Should().ContainSingle();
    }

    [AvaloniaFact(DisplayName = "色でない文字列にはスウォッチが出ない")]
    public async Task 色でない文字列にはスウォッチが出ない()
    {
        var (window, pane, vm) = await OpenPaneAsync().ConfigureAwait(true);
        await OpenFileAsync(vm, "a.txt", "abc#ff0000 と ただの文章\n").ConfigureAwait(true);

        FindSwatches(window, pane).Should().BeEmpty("直前が英数字の16進は対象外、ただの文章は色を含まない");
    }

    [AvaloniaFact(DisplayName = "editor.colorPreviewInCode=falseならスウォッチが出ない")]
    public async Task 設定でオフにできる()
    {
        var settings = new Settings { Editor = new EditorSettings { ColorPreviewInCode = false } };
        var vm = new EditorPaneViewModel(settings, new NullDialogService(), new AvaloniaUiServices());
        vm.SetProject(_root);
        var pane = new EditorPane { DataContext = vm };
        var window = _windows.Track(new Window { Width = 1000, Height = 800, Content = pane });
        window.Show();

        await OpenFileAsync(vm, "a.css", "color: #ff6600;\n").ConfigureAwait(true);

        FindSwatches(window, pane).Should().BeEmpty("colorPreviewInCode=falseのときは一切表示しないはず");
    }

    // ------------------------------------------------------------------
    // ヘルパ
    // ------------------------------------------------------------------

    private async Task<(Window Window, EditorPane Pane, EditorPaneViewModel Vm)> OpenPaneAsync()
    {
        var vm = new EditorPaneViewModel(new Settings(), new NullDialogService(), new AvaloniaUiServices());
        vm.SetProject(_root);
        var pane = new EditorPane { DataContext = vm };
        var window = _windows.Track(new Window { Width = 1000, Height = 800, Content = pane });
        window.Show();
        window.CaptureRenderedFrame().Should().NotBeNull();
        await Task.CompletedTask;
        return (window, pane, vm);
    }

    private async Task OpenFileAsync(EditorPaneViewModel vm, string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        await File.WriteAllTextAsync(path, content).ConfigureAwait(true);
        var result = await vm.OpenFileAsync(path).ConfigureAwait(true);
        result.IsSuccess.Should().BeTrue();
    }

    private static List<ColorSwatchElement> FindSwatches(Window window, EditorPane pane)
    {
        using var frame = window.CaptureRenderedFrame();
        frame.Should().NotBeNull();
        return pane.Editor.TextArea.TextView.VisualLines
            .SelectMany(v => v.Elements)
            .OfType<ColorSwatchElement>()
            .ToList();
    }
}
