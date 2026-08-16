using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;
using FluentAssertions;
using Graft.Editor;
using Graft.Features;
using Graft.UiTests.TestSupport;
using Xunit;

namespace Graft.UiTests;

/// <summary>
/// 実機での指摘（Windows）: 折りたたみマージンのL字線（マーカーから下へ伸びる縦線と終端の
/// 横線）を消す対処（<see cref="MarkerOnlyFoldingMargin"/>への差し替え、
/// <see cref="FoldingSupport"/>の<c>ReplaceFoldingMarginWithMarkerOnly</c>/<c>RemoveCustomMargin</c>）
/// の回帰防止テスト。特に重要なのは「解除（Uninstall）で自前のマージンを取り除き忘れない」
/// （<see cref="FoldingManager.Uninstall"/>は差し替え前の標準<see cref="FoldingMargin"/>インスタンス
/// しか対象にしないため、対処しないと<c>LeftMargins</c>に残り続ける・増殖する）こと。
///
/// GitGutterProviderやShowLineNumbersが追加するLineNumberMargin/DottedLineMarginなど
/// 他のマージンと並べたうえで検証し、実際のEditorPaneの構成（EditorPane.axaml.cs参照:
/// ShowLineNumbers=true → FoldingSupport.Attach → GitGutterProviderをindex 0へInsert）を
/// なるべく再現する。
/// </summary>
public class FoldingMarginTests : IDisposable
{
    private readonly ShownWindowTracker _windows = new();

