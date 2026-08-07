using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using FluentAssertions;
using Graft.Infra;
using Graft.Platform;
using Graft.Platform.Null;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests;

/// <summary>
/// エディタ本文の右クリックメニュー（選択範囲から修正依頼プロンプトをコピー、仕様書v2.1）の検証。
/// 既存の<c>EditorTests</c>と同じ作法で、実際にウィンドウへ載せて例外なく描画・操作できることを
/// 確認する。プロンプト本文そのものの組み立ては<c>PromptTemplateStoreSelectionTests</c>
/// （tests/Graft.Tests）で純粋メソッドとして検証済みのため、ここではUI経路の配線のみを見る。
/// </summary>
public class EditorSelectionPromptTests
{
    [AvaloniaFact(DisplayName = "エディタ本文の右クリックメニューが例外なく構築・描画できる")]
    public async Task 右クリックメニューが例外なく構築描画できる()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"graft-selprompt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, "foo.cs");
        await File.WriteAllTextAsync(filePath, "class Foo\n{\n    int X = 1;\n}\n");
        try
        {
            var clipboard = new FakeClipboard();
            var vm = new EditorPaneViewModel(new Settings(), new NullDialogService(), new FakeUiServices(clipboard));
            vm.SetProject(dir);
            var opened = await vm.OpenFileAsync(filePath);
            opened.IsSuccess.Should().BeTrue();

            var pane = new EditorPane { DataContext = vm };
            var window = new Window { Width = 800, Height = 600, Content = pane };
            window.Show();

            var editor = window.GetVisualDescendants().OfType<TextEditor>().Single();
            var contextMenu = editor.ContextMenu;
            contextMenu.Should().NotBeNull("エディタ本文には右クリックメニューが設定されている必要がある");

            var act = () => contextMenu!.Open(editor);
            act.Should().NotThrow("右クリックメニューは例外なく構築・描画できる必要がある");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [AvaloniaFact(DisplayName = "選択が無いときは修正依頼メニュー項目が無効化される")]
    public async Task 選択が無いときは修正依頼項目が無効化される()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"graft-selprompt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, "foo.cs");
        await File.WriteAllTextAsync(filePath, "class Foo\n{\n    int X = 1;\n}\n");
        try
        {
            var (window, editor, _) = await OpenEditorWindowAsync(dir, filePath, new FakeClipboard());
            _ = window;

            editor.Select(0, 0); // 選択なし
            var menuItem = OpenContextMenuAndFindPromptItem(editor);

            menuItem.IsEnabled.Should().BeFalse("選択が無いときは修正依頼項目を無効化する必要がある");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [AvaloniaFact(DisplayName = "選択範囲から修正依頼プロンプトを組み立ててクリップボードへコピーする")]
    public async Task 選択範囲から修正依頼プロンプトをクリップボードへコピーする()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"graft-selprompt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, "foo.cs");
        await File.WriteAllTextAsync(filePath, "class Foo\n{\n    int X = 1;\n}\n");
        try
        {
            var clipboard = new FakeClipboard();
            var (window, editor, _) = await OpenEditorWindowAsync(dir, filePath, clipboard);
            _ = window;

            // 2行目「{」を選択する。
            var line = editor.Document.GetLineByNumber(2);
            editor.Select(line.Offset, line.Length);

            var menuItem = OpenContextMenuAndFindPromptItem(editor);
            menuItem.IsEnabled.Should().BeTrue("選択があるときは修正依頼項目を有効にする必要がある");

            menuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

            clipboard.Text.Should().NotBeNull("クリップボードへプロンプトが書き込まれている必要がある");
            clipboard.Text.Should().Contain("foo.cs（2〜2行目）", "対象ファイルと行範囲を含める必要がある");
            clipboard.Text.Should().Contain("{", "選択したコードを含める必要がある");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static async Task<(Window Window, TextEditor Editor, EditorPaneViewModel ViewModel)> OpenEditorWindowAsync(
        string projectRoot, string filePath, FakeClipboard clipboard)
    {
        var vm = new EditorPaneViewModel(new Settings(), new NullDialogService(), new FakeUiServices(clipboard));
        vm.SetProject(projectRoot);
        (await vm.OpenFileAsync(filePath)).IsSuccess.Should().BeTrue();

        var pane = new EditorPane { DataContext = vm };
        var window = new Window { Width = 800, Height = 600, Content = pane };
        window.Show();

        var editor = window.GetVisualDescendants().OfType<TextEditor>().Single();
        return (window, editor, vm);
    }

    /// <summary>
    /// 実際の右クリックと同じ経路（<see cref="Avalonia.Controls.ContextRequestedEventArgs"/>の
    /// ルーティングイベント）でメニューを開き、「選択範囲の修正依頼プロンプトをコピー」項目を返す。
    /// <see cref="ContextMenu.Open(Control)"/>を直接呼ぶ経路ではOpeningイベントが発火しないため、
    /// 本番と同じ経路を使う必要がある。
    /// </summary>
    private static MenuItem OpenContextMenuAndFindPromptItem(TextEditor editor)
    {
        editor.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent });
        Dispatcher.UIThread.RunJobs();

        var contextMenu = editor.ContextMenu!;
        return contextMenu.GetLogicalDescendants().OfType<MenuItem>()
            .Single(m => Equals(m.Header, "選択範囲の修正依頼プロンプトをコピー"));
    }

    /// <summary>テストから内容を差し替えられるクリップボード。</summary>
    private sealed class FakeClipboard : IClipboardAccess
    {
        public string? Text { get; set; }

        public void SetText(string text) => Text = text;

        public Task<string?> GetTextAsync() => Task.FromResult(Text);
    }

    /// <summary>クリップボードだけ差し替えたUI機能一式。画面情報とタイマーは本物を使う。</summary>
    private sealed class FakeUiServices : IUiServices
    {
        private readonly AvaloniaUiServices _inner = new();

        public FakeUiServices(IClipboardAccess clipboard)
        {
            Clipboard = clipboard;
        }

        public IClipboardAccess Clipboard { get; }

        public IScreenInfo Screens => _inner.Screens;

        public IUiTimer CreateTimer(TimeSpan interval, Action onTick) => _inner.CreateTimer(interval, onTick);
    }
}
