using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Graft.Infra;
using Graft.Platform;
using Graft.Platform.Null;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;
using Graft.Views;
using Xunit;

namespace Graft.UiTests;

/// <summary>
/// 不具合修正の回帰テスト: Windows実機で報告された「コードを開いた時に1行目が表示しきれて
/// いない（カーソルは1:1なのに、表示だけ半行ぶん下へスクロールした状態になる）」への対応。
///
/// 【真因（EditorPane.axaml.csのRestoreViewStateFromのコメント参照）】
/// EditorPaneは単一のAvaloniaEdit Editor/ScrollViewerを全タブで使い回し、Documentだけを
/// 差し替える方式を採る。タブ切替の直後に走るレイアウトパスで新しい文書のExtent（コンテンツ
/// 全体の高さ）が再計算される際、AvaloniaのScrollContentPresenter/ScrollViewer内部で
/// 「ExtentがOffsetより先に伝播し、その途中でOffsetの生値（まだ更新前の直前タブの値）が
/// 新Extentに対して再クランプされる」という一過性の競合が起き、VerticalOffsetが行の高さの
/// 半分程度だけ動いてしまう（実測: 既定フォントで8.775px、行高17.55pxのちょうど半分）。
/// これはEditorPane側のスクロール位置復元ロジック（HasViewStateの判定）とは独立した、
/// Avalonia側のレイアウトタイミングに起因する問題のため、ヘッドレス（Xvfbなし、
/// Avalonia.Headless+Skiaによる実フォント計測込み）で再現・検証できる。
/// </summary>
public class EditorOpenScrollPositionTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-open-scroll", Guid.NewGuid().ToString("N"));

    private readonly ShownWindowTracker _windows = new();

    public void Dispose()
    {
        _windows.Dispose();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    /// <summary>利用者報告の再現条件そのもの: タブを複数開いた状態から、新しいファイル
    /// （content-script.css相当、行数が多く前のタブより文書が長い）をエクスプローラ経由で
    /// 開く。開いた直後・レイアウト確定後のいずれでもVerticalOffsetは0（＝1行目が完全に
    /// 見える）でなければならない。</summary>
    [AvaloniaFact(DisplayName = "複数タブが開いた状態でCSSファイルを開くと1行目が完全に見える（VerticalOffset=0）")]
    public async Task 複数タブの状態でファイルを開くと1行目が完全に見える()
    {
        Directory.CreateDirectory(_root);
        var vm = new EditorPaneViewModel(new Settings(), new NullDialogService(), new AvaloniaUiServices());
        vm.SetProject(_root);

        // 利用者報告どおり、事前に何枚かタブを開いておく（短い文書。後で開く長い文書との
        // 対比で、Extent＝コンテンツ全体の高さが大きく変わる状況を作る）。
        for (var i = 1; i < 4; i++)
        {
            var p = Path.Combine(_root, $"other{i}.txt");
            await File.WriteAllTextAsync(p, $"content {i}\nline2\nline3\n");
            (await vm.OpenFileAsync(p)).IsSuccess.Should().BeTrue();
        }

        var pane = new EditorPane { DataContext = vm };
        var window = _windows.Track(new Window { Width = 1000, Height = 700, Content = pane });
        window.Show();
        window.CaptureRenderedFrame().Should().NotBeNull();
        Dispatcher.UIThread.RunJobs();

        // content-script.css相当: 行数が多く、直前のタブ（3行）よりずっと長い文書。
        var cssContent = string.Join("\n", Enumerable.Range(1, 200).Select(i => $".rule{i} {{ color: red; }}"));
        var cssPath = Path.Combine(_root, "content-script.css");
        await File.WriteAllTextAsync(cssPath, cssContent);
        var opened = await vm.OpenFileAsync(cssPath);
        opened.IsSuccess.Should().BeTrue();

        var editor = window.GetVisualDescendants().OfType<AvaloniaEdit.TextEditor>().Single(e => e.Name == "Editor");

        // レイアウト・描画パスを複数回走らせ、後から遅れてズレが入らないことも確認する
        // （不具合発生時は、開いた直後は0でもレイアウトパスの後にずれる）。
        for (var i = 0; i < 3; i++)
        {
            window.CaptureRenderedFrame().Should().NotBeNull();
            Dispatcher.UIThread.RunJobs();
        }

        editor.VerticalOffset.Should().Be(0,
            "ファイルを開いた直後はカーソルが1行目（1:1）にあり、1行目全体が見えているはず");
        editor.TextArea.Caret.Line.Should().Be(1);
        editor.TextArea.Caret.Column.Should().Be(1);
    }

    /// <summary>タブが1枚も無い状態（アプリ起動直後の最初の1枚）から開く場合も同様に0になる
    /// ことを確認する（複数タブ限定の問題ではないことの確認）。</summary>
    [AvaloniaFact(DisplayName = "最初の1枚目のタブとしてCSSファイルを開いても1行目が完全に見える")]
    public async Task 最初のタブとして開いても1行目が完全に見える()
    {
        Directory.CreateDirectory(_root);
        var vm = new EditorPaneViewModel(new Settings(), new NullDialogService(), new AvaloniaUiServices());
        vm.SetProject(_root);

        var pane = new EditorPane { DataContext = vm };
        var window = _windows.Track(new Window { Width = 1000, Height = 700, Content = pane });
        window.Show();
        window.CaptureRenderedFrame().Should().NotBeNull();
        Dispatcher.UIThread.RunJobs();

        var cssContent = string.Join("\n", Enumerable.Range(1, 200).Select(i => $".rule{i} {{ color: red; }}"));
        var cssPath = Path.Combine(_root, "content-script.css");
        await File.WriteAllTextAsync(cssPath, cssContent);
        (await vm.OpenFileAsync(cssPath)).IsSuccess.Should().BeTrue();

        var editor = window.GetVisualDescendants().OfType<AvaloniaEdit.TextEditor>().Single(e => e.Name == "Editor");
        for (var i = 0; i < 3; i++)
        {
            window.CaptureRenderedFrame().Should().NotBeNull();
            Dispatcher.UIThread.RunJobs();
        }

        editor.VerticalOffset.Should().Be(0, "最初の1枚目として開いた場合も1行目が完全に見えているはず");
    }

    /// <summary>既存の「タブを離れて戻ったときにスクロール位置が復元される」挙動が、今回の
    /// 修正（HasViewStateがfalseの場合の遅延再スクロール追加）で壊れていないことを確認する。</summary>
    [AvaloniaFact(DisplayName = "タブを離れて戻るとスクロール位置が復元される")]
    public async Task タブを離れて戻るとスクロール位置が復元される()
    {
        Directory.CreateDirectory(_root);
        var vm = new EditorPaneViewModel(new Settings(), new NullDialogService(), new AvaloniaUiServices());
        vm.SetProject(_root);

        var longContent = string.Join("\n", Enumerable.Range(1, 500).Select(i => $"line {i}"));
        var longPath = Path.Combine(_root, "long.txt");
        await File.WriteAllTextAsync(longPath, longContent);
        var otherPath = Path.Combine(_root, "other.txt");
        await File.WriteAllTextAsync(otherPath, "short\n");

        var longTab = (await vm.OpenFileAsync(longPath)).Value;
        var otherTab = (await vm.OpenFileAsync(otherPath)).Value;

        var pane = new EditorPane { DataContext = vm };
        var window = _windows.Track(new Window { Width = 1000, Height = 700, Content = pane });
        window.Show();
        window.CaptureRenderedFrame().Should().NotBeNull();
        Dispatcher.UIThread.RunJobs();

        var editor = window.GetVisualDescendants().OfType<AvaloniaEdit.TextEditor>().Single(e => e.Name == "Editor");

        // long.txtへ切り替える。開いた直後のレイアウト確定（本修正の遅延再スクロールを含む）が
        // 落ち着くまで数回レイアウト・描画パスを走らせてから、下の方までスクロールする
        // （ScrollToLineはキャレットを動かさず表示位置だけを動かす。AvaloniaEdit 11.1.0では
        // ScrollToVerticalOffsetが未実装のno-opのため、ここでは使わない。
        // EditorPane.axaml.csのRestoreViewStateFromのコメント「不具合修正2」参照）。
        vm.ActiveTab = longTab;
        for (var i = 0; i < 3; i++)
        {
            window.CaptureRenderedFrame().Should().NotBeNull();
            Dispatcher.UIThread.RunJobs();
        }
        editor.ScrollToLine(400);
        window.CaptureRenderedFrame().Should().NotBeNull();
        Dispatcher.UIThread.RunJobs();
        var scrolledOffset = editor.VerticalOffset;
        scrolledOffset.Should().BeGreaterThan(0, "テスト条件として実際にスクロールできている必要がある");
        editor.TextArea.Caret.Line.Should().Be(1, "スクロールだけでキャレット行は動いていないはず");

        // 別タブへ移り、また戻る。
        vm.ActiveTab = otherTab;
        window.CaptureRenderedFrame().Should().NotBeNull();
        Dispatcher.UIThread.RunJobs();
        vm.ActiveTab = longTab;
        for (var i = 0; i < 3; i++)
        {
            window.CaptureRenderedFrame().Should().NotBeNull();
            Dispatcher.UIThread.RunJobs();
        }

        editor.VerticalOffset.Should().BeApproximately(scrolledOffset, 1.0,
            "タブを離れる前のスクロール位置へ戻ってきているはず");
    }

    /// <summary>
    /// 回帰テスト: 「1行目が完全に見える」ようにする修正の初版は、新規タブ用の遅延補正で
    /// <c>MoveCaretTo</c>（Caret.Line/Columnへの直接代入＝選択範囲を消す）を呼んでいたため、
    /// ファイルを開いた直後に素早く選択操作を行うと、後から発火する遅延補正がその選択を
    /// 消してしまう回帰があった（EditorSelectionPromptTestsの
    /// 「選択範囲から修正依頼プロンプトをクリップボードへコピーする」で実際に検出）。
    /// 遅延補正はスクロール位置の再適用に留め、キャレット・選択範囲には触れないことを確認する。
    /// </summary>
    [AvaloniaFact(DisplayName = "ファイルを開いた直後に選択しても、遅延スクロール補正が選択を消さない")]
    public async Task ファイルを開いた直後の選択が遅延補正で消えない()
    {
        Directory.CreateDirectory(_root);
        var vm = new EditorPaneViewModel(new Settings(), new NullDialogService(), new AvaloniaUiServices());
        vm.SetProject(_root);

        var path = Path.Combine(_root, "foo.cs");
        await File.WriteAllTextAsync(path, "class Foo\n{\n    int X = 1;\n}\n");
        (await vm.OpenFileAsync(path)).IsSuccess.Should().BeTrue();

        var pane = new EditorPane { DataContext = vm };
        var window = _windows.Track(new Window { Width = 800, Height = 600, Content = pane });
        window.Show();

        var editor = window.GetVisualDescendants().OfType<AvaloniaEdit.TextEditor>().Single(e => e.Name == "Editor");

        // タブを開いた直後、レイアウト確定前（＝遅延補正がまだ発火する前）に選択操作を行う
        // （EditorSelectionPromptTestsと同じ順序: Select→RunJobs）。
        var line = editor.Document.GetLineByNumber(2);
        editor.Select(line.Offset, line.Length);

        // RunJobsで遅延補正（Background優先度）を含むすべての保留ジョブを処理させる。
        window.CaptureRenderedFrame().Should().NotBeNull();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame().Should().NotBeNull();
        Dispatcher.UIThread.RunJobs();

        editor.SelectionLength.Should().Be(line.Length,
            "開いた直後の遅延スクロール補正で選択範囲が消えてはならない");
        editor.SelectedText.Should().Be("{");
    }
}
