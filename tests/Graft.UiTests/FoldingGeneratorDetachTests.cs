using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;
using FluentAssertions;
using Graft.Editor;
using Graft.UiTests.TestSupport;
using Xunit;

namespace Graft.UiTests;

/// <summary>
/// 課題#73（スクロールバーのドラッグがマウスに追い付かない）の対処——「1つも畳まれていない間は
/// <see cref="FoldingElementGenerator"/>を<c>TextView.ElementGenerators</c>から外す」
/// （<see cref="FoldingSupport"/>クラスコメントの【課題#73】節）——の回帰テスト。
///
/// この対処は折りたたみ機能そのものへ手を入れるため、次の2点を手厚く押さえる。
/// (1) 付け外しが畳み状態と正しく同期していること（畳み状態が変わりうる全経路: マーカーの
///     クリック・折りたたみコマンド3種・デバウンス後の再計算・タブ切替）。
/// (2) 外れている間の表示が、付いている場合と<b>完全に同一</b>であること
///     （<see cref="描画結果は生成器の有無で1バイトも変わらない"/>。実装判断の根拠にした
///     PNGバイト一致の実測を、テストとして固定したもの）。
///
/// 「畳めていること」の確認には<c>VisualLine.LastDocumentLine</c>を使う。畳まれた範囲の行は
/// 1つの<c>VisualLine</c>へまとめられるため、開始行の<c>VisualLine</c>が終了行まで伸びていれば
/// 実際に行が隠れている。ここは単に生成器が付いているかどうかより厳しい確認になっている:
/// 畳む瞬間に生成器が付いていないと<c>FoldingSection.ValidateCollapsedLineSections</c>が
/// <c>CollapsedLineSection</c>を1つも作れず（<c>FoldingManager.TextViews</c>が空のため）、
/// マーカーだけ畳まれた見た目で本文が畳まれない、という壊れ方をするため。
/// </summary>
public class FoldingGeneratorDetachTests : IDisposable
{
    /// <summary>括弧ベース（.cs）で3つの入れ子の折りたたみ範囲ができるソース。</summary>
    private const string NestedSource =
        "class A\n{\n    void Foo()\n    {\n        if (x)\n        {\n            Bar();\n        }\n    }\n}\n";

    private readonly ShownWindowTracker _windows = new();

    /// <summary>各テストで作った<see cref="FoldingSupport"/>（テスト終了時にまとめて破棄する）。</summary>
    private readonly List<FoldingSupport> _foldings = new();

    public void Dispose()
    {
        foreach (var folding in _foldings) folding.Dispose();
        _windows.Dispose();
        GC.SuppressFinalize(this);
    }

    [AvaloniaFact(DisplayName = "課題#73: 何も畳んでいない間は折りたたみ生成器がElementGeneratorsから外れている")]
    public void 何も畳んでいなければ生成器は外れている()
    {
        var (_, editor, folding, document) = CreateFoldingEditor(NestedSource);

        folding.Manager.Should().NotBeNull();
        folding.Manager!.AllFoldings.Should().NotBeEmpty("折りたたみ範囲の計算自体は行われている");
        folding.Manager.AllFoldings.Should().OnlyContain(fs => !fs.IsFolded);
        GeneratorCount(editor).Should().Be(0,
            "1つも畳まれていなければ生成器は何も生成しないため、外しておける（課題#73の中核）");

        // 外れていても、折りたたみマージンの＋/－マーカーは従来どおり出る（マージンは
        // FoldingManagerを直接見ており、生成器とは無関係）。
        editor.TextArea.LeftMargins.OfType<FoldingMargin>().Should().ContainSingle();
        document.LineCount.Should().Be(11);
    }

