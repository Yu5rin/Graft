using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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

    /// <summary>
    /// 統合テスト（Markdownプレビュー機能との競合確認）: 本クラスが対象とする遅延スクロール
    /// 補正（<see cref="EditorPane.RestoreViewStateFrom"/>）は、タブ切替（<c>ApplyDocumentTab</c>）
    /// のたびに、その時点のプレビュー/編集モードのスクロール位置を捉えてBackground優先度で
    /// 予約する。.mdタブを一度離れて（プレビュー表示のまま）戻ってきたあと、プレビュー本文の
    /// ブロックをダブルクリックして編集モードへ切り替えた場合に、最終的なスクロール位置が
    /// ダブルクリックした段落へ正しく合っている（タブ再訪時点の古い位置へ巻き戻っていない）
    /// ことを確認する。
    ///
    /// 【設計メモ】ヘッドレステストで両者の競合を狙って発火順序を作ろうとしたところ、
    /// <c>window.CaptureRenderedFrame()</c>自体がBackground優先度のジョブも含めて実行して
    /// しまう（実測で確認済み。<see cref="Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame"/>
    /// 参照）ため、プレビュー側のブロックをダブルクリックするために必要なレイアウト確定
    /// （少なくとも1回のCaptureRenderedFrame）が、常にその前段階で遅延補正を先に消化してしまい、
    /// 「エディタが見えている状態で古い位置に巻き戻る」という順序をヘッドレス環境内では意図的に
    /// 作れなかった（実機の連続レンダリングでも同様に、ヒットテスト可能な操作の前には必ず
    /// レイアウト/レンダーパスが完了しており、その過程でBackground優先度のジョブも先に処理される
    /// ため、同じ理由で競合しないと判断できる）。そのため本テストは狙った内部レースを直接
    /// 再現するのではなく、実際に利用者が行う一連の操作（タブを離れて戻る→ダブルクリックで
    /// 編集モードへ切替）を最後まで実行し、最終的な表示位置が正しいことを確認する形にした。
    /// <see cref="EditorPane.axaml.cs"/>のRestoreViewStateFromには、念のため
    /// （プレビュー⇔編集の切替とタブ再訪の重なりに対する防御的な措置として）モードが変わって
    /// いたら遅延補正を行わないガードを残してある。
    /// </summary>
    [AvaloniaFact(DisplayName = "タブを離れて戻った直後にプレビューをダブルクリックしても正しい行へ切り替わる")]
    public async Task タブ再訪後のダブルクリックで正しい行の編集モードへ切り替わる()
    {
        Directory.CreateDirectory(_root);
        var vm = new EditorPaneViewModel(new Settings(), new NullDialogService(), new AvaloniaUiServices());
        vm.SetProject(_root);

        var sb = new System.Text.StringBuilder("# 見出し\n\n");
        for (var i = 0; i < 80; i++) sb.Append($"段落{i}の本文です。ダブルクリックのターゲット候補です。\n\n");
        var mdPath = Path.Combine(_root, "long.md");
        await File.WriteAllTextAsync(mdPath, sb.ToString());
        var otherPath = Path.Combine(_root, "other.txt");
        await File.WriteAllTextAsync(otherPath, "short\n");

        var mdTab = (await vm.OpenFileAsync(mdPath)).Value;
        var otherTab = (await vm.OpenFileAsync(otherPath)).Value;

        var pane = new EditorPane { DataContext = vm };
        var window = _windows.Track(new Window { Width = 1000, Height = 700, Content = pane });
        window.Show();

        vm.ActiveTab = mdTab;
        for (var i = 0; i < 3; i++)
        {
            window.CaptureRenderedFrame().Should().NotBeNull();
            Dispatcher.UIThread.RunJobs();
        }
        mdTab.ShowMarkdownPreview.Should().BeTrue("初期状態はプレビュー表示のはず（テスト条件）");

        // 別タブへ移り、また戻る。mdTabにHasViewState=trueが保存され、RestoreViewStateFromが
        // 遅延補正をBackground優先度で予約する。
        vm.ActiveTab = otherTab;
        window.CaptureRenderedFrame().Should().NotBeNull();
        Dispatcher.UIThread.RunJobs();

        vm.ActiveTab = mdTab;
        for (var i = 0; i < 3; i++)
        {
            window.CaptureRenderedFrame().Should().NotBeNull();
            Dispatcher.UIThread.RunJobs();
        }

        // プレビュー本文の後方のブロックをダブルクリックして編集モードへ切り替える。ターゲットは
        // 初期スクロール位置（先頭）では画面外にあるため、まずBringIntoViewでプレビューの
        // ScrollViewerをスクロールしてから実座標を取る。
        var target = pane.MarkdownPreviewHost.GetVisualDescendants().OfType<SelectableTextBlock>()
            .Single(b => (b.Text ?? b.Inlines?.Text ?? string.Empty).Contains("段落70の本文です"));
        target.BringIntoView();
        window.CaptureRenderedFrame().Should().NotBeNull();
        var point = target.TranslatePoint(new Point(4, 4), window)!.Value;
        point.Y.Should().BeInRange(0, 700,
            "テスト条件として、ダブルクリック対象はBringIntoViewでウィンドウの可視範囲内に来ている必要がある");
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);

        mdTab.ShowMarkdownPreview.Should().BeFalse("ダブルクリックで編集モードへ切り替わったはず");
        pane.Editor.IsVisible.Should().BeTrue();

        var expectedLine = pane.Editor.Document.GetLineByOffset(
            pane.Editor.Document.Text.IndexOf("段落70の本文です", StringComparison.Ordinal)).LineNumber;
        pane.Editor.TextArea.Caret.Line.Should().Be(expectedLine,
            "ダブルクリックした段落に対応する行へカーソルが置かれているはず");

        // レイアウト・遅延補正を含めて完全に落ち着かせてから、最終的なスクロール位置を確認する。
        for (var i = 0; i < 3; i++)
        {
            window.CaptureRenderedFrame().Should().NotBeNull();
            Dispatcher.UIThread.RunJobs();
        }

        var offset = ((IScrollable)pane.Editor.TextArea).Offset.Y;
        offset.Should().BeGreaterThan(0,
            "ダブルクリックした段落は文書の後方にあり、編集モードでは実際にスクロールしているはず" +
            "（タブ再訪時点の古い位置＝0へ巻き戻っていたら失敗する）");
    }
}
