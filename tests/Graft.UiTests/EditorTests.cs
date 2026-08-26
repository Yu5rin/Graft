using System.Runtime.CompilerServices;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using FluentAssertions;
using Graft.Core;
using Graft.Editor;
using Graft.Features;
using Graft.Themes;
using Graft.UiTests.TestSupport;

namespace Graft.UiTests;

/// <summary>
/// エディタ層（フェーズL3: AvalonEdit→AvaloniaEdit移植）の検証テスト（仕様書v2.1 18章・
/// 附録A.7）。<c>src/Graft/Editor/</c> の各クラスが例外なく構築・描画でき、
/// 4.5節のエンコーディング・改行保持と4.1節のシンタックスハイライト接続が
/// v2.0のWPF版と同じ挙動を保つことを検証する。代表画面のスクリーンショットも保存する。
/// </summary>
public class EditorTests : IDisposable
{
    // 課題（CIで不定期に再発する「Unable to locate 'Avalonia.Platform.IFontManagerImpl'」）:
    // 本クラスはAvaloniaEditのTextEditor（TextViewを内包）を載せたWindowをShow()するテストを
    // 複数持つが、以前はどれもClose()せず、ShownWindowTrackerにも乗せていなかった
    // （閉じ忘れの実例。TestSupport/ShownWindowTracker.cs参照）。他のシナリオテストと
    // 同じ後始末に揃える。
    private readonly ShownWindowTracker _windows = new();

    public void Dispose()
    {
        _windows.Dispose();
        GC.SuppressFinalize(this);
    }

    [AvaloniaFact(DisplayName = "TextEditorを含むウィンドウを構築して描画しても例外が出ない")]
    public void TextEditorを含むウィンドウを構築して描画できる()
    {
        ThemeManager.SetTheme(AppTheme.Dark);
        var (window, editor) = CreateEditorWindow();
        editor.Document = new TextDocument("こんにちは\nGraft\n");
        window.Show();

        using var frame = window.CaptureRenderedFrame();
        frame.Should().NotBeNull("リソース解決に失敗すると描画そのものができない");
        SaveScreenshot(window, "editor-basic.png");
    }

    [AvaloniaFact(DisplayName = "UTF-8のBOM有無とCRLF/LFの組み合わせを保持したまま編集・保存できる")]
    public async Task エンコーディングと改行が編集保存後も保持される()
    {
        foreach (var hasBom in new[] { true, false })
        {
            foreach (var newLine in new[] { "\r\n", "\n" })
            {
                await AssertRoundTripAsync(hasBom, newLine);
            }
        }
    }

