using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using FluentAssertions;
using Graft.Editor;
using Graft.Infra;
using Graft.UiTests.TestSupport;
using Xunit.Abstractions;

namespace Graft.UiTests;

/// <summary>
/// 課題#72「折り返し行のインデント継承」の検証。
///
/// <para>
/// 実装は<see cref="WrapIndentSupport"/>のクラスコメントのとおり、AvaloniaEdit 11.1.0 の
/// バグ（<c>firstLineInParagraph</c>の設定漏れ）とAvalonia 11.2.3 の制約
/// （<c>TextFormatter</c>が<c>TextParagraphProperties.Indent</c>を読まない）の両方を
/// 迂回するため、<c>TextView._formatter</c>（privateフィールド）をリフレクションで
/// 差し替える。private APIに依存する以上、
/// <list type="number">
///   <item>意図した見た目・座標系になっていること（描画・キャレット・選択・ヒットテスト）</item>
///   <item>将来AvaloniaEdit側が変わってリフレクションが失敗したとき、例外ではなく
///   「字下げが効かないだけ」へ縮退すること</item>
/// </list>
/// の両方をテストで押さえる必要がある。
/// </para>
/// </summary>
public class WrapIndentTests : IDisposable
{
    /// <summary>折り返しが必ず複数行になる、深く字下げされた1行。</summary>
    private const string IndentedLine =
        "        var result = SomeVeryLongMethodName(argument1, argument2, argument3, argument4, argument5);";

    private readonly ITestOutputHelper _output;
    private readonly ShownWindowTracker _windows = new();
    private readonly string _baseDirectory =
        Path.Combine(Path.GetTempPath(), "graft-ui-tests", Guid.NewGuid().ToString("N"));

    public WrapIndentTests(ITestOutputHelper output) => _output = output;