    [AvaloniaFact(DisplayName = "課題#73: 折りたたみマーカーのクリックで畳むと生成器が戻り、実際に行が隠れる")]
    public void マーカークリックで畳むと生成器が戻り行が隠れる()
    {
        var (window, editor, folding, document) = CreateFoldingEditor(NestedSource);
        GeneratorCount(editor).Should().Be(0);

        // 2行目の「{」から10行目の「}」までが最も外側の折りたたみ範囲（BraceFoldingStrategy）。
        var outer = folding.Manager!.AllFoldings.OrderBy(fs => fs.StartOffset).First();
        outer.StartOffset.Should().Be(document.GetLineByNumber(2).Offset);

        ClickFoldingMarkerOnLine(window, editor, lineNumber: 2);

        outer.IsFolded.Should().BeTrue("マーカーのクリックで畳まれるはず");
        GeneratorCount(editor).Should().Be(1, "1つでも畳まれたら生成器を戻す必要がある");
        FoldedThrough(editor, document, startLine: 2).Should().BeGreaterThan(2,
            "実際に後続行が隠れて1つのVisualLineへまとまっているはず");

        // もう一度クリックして展開すると、また外れる。
        ClickFoldingMarkerOnLine(window, editor, lineNumber: 2);
        outer.IsFolded.Should().BeFalse();
        GeneratorCount(editor).Should().Be(0, "全部展開されたら再び外す");
        FoldedThrough(editor, document, startLine: 2).Should().Be(2, "展開後は1行1VisualLineへ戻る");
    }

    /// <summary>
    /// 課題#73: マーカーを押してから離すまでの間（実際のクリックは100ms前後あり、その間に
    /// 何フレームも描画される）に、畳みが表示へ正しく反映されていることを確認する。
    ///
    /// これは<see cref="FoldingSupport"/>がマージンのPointerPressedを購読している（PointerReleased
    /// だけに頼っていない）理由そのもののテスト。押した瞬間は<c>FoldingManager.TextViews</c>が
    /// 空のままIsFolded=trueになるため<c>CollapsedLineSection</c>が作られないが、同じイベント
    /// 処理の中で生成器を戻すことで<c>FoldingManager.AddToTextView</c>が作り直してくれる
    /// （<see cref="FoldingSupport"/>のHookFoldingMarginのコメント参照）。離した時に同期する
    /// 実装だと、押している間のフレームが「マーカーは畳まれた表示なのに本文は畳まれていない」
    /// 状態で描かれてしまう。
    /// </summary>
    [AvaloniaFact(DisplayName = "課題#73: マーカーを押した時点（離す前）で、既に本文の折りたたみが表示へ反映されている")]
    public void 押した時点で折りたたみが表示へ反映される()
    {
        var (window, editor, folding, document) = CreateFoldingEditor(NestedSource);
        var outer = folding.Manager!.AllFoldings.OrderBy(fs => fs.StartOffset).First();

        PressFoldingMarkerOnLine(window, editor, lineNumber: 2);
        try
        {
            window.CaptureRenderedFrame().Should().NotBeNull(); // 押している間のフレーム。

            outer.IsFolded.Should().BeTrue("押した時点でAvaloniaEdit側が畳んでいる");
            GeneratorCount(editor).Should().Be(1, "押した時点（離す前）で既に生成器が戻っている必要がある");
            FoldedThrough(editor, document, startLine: 2).Should().BeGreaterThan(2,
                "押している間のフレームでも、本文が実際に畳まれて描かれている必要がある");
        }
        finally
        {
            ReleaseFoldingMarker(window, editor, lineNumber: 2);
        }
    }

