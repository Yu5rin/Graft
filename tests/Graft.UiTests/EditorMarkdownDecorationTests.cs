using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using FluentAssertions;
using Graft.Infra;
using Graft.Platform;
using Graft.Platform.Null;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests;

/// <summary>
/// Markdownプレビュー機能の追加要件（案B: 編集モードでの控えめな装飾）の回帰テスト。
/// <see cref="Graft.Editor.MarkdownInlineColorizer"/>が.mdファイルの編集モードでのみ有効になり、
/// 見出し・強調・インラインコード・リンクへ書体・色を適用すること、行の高さ（フォントサイズ）は
/// 変更しないことを検証する。
/// </summary>
public class EditorMarkdownDecorationTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-md-decoration", Guid.NewGuid().ToString("N"));
    private readonly ShownWindowTracker _windows = new();

    public EditorMarkdownDecorationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _windows.Dispose();
        TempDirectoryCleanup.TryDeleteRecursive(_root);
        GC.SuppressFinalize(this);
    }

    [AvaloniaFact(DisplayName = "編集モードでMarkdownの見出し・強調・インラインコード・リンクが装飾される")]
    public async Task 編集モードでMarkdownが控えめに装飾される()
    {
        var md = "# 見出し行\n\n本文に**強調**と`コード`と[リンク](https://example.com)があります。\n";
        var (window, pane, vm) = await OpenPaneAsync().ConfigureAwait(true);
        await OpenFileAsync(vm, "doc.md", md).ConfigureAwait(true);
        window.CaptureRenderedFrame();

        pane.MarkdownModeToggleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        using var frame = window.CaptureRenderedFrame();
        frame.Should().NotBeNull();

        var visualLines = pane.Editor.TextArea.TextView.VisualLines;
        var headingLine = visualLines.Single(v => v.FirstDocumentLine.LineNumber == 1);
        var blankLine = visualLines.Single(v => v.FirstDocumentLine.LineNumber == 2);
        var bodyLine = visualLines.Single(v => v.FirstDocumentLine.LineNumber == 3);

        HasBold(headingLine).Should().BeTrue("見出し行は太字になるはず");

        headingLine.Height.Should().Be(blankLine.Height,
            "フォントサイズは変更しない方針のため、見出し行の高さは通常行と同じはず");

        HasBold(bodyLine).Should().BeTrue("**強調**の範囲は太字になるはず");

        var codeFontFamily = ResolveFontFamily("CodeFontFamily");
        HasFontFamily(bodyLine, codeFontFamily.Name).Should().BeTrue("`コード`の範囲は等幅フォントになるはず");
        HasBackground(bodyLine).Should().BeTrue("`コード`の範囲は背景色が付くはず");

        var accentColor = ResolveThemeColor("Accent");
        HasForeground(bodyLine, accentColor).Should().BeTrue("[リンク](url)の範囲は色付きになるはず");
    }

    [AvaloniaFact(DisplayName = "非Markdownファイルでは#で始まる行があっても太字にならない")]
    public async Task 非Markdownファイルは装飾されない()
    {
        var text = "# これはMarkdownの見出しではなく単なるコメントです\n本文\n";
        var (window, pane, vm) = await OpenPaneAsync().ConfigureAwait(true);
        await OpenFileAsync(vm, "notes.txt", text).ConfigureAwait(true);
        using var frame = window.CaptureRenderedFrame();
        frame.Should().NotBeNull();

        var visualLines = pane.Editor.TextArea.TextView.VisualLines;
        var firstLine = visualLines.Single(v => v.FirstDocumentLine.LineNumber == 1);

        HasBold(firstLine).Should().BeFalse("非Markdownファイルは案Bの装飾対象外のはず");
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

    private static bool HasBold(AvaloniaEdit.Rendering.VisualLine visual)
        => visual.Elements.Any(e => e.TextRunProperties.Typeface.Weight == FontWeight.Bold);

    private static bool HasFontFamily(AvaloniaEdit.Rendering.VisualLine visual, string fontFamilyName)
        => visual.Elements.Any(e => Equals(e.TextRunProperties.Typeface.FontFamily.Name, fontFamilyName));

    private static bool HasBackground(AvaloniaEdit.Rendering.VisualLine visual)
        => visual.Elements.Any(e => e.TextRunProperties.BackgroundBrush != null);

    private static bool HasForeground(AvaloniaEdit.Rendering.VisualLine visual, Color color)
        => visual.Elements.Any(e => e.TextRunProperties.ForegroundBrush is ISolidColorBrush b && b.Color == color);

    private static FontFamily ResolveFontFamily(string key)
    {
        Avalonia.Application.Current!.TryFindResource(key, null, out var value);
        value.Should().NotBeNull($"テーマリソース '{key}' が解決できる必要がある");
        return (FontFamily)value!;
    }

    private static Color ResolveThemeColor(string brushKey)
    {
        Avalonia.Application.Current!.TryFindResource(brushKey, null, out var value);
        value.Should().NotBeNull($"テーマリソース '{brushKey}' が解決できる必要がある");
        return ((ISolidColorBrush)value!).Color;
    }
}
