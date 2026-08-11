using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using FluentAssertions;
using Graft.Core;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;
using Graft.Platform.Null;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests;

/// <summary>
/// Markdownプレビュー機能の、<see cref="ShellWindow"/>を経由した統合回帰テスト。
///
/// 【実機（Xvfb）検証で発覚した不具合】<see cref="Graft.Views.EditorPane"/>単体で組み立てた
/// ヘッドレステスト（<c>MarkdownPreviewTests.切替ボタンとEscでモードを切り替えられる</c>）は
/// 通っていたにもかかわらず、実際にアプリを起動してEscapeキーを押しても編集モードから
/// プレビューへ戻らないという不具合が実機検証で見つかった。原因は
/// <see cref="ShellWindow.Keyboard.cs"/>の<c>OnTunnelKeyDown</c>が、<see cref="EditorPane"/>の
/// 祖先（トンネリングでより先に実行される）としてEscapeを無条件に「キューの破棄」として
/// 処理し、<c>e.Handled = true</c>にしてしまっていたため、<see cref="EditorPane"/>自身のEscape
/// 処理（検索オーバーレイを閉じる・Markdownプレビューへ戻る）に一切イベントが届いていなかった
/// ことだった。<see cref="EditorPane"/>単体のテストはこの祖先を持たないため検出できなかった。
///
/// 本テストは<see cref="ShellWindow"/>を実際に組み立てて同じ経路を再現し、この不具合が
/// 再発しないことを確認する（<c>ShouldDeferEscapeToEditor</c>の回帰ガード）。
/// </summary>
public class MarkdownPreviewShellIntegrationTests : IDisposable
{
    private readonly string _baseDirectory =
        Path.Combine(Path.GetTempPath(), "graft-md-shell", Guid.NewGuid().ToString("N"));
    private readonly ShownWindowTracker _windows = new();

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

    [AvaloniaFact(DisplayName = "実機不具合の回帰: ShellWindow経由でも、Markdown編集モードでのEscapeがキューの破棄に奪われずプレビューへ戻る")]
    public async Task シェル経由でもEscapeでMarkdownプレビューへ戻る()
    {
        var appPaths = new AppPaths(_baseDirectory);
        appPaths.EnsureCoreDirectoriesExist();
        var (shell, window) = BuildShellAndWindow(appPaths);
        window.Show();
        ShellWindowLoadWaiter.WaitForLayoutApplied(window);

        var filePath = Path.Combine(_baseDirectory, "doc.md");
        await File.WriteAllTextAsync(filePath, "# 見出し\n\n本文です。\n");

        var result = await shell.Editor.OpenFileAsync(filePath);
        result.IsSuccess.Should().BeTrue();
        var tab = result.Value;
        tab.ShowMarkdownPreview.Should().BeTrue("前提: .mdは既定でプレビュー表示のはず");

        // 編集モードへ切り替える（ボタン操作自体はMarkdownPreviewTests側で別途検証済みのため、
        // ここでは状態遷移だけを起こしてEditorPane側の表示切替を動かす）。
        tab.ShowMarkdownPreview = false;
        window.CaptureRenderedFrame().Should().NotBeNull(); // Editor.Focus()の反映も含め描画を進める

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);

        tab.ShowMarkdownPreview.Should().BeTrue(
            "実機で発覚した不具合の回帰確認: ShellWindowのEscape（キューの破棄）に奪われず、"
            + "EditorPane自身のEscape処理（プレビューへ戻る）まで届く必要がある");