    [AvaloniaFact(DisplayName = "課題#73: 折りたたみレベル指定でも畳めて生成器が戻り、該当レベルが無ければ外れる")]
    public void レベル指定の折りたたみでも同期する()
    {
        var (window, editor, folding, document) = CreateFoldingEditor(NestedSource);

        folding.FoldToLevel(1);
        folding.Manager!.AllFoldings.Count(fs => fs.IsFolded).Should().Be(1, "最も外側の1つだけが畳まれる");
        GeneratorCount(editor).Should().Be(1);
        window.CaptureRenderedFrame().Should().NotBeNull();
        FoldedThrough(editor, document, startLine: 2).Should().BeGreaterThan(2);

        folding.FoldToLevel(2);
        GeneratorCount(editor).Should().Be(1);
        window.CaptureRenderedFrame().Should().NotBeNull();
        FoldedThrough(editor, document, startLine: 4).Should().BeGreaterThan(4,
            "レベル2（void Fooの本体）が畳まれているはず");

        // このソースの入れ子は3段までなので、レベル5を指定すると「1つも該当しない」＝全展開。
        folding.FoldToLevel(5);
        folding.Manager.AllFoldings.Should().OnlyContain(fs => !fs.IsFolded);
        GeneratorCount(editor).Should().Be(0, "結果的に1つも畳まれなければ外す");
    }

    [AvaloniaFact(DisplayName = "課題#73: すべてのコメントブロックの折りたたみでも生成器が戻り、実際に畳まれる")]
    public void コメントブロックの折りたたみでも同期する()
    {
        const string source = "// 一行目のコメント\n// 二行目のコメント\nvoid Foo()\n{\n    Bar();\n}\n";
        var (window, editor, folding, document) = CreateFoldingEditor(source);
        GeneratorCount(editor).Should().Be(0);

        folding.FoldAllComments();

        folding.Manager!.AllFoldings.Count(fs => fs.IsFolded).Should().Be(1, "連続する2行のコメントが1範囲になる");
        GeneratorCount(editor).Should().Be(1);
        window.CaptureRenderedFrame().Should().NotBeNull();
        FoldedThrough(editor, document, startLine: 1).Should().Be(2, "1〜2行目が1つのVisualLineへまとまる");
    }

    [AvaloniaFact(DisplayName = "課題#73: 再帰的な折りたたみでも生成器が戻り、内側まで畳まれる")]
    public void 再帰的な折りたたみでも同期する()
    {
        var (window, editor, folding, document) = CreateFoldingEditor(NestedSource);

        folding.FoldRecursiveAt(document.GetLineByNumber(5).Offset); // 「if (x)」の行。

        folding.Manager!.AllFoldings.Count(fs => fs.IsFolded).Should().BeGreaterThan(0);
        GeneratorCount(editor).Should().Be(1);
        window.CaptureRenderedFrame().Should().NotBeNull();
        FoldedThrough(editor, document, startLine: 4).Should().BeGreaterThan(4);
    }

    [AvaloniaFact(DisplayName = "課題#73: 畳んだ範囲が編集で消えると、デバウンス後の再計算で生成器が外れる")]
    public async Task 編集で畳んだ範囲が消えれば再計算時に外れる()
    {
        var (window, editor, folding, document) = CreateFoldingEditor(NestedSource);

        folding.FoldToLevel(1);
        GeneratorCount(editor).Should().Be(1);

        // 折りたたみ範囲が1つも無くなる内容へ差し替える（DocumentSession.ReloadAsyncと同じく
        // 同一インスタンスのTextを書き換える経路）。
        document.Text = "var a = 1;\nvar b = 2;\n";

        await Task.Delay(500); // 再計算のデバウンス（300ms）が発火するまで待つ。
        window.CaptureRenderedFrame().Should().NotBeNull();

        folding.Manager!.AllFoldings.Should().BeEmpty("括弧が無くなったので折りたたみ範囲も消える");
        GeneratorCount(editor).Should().Be(0, "畳まれた範囲がゼロになったら外し直す（回収経路）");
    }

