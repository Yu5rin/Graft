using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;
using FluentAssertions;
using Graft.Editor;
using Graft.UiTests.TestSupport;
using Xunit;

namespace Graft.UiTests;

/// <summary>
/// 実機での指摘（Windows）: 折りたたみマーカー周りの余白をPaneと同じにする対処
/// （<see cref="MarkerOnlyFoldingMargin"/>の<c>MeasureOverride</c>/<c>ArrangeOverride</c>）の
/// 回帰防止テスト。移植元Pane（<c>src/style.css</c>・<c>src/editor.js</c>）の数値
/// 「本文エリア左端 --5px-- マーカー左端 --15px(マーカー本体)-- マーカー右端 --5px-- コード開始位置」
/// を、マージンの<c>Bounds</c>とマーカーの<c>Bounds</c>から直接検証する
/// （スクリーンショットの目測ではなく、実際にレイアウトされた値そのものを見る）。
/// </summary>
public class FoldingMarginLayoutTests : IDisposable
{
    private readonly ShownWindowTracker _windows = new();

    public void Dispose()
    {
        _windows.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>フォントサイズを変えても崩れないことを併せて確認する（Ctrl+ホイールでの
    /// フォントサイズ変更を想定。実機確認の結果は本タスクの報告参照）。</summary>
    [AvaloniaTheory(DisplayName = "折りたたみマージンの幅は常に25px（5+15+5）固定でフォントサイズに追従しない")]
    [InlineData(13.0)]
    [InlineData(32.0)] // Ctrl+ホイールで到達しうる上限（EditorPaneViewModel.MaxFontSize）。
    [InlineData(8.0)]  // 下限（EditorPaneViewModel.MinFontSize）。
    public void マージン幅は常に25px(double fontSize)
    {
        var document = new TextDocument("void Foo()\n{\n    Bar();\n}\n");
        var editor = new TextEditor { Document = document, ShowLineNumbers = true, FontSize = fontSize };
        var window = _windows.Track(new Window { Width = 800, Height = 600, Content = editor });

        using var folding = new FoldingSupport(editor);
        folding.Attach(document, ".cs");

        window.Show();
        window.CaptureRenderedFrame().Should().NotBeNull();

        var margin = editor.TextArea.LeftMargins.OfType<FoldingMargin>().Single();
        margin.Should().BeOfType<MarkerOnlyFoldingMargin>();
        margin.Bounds.Width.Should().Be(MarkerOnlyFoldingMargin.TotalWidth,
            $"フォントサイズ{fontSize}pxでもマージン幅は25px固定のはず（Pane同数値、AvaloniaEdit既定の" +
            "1.3333*FontSize相当には追従しない）");
    }

    [AvaloniaFact(DisplayName = "折りたたみマーカーはマージン内で左5px・幅15pxに固定配置され、右側の隙間も5pxになる")]
    public void マーカーは左5px幅15pxに配置され左右対称になる()
    {
        var document = new TextDocument("void Foo()\n{\n    Bar();\n}\n");
        var editor = new TextEditor { Document = document, ShowLineNumbers = true, FontSize = 13 };
        var window = _windows.Track(new Window { Width = 800, Height = 600, Content = editor });

        using var folding = new FoldingSupport(editor);
        folding.Attach(document, ".cs"); // 括弧ベース戦略。2行目の"{"から折りたたみ範囲が1つできる。

        window.Show();
        window.CaptureRenderedFrame().Should().NotBeNull();

        var margin = editor.TextArea.LeftMargins.OfType<FoldingMargin>().Single();
        var marker = margin.GetVisualChildren().FirstOrDefault(v => v.GetType().Name == "FoldingMarginMarker")
            as Control;
        marker.Should().NotBeNull("折りたたみ範囲が1つあるので、マーカーが最低1つ生成されているはず");

        marker!.Bounds.X.Should().Be(MarkerOnlyFoldingMargin.GapLeft,
            "マーカー左端は本文エリア左端（マージンのX=0）から5pxのはず（Pane同数値）");
        marker.Bounds.Width.Should().Be(MarkerOnlyFoldingMargin.MarkerSize, "マーカー本体は15px四方のはず（Pane同数値）");
        marker.Bounds.Height.Should().Be(MarkerOnlyFoldingMargin.MarkerSize, "マーカー本体は15px四方のはず（Pane同数値）");

        var rightGap = margin.Bounds.Width - (marker.Bounds.X + marker.Bounds.Width);
        rightGap.Should().Be(MarkerOnlyFoldingMargin.GapRight,
            "マーカー右端からコード開始位置までの隙間も5pxのはず（左右対称、依頼の要点）");
        rightGap.Should().Be(marker.Bounds.X, "左右の隙間は数値として完全に一致する（左右対称）はず");
    }
}
