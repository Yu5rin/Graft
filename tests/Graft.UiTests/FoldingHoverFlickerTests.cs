using Avalonia;
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
/// 実機での指摘（Windows）: 折りたたみマーカーにカーソルを合わせている間、対応するインデント
/// ガイド（縦線）の強調がちらつく不具合の回帰防止テスト。
///
/// 【真因（<see cref="Editor.TextViewRedraw"/>のクラスコメントで詳述）】
/// <c>TextView.InvalidateLayer(KnownLayer)</c>の実装は実質<c>InvalidateMeasure()</c>であり、
/// 呼ぶたびに<c>TextView</c>の可視行（<c>VisualLines</c>）が作り直される。可視行が作り直されると
/// <c>AvaloniaEdit.Folding.FoldingMargin.OnTextViewVisualLinesChanged</c>が＋/－マーカー
/// （<c>FoldingMarginMarker</c>）を全部破棄して作り直すため、ポインタ直下にあったマーカーの
/// インスタンスが入れ替わる。マーカーが入れ替わるとAvaloniaのポインタオーバー判定が崩れ、
/// <see cref="FoldingSupport"/>が購読している<c>FoldingMargin.PointerExited</c>が発火して
/// ホバー強調が解除される→再び<c>HoveredFoldingChanged</c>が発火→（修正前は）<c>InvalidateLayer</c>
/// を再び呼ぶ→可視行の作り直し……という循環がちらつきとして見えていた。
///
/// このテストは「ホバーに伴う再描画要求（<see cref="IndentGuideRenderer.OnHoveredFoldingChanged"/>）
/// が、可視行・折りたたみマーカーのインスタンスをそのまま保つこと」を直接検証する。ポインタの
/// 実際の入退室イベントの発火タイミング（環境依存でフレーク要因になりやすい）に頼らず、
/// 「再生成されるべきでないオブジェクトが本当に再生成されていないか」という、より決定的で
/// 環境に依存しない観点で確認する。
/// </summary>
public class FoldingHoverFlickerTests : IDisposable
{
    private readonly ShownWindowTracker _windows = new();

    public void Dispose()
    {
        _windows.Dispose();
        GC.SuppressFinalize(this);
    }

