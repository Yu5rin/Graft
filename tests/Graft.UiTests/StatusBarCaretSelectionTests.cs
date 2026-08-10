using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using FluentAssertions;
using Graft.Infra;
using Graft.Platform;
using Graft.Platform.Null;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests;

/// <summary>
/// 細かいユーザビリティ改善1: ステータスバーのカーソル位置「行:列」表示（例: "12:34"）と、
/// 選択中の文字数表示（例: "(選択 128文字)"）。実際のAvalonEdit（<see cref="TextEditor"/>）を
/// 操作して、View（EditorPane.axaml.cs）からViewModel（EditorPaneViewModel）への配線が
/// 実際に効くことを確認する（EditorSelectionPromptTestsと同じ作法）。
/// </summary>
public class StatusBarCaretSelectionTests
{
    [AvaloniaFact(DisplayName = "カーソル移動で「行:列」形式のCaretTextが更新される")]
    public async Task カーソル移動でCaretTextが更新される()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"graft-caret-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, "foo.txt");
        await File.WriteAllTextAsync(filePath, "abcde\nfghij\n");
        try
        {
            var vm = new EditorPaneViewModel(new Settings(), new NullDialogService(), new FakeUiServices());
            vm.SetProject(dir);
            (await vm.OpenFileAsync(filePath)).IsSuccess.Should().BeTrue();

            var pane = new EditorPane { DataContext = vm };
            var window = new Window { Width = 800, Height = 600, Content = pane };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var editor = window.GetVisualDescendants().OfType<TextEditor>().Single();

            // 2行目の4列目（"fghij"の4文字目の直前）へカーソルを置く。
            editor.TextArea.Caret.Line = 2;
            editor.TextArea.Caret.Column = 4;
            Dispatcher.UIThread.RunJobs();

            vm.CaretText.Should().Be("2:4", "「12:34」のような「行:列」形式で表示する必要がある");
        }
        finally
        {
            await TempDirectoryCleanup.TryDeleteRecursiveAsync(dir).ConfigureAwait(true);
        }
    }

    [AvaloniaFact(DisplayName = "選択すると「(選択 N文字)」が表示され、選択解除で消える")]
    public async Task 選択で文字数表示が出て解除で消える()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"graft-select-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, "foo.txt");
        await File.WriteAllTextAsync(filePath, "0123456789");
        try
        {
            var vm = new EditorPaneViewModel(new Settings(), new NullDialogService(), new FakeUiServices());
            vm.SetProject(dir);
            (await vm.OpenFileAsync(filePath)).IsSuccess.Should().BeTrue();

            var pane = new EditorPane { DataContext = vm };
            var window = new Window { Width = 800, Height = 600, Content = pane };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var editor = window.GetVisualDescendants().OfType<TextEditor>().Single();

            vm.SelectionText.Should().BeEmpty("選択が無い間は表示しない");

            editor.Select(0, 5); // "01234" を選択
            Dispatcher.UIThread.RunJobs();
            vm.SelectionText.Should().Be("(選択 5文字)");

            editor.Select(0, 0); // 選択解除
            Dispatcher.UIThread.RunJobs();
            vm.SelectionText.Should().BeEmpty("選択を解除したら消える必要がある");
        }
        finally
        {
            await TempDirectoryCleanup.TryDeleteRecursiveAsync(dir).ConfigureAwait(true);
        }
    }

    [AvaloniaFact(DisplayName = "差分タブ（カーソルの概念が無いタブ）ではCaretText・SelectionTextが空文字になる")]
    public void 差分タブでは空文字になる()
    {
        var vm = new EditorPaneViewModel(new Settings(), new NullDialogService(), new FakeUiServices());
        var diff = new DiffViewModel(new Settings(), new FakeUiServices());
        vm.ShowDiffTab(diff);

        vm.CaretText.Should().BeEmpty();
        vm.SelectionText.Should().BeEmpty();
    }

    private sealed class FakeUiServices : IUiServices
    {
        private readonly AvaloniaUiServices _inner = new();

        public IClipboardAccess Clipboard => _inner.Clipboard;

        public IScreenInfo Screens => _inner.Screens;

        public IUiTimer CreateTimer(TimeSpan interval, Action onTick) => _inner.CreateTimer(interval, onTick);
    }
}
