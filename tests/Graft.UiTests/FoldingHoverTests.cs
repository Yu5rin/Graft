using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;
using FluentAssertions;
using Graft.Editor;
using Graft.UiTests.TestSupport;
using Xunit;

namespace Graft.UiTests;

/// <summary>
/// 検討書「折りたたみの機能追加」(a) マーカーのホバー強調の回帰テスト。
/// <see cref="FoldingSupport.HoveredFoldingChanged"/>が、実際に折りたたみマーカー上へ
/// マウスを乗せたとき（Avalonia.Headlessの疑似マウス操作）に、対応する範囲を伴って
/// 発火することを確認する。<see cref="Editor.IndentGuideRenderer"/>はこのイベントを
/// 購読するだけなので、発火経路そのものをここで押さえれば縦線側の配線も間接的に保証できる
/// （実際の縦線の色変化はXvfbでの目視確認で確認済み。手順は本タスクの報告参照）。
/// </summary>
public class FoldingHoverTests : IDisposable
{
    private readonly ShownWindowTracker _windows = new();

    public void Dispose()
    {
        _windows.Dispose();
        GC.SuppressFinalize(this);
    }

    [AvaloniaFact(DisplayName = "折りたたみマーカーにマウスを乗せるとHoveredFoldingChangedが対応する範囲で発火する")]
    public void マーカーホバーでHoveredFoldingChangedが発火する()
    {
        const string text = "void Foo()\n{\n    Bar();\n}\n";
        var document = new TextDocument(text);
        var editor = new TextEditor { Document = document, ShowLineNumbers = true };
        var window = _windows.Track(new Window { Width = 600, Height = 400, Content = editor });

        using var folding = new FoldingSupport(editor);
        folding.Attach(document, ".cs"); // 括弧ベース戦略。1行目「{」の直前が折りたたみ開始。

        window.Show();
        window.CaptureRenderedFrame().Should().NotBeNull(); // レイアウト確定（マージンのマーカー生成に必要）。

        var margin = editor.TextArea.LeftMargins.OfType<FoldingMargin>().Single();

        FoldingSection? hoveredAtLeastOnce = null;
        var raisedCount = 0;
        folding.HoveredFoldingChanged += (_, fs) =>
        {
            raisedCount++;
            if (fs is not null) hoveredAtLeastOnce = fs;
        };

        // 折りたたみ範囲は2行目の"{"から始まる（BraceFoldingStrategy、"{"文字位置＝
        // GetLineByNumber(2).Offset）。マーカーはAvaloniaEdit標準の挙動でその行（2行目）に
        // 出る。マージンとtextViewは同じY座標系（スクロール位置を差し引いた可視領域の上端が0）
        // を共有する（GitGutterProvider.DrawBandが同じ前提でtop = line.VisualTop -
        // textView.VerticalOffset を自身のローカルY座標として使っているのと同じ理由）。
        var textView = editor.TextArea.TextView;
        var visualLine = textView.GetOrConstructVisualLine(document.GetLineByNumber(2));
        var yInMargin = visualLine.VisualTop + visualLine.Height / 2 - textView.VerticalOffset;
        var xInMargin = margin.Bounds.Width / 2;

        var pointInWindow = margin.TranslatePoint(new Point(xInMargin, yInMargin), window);
        pointInWindow.Should().NotBeNull("マージンがウィンドウ内に配置されていること（レイアウト確定後）");

        window.MouseMove(pointInWindow!.Value);
        window.CaptureRenderedFrame().Should().NotBeNull();

        raisedCount.Should().BeGreaterThan(0,
            "マーカー上へマウスを乗せた時点でHoveredFoldingChangedが最低1回発火するはず");
        hoveredAtLeastOnce.Should().NotBeNull("発火時のいずれかでnullでない範囲が渡ってくるはず");
        hoveredAtLeastOnce!.StartOffset.Should().Be(document.GetLineByNumber(2).Offset,
            "2行目の\"{\"の位置が折りたたみ範囲の開始オフセットのはず（BraceFoldingStrategy）");

        // マージンの外へ出るとnullへ戻る（ホバー解除）ことも確認する。
        window.MouseMove(new Point(500, 380));
        window.CaptureRenderedFrame().Should().NotBeNull();
        hoveredAtLeastOnce = null;
        folding.HoveredFoldingChanged += (_, fs) => hoveredAtLeastOnce = fs;
        window.MouseMove(new Point(10, 10));
        window.MouseMove(pointInWindow.Value); // 経路上で一旦マーカーへ戻ってから、
        window.MouseMove(new Point(500, 380)); // 完全にマージンの外へ出す。
        window.CaptureRenderedFrame().Should().NotBeNull();
        hoveredAtLeastOnce.Should().BeNull("マージンの外へ出たらホバー中の範囲はnullに戻るはず");
    }
}