    [AvaloniaFact(DisplayName = "折りたたみマーカーへのホバーは可視行・マーカーのインスタンスを再生成させない")]
    public void ホバーで可視行とマーカーが再生成されない()
    {
        const string text = "void Foo()\n{\n    Bar();\n}\n";
        var document = new TextDocument(text);
        var editor = new TextEditor { Document = document, ShowLineNumbers = true };
        var window = _windows.Track(new Window { Width = 600, Height = 400, Content = editor });

        using var folding = new FoldingSupport(editor);
        using var indentGuide = new IndentGuideRenderer(editor, folding);
        folding.Attach(document, ".cs"); // 括弧ベース戦略。1行目「{」の直前が折りたたみ開始。

        window.Show();
        window.CaptureRenderedFrame().Should().NotBeNull(); // レイアウト確定（マーカー生成に必要）。

        var margin = editor.TextArea.LeftMargins.OfType<FoldingMargin>().Single();
        var textView = editor.TextArea.TextView;

        var markersBefore = margin.GetVisualChildren()
            .Where(v => v.GetType().Name == "FoldingMarginMarker")
            .ToList();
        markersBefore.Should().NotBeEmpty("前提: 折りたたみ範囲が1つあるのでマーカーが最低1つ生成されているはず");
        var visualLinesBefore = textView.VisualLines.ToList();

        // FoldingHoverTests.csと同じ座標の求め方（マージンとtextViewは同じY座標系を共有する）。
        var visualLine = textView.GetOrConstructVisualLine(document.GetLineByNumber(2));
        var yInMargin = visualLine.VisualTop + visualLine.Height / 2 - textView.VerticalOffset;
        var xInMargin = margin.Bounds.Width / 2;
        var pointInWindow = margin.TranslatePoint(new Point(xInMargin, yInMargin), window);
        pointInWindow.Should().NotBeNull("マージンがウィンドウ内に配置されていること（レイアウト確定後）");

        FoldingSection? lastHovered = null;
        var hoveredToNullCount = 0;
        folding.HoveredFoldingChanged += (_, fs) =>
        {
            if (fs is null && lastHovered is not null) hoveredToNullCount++;
            lastHovered = fs;
        };

        window.MouseMove(pointInWindow!.Value);
        window.CaptureRenderedFrame().Should().NotBeNull();
        lastHovered.Should().NotBeNull("マーカー上へマウスを乗せた時点でホバー中の範囲を保持しているはず");

        // 「マウスを動かさず静止させる」を、実際のポインタ入力を伴わない複数回の再描画パスで
        // 模擬する（マウスの実イベントを送らないため、ホバーの状態自体はここでは変化しない前提。
        // 万一ここでnullへ戻る=hoveredToNullCountが増える=真因のとおりマーカー再生成に伴う
        // ちらつきが起きているということ）。
        for (var i = 0; i < 10; i++)
        {
            window.CaptureRenderedFrame().Should().NotBeNull();
        }

        hoveredToNullCount.Should().Be(0,
            "マウスを動かしていないのに、ホバー中の範囲が一度でもnullへ戻ったら" +
            "ちらつきが再発している（真因: 可視行・マーカーの意図しない再生成）");
        lastHovered.Should().NotBeNull("静止させ続けた後もホバー中の範囲を保持しているはず");

        var markersAfter = margin.GetVisualChildren()
            .Where(v => v.GetType().Name == "FoldingMarginMarker")
            .ToList();
        markersAfter.Should().Equal(markersBefore,
            "ホバーに伴う再描画要求（IndentGuideRenderer.OnHoveredFoldingChanged）は、" +
            "FoldingMarginMarkerのインスタンスをそのまま保つはず（TextViewRedrawのクラスコメント参照）。" +
            "再生成されると、ポインタ直下のマーカーが入れ替わりPointerExitedが発火し、ホバー強調が" +
            "ちらつく真因そのものになる");

        var visualLinesAfter = textView.VisualLines.ToList();
        visualLinesAfter.Should().Equal(visualLinesBefore,
            "可視行（VisualLine）のインスタンスも作り直されていないはず。" +
            "これはTextView.InvalidateMeasureが呼ばれていない直接的な証拠でもある");
    }

    [AvaloniaFact(DisplayName = "インデントガイドのモード切替・テーマ切替も可視行・マーカーを再生成させない")]
    public void モード切替とテーマ切替でも可視行とマーカーが再生成されない()
    {
        const string text = "void Foo()\n{\n    Bar();\n}\n";
        var document = new TextDocument(text);
        var editor = new TextEditor { Document = document, ShowLineNumbers = true };
        var window = _windows.Track(new Window { Width = 600, Height = 400, Content = editor });

        using var folding = new FoldingSupport(editor);
        using var indentGuide = new IndentGuideRenderer(editor, folding);
        folding.Attach(document, ".cs");

        window.Show();
        window.CaptureRenderedFrame().Should().NotBeNull();

        var margin = editor.TextArea.LeftMargins.OfType<FoldingMargin>().Single();
        var textView = editor.TextArea.TextView;
        var markersBefore = margin.GetVisualChildren()
            .Where(v => v.GetType().Name == "FoldingMarginMarker")
            .ToList();
        var visualLinesBefore = textView.VisualLines.ToList();

        // SetMode: IndentGuideRenderer.SetModeが可視行を作り直さないことの直接検証
        // （案件1の対処箇所の1つ。クラスコメント参照）。
        indentGuide.SetMode(IndentGuideMode.AllIndentation);
        window.CaptureRenderedFrame().Should().NotBeNull();

        margin.GetVisualChildren().Where(v => v.GetType().Name == "FoldingMarginMarker")
            .Should().Equal(markersBefore, "SetModeによる再描画も可視行の作り直しを引き起こしてはいけない");
        textView.VisualLines.ToList().Should().Equal(visualLinesBefore,
            "SetModeによる再描画も可視行の作り直しを引き起こしてはいけない");
    }
}
