using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using FluentAssertions;
using Graft.Infra;
using Graft.Platform;
using Graft.Platform.Null;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests;

/// <summary>
/// Markdown編集支援（検討書「Markdownの編集支援」）の回帰テスト。実際のキー入力
/// （<c>window.KeyPressQwerty</c>）を<see cref="EditorPane"/>へ流し込み、リスト・引用の
/// Enter継続/脱出・表のTab/Enterが働くこと、<c>.md</c>以外ではTabの意味が変わらないことを検証する。
/// 純粋な判定ロジックそのもの（遅延継続の落とし穴を含む）は
/// <c>tests/Graft.Tests/MarkdownBlockContinuationTests.cs</c>で検証済みのため、ここでは
/// キー入力からドキュメントへの反映という統合部分に絞る。
/// </summary>
public class EditorMarkdownEditingSupportTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-md-editing", Guid.NewGuid().ToString("N"));
    private readonly ShownWindowTracker _windows = new();

    public EditorMarkdownEditingSupportTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _windows.Dispose();
        TempDirectoryCleanup.TryDeleteRecursive(_root);
        GC.SuppressFinalize(this);
    }

    [AvaloniaFact(DisplayName = "箇条書きの行末でEnterすると同じマーカーで新しい項目が続く")]
    public async Task 箇条書きの継続()
    {
        var (window, pane, _) = await OpenMarkdownFileAsync("- 項目1").ConfigureAwait(true);
        PlaceCaretAtEndOfLine(window, pane, line: 1);

        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);

        pane.Editor.Document.Text.Should().Be("- 項目1\n- ");
    }

    [AvaloniaFact(DisplayName = "空の箇条書き項目でEnterすると2回目でマーカーが消えて脱出する")]
    public async Task 箇条書きの空項目は2回目のEnterで脱出する()
    {
        var (window, pane, _) = await OpenMarkdownFileAsync("- 項目1").ConfigureAwait(true);
        PlaceCaretAtEndOfLine(window, pane, line: 1);

        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); // 1回目: 継続(空項目ができる)
        pane.Editor.Document.Text.Should().Be("- 項目1\n- ");
        window.CaptureRenderedFrame();

        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); // 2回目: 脱出
        pane.Editor.Document.Text.Should().Be("- 項目1\n", "空項目のマーカーが消えてプレーンな行に戻るはず");
    }

    [AvaloniaFact(DisplayName = "引用から完全に抜けるときは空行を挟む")]
    public async Task 引用から抜けるときは空行を挟む()
    {
        var (window, pane, _) = await OpenMarkdownFileAsync("> 引用\n> ").ConfigureAwait(true);
        PlaceCaretAtEndOfLine(window, pane, line: 2); // "> "の行末（2文字目の後ろ）

        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);

        var text = pane.Editor.Document.Text;
        text.Should().Be("> 引用\n\n", "引用マーカーが消えるのと同時に空行が1行挟まるはず");
    }

    [AvaloniaFact(DisplayName = "表のセルでTabすると次のセルが選択される")]
    public async Task 表でTabすると次のセルへ移動する()
    {
        var content = "| A | B |\n| --- | --- |\n| 1 | 2 |";
        var (window, pane, _) = await OpenMarkdownFileAsync(content).ConfigureAwait(true);

        // 1行目("| A | B |")の"A"の直後(1マス目)へキャレットを置く。
        var doc = pane.Editor.Document;
        var firstLine = doc.GetLineByNumber(1);
        pane.Editor.CaretOffset = firstLine.Offset + 3; // "| A" の直後。
        pane.Editor.Focus();
        window.CaptureRenderedFrame();

        window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);

        pane.Editor.SelectionLength.Should().BeGreaterThan(0, "次のセルの内容が選択されるはず");
        pane.Editor.SelectedText.Should().Be("B");
    }

    [AvaloniaFact(DisplayName = "★表で連続してTab/Shift+Tabしてもセル間を移動し続けられる(選択状態で止まらない)")]
    public async Task 表で連続したTabとShiftTabが効き続ける()
    {
        // 実機のXvfb操作テストで発覚した不具合の再発防止: HandleTabがSelection.IsEmptyで
        // 早期returnしていたため、1回目のTabでSelectCellが選択状態を作った直後、2回目以降の
        // Tab/Shift+Tabが「選択があるから」と常に抜けてしまい、セル間を連続移動できなかった。
        var content = "| A | B |\n| --- | --- |\n| 1 | 2 |";
        var (window, pane, _) = await OpenMarkdownFileAsync(content).ConfigureAwait(true);

        var doc = pane.Editor.Document;
        var firstLine = doc.GetLineByNumber(1);
        pane.Editor.CaretOffset = firstLine.Offset + 3; // "| A" の直後。
        pane.Editor.Focus();
        window.CaptureRenderedFrame();

        window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None); // 1回目: A→B
        pane.Editor.SelectedText.Should().Be("B");

        window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.Shift); // 2回目: B→A (Shift+Tabで戻る)
        pane.Editor.SelectedText.Should().Be("A", "選択状態のままでもShift+Tabで前のセルへ戻れるはず");

        window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None); // 3回目: A→B (再度前進できる)
        pane.Editor.SelectedText.Should().Be("B", "選択状態のままでも次のTabでまた次のセルへ進めるはず");
    }

    [AvaloniaFact(DisplayName = "表の最終セルでEnterすると行が1つ追加される")]
    public async Task 表の最終セルでEnterすると行が追加される()
    {
        var content = "| A | B |\n| --- | --- |\n| 1 | 2 |";
        var (window, pane, _) = await OpenMarkdownFileAsync(content).ConfigureAwait(true);

        var doc = pane.Editor.Document;
        var lastLine = doc.GetLineByNumber(3); // "| 1 | 2 |"
        pane.Editor.CaretOffset = lastLine.Offset + lastLine.Length; // 行末（最終セルの中）。
        pane.Editor.Focus();
        window.CaptureRenderedFrame();

        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);

        doc.LineCount.Should().Be(4, "本文の行が1つ追加されるはず");
        doc.GetText(doc.GetLineByNumber(4).Offset, doc.GetLineByNumber(4).Length)
            .Should().Contain("|", "追加された行も表の行の形をしているはず");
    }

    [AvaloniaFact(DisplayName = ".md以外のファイルではTabの意味が変わらない(通常のインデント挿入のまま)")]
    public async Task Markdown以外ではTabの意味が変わらない()
    {
        var vm = new EditorPaneViewModel(new Settings(), new NullDialogService(), new AvaloniaUiServices());
        vm.SetProject(_root);
        var pane = new EditorPane { DataContext = vm };
        var window = _windows.Track(new Window { Width = 1000, Height = 800, Content = pane });
        window.Show();

        var path = Path.Combine(_root, "a.py");
        await File.WriteAllTextAsync(path, "x = 1\n").ConfigureAwait(true);
        var result = await vm.OpenFileAsync(path).ConfigureAwait(true);
        result.IsSuccess.Should().BeTrue();

        pane.Editor.CaretOffset = 0;
        pane.Editor.Focus();
        window.CaptureRenderedFrame();
        var before = pane.Editor.Document.Text;

        window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);

        var after = pane.Editor.Document.Text;
        after.Should().NotBe(before, "Tabキー自体は普段どおりインデントとして働くはず");
        after.Length.Should().BeGreaterThan(before.Length);
        pane.Editor.SelectionLength.Should().Be(0, "表のセル選択のような特別な選択状態にはならないはず");
    }

    [AvaloniaFact(DisplayName = "Markdownプレビュー表示中はEnterによるリスト継続が働かない")]
    public async Task プレビュー表示中はリスト継続が働かない()
    {
        var (window, pane, vm) = await OpenMarkdownFileAsync("- 項目1").ConfigureAwait(true);
        vm.ActiveTab!.ShowMarkdownPreview = true;
        window.CaptureRenderedFrame();

        PlaceCaretAtEndOfLine(window, pane, line: 1);
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);

        // プレビュー表示中はEditor自体が非表示(Editor.IsVisible=false)でFocus()も実質効かないため、
        // Enterキー自体がEditorへ届くとは限らない。ここで検証したいのは「リスト継続の特別処理
        // （マーカー"- "を新しい行に補う）が働かないこと」であり、フォーカスできない状態で
        // Enterを送った結果どうなるか(何も起きない/既定のEnterが素通しされる等)まではAvaloniaEdit
        // 側の一般的な挙動なので厳密には問わない。
        pane.Editor.Document.Text.Should().NotContain("- 項目1\n- ", "プレビュー表示中はリスト継続の特別処理が働かないはず");
    }

    // ------------------------------------------------------------------
    // ヘルパ
    // ------------------------------------------------------------------

    private async Task<(Window Window, EditorPane Pane, EditorPaneViewModel Vm)> OpenMarkdownFileAsync(string content)
    {
        var vm = new EditorPaneViewModel(new Settings(), new NullDialogService(), new AvaloniaUiServices());
        vm.SetProject(_root);
        var pane = new EditorPane { DataContext = vm };
        var window = _windows.Track(new Window { Width = 1000, Height = 800, Content = pane });
        window.Show();

        var path = Path.Combine(_root, "doc.md");
        await File.WriteAllTextAsync(path, content).ConfigureAwait(true);
        var result = await vm.OpenFileAsync(path).ConfigureAwait(true);
        result.IsSuccess.Should().BeTrue();

        // .mdは既定でMarkdownプレビュー表示（Editor.IsVisible=false）で開かれる
        // （MarkdownPreviewShellIntegrationTests参照）。キー入力の検証には編集モードへ
        // 切り替える必要がある。
        result.Value.ShowMarkdownPreview = false;
        window.CaptureRenderedFrame().Should().NotBeNull();

        return (window, pane, vm);
    }

    private static void PlaceCaretAtEndOfLine(Window window, EditorPane pane, int line)
    {
        var docLine = pane.Editor.Document.GetLineByNumber(line);
        pane.Editor.CaretOffset = docLine.Offset + docLine.Length;
        pane.Editor.Focus();
        window.CaptureRenderedFrame(); // Focus()の反映を進める（MarkdownPreviewShellIntegrationTestsと同じ作法）。
    }
}