    [AvaloniaFact(DisplayName = "課題#73: タブ切替（文書の差し替え）後も折りたたみが機能し、生成器の付け外しが正しい")]
    public void タブ切替後も折りたたみが機能する()
    {
        var (window, editor, folding, docA) = CreateFoldingEditor(NestedSource);

        folding.FoldToLevel(1);
        GeneratorCount(editor).Should().Be(1);

        // EditorPane.ApplyDocumentTabと同じ順序（PrepareForDocumentSwap → Document代入 → Attach）。
        var docB = new TextDocument("class B\n{\n    void Baz()\n    {\n        Qux();\n    }\n}\n");
        folding.PrepareForDocumentSwap();
        editor.Document = docB;
        folding.Attach(docB, ".cs");
        window.CaptureRenderedFrame().Should().NotBeNull();

        GeneratorCount(editor).Should().Be(0, "新しい文書では何も畳まれていないので外れているはず");
        docA.LineCount.Should().Be(11);

        folding.FoldToLevel(1);
        folding.Manager!.AllFoldings.Count(fs => fs.IsFolded).Should().Be(1);
        GeneratorCount(editor).Should().Be(1, "切替後の文書でも畳めば戻る");
        window.CaptureRenderedFrame().Should().NotBeNull();
        FoldedThrough(editor, docB, startLine: 2).Should().BeGreaterThan(2);
    }

    [AvaloniaFact(DisplayName = "課題#73: 折りたたみを設定で無効化・再有効化しても生成器が二重に付かない")]
    public void 無効化と再有効化を繰り返しても生成器は1つ以下()
    {
        var (window, editor, folding, document) = CreateFoldingEditor(NestedSource);

        for (var i = 0; i < 3; i++)
        {
            folding.SetEnabled(false);
            GeneratorCount(editor).Should().Be(0, "無効化中は当然0個");

            folding.SetEnabled(true);
            GeneratorCount(editor).Should().Be(0, "再有効化直後は何も畳まれていないので0個");

            folding.FoldToLevel(1);
            GeneratorCount(editor).Should().Be(1, "何度繰り返しても増殖しない");
            window.CaptureRenderedFrame().Should().NotBeNull();
            FoldedThrough(editor, document, startLine: 2).Should().BeGreaterThan(2);
        }
    }

    /// <summary>
    /// 課題#73の等価性の根拠（実装判断の決め手）をテストとして固定する。生成器を外した状態と
    /// 付けた状態で、描画結果のPNGが1バイトも変わらないことを確認する。
    /// 「何も畳まれていないとき<see cref="FoldingManager.GetNextFoldedFoldingStart"/>は必ず-1を
    /// 返し、生成器は何も生成しない」ため、外しても表示は完全に同一になる。
    ///
    /// 付けた状態は、<see cref="FoldingElementGenerator"/>（公開型）を同じ
    /// <see cref="FoldingManager"/>で作って先頭へ差し込むことで作る（<c>FoldingManager.Install</c>が
    /// 行っているのと同じこと）。製品コードには「畳まずに付けたままにする」経路が無いため、
    /// テスト側でその状態を再現している。
    /// </summary>
    [AvaloniaFact(DisplayName = "課題#73: 何も畳んでいない状態の描画結果は、折りたたみ生成器の有無で1バイトも変わらない")]
    public void 描画結果は生成器の有無で1バイトも変わらない()
    {
        // 500行目付近まで送った状態で比べる（可視行がすべて作り直された状態にするため）。
        var text = string.Concat(Enumerable.Range(0, 2_000).Select(i => (i % 4) switch
        {
            0 => $"class Sample{i}\n",
            1 => "{\n",
            2 => $"    public int Value{i} => {i};\n",
            _ => "}\n",
        }));

        var (window, editor, folding, document) = CreateFoldingEditor(text);
        editor.ScrollToLine(500);
        window.CaptureRenderedFrame().Should().NotBeNull();

        GeneratorCount(editor).Should().Be(0, "何も畳んでいないので外れている");
        var withoutGenerator = CapturePng(window);

        var generator = new FoldingElementGenerator { FoldingManager = folding.Manager };
        editor.TextArea.TextView.ElementGenerators.Insert(0, generator);
        try
        {
            GeneratorCount(editor).Should().Be(1);
            var withGenerator = CapturePng(window);

            withGenerator.Should().Equal(withoutGenerator,
                "何も畳まれていなければ生成器は何も生成しないため、描画結果は完全に一致するはず");
            withoutGenerator.Length.Should().BeGreaterThan(1_000, "空の画像同士を比べていないことの確認");
        }
        finally
        {
            editor.TextArea.TextView.ElementGenerators.Remove(generator);
            document.LineCount.Should().Be(2_001);
        }
    }