    public void Dispose()
    {
        _windows.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>実際のEditorPaneと同じ順序でマージンを組み立てる
    /// （行番号→GitGutterをindex 0へ挿入→折りたたみをAttach）。</summary>
    private (Window Window, TextEditor Editor, GitGutterProvider GitGutter) CreateEditorWithMargins()
    {
        var editor = new TextEditor { Width = 800, Height = 600, ShowLineNumbers = true };
        var window = _windows.Track(new Window { Width = 800, Height = 600, Content = editor });

        var gitGutter = new GitGutterProvider(editor, new GitIntegration());
        editor.TextArea.LeftMargins.Insert(0, gitGutter); // EditorPane.axaml.csと同じ挿入位置。

        return (window, editor, gitGutter);
    }

    [AvaloniaFact(DisplayName = "折りたたみ有効時、LeftMargins上の折りたたみマージンはMarkerOnlyFoldingMarginである")]
    public void 折りたたみマージンはMarkerOnlyFoldingMarginに差し替わっている()
    {
        var (_, editor, _) = CreateEditorWithMargins();
        var document = new TextDocument("void Foo()\n{\n    Bar();\n}\n");
        editor.Document = document;

        using var folding = new FoldingSupport(editor);
        folding.Attach(document, ".cs");

        var foldingMargins = editor.TextArea.LeftMargins.OfType<FoldingMargin>().ToList();
        foldingMargins.Should().HaveCount(1, "折りたたみマージンはちょうど1つだけ存在するはず");
        foldingMargins[0].Should().BeOfType<MarkerOnlyFoldingMargin>(
            "L字線を描かないよう差し替えた自前のマージンであるはず（標準のFoldingMarginのままではない）");
    }

    [AvaloniaFact(DisplayName = "折りたたみマージンへの差し替えでも他のマージンとの並び順は変わらない")]
    public void マージンの並び順が差し替え前後で変わらない()
    {
        var (_, editor, gitGutter) = CreateEditorWithMargins();
        var document = new TextDocument("void Foo()\n{\n    Bar();\n}\n");
        editor.Document = document;

        // Attach前: [GitGutter, LineNumberMargin, DottedLineMargin]（ShowLineNumbers=trueが
        // TextEditor側で自動的に挿入する2つ。標準のFoldingMarginはまだ存在しない）。
        var beforeTypes = editor.TextArea.LeftMargins.Select(m => m.GetType().Name).ToList();
        editor.TextArea.LeftMargins[0].Should().BeSameAs(gitGutter, "GitGutterは常に先頭に挿入される");
        beforeTypes.Should().NotContain("FoldingMargin", "Attach前は折りたたみマージンがまだ存在しない");

        using var folding = new FoldingSupport(editor);
        folding.Attach(document, ".cs");

        var afterMargins = editor.TextArea.LeftMargins;

        // 差し替え後もGitGutter・LineNumberMargin・DottedLineMarginの相対位置（先頭3つ）は
        // 一切変わらず、末尾（標準のFoldingMarginが元々追加されていたのと同じインデックス）に
        // MarkerOnlyFoldingMarginが1つ増えているだけのはず。末尾へ「追加し直す」実装だと
        // ここが崩れないが、万一先頭へ挿入する・並び順を並べ替えるような実装変更が入った場合に
        // 検知できるよう、先頭3つの型名と参照の両方を確認する。
        afterMargins.Count.Should().Be(beforeTypes.Count + 1,
            "折りたたみマージンが1つ増えるだけで、他のマージンが増減してはいけない");
        for (var i = 0; i < beforeTypes.Count; i++)
        {
            afterMargins[i].GetType().Name.Should().Be(beforeTypes[i],
                $"index {i} のマージン種別は差し替えの前後で変わらないはず");
        }
        afterMargins[0].Should().BeSameAs(gitGutter, "GitGutter自体のインスタンス・位置も変わらないはず");
        afterMargins[^1].Should().BeOfType<MarkerOnlyFoldingMargin>(
            "標準のFoldingMarginが追加されるのと同じ末尾の位置に、差し替え後のマージンがあるはず");
    }

    [AvaloniaFact(DisplayName = "折りたたみを無効化するとLeftMarginsに折りたたみマージンが1つも残らない")]
    public void 無効化後は折りたたみマージンが残らない()
    {
        var (_, editor, _) = CreateEditorWithMargins();
        var document = new TextDocument("void Foo()\n{\n    Bar();\n}\n");
        editor.Document = document;

        using var folding = new FoldingSupport(editor);
        folding.Attach(document, ".cs");
        editor.TextArea.LeftMargins.OfType<FoldingMargin>().Should().HaveCount(1, "前提: 有効化直後は1つ存在する");

        folding.SetEnabled(false);

        editor.TextArea.LeftMargins.OfType<FoldingMargin>().Should().BeEmpty(
            "FoldingManager.Uninstallは差し替え前の標準FoldingMarginしか対象にしないため、" +
            "自前のMarkerOnlyFoldingMarginを明示的に取り除かないとここに残ってしまう（回帰防止の本命）");
    }

    [AvaloniaFact(DisplayName = "文書を差し替えて解除してもLeftMarginsに折りたたみマージンが残らない")]
    public void 文書差し替えによる解除後も折りたたみマージンが残らない()
    {
        var (_, editor, _) = CreateEditorWithMargins();
        var document1 = new TextDocument("void Foo()\n{\n    Bar();\n}\n");
        editor.Document = document1;

        using var folding = new FoldingSupport(editor);
        folding.Attach(document1, ".cs");
        editor.TextArea.LeftMargins.OfType<FoldingMargin>().Should().HaveCount(1);

        // タブ切替相当: Editor.Documentの差し替え → Attachの呼び直し
        // （FoldingSupportクラスコメントの「不具合1」参照。DocumentChangedで同期的にUninstallされる）。
        var document2 = new TextDocument("def foo():\n    pass\n");
        editor.Document = document2;
        folding.Attach(document2, ".py");

        editor.TextArea.LeftMargins.OfType<FoldingMargin>().Should().HaveCount(1,
            "文書差し替え後も折りたたみマージンは新しい文書向けに1つだけ存在し、古いものが残ってはいけない");

        // FoldingSupport自体をDispose（最終的な解除経路）した後は1つも残らない。
        folding.Dispose();
        editor.TextArea.LeftMargins.OfType<FoldingMargin>().Should().BeEmpty(
            "Disposeによる最終的な解除後は折りたたみマージンが1つも残らないはず");
    }

    [AvaloniaFact(DisplayName = "有効→無効→有効を繰り返しても折りたたみマージンは増殖しない")]
    public void 有効無効の繰り返しでマージンが増殖しない()
    {
        var (_, editor, _) = CreateEditorWithMargins();
        var document = new TextDocument("void Foo()\n{\n    Bar();\n}\n");
        editor.Document = document;

        using var folding = new FoldingSupport(editor);
        folding.Attach(document, ".cs");

        for (var i = 0; i < 5; i++)
        {
            folding.SetEnabled(false);
            editor.TextArea.LeftMargins.OfType<FoldingMargin>().Should().BeEmpty(
                $"{i}回目の無効化直後は折りたたみマージンが残っていないはず");

            folding.SetEnabled(true);
            var margins = editor.TextArea.LeftMargins.OfType<FoldingMargin>().ToList();
            margins.Should().HaveCount(1, $"{i}回目の再有効化後も折りたたみマージンはちょうど1つのはず（増殖しない）");
            margins[0].Should().BeOfType<MarkerOnlyFoldingMargin>();
        }
    }
}
