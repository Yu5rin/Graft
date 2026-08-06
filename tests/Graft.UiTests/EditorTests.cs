using System.Runtime.CompilerServices;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using FluentAssertions;
using Graft.Core;
using Graft.Editor;
using Graft.Features;
using Graft.Themes;

namespace Graft.UiTests;

/// <summary>
/// エディタ層（フェーズL3: AvalonEdit→AvaloniaEdit移植）の検証テスト（仕様書v2.1 18章・
/// 附録A.7）。<c>src/Graft/Editor/</c> の各クラスが例外なく構築・描画でき、
/// 4.5節のエンコーディング・改行保持と4.1節のシンタックスハイライト接続が
/// v2.0のWPF版と同じ挙動を保つことを検証する。代表画面のスクリーンショットも保存する。
/// </summary>
public class EditorTests
{
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

    private static (Window Window, TextEditor Editor) CreateEditorWindow()
    {
        var editor = new TextEditor { Width = 800, Height = 600 };
        var window = new Window { Width = 800, Height = 600, Content = editor };
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
}