    public void Dispose()
    {
        _windows.Dispose();
        try
        {
            if (Directory.Exists(_baseDirectory)) Directory.Delete(_baseDirectory, recursive: true);
        }
        catch (IOException)
        {
            // 後始末の失敗は検証結果に影響しない。
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 折り返し有効のエディタを1つ作り、<see cref="WrapIndentSupport"/>を適用して表示する。
    /// EditorPaneを丸ごと組み立てると検証したい経路以外（タブ・折りたたみ・Gitガター）まで
    /// 巻き込むため、ここでは素の<see cref="TextEditor"/>へ本機能だけを載せる
    /// （EditorPane経由の実配線は末尾の別テストで確認する）。
    /// </summary>
    private (TextEditor Editor, Window Window, WrapIndentSupport Support) CreateEditor(
        string text, bool wordWrap = true, double width = 320, Action<TextEditor>? configure = null)
    {
        var editor = new TextEditor
        {
            Document = new TextDocument(text),
            WordWrap = wordWrap,
            FontFamily = FontFamily.Default,
            FontSize = 14,
        };
        configure?.Invoke(editor);
        var support = new WrapIndentSupport(editor);
        var window = _windows.Track(new Window { Width = width, Height = 200, Content = editor });
        window.Show();
        window.CaptureRenderedFrame().Should().NotBeNull();
        return (editor, window, support);
    }

    /// <summary>1行目のVisualLineを組み立て直して返す。</summary>
    private static VisualLine FirstVisualLine(TextEditor editor)
        => editor.TextArea.TextView.GetOrConstructVisualLine(editor.Document.GetLineByNumber(1));

    /// <summary>各TextLine（折り返しの各段）の先頭X座標を並べる。</summary>
    private static List<double> LineStartXPositions(VisualLine visualLine)
    {
        var xs = new List<double>();
        foreach (var textLine in visualLine.TextLines)
        {
            var startColumn = visualLine.GetTextLineVisualStartColumn(textLine);
            xs.Add(visualLine.GetTextLineVisualXPosition(textLine, startColumn));
        }
        return xs;
    }

    [AvaloniaFact(DisplayName = "課題#72: 折り返しの2行目以降が1行目のインデント位置へ揃う")]
    public void 折り返しの二行目以降が字下げされる()
    {
        var (editor, _, support) = CreateEditor(IndentedLine);
        support.IsInstalled.Should().BeTrue("TextView._formatterの差し替えに成功しているはず");

        var visualLine = FirstVisualLine(editor);
        visualLine.TextLines.Count.Should().BeGreaterThan(1, "この幅では必ず折り返すはず");

        var xs = LineStartXPositions(visualLine);
        _output.WriteLine($"各段の先頭X: [{string.Join(", ", xs.Select(x => x.ToString("F2")))}]");

        xs[0].Should().Be(0, "1行目は行頭から始まる");
        for (var i = 1; i < xs.Count; i++)
        {
            xs[i].Should().BeGreaterThan(0, "2行目以降は字下げされているはず");
        }

        // 1行目の字下げ（行頭8個の空白）の幅と一致していること。
        var indentWidth = visualLine.TextLines[0].GetDistanceFromCharacterHit(new CharacterHit(8, 0));
        xs[1].Should().BeApproximately(indentWidth, 0.5, "1行目のインデント幅と同じ位置から始まるはず");

        // 幅を狭めているので、字下げしても右端をはみ出さない。
        var available = editor.TextArea.TextView.Bounds.Width;
        foreach (var textLine in visualLine.TextLines)
        {
            textLine.WidthIncludingTrailingWhitespace.Should().BeLessThanOrEqualTo(available + 1,
                "折り返し幅を字下げ量ぶん狭めているため、はみ出さないはず");
        }
    }

    [AvaloniaFact(DisplayName = "課題#72: タブ字下げ＋空白の可視化（整形の再入）でも字下げが効く")]
    public void タブ字下げと空白可視化でも字下げが効く()
    {
        // ShowSpaces/ShowTabs/ShowEndOfLineをオンにすると、AvaloniaEditの
        // FormattedTextElement.PrepareTextが整形の最中にFormatLineを再入呼び出しする。
        // 番人（WrapIndentTextFormatter._formatting）が無いとここで段落の状態が壊れる。
        var (editor, _, _) = CreateEditor(
            "\t\tvar result = SomeVeryLongMethodName(argument1, argument2, argument3, argument4, argument5);",
            configure: e =>
            {
                e.Options.ShowSpaces = true;
                e.Options.ShowTabs = true;
                e.Options.ShowEndOfLine = true;
            });

        var visualLine = FirstVisualLine(editor);
        visualLine.TextLines.Count.Should().BeGreaterThan(1);

        var xs = LineStartXPositions(visualLine);
        _output.WriteLine($"各段の先頭X: [{string.Join(", ", xs.Select(x => x.ToString("F2")))}]");
        xs[1].Should().BeGreaterThan(0, "タブ2つぶんの字下げが継承されているはず");
    }

    [AvaloniaFact(DisplayName = "課題#72: ヒットテスト（X座標→列）が字下げ後の座標と一致する")]
    public void ヒットテストが字下げ後の座標と一致する()
    {
        var (editor, _, _) = CreateEditor(IndentedLine);

        var visualLine = FirstVisualLine(editor);
        var second = visualLine.TextLines[1];
        var startColumn = visualLine.GetTextLineVisualStartColumn(second);

        for (var column = startColumn; column < startColumn + Math.Min(10, second.Length); column++)
        {
            var x = visualLine.GetTextLineVisualXPosition(second, column);
            var back = visualLine.GetVisualColumn(second, x + 0.1, allowVirtualSpace: false);
            back.Should().Be(column, $"列{column}のX={x:F2}から逆算した列が一致すること");
        }
    }

    [AvaloniaFact(DisplayName = "課題#72: 選択範囲の矩形も字下げに追従する")]
    public void 選択範囲の矩形も字下げに追従する()
    {
        var (editor, _, _) = CreateEditor(IndentedLine);
        var textView = editor.TextArea.TextView;

        var visualLine = FirstVisualLine(editor);
        var second = visualLine.TextLines[1];
        var startColumn = visualLine.GetTextLineVisualStartColumn(second);
        var x = visualLine.GetTextLineVisualXPosition(second, startColumn);

        var startOffset = visualLine.StartOffset + visualLine.GetRelativeOffset(startColumn);
        var endOffset = visualLine.StartOffset + visualLine.GetRelativeOffset(startColumn + 5);
        var rects = BackgroundGeometryBuilder
            .GetRectsForSegment(textView, new TextSegment { StartOffset = startOffset, EndOffset = endOffset })
            .ToList();

        _output.WriteLine($"字下げX={x:F2}, 選択矩形=[{string.Join(", ", rects.Select(r => $"({r.X:F2},{r.Width:F2})"))}]");
        rects.Should().NotBeEmpty();
        rects[0].X.Should().BeApproximately(x, 0.5,
            "BackgroundGeometryBuilderはTextBounds.Rectangleを使うため、"
            + "WrapIndentTextLine.GetTextBoundsでずらした結果が反映されているはず");
    }

    [AvaloniaFact(DisplayName = "課題#72: キャレットの位置も字下げに追従する")]
    public void キャレットの位置も字下げに追従する()
    {
        var (editor, window, _) = CreateEditor(IndentedLine);
        var textView = editor.TextArea.TextView;

        var visualLine = FirstVisualLine(editor);
        var second = visualLine.TextLines[1];
        var startColumn = visualLine.GetTextLineVisualStartColumn(second);
        var expectedX = visualLine.GetTextLineVisualXPosition(second, startColumn);

        // 折り返し2行目の先頭へキャレットを置く。
        var offset = visualLine.StartOffset + visualLine.GetRelativeOffset(startColumn);
        editor.TextArea.Caret.Offset = offset;
        window.CaptureRenderedFrame().Should().NotBeNull();

        var caretPosition = editor.TextArea.Caret.CalculateCaretRectangle();
        _output.WriteLine($"字下げX={expectedX:F2}, キャレット矩形={caretPosition}");
        (caretPosition.X + textView.ScrollOffset.X).Should().BeApproximately(expectedX, 0.5);
    }

    [AvaloniaFact(DisplayName = "課題#72: 折り返しが無効なときは行を一切包まない（従来どおり）")]
    public void 折り返し無効なら包まない()
    {
        var (editor, _, support) = CreateEditor(IndentedLine, wordWrap: false);
        support.IsInstalled.Should().BeTrue();

        var visualLine = FirstVisualLine(editor);
        visualLine.TextLines.Count.Should().Be(1, "折り返しが無効なら段は1つだけ");
        visualLine.TextLines[0].Should().NotBeOfType<WrapIndentTextLine>(
            "折り返しが無効なときはFormatLineがpreviousLineBreak==nullでしか呼ばれず、"
            + "ぶら下げインデントの計算も包み込みも一切走らない");
    }

    [AvaloniaFact(DisplayName = "課題#72: 文書を差し替えても字下げが効いたままになる（TextViewが整形器を作り直すため）")]
    public void 文書を差し替えても入れ直される()
    {
        var (editor, window, support) = CreateEditor(IndentedLine);
        support.IsInstalled.Should().BeTrue();

        // TextView.OnDocumentChangedは新しい文書のたび _formatter を素の整形器で上書きする。
        // WrapIndentSupportがTextView.DocumentChangedを購読して入れ直しているはず。
        editor.Document = new TextDocument(IndentedLine);
        window.CaptureRenderedFrame().Should().NotBeNull();

        support.IsInstalled.Should().BeTrue("文書の差し替え後も差し替えが維持されているはず");
        LineStartXPositions(FirstVisualLine(editor))[1].Should().BeGreaterThan(0);
    }

    [AvaloniaFact(DisplayName = "課題#72: 編集して行が組み立て直されても字下げが保たれる")]
    public void 編集後も字下げが保たれる()
    {
        // 編集のたびにVisualLineは破棄され、整形も一からやり直される。整形器が段落ごとの
        // 状態（1行目のTextLine・字下げ量）を持つ設計のため、前の行の状態が残って
        // 誤った字下げにならないことを確かめる。
        var (editor, window, _) = CreateEditor(IndentedLine);
        var before = LineStartXPositions(FirstVisualLine(editor))[1];
        before.Should().BeGreaterThan(0);

        // 行頭へさらに4つ空白を足す＝字下げが深くなる。
        editor.Document.Insert(0, "    ");
        window.CaptureRenderedFrame().Should().NotBeNull();

        var after = LineStartXPositions(FirstVisualLine(editor))[1];
        _output.WriteLine($"編集前の継続行X={before:F2} → 編集後={after:F2}");
        after.Should().BeGreaterThan(before, "字下げが深くなったぶん継続行の開始位置も右へ動くはず");

        // 逆に字下げを全て削ると、継続行も行頭から始まるようになる。
        editor.Document.Remove(0, 12);
        window.CaptureRenderedFrame().Should().NotBeNull();
        LineStartXPositions(FirstVisualLine(editor))[1].Should().Be(0,
            "字下げが無くなれば継続行も行頭から始まる（前の状態が残っていないこと）");
    }

    [AvaloniaFact(DisplayName = "課題#72: リフレクションに失敗しても例外を投げず、字下げなしで動き続ける（縮退）")]
    public async Task リフレクションに失敗しても縮退して動く()
    {
        var appPaths = new AppPaths(_baseDirectory);
        appPaths.EnsureCoreDirectoriesExist();
        var logger = new Logger(appPaths, autoCleanupOnStart: false);

        var editor = new TextEditor
        {
            Document = new TextDocument(IndentedLine),
            WordWrap = true,
            FontFamily = FontFamily.Default,
            FontSize = 14,
        };

        // 将来AvaloniaEditが _formatter の名前・型を変えた状況を再現する。
        var support = new WrapIndentSupport(editor, "_formatterThatDoesNotExist");
        support.Logger = logger;
        support.IsInstalled.Should().BeFalse("フィールドが見つからないので何も差し替えられていないはず");

        var window = _windows.Track(new Window { Width = 320, Height = 200, Content = editor });
        window.Show();
        window.CaptureRenderedFrame().Should().NotBeNull("字下げは効かなくても描画は通常どおり行えること");

        // 折り返し自体は従来どおり効き、2行目は行頭（X=0）から始まる。
        var visualLine = FirstVisualLine(editor);
        visualLine.TextLines.Count.Should().BeGreaterThan(1);
        LineStartXPositions(visualLine)[1].Should().Be(0, "縮退時は従来どおり字下げなしで表示される");

        // 「いつのまにか効かなくなっていた」ことに気付けるよう、ログへ1回だけ記録している。
        await logger.DisposeAsync();
        var logText = await File.ReadAllTextAsync(appPaths.GetLogFilePath(DateOnly.FromDateTime(DateTime.Now)));
        logText.Should().Contain("wrap-indent-install");
        logText.Should().Contain("_formatterThatDoesNotExist");
    }

    [AvaloniaFact(DisplayName = "課題#72: 字下げがエディタ幅の半分を超える場合は字下げしない（AvaloniaEdit本来の安全弁）")]
    public void 幅の半分を超える字下げは適用しない()
    {
        // 極端に深い字下げ（空白60個）＋狭いウィンドウ。
        var text = new string(' ', 60) + "value = Compute(alpha, bravo, charlie, delta, echo, foxtrot, golf);";
        var (editor, _, _) = CreateEditor(text, width: 260);

        var visualLine = FirstVisualLine(editor);
        visualLine.TextLines.Count.Should().BeGreaterThan(1);
        LineStartXPositions(visualLine)[1].Should().Be(0,
            "字下げがエディタ幅の半分以上になる場合、折り返し後に表示できる文字が極端に減るため"
            + "字下げを捨てる（AvaloniaEdit TextView.cs 1121行と同じ判断）");
    }

    [AvaloniaFact(DisplayName = "課題#72: 右横書き（RTL）を含む行では字下げせず従来どおり表示する（安全側の縮退）")]
    public void 右横書きを含む行では字下げしない()
    {
        // AvaloniaEditの段落は常に左横書き固定（VisualLineTextParagraphPropertiesが
        // FlowDirection.LeftToRight・TextAlignment.Leftを返す実装）だが、行の中にRTLの
        // 文字列があると字形が視覚的に並べ替えられ、「行頭の空白の幅」を
        // GetDistanceFromCharacterHitで測る前提そのものが崩れる。実測でも
        // TextView幅312pxの行に対しインデント幅として246pxという誤った値が返っていた
        // （WrapIndentTextFormatter.ComputeIndentのコメント参照）。
        // そのためRTLを含む行では字下げを適用しない、という安全側の縮退を選んだ。
        const string rightToLeft =
            "        שלום עולם זהו טקסט ארוך מאוד בעברית שאמור להיות עטוף לשורות רבות בעורך הזה";
        var (editor, _, _) = CreateEditor(rightToLeft);

        var visualLine = FirstVisualLine(editor);
        visualLine.TextLines.Count.Should().BeGreaterThan(1, "この幅では折り返すはず");

        var available = editor.TextArea.TextView.Bounds.Width;
        foreach (var textLine in visualLine.TextLines)
        {
            _output.WriteLine($"  TextLine: 型={textLine.GetType().Name} Start={textLine.Start:F2} "
                + $"Width={textLine.Width:F2} Len={textLine.Length}");
            textLine.Should().NotBeOfType<WrapIndentTextLine>("RTLを含む行では字下げを適用しない");
            textLine.Start.Should().Be(0);
            textLine.WidthIncludingTrailingWhitespace.Should().BeLessThanOrEqualTo(available + 1);
        }

        // 縮退していても、ヒットテストは例外を投げず行内の列を返せること。
        var second = visualLine.TextLines[1];
        var startColumn = visualLine.GetTextLineVisualStartColumn(second);
        var column = visualLine.GetVisualColumn(second, 1.0, allowVirtualSpace: false);
        column.Should().BeInRange(startColumn, startColumn + second.Length);
    }

    [AvaloniaFact(DisplayName = "課題#72: 左横書きの行に右横書きの語が混ざっている場合も字下げしない")]
    public void 双方向テキストが混ざる行でも字下げしない()
    {
        // コード中の文字列リテラルにヘブライ語が入っている、といった現実的なケース。
        // 判定は「1行目にRTLの字形範囲が1つでもあるか」（ShapedTextRun.BidiLevelが奇数）
        // という粗いものなので、この行も字下げの対象外になる。過剰に見えるが、
        // 誤った位置へ字下げするより「従来どおり」の方が害が小さいという判断。
        var (editor, _, _) = CreateEditor(
            "        var message = \"שלום עולם\" + suffix + AnotherVeryLongIdentifierName + Trailing;");

        var visualLine = FirstVisualLine(editor);
        visualLine.TextLines.Count.Should().BeGreaterThan(1);
        visualLine.TextLines[1].Should().NotBeOfType<WrapIndentTextLine>();
    }

    [AvaloniaFact(DisplayName = "課題#72: インデントガイド（縦線）と折り返し継続行の字下げが衝突しない")]
    public void インデントガイドと衝突しない()
    {
        // IndentGuideRenderer.DrawAllIndentationLevelsは、行の実インデント列より「手前」の
        // 各レベル（列0・tabSize・2*tabSize…）へ、そのVisualLineの高さ全体（＝折り返しの
        // 全段ぶん）に渡って縦線を引く（IndentGuideRenderer参照。columnWidthは
        // TextView.WideSpaceWidth）。本機能を入れると継続行の本文は行の実インデント位置から
        // 始まるため、縦線の上へ文字が重なることが無くなる（従来はX=0から書き始めていたので
        // 継続行が縦線を横切っていた）。ここではその位置関係が保たれていることを確かめる。
        var (editor, _, _) = CreateEditor(IndentedLine);
        var textView = editor.TextArea.TextView;

        var visualLine = FirstVisualLine(editor);
        var continuationX = LineStartXPositions(visualLine)[1];

        var tabSize = editor.Options.IndentationSize;
        var indentColumn = IndentGuideCalculator.LeadingWhitespaceVisualColumn(IndentedLine, tabSize);
        var levelCount = IndentGuideCalculator.LevelCount(indentColumn, tabSize);
        levelCount.Should().BeGreaterThan(0, "空白8個・タブ幅4なので2本の縦線が引かれるはず");

        for (var level = 0; level < levelCount; level++)
        {
            var guideX = level * tabSize * textView.WideSpaceWidth;
            _output.WriteLine($"  縦線{level}: X={guideX:F2} / 継続行の開始X={continuationX:F2}");
            guideX.Should().BeLessThan(continuationX,
                "縦線は継続行の本文より必ず左側にあり、文字と重ならないこと");
        }
    }

    [AvaloniaFact(DisplayName = "課題#72: 実際の描画でも折り返し2行目の最左描画列が1行目と一致する")]
    public void 実際の描画で最左描画列が一致する()
    {
        var (editor, window, _) = CreateEditor(IndentedLine, width: 320);
        using var frame = window.CaptureRenderedFrame();
        frame.Should().NotBeNull();

        var visualLine = FirstVisualLine(editor);
        visualLine.TextLines.Count.Should().BeGreaterThan(1);

        // 1行目・2行目それぞれの縦位置（行の中央付近）を求め、その帯の中で
        // 背景色と異なる画素が最初に現れるX座標を比べる。
        var lineHeight = visualLine.TextLines[0].Height;
        var firstBand = ((int)(lineHeight * 0.2), (int)(lineHeight * 0.9));
        var secondBand = ((int)(lineHeight * 1.2), (int)(lineHeight * 1.9));

        var pixels = ReadPixels(frame!);
        var firstLeft = LeftmostDrawnColumn(pixels, firstBand.Item1, firstBand.Item2);
        var secondLeft = LeftmostDrawnColumn(pixels, secondBand.Item1, secondBand.Item2);

        _output.WriteLine($"1行目の最左描画列={firstLeft}, 2行目の最左描画列={secondLeft}");
        firstLeft.Should().BeGreaterThan(0, "1行目は空白8個ぶん字下げされている");
        secondLeft.Should().BeGreaterThan(0, "2行目も字下げされて描かれているはず");
        // 描画は字体のサイドベアリング（文字ごとの左右の余白）ぶんだけ前後するため、
        // 数画素の差は許容する。字下げが効いていなければ差は数十画素になる。
        Math.Abs(firstLeft - secondLeft).Should().BeLessThanOrEqualTo(4,
            "折り返し2行目の描画開始位置が1行目のインデント位置とほぼ一致すること");
    }

    /// <summary>
    /// EditorPaneの実配線（<see cref="Graft.Views.EditorPane"/>のコンストラクタで
    /// <see cref="WrapIndentSupport"/>を構築している）が効いていること、かつタブ切替
    /// （<c>Editor.Document</c>の差し替え）を経ても外れないことを確認する。
    /// </summary>
    [AvaloniaFact(DisplayName = "課題#72: EditorPaneのエディタでも有効で、文書の差し替えを経ても外れない")]
    public void EditorPaneのエディタでも有効になっている()
    {
        var pane = new Graft.Views.EditorPane();
        var textEditor = pane.GetControl<TextEditor>("Editor");
        var window = _windows.Track(new Window { Width = 400, Height = 300, Content = pane });
        window.Show();
        window.CaptureRenderedFrame().Should().NotBeNull();

        CurrentFormatter(textEditor).Should().BeOfType<WrapIndentTextFormatter>(
            "EditorPaneのコンストラクタでWrapIndentSupportを構築しているはず");

        textEditor.Document = new TextDocument(IndentedLine);
        window.CaptureRenderedFrame().Should().NotBeNull();
        CurrentFormatter(textEditor).Should().BeOfType<WrapIndentTextFormatter>(
            "文書の差し替え（タブ切替）でTextViewが整形器を作り直しても入れ直されるはず");
    }

    /// <summary>テストからだけ使う、<c>TextView._formatter</c>の現在値の覗き見。</summary>
    private static object? CurrentFormatter(TextEditor editor)
        => typeof(TextView)
            .GetField("_formatter", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(editor.TextArea.TextView);

    /// <summary>描画結果を「1画素＝int」の2次元配列として読み出す。</summary>
    private static int[,] ReadPixels(WriteableBitmap bitmap)
    {
        using var framebuffer = bitmap.Lock();
        var width = framebuffer.Size.Width;
        var height = framebuffer.Size.Height;
        var pixels = new int[height, width];
        var row = new int[width];
        for (var y = 0; y < height; y++)
        {
            Marshal.Copy(framebuffer.Address + (y * framebuffer.RowBytes), row, 0, width);
            for (var x = 0; x < width; x++) pixels[y, x] = row[x];
        }
        return pixels;
    }

    /// <summary>
    /// 指定した縦の帯の中で、背景色（右下隅の画素の色）と異なる画素が最初に現れるX座標を返す。
    /// 見つからなければ-1。
    /// </summary>
    private static int LeftmostDrawnColumn(int[,] pixels, int topY, int bottomY)
    {
        var height = pixels.GetLength(0);
        var width = pixels.GetLength(1);
        var background = pixels[height - 2, width - 2];

        var leftmost = -1;
        for (var y = Math.Max(0, topY); y <= Math.Min(height - 1, bottomY); y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (pixels[y, x] == background) continue;
                if (leftmost < 0 || x < leftmost) leftmost = x;
                break;
            }
        }
        return leftmost;
    }
}