    /// <summary>折りたたみ生成器が<c>TextView.ElementGenerators</c>に入っている数（0または1）。</summary>
    private static int GeneratorCount(TextEditor editor)
        => editor.TextArea.TextView.ElementGenerators.OfType<FoldingElementGenerator>().Count();

    /// <summary>
    /// <paramref name="startLine"/>行目の<c>VisualLine</c>が何行目まで含んでいるか。畳まれて
    /// いれば終了行まで伸び、畳まれていなければ<paramref name="startLine"/>のまま。
    /// </summary>
    private static int FoldedThrough(TextEditor editor, TextDocument document, int startLine)
    {
        var textView = editor.TextArea.TextView;
        var visualLine = textView.GetOrConstructVisualLine(document.GetLineByNumber(startLine));
        return visualLine.LastDocumentLine.LineNumber;
    }

    /// <summary>
    /// 指定行の折りたたみマーカーを実際にクリックする（Avalonia.Headlessの疑似マウス操作）。
    /// マージンとTextViewが同じY座標系を共有する点は<see cref="FoldingHoverTests"/>と同じ。
    /// </summary>
    private static void ClickFoldingMarkerOnLine(Window window, TextEditor editor, int lineNumber)
    {
        PressFoldingMarkerOnLine(window, editor, lineNumber);
        ReleaseFoldingMarker(window, editor, lineNumber);
    }

    /// <summary>マーカーの上でボタンを押す（離さない）。</summary>
    private static void PressFoldingMarkerOnLine(Window window, TextEditor editor, int lineNumber)
        => window.MouseDown(FoldingMarkerPoint(window, editor, lineNumber), MouseButton.Left);

    /// <summary>押していたボタンを離し、描画まで進める。</summary>
    private static void ReleaseFoldingMarker(Window window, TextEditor editor, int lineNumber)
    {
        window.MouseUp(FoldingMarkerPoint(window, editor, lineNumber), MouseButton.Left);
        window.CaptureRenderedFrame().Should().NotBeNull();
    }

    private static Point FoldingMarkerPoint(Window window, TextEditor editor, int lineNumber)
    {
        var margin = editor.TextArea.LeftMargins.OfType<FoldingMargin>().Single();
        var textView = editor.TextArea.TextView;
        var visualLine = textView.GetOrConstructVisualLine(editor.Document.GetLineByNumber(lineNumber));
        var y = visualLine.VisualTop + visualLine.Height / 2 - textView.VerticalOffset;
        var point = margin.TranslatePoint(new Point(margin.Bounds.Width / 2, y), window);
        point.Should().NotBeNull("マージンがウィンドウ内に配置されていること（レイアウト確定後）");
        return point!.Value;
    }

    private static byte[] CapturePng(Window window)
    {
        using var frame = window.CaptureRenderedFrame();
        frame.Should().NotBeNull();
        using var stream = new MemoryStream();
        frame!.Save(stream);
        return stream.ToArray();
    }

    /// <summary>折りたたみを取り付けたエディタを表示状態で用意する（描画・レイアウト確定済み）。</summary>
    private (Window Window, TextEditor Editor, FoldingSupport Folding, TextDocument Document)
        CreateFoldingEditor(string text)
    {
        var document = new TextDocument(text);
        var editor = new TextEditor { Document = document, ShowLineNumbers = true };
        var window = _windows.Track(new Window { Width = 900, Height = 600, Content = editor });

        var folding = new FoldingSupport(editor);
        _foldings.Add(folding);
        folding.Attach(document, ".cs"); // 括弧ベース戦略。

        window.Show();
        window.CaptureRenderedFrame().Should().NotBeNull();
        return (window, editor, folding, document);
    }
}