        window.Close();
    }

    [AvaloniaFact(DisplayName = "Markdown編集モードでなければ、ShellWindow経由のEscapeは従来どおりキューの破棄として扱われる")]
    public async Task 通常時はEscapeが従来どおりキューの破棄に使われる()
    {
        var appPaths = new AppPaths(_baseDirectory);
        appPaths.EnsureCoreDirectoriesExist();
        var (shell, window) = BuildShellAndWindow(appPaths);
        window.Show();
        ShellWindowLoadWaiter.WaitForLayoutApplied(window);

        var filePath = Path.Combine(_baseDirectory, "plain.txt");
        await File.WriteAllTextAsync(filePath, "ただのテキストです。\n");

        var result = await shell.Editor.OpenFileAsync(filePath);
        result.IsSuccess.Should().BeTrue();
        window.CaptureRenderedFrame().Should().NotBeNull();

        // 解析結果が無い状態ではDiscardCommand.CanExecuteはfalseのため実行結果を外部から
        // 直接観測しづらい。ここでは「例外なく実行され、Markdownプレビュー機能側のガード
        // （ShouldDeferEscapeToEditor）が非Markdownファイルで誤って介入しない」ことのみを
        // 確認する（回帰ガードとしての主目的は前のテストの反対側の確認）。
        var act = () => window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        act.Should().NotThrow();

        window.Close();
    }

    // ------------------------------------------------------------------
    // 不具合1の回帰: プレビューでチェックリストをON/OFFした後、Ctrl+Zで取り消せない
    //
    // 【実機報告と真因】直前のコミットでチェックリストをクリックでON/OFFできるようにした際、
    // 実装したエージェントは「AvaloniaEditのUndoStackへ自動的に記録されるため、Ctrl+Zで
    // 戻せる」とXvfbで確認したと報告していたが、その確認は<c>UndoStack.Undo()</c>を直接
    // 呼んでいただけで、実際のキー入力（Ctrl+Z）をShellWindow経由で送ってはいなかった
    // （MarkdownPreviewTests.チェックリストのクリックはCtrlZで取り消せる、参照）。
    // 実機では、プレビュー表示中はAvaloniaEdit本体（Editor）が非表示でフォーカスを持てず、
    // かつチェック操作のたびにプレビューが丸ごと再構築されてフォーカス中のチェックボックス
    // 自体が消えるため、Ctrl+Zがどこにも届かず「素のCtrl+Z」の経路（附録Aのキーマップ移行
    // 通知のみ）へ流れて握りつぶされていた。本テストはShellWindowを実際に組み立て、
    // マウスでチェックボックスをクリックしたのち、実際のCtrl+Zキー入力をShellWindow経由で
    // 送ることでこの不具合の再発を検出する（EditorPane.MarkdownPreview.cs の
    // TryHandleMarkdownPreviewUndoRedo が本修正）。
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "実機不具合の回帰: プレビュー表示中、チェックを2つ切り替えてもCtrl+Zで1つずつ順に取り消せる")]
    public async Task プレビュー表示中にCtrlZでチェックの切り替えを1つずつ取り消せる()
    {
        var appPaths = new AppPaths(_baseDirectory);
        appPaths.EnsureCoreDirectoriesExist();
        var (shell, window) = BuildShellAndWindow(appPaths);
        window.Show();
        ShellWindowLoadWaiter.WaitForLayoutApplied(window);

        var filePath = Path.Combine(_baseDirectory, "checklist.md");
        var original = "- [ ] 項目1\n- [ ] 項目2\n";
        await File.WriteAllTextAsync(filePath, original);

        var result = await shell.Editor.OpenFileAsync(filePath);
        result.IsSuccess.Should().BeTrue();
        var tab = result.Value;
        tab.ShowMarkdownPreview.Should().BeTrue("前提: .mdは既定でプレビュー表示のはず");
        window.CaptureRenderedFrame().Should().NotBeNull();

        var pane = window.GetVisualDescendants().OfType<EditorPane>().Single();

        ClickCheckbox(window, pane, index: 0);
        tab.Session.Document.Text.Should().Be("- [x] 項目1\n- [ ] 項目2\n", "1つ目のクリックが反映されているはず");

        // 1つ目のクリック後にプレビューが再構築され、そのチェックボックス自体が破棄される
        // （不具合の真因の一部）。2つ目のチェックボックスは再構築後の新しいControlから探す。
        ClickCheckbox(window, pane, index: 1);
        tab.Session.Document.Text.Should().Be("- [x] 項目1\n- [x] 項目2\n", "2つ目のクリックも反映されているはず");
        tab.Session.IsModified.Should().BeTrue();

        window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
        tab.Session.Document.Text.Should().Be(
            "- [x] 項目1\n- [ ] 項目2\n",
            "実機不具合の回帰確認: 1回目のCtrl+Zで2つ目の切り替えだけが取り消されるはず"
            + "（まとめて1回で両方戻ってはいけない）");

        window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
        tab.Session.Document.Text.Should().Be(
            original, "2回目のCtrl+Zで1つ目の切り替えも取り消され、元の内容へ戻るはず");
        tab.Session.IsModified.Should().BeFalse("元ファイルの状態まで戻れば未保存マークも消えるはず");

        window.CaptureRenderedFrame().Should().NotBeNull();
        var checkboxesAfterUndo = pane.MarkdownPreviewHost.GetVisualDescendants().OfType<CheckBox>().ToList();
        checkboxesAfterUndo.Should().OnlyContain(c => c.IsChecked == false, "取り消し後はプレビューの表示も追従するはず");

        window.Close();
    }

    [AvaloniaFact(DisplayName = "実機不具合の回帰: プレビュー表示中、Ctrl+Zで取り消した後にCtrl+Yでやり直せる")]
    public async Task プレビュー表示中にCtrlYでやり直せる()
    {
        var appPaths = new AppPaths(_baseDirectory);
        appPaths.EnsureCoreDirectoriesExist();
        var (shell, window) = BuildShellAndWindow(appPaths);
        window.Show();
        ShellWindowLoadWaiter.WaitForLayoutApplied(window);

        var filePath = Path.Combine(_baseDirectory, "checklist-redo.md");
        var original = "- [ ] 項目1\n";
        await File.WriteAllTextAsync(filePath, original);

        var result = await shell.Editor.OpenFileAsync(filePath);
        result.IsSuccess.Should().BeTrue();
        var tab = result.Value;
        window.CaptureRenderedFrame().Should().NotBeNull();

        var pane = window.GetVisualDescendants().OfType<EditorPane>().Single();
        ClickCheckbox(window, pane, index: 0);
        tab.Session.Document.Text.Should().Be("- [x] 項目1\n");

        window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
        tab.Session.Document.Text.Should().Be(original, "Ctrl+Zで取り消し済み");

        window.KeyPressQwerty(PhysicalKey.Y, RawInputModifiers.Control);
        tab.Session.Document.Text.Should().Be("- [x] 項目1\n", "Ctrl+Yでやり直せるはず");

        window.Close();
    }

    [AvaloniaFact(DisplayName = "プレビュー表示中でなければ、Ctrl+Zはこの新しい経路に横取りされない（従来どおり素のCtrl+Zの経路へ）")]
    public async Task 編集モード中はプレビュー用のCtrlZ処理が介入しない()
    {
        var appPaths = new AppPaths(_baseDirectory);
        appPaths.EnsureCoreDirectoriesExist();
        var (shell, window) = BuildShellAndWindow(appPaths);
        window.Show();
        ShellWindowLoadWaiter.WaitForLayoutApplied(window);

        var filePath = Path.Combine(_baseDirectory, "edit-mode.md");
        await File.WriteAllTextAsync(filePath, "- [ ] 項目1\n");

        var result = await shell.Editor.OpenFileAsync(filePath);
        result.IsSuccess.Should().BeTrue();
        var tab = result.Value;
        tab.ShowMarkdownPreview = false; // 編集モードへ切り替える。
        window.CaptureRenderedFrame().Should().NotBeNull();

        var pane = window.GetVisualDescendants().OfType<EditorPane>().Single();
        var act = () => window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);

        act.Should().NotThrow();
        pane.TryHandleMarkdownPreviewUndoRedo(redo: false).Should().BeFalse(
            "編集モード中（プレビュー非表示）はプレビュー用のUndo/Redo処理の対象外のはず。"
            + "AvaloniaEdit本体の既定のCtrl+Zに委ねる従来の経路のままであることの確認");

        window.Close();
    }

    /// <summary>
    /// プレビュー内の<paramref name="index"/>番目（描画順）のチェックボックスをマウスでクリックする。
    /// クリックのたびにプレビューが再構築されるため、毎回<see cref="EditorPane.MarkdownPreviewHost"/>から
    /// 最新のControlを探し直す（不具合の真因: チェック操作のたびに古いControlは破棄される）。
    /// </summary>
    private static void ClickCheckbox(ShellWindow window, EditorPane pane, int index)
    {
        var checkbox = pane.MarkdownPreviewHost.GetVisualDescendants().OfType<CheckBox>().ElementAt(index);
        var point = checkbox.TranslatePoint(
            new Point(checkbox.Bounds.Width / 2, checkbox.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        window.CaptureRenderedFrame();
    }

    private (ShellViewModel Shell, ShellWindow Window) BuildShellAndWindow(AppPaths appPaths)
    {
        IDialogService dialogs = new NullDialogService();
        IUiServices ui = new AvaloniaUiServices();

        var shell = Graft.Views.StartupCoordinator.BuildShellViewModel(
            appPaths,
            new Settings(),
            new SettingsStore(appPaths),
            new PatchQueue(appPaths),
            new ProjectStore(appPaths),
            new RevisionStore(appPaths),
            new RevisionRestorer(appPaths),
            dialogs,
            ui,
            openSettings: () => { });

        var window = _windows.Track(new ShellWindow(shell) { Width = 1280, Height = 800 });
        return (shell, window);
    }
}