    [AvaloniaFact(DisplayName = "バイナリファイルを開こうとするとE703で失敗する")]
    public async Task バイナリファイルを開こうとするとE703になる()
    {
        var path = Path.Combine(Path.GetTempPath(), $"graft-bin-{Guid.NewGuid():N}.dat");
        await File.WriteAllBytesAsync(path, new byte[] { 0x00, 0x01, 0x02, 0x03, 0xFF, 0xFE });
        try
        {
            var result = await DocumentSession.OpenAsync(path, projectRoot: string.Empty);
            result.IsSuccess.Should().BeFalse("NULバイトを含むファイルはバイナリ判定される必要がある");
            result.Issues.Should().Contain(i => i.Code == ErrorCode.E703);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact(DisplayName = "自前レキサのカラライザが数言語で例外なく動作する")]
    public void シンタックスハイライトのカラライザが例外なく動作する()
    {
        ThemeManager.SetTheme(AppTheme.Dark);
        var samples = new (string Extension, string Text)[]
        {
            ("py", "def foo(x):\n    # コメント\n    return x + 1\n"),
            ("cs", "public class Foo\n{\n    // comment\n    int X = 1;\n}\n"),
            ("json", "{\n  \"a\": 1,\n  \"b\": \"text\"\n}\n"),
            ("html", "<html>\n  <!-- comment -->\n  <body>hi</body>\n</html>\n"),
            ("sh", "#!/bin/bash\n# comment\necho \"hi\"\n"),
            ("md", "# 見出し\n\n本文です。\n"),
        };

        var (window, editor) = CreateEditorWindow();
        using var bridge = new SyntaxHighlightBridge(editor);
        editor.TextArea.TextView.LineTransformers.Add(bridge);
        window.Show();

        foreach (var (extension, text) in samples)
        {
            var document = new TextDocument(text);
            editor.Document = document;
            bridge.Attach(document, extension, syntaxEnabled: true);
            var act = () => window.CaptureRenderedFrame()?.Dispose();
            act.Should().NotThrow($"拡張子 '{extension}' のカラライズで例外が出てはならない");
        }

        SaveScreenshot(window, "editor-syntax.png");
    }

    [AvaloniaFact(DisplayName = "行の複製・移動・削除がAvaloniaEdit版でも正しく動く")]
    public void 行の複製移動削除が正しく動作する()
    {
        var (_, editor) = CreateEditorWindow();
        editor.Document = new TextDocument("a\nb\nc\n");

        // 複製: 2行目「b」を複製して a,b,b,c にする。
        editor.TextArea.Caret.Line = 2;
        EditorCommands.DuplicateLines(editor);
        editor.Document.Text.Should().Be("a\nb\nb\nc\n", "カーソル行が直下に複製されるはず");

        // 上へ移動: 4行目「c」を1つ上へ動かして a,b,c,b にする。
        // 複製直後は2行目と3行目が同じ「b」のため、区別できる4行目を対象にする。
        editor.TextArea.Caret.Line = 4;
        EditorCommands.MoveLinesUp(editor);
        editor.Document.Text.Should().Be("a\nb\nc\nb\n", "カーソル行が1つ上の行と入れ替わるはず");

        // 削除: 1行目「a」を消す。
        editor.TextArea.Caret.Line = 1;
        EditorCommands.DeleteLines(editor);
        editor.Document.Text.Should().Be("b\nc\nb\n", "カーソル行だけが取り除かれるはず");
    }

    [AvaloniaFact(DisplayName = "Ctrl+/相当のコメント切替が言語ルールの記号で行われる")]
    public void コメント切替が言語ルールの記号で行われる()
    {
        var (_, editor) = CreateEditorWindow();
        editor.Document = new TextDocument("print(1)\n");
        var rule = SyntaxLexer.RuleForExtension("py");

        EditorCommands.ToggleLineComment(editor, rule);
        editor.Document.Text.Should().StartWith("#");

        EditorCommands.ToggleLineComment(editor, rule);
        editor.Document.Text.Should().Be("print(1)\n");
    }

    [AvaloniaFact(DisplayName = "括弧を入力すると自動で閉じ括弧が挿入される")]
    public void 括弧の自動対応が動作する()
    {
        var (window, editor) = CreateEditorWindow();
        var document = new TextDocument(string.Empty);
        editor.Document = document;
        window.Show();

        using var brackets = new BracketSupport(editor);
        brackets.Attach(document, "py");

        // headless環境では KeyTextInput がフォーカス経路の都合でTextAreaまで届かないため、
        // 実際の入力と同じ TextInput イベントを TextArea へ直接発生させて配線を検証する。
        TypeText(editor, "(");
        document.Text.Should().Be("()", "自動閉じ括弧が挿入される必要がある");
    }

    /// <summary>
    /// エディタへ文字入力を発生させる。AvaloniaEdit は TextArea の TextInput を購読して
    /// 実際の挿入を行うため、そこへ直接イベントを送る。
    /// </summary>
    private static void TypeText(TextEditor editor, string text)
        => editor.TextArea.RaiseEvent(new Avalonia.Input.TextInputEventArgs
        {
            RoutedEvent = Avalonia.Input.InputElement.TextInputEvent,
            Text = text,
        });

    [AvaloniaFact(DisplayName = "折りたたみサポートを取り付けても例外が出ない")]
    public void 折りたたみサポートが例外なく動作する()
    {
        var (window, editor) = CreateEditorWindow();
        var document = new TextDocument("if x:\n    a = 1\n    b = 2\nprint(a)\n");
        editor.Document = document;
        window.Show();

        using var folding = new FoldingSupport(editor);
        var act = () => folding.Attach(document, "py");
        act.Should().NotThrow();

        folding.SetEnabled(false);
        folding.SetEnabled(true);
    }

    /// <summary>
    /// 不具合1の回帰テスト。Windows実機で
    /// <c>System.ArgumentException: Invalid document at AvaloniaEdit.Folding.
    /// FoldingElementGenerator.StartGeneration</c> が未処理のままアプリごと落ちた不具合の再現。
    ///
    /// EditorPane.axaml.cs ApplyDocumentTabと同じ順序（<c>Editor.Document</c>を先に差し替えてから
    /// <see cref="FoldingSupport.Attach(TextDocument, string)"/>を呼ぶ）を再現し、その間に
    /// 描画パスが割り込むケースを検証する。<see cref="FoldingSupport"/>のクラスコメント（不具合1）
    /// のとおり修正前のコードでは実際にこのテストが
    /// <c>System.ArgumentException: Invalid document</c>で失敗することを確認済み。
    /// </summary>
    [AvaloniaFact(DisplayName = "不具合1回帰: 文書差し替えと折りたたみ再接続の間に描画が割り込んでも例外が出ない")]
    public void 文書差し替え後の折りたたみ更新で例外が出ない()
    {
        var (window, editor) = CreateEditorWindow();
        var docA = new TextDocument("if x:\n    a = 1\n    b = 2\nprint(a)\n");
        editor.Document = docA;
        window.Show();

        using var folding = new FoldingSupport(editor);
        folding.Attach(docA, "py");
        using (window.CaptureRenderedFrame()) { }

        var docB = new TextDocument("if y:\n    c = 3\n    d = 4\nprint(c)\n");
        editor.Document = docB; // EditorPane.axaml.cs ApplyDocumentTabと同じ順序（先にDocumentを差し替え）

        // folding.Attach(docB, ...)より前に描画パスが割り込んでも、古いFoldingManagerが
        // 残っていてInvalid documentが飛んではならない。
        var act = () => window.CaptureRenderedFrame()?.Dispose();
        act.Should().NotThrow("文書差し替えと折りたたみ再接続の間で描画が走っても落ちてはならない");

        folding.Attach(docB, "py");
        act.Should().NotThrow();
    }

    /// <summary>
    /// 課題#82の入口特定テスト（機序の実証）。<see cref="FoldingSupport"/>クラスコメントの
    /// 【課題#82】節で辿った経路（<c>Editor.Document</c>代入 → <c>TextArea.Document</c> →
    /// <c>TextView.Document</c>の順に切り替わったあと、<c>Caret.Location</c>のリセットで
    /// <c>Caret.PositionChanged</c>が同期発火し、<c>TextEditor.DocumentChanged</c>（本クラスが
    /// 古いFoldingManagerをuninstallする契機）はまだ発火していない）を、実際の
    /// <c>Editor.Document = docB</c>代入を通して確認する。
    ///
    /// <c>Caret.PositionChanged</c>のハンドラの中で<c>Dispatcher.UIThread.RunJobs()</c>を呼び、
    /// <c>TextView.Document</c>切り替え時に<c>InvalidateMeasure()</c>で積まれた保留中のRender
    /// ジョブを即座に処理させる。これは実機（Windows）でIME通知・フォーカス変更・
    /// アクセシビリティ通知等に伴うネイティブメッセージポンプが入れ子で呼ばれ、同じ
    /// タイミングで保留中の描画が処理されてしまう状況を、プラットフォーム非依存に
    /// headlessで模擬したもの。<see cref="FoldingSupport.PrepareForDocumentSwap"/>による
    /// 対処2を入れる前のコード（コンストラクタでの<c>TextEditor.DocumentChanged</c>購読のみ）
    /// では、このテストが実際に<c>System.ArgumentException: Invalid document</c>で失敗する
    /// ことを確認済み（本テストはその機序を固定するため、意図的にPrepareForDocumentSwapを
    /// 呼ばない）。
    ///
    /// 【課題#73対応に伴う書き換え】 課題#73（<see cref="FoldingSupport"/>クラスコメントの
    /// 【課題#73】節）で「1つも畳まれていない間は<c>FoldingElementGenerator</c>を
    /// <c>TextView.ElementGenerators</c>から外す」ようにしたため、<b>何も畳んでいない状態では
    /// この再現が成立しなくなった</b>（<c>ArgumentException("Invalid document")</c>の投擲箇所は
    /// AvaloniaEdit全ソース中で<c>Folding/FoldingElementGenerator.StartGeneration</c>の1箇所
    /// だけであり、生成器が外れていればそこを通らない）。
    /// テストの検証力を弱めないため、<b>先に1つ畳んで生成器が付いた状態を作ってから</b>同じ
    /// 再入を起こす形へ書き換えてある。これにより、課題#82の修正（<see cref="FoldingSupport.
    /// PrepareForDocumentSwap"/>）が引き続き効いていることを検証する力（下の
    /// 「PrepareForDocumentSwapを先に呼べば…」との対）はそのまま保たれる。
    /// 「何も畳んでいない通常状態ではこの経路自体を通らない」という課題#73の副次的な効果は、
    /// 別テスト（<see cref="何も畳んでいない通常状態では課題82の経路を通らない"/>）で守る。
    /// </summary>
    [AvaloniaFact(DisplayName = "課題#82入口特定: 折りたたみ中に文書代入するとキャレットのPositionChanged経由の再入で、対処1だけではInvalid documentが出る")]
    public void 文書代入中のキャレットPositionChanged経由の再入でInvalid_documentが起きる()
    {
        var (window, editor) = CreateEditorWindow();
        var docA = new TextDocument("if x:\n    a = 1\n    b = 2\nprint(a)\n");
        editor.Document = docA;
        window.Show();

        using var folding = new FoldingSupport(editor);
        folding.Attach(docA, "py");
        using (window.CaptureRenderedFrame()) { }

        // 課題#73: 1つ畳んで、FoldingElementGeneratorが取り付けられた状態にする
        // （＝Invalid documentを投げうる唯一の箇所を通る状態）。インデントベースの折りたたみ
        // では「if x:」行の末尾から「    b = 2」行の末尾までが1つの範囲になるため、その内側の
        // オフセット（2行目の先頭）を指定する。
        folding.FoldRecursiveAt(docA.GetLineByNumber(2).Offset);
        folding.Manager!.AllFoldings.Any(fs => fs.IsFolded).Should().BeTrue(
            "再現には折りたたみ生成器が取り付けられた状態が必要なため、実際に1つ畳めている必要がある");
        FoldingGeneratorCount(editor).Should().Be(1, "畳んだ以上、生成器は取り付けられているはず");

        // Caret.Location = (1,1) の代入がPositionChangedを発火させるには、代入前のキャレット位置が
        // (1,1)以外である必要がある（Caret.PositionのsetterはTextViewPosition比較で早期returnする。
        // AvaloniaEdit 11.1.0のEditing/Caret.csで確認済み）。タブ切替では元のタブのカーソル行が
        // 1行目でない限り、この条件は実際の利用でごく普通に成立する。
        // 畳んだ範囲（2〜3行目）の内側へ動かすとAvaloniaEditが自動で展開してしまう
        // （FoldingManagerInstallation.TextArea_Caret_PositionChanged）ため、範囲外の4行目にする。
        editor.TextArea.Caret.Line = 4;

        var docB = new TextDocument("if y:\n    c = 3\n    d = 4\nprint(c)\n");

        void ReentrantRenderViaNestedDispatch(object? s, EventArgs e) => Dispatcher.UIThread.RunJobs();
        editor.TextArea.Caret.PositionChanged += ReentrantRenderViaNestedDispatch;
        try
        {
            // folding.PrepareForDocumentSwap()を意図的に呼ばない（対処1のみの状態を再現するため）。
            var act = () => editor.Document = docB;
            act.Should().Throw<ArgumentException>()
                .WithMessage("Invalid document")
                .Where(ex => ex.Source == "AvaloniaEdit",
                    "App.axaml.csのAvaloniaEditExceptionGuardが継続を許可する条件と同じ発生元であるはず");
        }
        finally
        {
            editor.TextArea.Caret.PositionChanged -= ReentrantRenderViaNestedDispatch;
        }
    }

    /// <summary>
    /// 課題#73の副次的な安全性向上を守るテスト。上のテスト（課題#82入口特定）と条件を1つだけ
    /// 変えて——<b>何も畳まない</b>で——同じ再入を起こし、<c>Invalid document</c>が
    /// そもそも発生しないことを確認する。
    ///
    /// 課題#73の対処により、1つも畳まれていない間は<c>FoldingElementGenerator</c>が
    /// <c>TextView.ElementGenerators</c>から外れている。<c>ArgumentException("Invalid document")</c>を
    /// 投げるのはAvaloniaEdit全ソース中で<c>Folding/FoldingElementGenerator.StartGeneration</c>の
    /// 1箇所だけなので、外れている＝その経路自体を通らない。つまり利用者が実際にどこかを
    /// 畳んでいない限り（＝ほとんどの時間）、課題#82の窓は開かない。
    /// <see cref="FoldingSupport.PrepareForDocumentSwap"/>による対処2の代わりにはならない
    /// （1つでも畳んでいれば従来どおり生成器は付いている）ため、対処2と両方を維持する。
    /// </summary>
    [AvaloniaFact(DisplayName = "課題#73副次効果: 何も畳んでいない通常状態では、同じ再入を起こしても課題#82の経路を通らない")]
    public void 何も畳んでいない通常状態では課題82の経路を通らない()
    {
        var (window, editor) = CreateEditorWindow();
        var docA = new TextDocument("if x:\n    a = 1\n    b = 2\nprint(a)\n");
        editor.Document = docA;
        window.Show();

        using var folding = new FoldingSupport(editor);
        folding.Attach(docA, "py");
        using (window.CaptureRenderedFrame()) { }

        folding.Manager!.AllFoldings.Should().NotBeEmpty("折りたたみ範囲自体は計算されているはず（畳んでいないだけ）");
        folding.Manager.AllFoldings.Should().OnlyContain(fs => !fs.IsFolded, "この時点では1つも畳んでいない");
        FoldingGeneratorCount(editor).Should().Be(0,
            "1つも畳まれていない間は、Invalid documentを投げうる唯一の経路（FoldingElementGenerator）が外れているはず");

        editor.TextArea.Caret.Line = 4;
        var docB = new TextDocument("if y:\n    c = 3\n    d = 4\nprint(c)\n");

        void ReentrantRenderViaNestedDispatch(object? s, EventArgs e) => Dispatcher.UIThread.RunJobs();
        editor.TextArea.Caret.PositionChanged += ReentrantRenderViaNestedDispatch;
        try
        {
            // 上のテストと同じく、意図的にPrepareForDocumentSwapを呼ばない。
            var act = () => editor.Document = docB;
            act.Should().NotThrow(
                "何も畳んでいなければ生成器が外れており、Invalid documentを投げる箇所自体を通らない");
        }
        finally
        {
            editor.TextArea.Caret.PositionChanged -= ReentrantRenderViaNestedDispatch;
        }
    }

    /// <summary>課題#73: <c>TextView.ElementGenerators</c>に折りたたみ生成器がいくつ入っているか
    /// （0または1）。製品コードへテスト専用のAPIを足さずに、公開されているリストを直接数える。</summary>
    private static int FoldingGeneratorCount(TextEditor editor)
        => editor.TextArea.TextView.ElementGenerators.OfType<AvaloniaEdit.Folding.FoldingElementGenerator>().Count();

    /// <summary>
    /// 課題#82の修正確認テスト。上のテストと全く同じ再入（<c>Caret.PositionChanged</c>経由の
    /// ディスパッチャ再入）を起こしても、<see cref="FoldingSupport.PrepareForDocumentSwap"/>を
    /// <c>Editor.Document</c>代入の"前"に呼んでおけば例外が出ないことを確認する
    /// （<see cref="Views.EditorPane.ApplyDocumentTab"/>・<see cref="Views.EditorPane.
    /// ApplyEmptyTab"/>が実際に採用している順序と同じ）。
    ///
    /// 課題#73対応後は、上のテストと同じく<b>先に1つ畳んで</b>から検証する。畳まずに実行すると
    /// 折りたたみ生成器が外れており（課題#73）、<see cref="FoldingSupport.PrepareForDocumentSwap"/>を
    /// 呼ばなくても例外が出ないため、このテストが「対処2が効いていること」を検証できなくなる
    /// （常に成功する空のテストになってしまう）。
    /// </summary>
    [AvaloniaFact(DisplayName = "課題#82修正確認: PrepareForDocumentSwapを先に呼べば同じ再入でも例外が出ない")]
    public void PrepareForDocumentSwapを先に呼べば再入があっても例外が出ない()
    {
        var (window, editor) = CreateEditorWindow();
        var docA = new TextDocument("if x:\n    a = 1\n    b = 2\nprint(a)\n");
        editor.Document = docA;
        window.Show();

        using var folding = new FoldingSupport(editor);
        folding.Attach(docA, "py");
        using (window.CaptureRenderedFrame()) { }

        // 課題#73: 上のテスト（入口特定）と同じ条件を作る。ここを畳まないと、対処2が無くても
        // 例外が出ない状態になってしまい、このテストが何も守らなくなる。
        folding.FoldRecursiveAt(docA.GetLineByNumber(2).Offset);
        FoldingGeneratorCount(editor).Should().Be(1, "対処2の効果を検証するには生成器が付いた状態が必要");

        editor.TextArea.Caret.Line = 4;
        var docB = new TextDocument("if y:\n    c = 3\n    d = 4\nprint(c)\n");

        void ReentrantRenderViaNestedDispatch(object? s, EventArgs e) => Dispatcher.UIThread.RunJobs();
        editor.TextArea.Caret.PositionChanged += ReentrantRenderViaNestedDispatch;
        try
        {
            var act = () =>
            {
                folding.PrepareForDocumentSwap(); // EditorPane.ApplyDocumentTabと同じ順序（代入の前）。
                editor.Document = docB;
            };
            act.Should().NotThrow("代入前にFoldingManagerを外しておけば、代入中のどの再入でも食い違いが生じない");
        }
        finally
        {
            editor.TextArea.Caret.PositionChanged -= ReentrantRenderViaNestedDispatch;
        }

        folding.Attach(docB, "py");
        var render = () => window.CaptureRenderedFrame()?.Dispose();
        render.Should().NotThrow("再接続後の通常描画も引き続き問題なく動作するはず");
    }

    /// <summary>
    /// 不具合1の回帰テスト。再読込（<see cref="DocumentSession.ReloadAsync"/>）は同一の
    /// <see cref="TextDocument"/>インスタンスの<c>Text</c>を書き換えるだけで<c>Editor.Document</c>
    /// 自体は差し替わらないが、これによりデバウンスタイマー経由の再計算が走る。再計算が
    /// 実際に発火（300msのデバウンス後）してから描画しても例外が出ないことを確認する。
    /// </summary>
    [AvaloniaFact(DisplayName = "不具合1回帰: 再読込（同一文書のText差し替え）後の折りたたみ更新でも例外が出ない")]
    public async Task 再読込後の折りたたみ更新で例外が出ない()
    {
        var (window, editor) = CreateEditorWindow();
        var document = new TextDocument("if x:\n    a = 1\nprint(a)\n");
        editor.Document = document;
        window.Show();

        using var folding = new FoldingSupport(editor);
        folding.Attach(document, "py");
        using (window.CaptureRenderedFrame()) { }

        // DocumentSession.ReloadAsyncと同じく、同一インスタンスのTextを書き換える。
        document.Text = "if y:\n    c = 3\n    d = 4\nprint(c)\n";

        // デバウンス（300ms）が発火するまで待ってから描画する。
        await Task.Delay(500);
        var act = () => window.CaptureRenderedFrame()?.Dispose();
        act.Should().NotThrow("再読込後のデバウンス経由の折りたたみ再計算で例外が出てはならない");
    }

    /// <summary>
    /// 不具合1の回帰テスト。編集でデバウンスタイマーが動き出した直後にタブ切替（文書の差し替え）が
    /// 起きても、古い文書向けに開始したタイマーが新しい文書のFoldingManagerへ誤って作用しないこと
    /// （＝タイマーが確実に停止・再作成されること）を確認する。
    /// </summary>
    [AvaloniaFact(DisplayName = "不具合1回帰: デバウンス待ち中に文書を差し替えても古い文書向けの再計算は走らない")]
    public async Task デバウンス待ち中の文書差し替えで古い文書へ再計算しない()
    {
        var (window, editor) = CreateEditorWindow();
        var docA = new TextDocument("if x:\n    a = 1\nprint(a)\n");
        editor.Document = docA;
        window.Show();

        using var folding = new FoldingSupport(editor);
        folding.Attach(docA, "py");
        using (window.CaptureRenderedFrame()) { }

        // docAを編集してデバウンスタイマーを開始させる。
        docA.Insert(docA.TextLength, "\nprint(a)\n");

        // デバウンスが完了する前に、タブ切替を模してdocBへ差し替える。
        var docB = new TextDocument("if y:\n    c = 3\nprint(c)\n");
        editor.Document = docB;
        folding.Attach(docB, "py");

        // 元のデバウンスタイマーが発火するはずだったタイミングまで待っても例外が出ないこと。
        await Task.Delay(500);
        var act = () => window.CaptureRenderedFrame()?.Dispose();
        act.Should().NotThrow("古い文書向けに開始したデバウンスタイマーが新しい文書へ誤って作用してはならない");
    }

    [AvaloniaFact(DisplayName = "単語ベース補完がプレフィックスに一致する候補を提示できる")]
    public void 単語ベース補完が例外なく動作する()
    {
        var (window, editor) = CreateEditorWindow();
        editor.Document = new TextDocument("alpha alp\n");
        editor.CaretOffset = editor.Document.TextLength;
        window.Show();
        editor.Focus();

        var completion = new CompletionProvider(editor);
        var act = () => completion.RequestCompletion();
        act.Should().NotThrow("候補が無い/ある双方のケースで例外を投げてはならない");
    }

    [AvaloniaFact(DisplayName = "Gitガターを組み込んでも非Gitディレクトリで例外が出ない")]
    public async Task Gitガターが非Gitディレクトリで例外なく動作する()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"graft-git-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var (window, editor) = CreateEditorWindow();
            editor.Document = new TextDocument("a\nb\n");
            window.Show();

            using var gutter = new GitGutterProvider(editor, new GitIntegration());
            editor.TextArea.LeftMargins.Insert(0, gutter);
            gutter.SetTarget(dir, "file.txt");
            await gutter.RefreshAsync();

            var act = () => window.CaptureRenderedFrame()?.Dispose();
            act.Should().NotThrow();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [AvaloniaFact(DisplayName = "ファイル監視は開始でき無効なパスではE704として失敗する")]
    public void ファイル監視の開始と失敗時のE704縮退を確認できる()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"graft-watch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            using var watcher = new FileWatchService();
            var ok = watcher.Start(dir);
            ok.IsSuccess.Should().BeTrue("実在するディレクトリでは監視を開始できる必要がある");
            watcher.Stop();

            var invalidPath = Path.Combine(dir, "not-exists", "deeper");
            var failed = watcher.Start(invalidPath);
            failed.IsSuccess.Should().BeFalse("存在しないパスでは監視開始に失敗する必要がある");
            failed.Issues.Should().Contain(i => i.Code == ErrorCode.E704);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static async Task AssertRoundTripAsync(bool hasBom, string newLine)
    {
        var path = Path.Combine(Path.GetTempPath(), $"graft-shape-{Guid.NewGuid():N}.txt");
        var original = $"最初の行{newLine}二行目{newLine}";
        var bytes = BuildBytes(original, hasBom);
        await File.WriteAllBytesAsync(path, bytes);

        try
        {
            var opened = await DocumentSession.OpenAsync(path, projectRoot: string.Empty);
            opened.IsSuccess.Should().BeTrue();
            using var session = opened.Value;
            session.Shape.HasBom.Should().Be(hasBom, $"BOM有無(hasBom={hasBom})が判定と一致する必要がある");
            session.Document.Text.Should().Contain("最初の行").And.Contain("二行目");

            session.Document.Insert(session.Document.TextLength, "追記");
            var saved = await session.SaveAsync();
            saved.IsSuccess.Should().BeTrue();

            var savedBytes = await File.ReadAllBytesAsync(path);
            var startsWithBom = savedBytes.Length >= 3 && savedBytes[0] == 0xEF && savedBytes[1] == 0xBB && savedBytes[2] == 0xBF;
            startsWithBom.Should().Be(hasBom, "保存後もBOMの有無が維持される必要がある");

            var savedText = Encoding.UTF8.GetString(savedBytes, startsWithBom ? 3 : 0, savedBytes.Length - (startsWithBom ? 3 : 0));
            savedText.Should().Contain(newLine + "追記", $"改行コード '{newLine.Replace("\r", "\\r").Replace("\n", "\\n")}' が保存後も維持される必要がある");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] BuildBytes(string text, bool hasBom)
    {
        var content = Encoding.UTF8.GetBytes(text);
        if (!hasBom) return content;

        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        var result = new byte[bom.Length + content.Length];
        bom.CopyTo(result, 0);
        content.CopyTo(result, bom.Length);
        return result;
    }

    private (Window Window, TextEditor Editor) CreateEditorWindow()
    {
        var editor = new TextEditor { Width = 800, Height = 600 };
        var window = _windows.Track(new Window { Width = 800, Height = 600, Content = editor });
        return (window, editor);
    }

    private static void SaveScreenshot(Window window, string fileName)
    {
        using var frame = window.CaptureRenderedFrame();
        if (frame is null) return;

        var path = Path.Combine(GetScreenshotDirectory(), fileName);
        frame.Save(path);
        File.Exists(path).Should().BeTrue($"スクリーンショットが '{path}' へ保存されている必要がある");
    }

    private static string GetScreenshotDirectory([CallerFilePath] string sourceFilePath = "")
    {
        var dir = Path.Combine(Path.GetDirectoryName(sourceFilePath)!, "screenshots");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [AvaloniaFact(DisplayName = "内容が同じファイルの再読込では文書へ触れない")]
    public async Task 内容が同じなら再読込しても文書を差し替えない()
    {
        // ファイル監視（4.6）は自分で保存した直後も変更として通知してくる。
        // そこで無条件に文書を差し替えると、保存のたびにカーソルが末尾へ飛び、
        // アンドゥ履歴も消える（実機で発生した不具合）。内容が同じなら何もしないこと。
        var path = Path.Combine(Path.GetTempPath(), $"graft-reload-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "1行目\n2行目\n");
        try
        {
            var opened = await DocumentSession.OpenAsync(path, projectRoot: string.Empty);
            opened.IsSuccess.Should().BeTrue();
            using var session = opened.Value;

            var changes = 0;
            session.Document.Changed += (_, _) => changes++;

            await session.SaveAsync();
            (await session.ReloadAsync()).IsSuccess.Should().BeTrue();

            changes.Should().Be(0, "内容が変わっていないのに文書を差し替えてはならない");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact(DisplayName = "内容が変わったファイルは再読込で反映される")]
    public async Task 内容が変わっていれば再読込で反映する()
    {
        var path = Path.Combine(Path.GetTempPath(), $"graft-reload2-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "変更前\n");
        try
        {
            var opened = await DocumentSession.OpenAsync(path, projectRoot: string.Empty);
            using var session = opened.Value;

            await File.WriteAllTextAsync(path, "変更後\n");
            (await session.ReloadAsync()).IsSuccess.Should().BeTrue();

            session.Document.Text.Should().Contain("変更後", "外部での変更は取り込む必要がある");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
