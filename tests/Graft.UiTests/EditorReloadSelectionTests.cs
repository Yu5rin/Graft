using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using AvaloniaEdit;
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
/// 適用後（外部変更検知）の再読込で、選択範囲が残っていると描画で例外が出る不具合の回帰テスト。
///
/// 【実機ログ（v1.0.23、適用 r33 の直前）】
/// <code>
/// InvalidOperationException: Operation is not valid due to the current state of the object.
///    at AvaloniaEdit.Document.DocumentLine.get_Offset()
///    at AvaloniaEdit.Rendering.BackgroundGeometryBuilder.GetRectsForSegmentImpl(...)
///    at AvaloniaEdit.Rendering.BackgroundGeometryBuilder.AddSegment(TextView textView, ISegment segment)
///    at AvaloniaEdit.Editing.SelectionLayer.Render(DrawingContext drawingContext)
/// </code>
///
/// 【headlessでの再現の可否】 例外そのもの（上と同じスタック）は再現できた。再読込は
/// <c>Document.Text = text</c>で文書全体を1回のReplaceで置き換え、その処理中（<c>Document.Changed</c>の
/// 最中）に限り、<c>TextView.VisualLines</c>が削除済みの<see cref="AvaloniaEdit.Document.DocumentLine"/>を
/// 握った行を1つ以上残す（調査時は1件）。この瞬間に<c>SelectionLayer.Render</c>を呼ぶと、選択範囲が
/// 非空であれば同じスタックで落ちる。一方、<b>通常のレンダリング経路（ウィンドウの描画パス）が
/// この瞬間に割り込む状況は、headlessでは自然には再現できなかった</b>（差し替えが終われば
/// 次の描画までにレイアウトが走り、行が作り直されるため。実機ではファイル監視による再読込が
/// 適用の途中の任意のタイミングで走るので、何らかの経路で描画が割り込んだとみられる）。
/// そのため本テストは、実機と同じ瞬間を<c>Document.Changed</c>の中からの<c>SelectionLayer.Render</c>
/// 呼び出しとして直接作り（FoldingReloadLifetimeTestsが取った手法と同じ）、差し替えの前に
/// 選択範囲を解除する修正の有無で結果が分かれることを確かめる。
/// </summary>
public class EditorReloadSelectionTests : IDisposable
{
    private readonly ShownWindowTracker _windows = new();
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-reload-selection", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _windows.Dispose();
        TempDirectoryCleanup.TryDeleteRecursive(_root);
        GC.SuppressFinalize(this);
    }

    private static string Lines(int count, string prefix)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < count; i++) sb.Append(prefix).Append(i).Append(" some text here\n");
        return sb.ToString();
    }

    private async Task<(EditorPaneViewModel Vm, TextEditor Editor, Avalonia.Controls.Window Window)> OpenPaneAsync()
    {
        Directory.CreateDirectory(_root);
        var vm = new EditorPaneViewModel(new Settings(), new NullDialogService(), new AvaloniaUiServices());
        vm.SetProject(_root);
        var pane = new EditorPane { DataContext = vm };
        var window = _windows.Track(new Avalonia.Controls.Window { Width = 800, Height = 400, Content = pane });
        window.Show();
        await Task.CompletedTask;
        return (vm, pane.GetVisualDescendants().OfType<TextEditor>().Single(), window);
    }

    private async Task<(EditorTabViewModel Tab, string Path)> OpenFileAsync(EditorPaneViewModel vm, string name, string content)
    {
        var path = Path.Combine(_root, name);
        await File.WriteAllTextAsync(path, content).ConfigureAwait(true);
        var opened = await vm.OpenFileAsync(path).ConfigureAwait(true);
        opened.IsSuccess.Should().BeTrue();
        return (opened.Value, path);
    }

    /// <summary>選択レイヤーを、いまの状態のまま1回描画する（描画パスの割り込みを直接作る）。</summary>
    private static void RenderSelectionLayer(TextEditor editor)
    {
        var layer = editor.TextArea.TextView.Layers.Single(l => l.GetType().Name == "SelectionLayer");
        using var bitmap = new RenderTargetBitmap(new PixelSize(800, 400));
        using var context = bitmap.CreateDrawingContext();
        layer.Render(context);
    }

    [AvaloniaFact(DisplayName = "回帰: 選択範囲が残ったままの再読込でも、差し替えの最中に描画が割り込んで例外が出ず、再読込後の選択は空になる")]
    public async Task 選択が残ったままの再読込で描画が割り込んでも例外が出ない()
    {
        var (vm, editor, window) = await OpenPaneAsync();
        var (tab, path) = await OpenFileAsync(vm, "a.txt", Lines(200, "old"));
        using (window.CaptureRenderedFrame()) { }

        // 全選択（差し替え後も選択が非空のまま残る形。ここでは差し替えを跨いで選択が生き残る）。
        editor.SelectAll();
        using (window.CaptureRenderedFrame()) { }
        editor.TextArea.Selection.IsEmpty.Should().BeFalse("前提: 選択がある");

        // 実機の「差し替えの最中に描画が割り込む」瞬間を、Changedの中からの描画として作る。
        Exception? interrupted = null;
        tab.Session.Document.Changed += (_, _) =>
        {
            try { RenderSelectionLayer(editor); }
            catch (Exception ex) { interrupted = ex; }
        };

        await File.WriteAllTextAsync(path, Lines(3, "new")).ConfigureAwait(true);
        await vm.NotifyExternalChangeAsync(path).ConfigureAwait(true);

        interrupted.Should().BeNull(
            "差し替えの前に選択を解除してあれば、SelectionLayerは削除済みの行に触れない（修正前は" +
            "DocumentLine.get_Offsetで InvalidOperationException になった）");
        tab.Session.Document.Text.Should().Be(Lines(3, "new"), "再読込そのものは行われている");
        editor.TextArea.Selection.IsEmpty.Should().BeTrue("再読込後に選択が残らないこと");

        var draw = () => window.CaptureRenderedFrame()?.Dispose();
        draw.Should().NotThrow("再読込の後の通常の描画も例外にならない");
    }

    [AvaloniaFact(DisplayName = "裏のタブが再読込されても、いま表示しているタブの選択範囲は解除されない")]
    public async Task 裏のタブの再読込は表示中の選択に影響しない()
    {
        var (vm, editor, window) = await OpenPaneAsync();
        var (_, backgroundPath) = await OpenFileAsync(vm, "back.txt", Lines(50, "back"));
        var (front, _) = await OpenFileAsync(vm, "front.txt", Lines(50, "front"));
        vm.ActiveTab.Should().BeSameAs(front, "前提: 後から開いたfront.txtが表示中");
        using (window.CaptureRenderedFrame()) { }

        editor.SelectAll();
        editor.TextArea.Selection.IsEmpty.Should().BeFalse();

        await File.WriteAllTextAsync(backgroundPath, Lines(3, "changed")).ConfigureAwait(true);
        await vm.NotifyExternalChangeAsync(backgroundPath).ConfigureAwait(true);

        editor.TextArea.Selection.IsEmpty.Should().BeFalse("表示していない文書の再読込で、いまの選択を消してはならない");
    }

    [AvaloniaFact(DisplayName = "DocumentSessionは、全体を差し替える直前（まだ旧内容のまま）にContentReplacingを発火し、内容が同じなら発火しない")]
    public async Task ContentReplacingは差し替えの直前に発火する()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "c.txt");
        await File.WriteAllTextAsync(path, "one\ntwo\n");
        var opened = await DocumentSession.OpenAsync(path, _root);
        using var session = opened.Value;

        var seenTexts = new List<string>();
        session.ContentReplacing += (_, _) => seenTexts.Add(session.Document.Text);

        await session.ReloadAsync();
        seenTexts.Should().BeEmpty("内容が同じなら文書へ触れないので発火しない");

        await File.WriteAllTextAsync(path, "three\n");
        await session.ReloadAsync();

        seenTexts.Should().Equal(new[] { "one\ntwo\n" }, "差し替えの直前＝旧内容のまま発火する");
        session.Document.Text.Should().Be("three\n");
    }
}
