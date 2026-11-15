using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;
using FluentAssertions;
using Graft.Infra;
using Graft.Platform;
using Graft.Platform.Null;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests;

/// <summary>
/// 適用後の再読込で、自前の背景描画（括弧の強調・検索結果の強調）が例外を出す不具合の回帰テスト。
///
/// 【実機ログ（v1.0.28、1日4回）】
/// <code>
/// InvalidOperationException: Operation is not valid due to the current state of the object.
///    at AvaloniaEdit.Document.DocumentLine.get_Offset()
///    at AvaloniaEdit.Rendering.BackgroundGeometryBuilder.GetRectsForSegmentImpl(...)
///    at AvaloniaEdit.Rendering.BackgroundGeometryBuilder.AddSegment(TextView textView, ISegment segment)
///    at Graft.Editor.BracketSupport.DrawBracketBox(...)
///    at Graft.Editor.BracketSupport.Draw(TextView textView, DrawingContext drawingContext)
///    at AvaloniaEdit.Rendering.TextView.RenderBackground(DrawingContext drawingContext, KnownLayer layer)
///    at AvaloniaEdit.Editing.SelectionLayer.Render(DrawingContext drawingContext)
/// </code>
/// 1.0.24で直した選択範囲の描画（<see cref="EditorReloadSelectionTests"/>）と同じ原因で、
/// 選択の解除では防げない。<c>TextViewRenderGuard</c>で「描く前に確かめる」形に一本化した。
/// 再現手法も同じで、<c>Document.Changed</c>の中から選択レイヤーを描画して、実機の
/// 「差し替えの最中に描画が割り込む」瞬間を作る。
/// </summary>
public class EditorReloadBackgroundRendererTests : IDisposable
{
    private readonly ShownWindowTracker _windows = new();
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-reload-bg-renderer", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _windows.Dispose();
        TempDirectoryCleanup.TryDeleteRecursive(_root);
        GC.SuppressFinalize(this);
    }

    /// <summary>1行目の先頭が「(a)」で、全体が<paramref name="count"/>行の文書。</summary>
    private static string BracketLines(int count, string prefix)
    {
        var sb = new StringBuilder();
        sb.Append("(a)\n");
        for (var i = 1; i < count; i++) sb.Append(prefix).Append(i).Append(" some text here\n");
        return sb.ToString();
    }

    private async Task<(EditorPaneViewModel Vm, EditorPane Pane, TextEditor Editor, Avalonia.Controls.Window Window)> OpenPaneAsync()
    {
        Directory.CreateDirectory(_root);
        var vm = new EditorPaneViewModel(new Settings(), new NullDialogService(), new AvaloniaUiServices());
        vm.SetProject(_root);
        var pane = new EditorPane { DataContext = vm };
        var window = _windows.Track(new Avalonia.Controls.Window { Width = 800, Height = 400, Content = pane });
        window.Show();
        await Task.CompletedTask;
        return (vm, pane, pane.GetVisualDescendants().OfType<TextEditor>().Single(), window);
    }

    private async Task<(EditorTabViewModel Tab, string Path)> OpenFileAsync(EditorPaneViewModel vm, string name, string content)
    {
        var path = Path.Combine(_root, name);
        await File.WriteAllTextAsync(path, content).ConfigureAwait(true);
        var opened = await vm.OpenFileAsync(path).ConfigureAwait(true);
        opened.IsSuccess.Should().BeTrue();
        return (opened.Value, path);
    }

    private static void RenderSelectionLayer(TextEditor editor)
    {
        var layer = editor.TextArea.TextView.Layers.Single(l => l.GetType().Name == "SelectionLayer");
        using var bitmap = new RenderTargetBitmap(new PixelSize(800, 400));
        using var context = bitmap.CreateDrawingContext();
        layer.Render(context);
    }

    /// <summary>1行目の左端（60px幅）に、指定色にほぼ一致するピクセルがいくつあるか。</summary>
    private static int CountColorPixelsInFirstLine(WriteableBitmap frame, Color target, TextEditor editor)
    {
        var textView = editor.TextArea.TextView;
        var top = (int)textView.GetVisualPosition(new TextViewPosition(1, 1), VisualYPosition.LineTop).Y;
        var bottom = (int)textView.GetVisualPosition(new TextViewPosition(1, 1), VisualYPosition.LineBottom).Y;
        var origin = textView.TranslatePoint(new Point(0, 0), (Visual)editor.GetVisualRoot()!) ?? default;
        using var fb = frame.Lock();
        var count = 0;
        var yStart = (int)origin.Y + top;
        var yEnd = (int)origin.Y + bottom;
        var xStart = (int)origin.X;
        for (var y = Math.Max(0, yStart); y < Math.Min(fb.Size.Height, yEnd); y++)
        {
            for (var x = Math.Max(0, xStart); x < Math.Min(fb.Size.Width, xStart + 60); x++)
            {
                var px = Marshal.ReadInt32(fb.Address, y * fb.RowBytes + x * 4);
                // このヘッドレス描画先のメモリ上の並びはR,G,B,A（実測: 背景の暗色が0xFF2B2714で、
                // 下位バイトがR）。枠線は1.3pxのため、辺の画素はアクセント色と背景の混色になり
                // 完全一致しない（実測: 目標#4C8DFFに対し最も近い画素が#41 7A D8）。許容幅を広めに取る。
                var r = px & 0xFF; var g = (px >> 8) & 0xFF; var b = (px >> 16) & 0xFF;
                if (Math.Abs(r - target.R) < 48 && Math.Abs(g - target.G) < 48 && Math.Abs(b - target.B) < 48) count++;
            }
        }
        return count;
    }

    [AvaloniaFact(DisplayName = "回帰: 括弧の強調が出ている状態で行数の少ない内容に再読込しても、差し替えの最中の描画で例外が出ない")]
    public async Task 括弧の強調が出たままの再読込で描画が割り込んでも例外が出ない()
    {
        var (vm, _, editor, window) = await OpenPaneAsync();
        var (tab, path) = await OpenFileAsync(vm, "b.js", BracketLines(200, "old"));
        using (window.CaptureRenderedFrame()) { }

        // 「(」の直後にキャレットを置く＝対応する括弧の強調が出る。
        editor.CaretOffset = 1;
        using (window.CaptureRenderedFrame()) { }

        Exception? interrupted = null;
        tab.Session.Document.Changed += (_, _) =>
        {
            try { RenderSelectionLayer(editor); }
            catch (Exception ex) { interrupted = ex; }
        };

        // 差し替えの最中、キャレットは新しい文書の末尾（TextLength）に寄る（実測: 44/44）。
        // 末尾が「)」なら、キャレットの直前の括弧とその対応が見つかり、強調の描画に進む。
        var replacement = BracketLines(3, "new") + "(b)";
        await File.WriteAllTextAsync(path, replacement).ConfigureAwait(true);
        await vm.NotifyExternalChangeAsync(path).ConfigureAwait(true);

        interrupted.Should().BeNull(
            "削除済みの行を握る可視行が残っていても、背景レンダラーは描く前に確かめて戻る" +
            "（修正前は BracketSupport.DrawBracketBox → DocumentLine.get_Offset で InvalidOperationException）");
        tab.Session.Document.Text.Should().Be(replacement);
        var draw = () => window.CaptureRenderedFrame()?.Dispose();
        draw.Should().NotThrow("再読込の後の通常の描画も例外にならない");
    }

    [AvaloniaFact(DisplayName = "再読込のあと、括弧の強調が新しい内容の正しい位置（1行目の括弧）に描かれる")]
    public async Task 再読込のあと括弧の強調が正しい位置に描かれる()
    {
        var (vm, _, editor, window) = await OpenPaneAsync();
        var (tab, path) = await OpenFileAsync(vm, "c.js", BracketLines(200, "old"));
        using (window.CaptureRenderedFrame()) { }

        Application.Current!.TryFindResource("AccentColor", null, out var accent).Should().BeTrue();
        var accentColor = (Color)accent!;

        // 【基準値】枠が無い状態（キャレットが括弧の隣にない）で、1行目の左端にアクセント色に
        // 「ほぼ一致」する画素がいくつあるか。判定の許容幅（±48）が広いため、テーマによっては
        // 文字（「(a)」）のアンチエイリアス画素がアクセント色に近くなり、枠が無くても0にならない。
        // 全件実行では、先に走ったテストが切り替えたテーマ（Nord: アクセント#88C0D0・文字#ECEFF4）が
        // 残っていることがあり、そのとき「枠が消えたはずなのに26画素残っている」と落ちていた
        // （失敗メッセージに画素の色と位置を出して判明。26画素は文字の縁の混色で、枠ではない）。
        // そこで枠なしの画素数を先に測って基準にし、「枠が出る＝基準より増える」
        // 「枠が消える＝基準に戻る」で判定する。基準は再読込の前後で別々に測る（200行の文書は
        // 縦スクロールバーが出て文字の位置が1画素ずれ、混色の画素数が1つ違った）。
        editor.CaretOffset = editor.Document.TextLength;
        int baselineBefore;
        using (var plain = window.CaptureRenderedFrame())
        {
            baselineBefore = CountColorPixelsInFirstLine(plain!, accentColor, editor);
        }

        // 判定方法そのものの妥当性: 再読込の前に強調を出したとき、実際にアクセント色が検出できること。
        editor.CaretOffset = 1;
        var beforeCount = await CaptureUntilAsync(
            window, editor, accentColor, count => count > baselineBefore).ConfigureAwait(true);
        beforeCount.Should().BeGreaterThan(baselineBefore, "前提: 強調の枠が検出できる");

        await File.WriteAllTextAsync(path, BracketLines(3, "new")).ConfigureAwait(true);
        await vm.NotifyExternalChangeAsync(path).ConfigureAwait(true);

        // 再読込後の基準（枠なし）。
        editor.CaretOffset = tab.Session.Document.TextLength;
        int baseline;
        using (var plainAfter = window.CaptureRenderedFrame())
        {
            baseline = CountColorPixelsInFirstLine(plainAfter!, accentColor, editor);
        }

        // 差し替え後のキャレットの位置はどこでもよいので、括弧の隣へ明示的に置いて強調を出す。
        editor.CaretOffset = 1;
        var shownCount = await CaptureUntilAsync(
            window, editor, accentColor, count => count > baseline).ConfigureAwait(true);
        shownCount.Should().BeGreaterThan(baseline,
            "再読込のあとの次の描画で、1行目の括弧に強調の枠（アクセント色）が描かれている");

        // 括弧から離れると枠は消える（上の判定が「何でも拾う」ものでないことの確認）。
        editor.CaretOffset = tab.Session.Document.TextLength;
        var goneCount = await CaptureUntilAsync(
            window, editor, accentColor, count => count == baseline).ConfigureAwait(true);
        goneCount.Should().Be(baseline, goneCount == baseline ? string.Empty : DescribeFailure(window, editor, tab, accentColor));
    }

    /// <summary>
    /// 描画結果が<paramref name="isExpected"/>を満たすまで、フレームを取り直して数える（最大5秒）。
    ///
    /// キャレットを動かした直後に1回だけ取得したフレームが、動かす前の描画のままになることがある
    /// （全件実行で「枠が出るはずが基準と同じ16画素のまま」と落ちた）。固定回数の取り直しではなく、
    /// 検証する条件そのものが成り立つまで待つ。時間切れのときは最後の画素数を返し、呼び出し側の
    /// アサーションが落ちる（本当に枠が出ない／消えない不具合は従来どおり検出される）。
    /// </summary>
    private static async Task<int> CaptureUntilAsync(
        Avalonia.Controls.Window window, TextEditor editor, Color target, Func<int, bool> isExpected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            int count;
            using (var frame = window.CaptureRenderedFrame())
            {
                count = CountColorPixelsInFirstLine(frame!, target, editor);
            }

            if (isExpected(count) || DateTime.UtcNow >= deadline) return count;
            await Task.Delay(10).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// 「括弧から離れたのに枠が消えていない」ときだけ呼ぶ、原因調査用の状態の書き出し。
    ///
    /// 以前このアサーションは全件実行で約10回に1回「Expected ... to be 0, but found 26」と落ちた。
    /// この書き出しで原因（テーマの残りによる文字縁の混色。baselineの注記参照）が分かった。
    /// 同種の再発に備え、失敗メッセージに画素の位置と編集欄の状態を載せたままにしてある。
    /// 通ったときは何も計算しない。
    /// </summary>
    private static string DescribeFailure(
        Avalonia.Controls.Window window, TextEditor editor, EditorTabViewModel tab, Color accent)
    {
        var textView = editor.TextArea.TextView;
        var sb = new StringBuilder();
        sb.Append($"[調査用] caret={editor.CaretOffset}(行{editor.TextArea.Caret.Line}) ");
        sb.Append($"セッションの文書長={tab.Session.Document.TextLength} 編集欄の文書長={editor.Document.TextLength} ");
        sb.Append($"同一の文書={ReferenceEquals(editor.Document, tab.Session.Document)} ");
        sb.Append($"背景描画器の数={textView.BackgroundRenderers.Count}（{string.Join(",", textView.BackgroundRenderers.Select(r => r.GetType().Name))}） ");
        sb.Append($"アクセント色={accent} 文字色={editor.Foreground} テーマ={Application.Current?.ActualThemeVariant} ");
        sb.Append($"文書={editor.Document.Text.Replace("\n", "|")} 画素(x,y,色)=");

        using var frame = window.CaptureRenderedFrame();
        var top = (int)textView.GetVisualPosition(new TextViewPosition(1, 1), VisualYPosition.LineTop).Y;
        var bottom = (int)textView.GetVisualPosition(new TextViewPosition(1, 1), VisualYPosition.LineBottom).Y;
        var origin = textView.TranslatePoint(new Point(0, 0), (Visual)editor.GetVisualRoot()!) ?? default;
        using var fb = frame!.Lock();
        for (var y = Math.Max(0, (int)origin.Y + top); y < Math.Min(fb.Size.Height, (int)origin.Y + bottom); y++)
        {
            for (var x = Math.Max(0, (int)origin.X); x < Math.Min(fb.Size.Width, (int)origin.X + 60); x++)
            {
                var px = Marshal.ReadInt32(fb.Address, y * fb.RowBytes + x * 4);
                var r = px & 0xFF; var g = (px >> 8) & 0xFF; var b = (px >> 16) & 0xFF;
                if (Math.Abs(r - accent.R) < 48 && Math.Abs(g - accent.G) < 48 && Math.Abs(b - accent.B) < 48)
                {
                    sb.Append($"({x},{y},{r:X2}{g:X2}{b:X2}) ");
                }
            }
        }

        return sb.ToString();
    }

    [AvaloniaFact(DisplayName = "回帰: 検索結果の強調が出ている状態で再読込しても、差し替えの最中の描画で例外が出ない")]
    public async Task 検索結果の強調が出たままの再読込で描画が割り込んでも例外が出ない()
    {
        var (vm, pane, editor, window) = await OpenPaneAsync();
        var (tab, path) = await OpenFileAsync(vm, "d.txt", BracketLines(200, "old"));
        using (window.CaptureRenderedFrame()) { }

        var search = pane.GetVisualDescendants().OfType<SearchOverlay>().Single();
        search.OpenFind();
        search.ViewModel.Query = "text";
        search.ViewModel.CommitAndFindNext();
        search.ViewModel.Matches.Count.Should().BeGreaterThan(0, "前提: 検索結果の強調が出ている");
        using (window.CaptureRenderedFrame()) { }

        Exception? interrupted = null;
        tab.Session.Document.Changed += (_, _) =>
        {
            try { RenderSelectionLayer(editor); }
            catch (Exception ex) { interrupted = ex; }
        };

        await File.WriteAllTextAsync(path, BracketLines(3, "new")).ConfigureAwait(true);
        await vm.NotifyExternalChangeAsync(path).ConfigureAwait(true);

        interrupted.Should().BeNull("検索結果の強調も、描く前に可視行が生きているかを確かめて戻る");
        var draw = () => window.CaptureRenderedFrame()?.Dispose();
        draw.Should().NotThrow();
    }
}
