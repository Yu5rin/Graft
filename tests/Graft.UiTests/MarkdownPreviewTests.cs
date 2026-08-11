using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Graft.Infra;
using Graft.Platform;
using Graft.Platform.Null;
using Graft.Themes;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests;

/// <summary>
/// Markdownプレビュー機能（利用者指示）の回帰テスト。
///
/// 検証する内容:
/// - .mdファイルを開くと既定でプレビュー表示になり、非.mdファイルは影響を受けないこと。
/// - 切替ボタン・ダブルクリック・Escでプレビュー⇔編集を切り替えられ、Escは検索欄のEscと
///   衝突しないこと。
/// - モードがタブごとに記憶されること（一度編集にしたタブは開いている間は編集のまま）。
/// - 未保存の編集内容がプレビューに反映されること（ディスクを読み直さない）。
/// - コードブロックに言語指定があれば構文強調が掛かり、無ければ色を付けないこと。
/// - 相対リンクでタブが開くこと・存在しないリンク先は例外にならず穏当に知らせること。
/// - 外部リンクは確認ダイアログを経てからのみブラウザ起動へ進み、拒否時は開かないこと。
/// - ダブルクリックした段落に対応する行へカーソルが置かれること。
/// - チェックリストが表示専用（クリックしても状態・ファイルが変わらない）で描画されること。
/// - サイズ上限（文字数・行数）を超えるMarkdownは編集モードで開き、理由が画面上に出ること。
///
/// 外部リンク・存在しないリンク先の確認は、<see cref="EditorPane.MarkdownLinkDialogs"/>・
/// <see cref="EditorPane.OpenExternalLinkAction"/>（テスト用の差し替え口。
/// <c>Graft/AssemblyInfo.cs</c>のInternalsVisibleTo経由）を使う。<see cref="AvaloniaDialogService"/>が
/// 組み立てる確認ダイアログはヘッドレステストから実際のボタン操作をする手段が無いため
/// （<c>DialogKeyboardCoverageTests</c>のコメントに同じ制約の記載がある）で、この差し替え口が
/// 唯一の現実的な検証手段となる。
/// </summary>
public class MarkdownPreviewTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-md-preview", Guid.NewGuid().ToString("N"));
    private readonly ShownWindowTracker _windows = new();

    public MarkdownPreviewTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _windows.Dispose();
        TempDirectoryCleanup.TryDeleteRecursive(_root);
        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------------
    // 1. 既定表示・非Markdownの非対象
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = ".mdファイルを開くと既定でプレビュー表示になる")]
    public async Task Markdownファイルは既定でプレビュー表示になる()
    {
        var (window, pane, vm) = await OpenPaneAsync().ConfigureAwait(true);
        var tab = await OpenFileAsync(vm, "doc.md", "# 見出し\n\n本文です。\n").ConfigureAwait(true);
        window.CaptureRenderedFrame();

        tab.IsMarkdownFile.Should().BeTrue();
        tab.ShowMarkdownPreview.Should().BeTrue();
        pane.MarkdownPreviewHost.IsVisible.Should().BeTrue();
        pane.Editor.IsVisible.Should().BeFalse();
        pane.MarkdownModeBar.IsVisible.Should().BeTrue();

        var heading = FindTextBlocks(pane).FirstOrDefault(t => t == "見出し");
        heading.Should().NotBeNull("見出しがプレビューに描画されているはず");
    }

    [AvaloniaFact(DisplayName = "非Markdownファイルはプレビュー機能の対象外で、常に編集画面のまま")]
    public async Task 非Markdownファイルはプレビュー機能の影響を受けない()
    {
        var (window, pane, vm) = await OpenPaneAsync().ConfigureAwait(true);
        var tab = await OpenFileAsync(vm, "sample.txt", "普通のテキストです。\n").ConfigureAwait(true);
        window.CaptureRenderedFrame();

        tab.IsMarkdownFile.Should().BeFalse();
        pane.MarkdownModeBar.IsVisible.Should().BeFalse();
        pane.Editor.IsVisible.Should().BeTrue();
        pane.MarkdownPreviewHost.IsVisible.Should().BeFalse();
    }

    // ------------------------------------------------------------------
    // 2. 切替ボタン・Esc・タブごとの記憶
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "切替ボタンで編集モードへ切り替えられ、Escでプレビューへ戻る")]
    public async Task 切替ボタンとEscでモードを切り替えられる()
    {
        var (window, pane, vm) = await OpenPaneAsync().ConfigureAwait(true);
        var tab = await OpenFileAsync(vm, "doc.md", "# 見出し\n\n本文です。\n").ConfigureAwait(true);
        window.CaptureRenderedFrame();

        pane.MarkdownModeToggleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.CaptureRenderedFrame();

        tab.ShowMarkdownPreview.Should().BeFalse();
        pane.Editor.IsVisible.Should().BeTrue();
        pane.MarkdownPreviewHost.IsVisible.Should().BeFalse();

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        window.CaptureRenderedFrame();

        tab.ShowMarkdownPreview.Should().BeTrue("Escで編集からプレビューへ戻るはず");
        pane.MarkdownPreviewHost.IsVisible.Should().BeTrue();
        pane.Editor.IsVisible.Should().BeFalse();
    }

    [AvaloniaFact(DisplayName = "検索オーバーレイが開いている間はEscがそちらへ優先され、プレビューへは戻らない")]
    public async Task 検索欄が開いている間はEscを奪わない()
    {
        var (window, pane, vm) = await OpenPaneAsync().ConfigureAwait(true);
        var tab = await OpenFileAsync(vm, "doc.md", "# 見出し\n\n本文です。\n").ConfigureAwait(true);
        window.CaptureRenderedFrame();

        pane.MarkdownModeToggleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.CaptureRenderedFrame();
        tab.ShowMarkdownPreview.Should().BeFalse();

        pane.Search.OpenFind();
        window.CaptureRenderedFrame();
        pane.Search.ViewModel.IsOpen.Should().BeTrue("前提: 検索欄が開いている");

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        window.CaptureRenderedFrame();

        pane.Search.ViewModel.IsOpen.Should().BeFalse("検索欄側のEscが実行されるはず");
        tab.ShowMarkdownPreview.Should().BeFalse("検索欄のEscに奪われている間はプレビューへ戻らないはず（既存機能を優先）");
    }

    [AvaloniaFact(DisplayName = "一度編集にしたタブは、別タブへ切り替えて戻ってきても編集モードのまま")]
    public async Task モードはタブごとに記憶される()
    {
        var (window, pane, vm) = await OpenPaneAsync().ConfigureAwait(true);
        var tabA = await OpenFileAsync(vm, "a.md", "# A\n\n本文A\n").ConfigureAwait(true);
        var tabB = await OpenFileAsync(vm, "b.md", "# B\n\n本文B\n").ConfigureAwait(true);
        window.CaptureRenderedFrame();

        vm.ActiveTab = tabA;
        window.CaptureRenderedFrame();
        pane.MarkdownModeToggleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        tabA.ShowMarkdownPreview.Should().BeFalse();

        vm.ActiveTab = tabB;
        window.CaptureRenderedFrame();
        tabB.ShowMarkdownPreview.Should().BeTrue("Bは触れていないので既定のプレビューのまま");
        pane.MarkdownPreviewHost.IsVisible.Should().BeTrue();

        vm.ActiveTab = tabA;
        window.CaptureRenderedFrame();
        tabA.ShowMarkdownPreview.Should().BeFalse("Aは編集モードのまま記憶されているはず");
        pane.Editor.IsVisible.Should().BeTrue();
    }

    // ------------------------------------------------------------------
    // 3. 未保存の編集内容の反映（必須要件）
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "未保存の編集内容がプレビューに反映される（ディスクは読み直さない）")]
    public async Task 未保存の編集がプレビューに反映される()
    {
        var (window, pane, vm) = await OpenPaneAsync().ConfigureAwait(true);
        var path = Path.Combine(_root, "doc.md");
        await File.WriteAllTextAsync(path, "# 見出し\n\n元の本文。\n").ConfigureAwait(true);
        var opened = await vm.OpenFileAsync(path).ConfigureAwait(true);
        opened.IsSuccess.Should().BeTrue();
        var tab = opened.Value;
        window.CaptureRenderedFrame();

        // 編集モードへ切り替えて本文を書き換える（保存はしない）。
        pane.MarkdownModeToggleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.CaptureRenderedFrame();
        pane.Editor.Document.Text = "# 見出し\n\n編集後の本文（未保存）。\n";
        tab.Session.IsModified.Should().BeTrue("保存前は未保存扱いのはず");

        (await File.ReadAllTextAsync(path).ConfigureAwait(true)).Should().Contain(
            "元の本文", "ディスク上の内容はまだ書き換わっていないはず");

        // プレビューへ戻す。
        pane.MarkdownModeToggleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.CaptureRenderedFrame();

        FindTextBlocks(pane).Should().Contain(
            t => t.Contains("編集後の本文"), "保存前でも編集中バッファの内容がプレビューへ反映されるはず");
    }

    // ------------------------------------------------------------------
    // 4. コードブロックの構文強調
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "言語指定付きコードブロックは構文強調され、指定なしは色を付けない")]
    public async Task コードブロックに構文強調がかかる()
    {
        var md = "```python\ndef highlighted_func():\n    return 1\n```\n\n```\nplain_block_line\n```\n";
        var (window, pane, vm) = await OpenPaneAsync().ConfigureAwait(true);
        await OpenFileAsync(vm, "doc.md", md).ConfigureAwait(true);
        window.CaptureRenderedFrame();

        var codeBlocks = pane.MarkdownPreviewHost.GetVisualDescendants().OfType<SelectableTextBlock>().ToList();

        var highlighted = codeBlocks.Single(b => (b.Text ?? b.Inlines?.Text ?? string.Empty).Contains("highlighted_func"));
        highlighted.Inlines.Should().NotBeNull();
        highlighted.Inlines!.Count.Should().BeGreaterThan(
            1, "言語指定があるコードブロックはトークンごとに複数のRunへ分割されるはず");

        var plain = codeBlocks.Single(b => (b.Text ?? b.Inlines?.Text ?? string.Empty).Contains("plain_block_line"));
        (plain.Text ?? string.Empty).Should().Contain(
            "plain_block_line", "言語指定が無いコードブロックはTextプロパティでそのまま描画されるはず");
    }

    // ------------------------------------------------------------------
    // 5. リンク（相対・外部）
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "相対リンクをクリックするとGraftのタブとして開く")]
    public async Task 相対リンクでタブが開く()
    {
        var (window, pane, vm) = await OpenPaneAsync().ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(_root, "other.md"), "# 別のファイル\n").ConfigureAwait(true);
        await OpenFileAsync(vm, "doc.md", "[別のファイルへ](./other.md)\n").ConfigureAwait(true);
        window.CaptureRenderedFrame();

        var link = pane.MarkdownPreviewHost.GetVisualDescendants().OfType<Button>()
            .Single(b => Equals(b.Content, "別のファイルへ"));
        link.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        await PumpUntilAsync(() => vm.Tabs.Any(t => t.IsDocument && t.Session.FileName == "other.md")).ConfigureAwait(true);

        vm.Tabs.Should().Contain(t => t.IsDocument && t.Session.FileName == "other.md");
        vm.ActiveTab!.IsDocument.Should().BeTrue();
        vm.ActiveTab!.Session.FileName.Should().Be("other.md", "相対リンクを開いたタブがアクティブになるはず");
    }

    [AvaloniaFact(DisplayName = "存在しない相対リンクは例外にならず穏当に知らせる")]
    public async Task 存在しない相対リンクは穏当に知らせる()
    {
        var (window, pane, vm) = await OpenPaneAsync().ConfigureAwait(true);
        await OpenFileAsync(vm, "doc.md", "[無いファイル](./missing.md)\n").ConfigureAwait(true);
        window.CaptureRenderedFrame();

        var fake = new RecordingDialogService();
        pane.MarkdownLinkDialogs = fake;

        var link = pane.MarkdownPreviewHost.GetVisualDescendants().OfType<Button>()
            .Single(b => Equals(b.Content, "無いファイル"));
        var act = () => link.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        act.Should().NotThrow();

        await PumpUntilAsync(() => fake.MessageCalls.Count > 0).ConfigureAwait(true);

        fake.MessageCalls.Should().ContainSingle();
        vm.Tabs.Should().HaveCount(1, "存在しないリンク先ではタブが増えてはならない");
    }

    [AvaloniaFact(DisplayName = "外部リンクは確認ダイアログで承認したときだけブラウザ起動へ進む")]
    public async Task 外部リンクは確認を経てから開く()
    {
        var (window, pane, vm) = await OpenPaneAsync().ConfigureAwait(true);
        await OpenFileAsync(vm, "doc.md", "[外部サイト](https://example.com/path)\n").ConfigureAwait(true);
        window.CaptureRenderedFrame();

        var fake = new RecordingDialogService { ConfirmResult = true };
        var openedUrls = new List<string>();
        pane.MarkdownLinkDialogs = fake;
        pane.OpenExternalLinkAction = openedUrls.Add;

        var link = pane.MarkdownPreviewHost.GetVisualDescendants().OfType<Button>()
            .Single(b => Equals(b.Content, "外部サイト"));
        link.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        await PumpUntilAsync(() => openedUrls.Count > 0).ConfigureAwait(true);

        fake.ConfirmCalls.Should().ContainSingle();
        fake.ConfirmCalls[0].Message.Should().Contain("https://example.com/path");
        openedUrls.Should().ContainSingle().Which.Should().Be("https://example.com/path");
    }

    [AvaloniaFact(DisplayName = "外部リンクの確認を拒否すると開かない（無警告で開かない）")]
    public async Task 外部リンクの確認を拒否すると開かない()
    {
        var (window, pane, vm) = await OpenPaneAsync().ConfigureAwait(true);
        await OpenFileAsync(vm, "doc.md", "[外部サイト](https://example.com/path)\n").ConfigureAwait(true);
        window.CaptureRenderedFrame();

        var fake = new RecordingDialogService { ConfirmResult = false };
        var openedUrls = new List<string>();
        pane.MarkdownLinkDialogs = fake;
        pane.OpenExternalLinkAction = openedUrls.Add;

        var link = pane.MarkdownPreviewHost.GetVisualDescendants().OfType<Button>()
            .Single(b => Equals(b.Content, "外部サイト"));
        link.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        await PumpUntilAsync(() => fake.ConfirmCalls.Count > 0).ConfigureAwait(true);
        // 拒否後にもし遅れて開かれてしまう実装ミスが無いか、少し待ってから確認する。
        await Task.Delay(50).ConfigureAwait(true);
        Dispatcher.UIThread.RunJobs();

        openedUrls.Should().BeEmpty("確認を拒否した場合は無警告で開いてはならない");
    }

    // ------------------------------------------------------------------
    // 6. ダブルクリックでのカーソル移動
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "プレビュー段落をダブルクリックすると編集モードへ切り替わり対応する行にカーソルが置かれる")]
    public async Task ダブルクリックで対応する行にカーソルが移動する()
    {
        var md = "# 見出し\n\n1行目の段落。\n\n2行目の段落（ここをダブルクリック）。\n";
        var (window, pane, vm) = await OpenPaneAsync().ConfigureAwait(true);
        var tab = await OpenFileAsync(vm, "doc.md", md).ConfigureAwait(true);
        window.CaptureRenderedFrame();

        var target = pane.MarkdownPreviewHost.GetVisualDescendants().OfType<SelectableTextBlock>()
            .Single(b => (b.Text ?? b.Inlines?.Text ?? string.Empty).Contains("2行目の段落"));

        var point = target.TranslatePoint(new Point(4, 4), window)!.Value;
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        window.CaptureRenderedFrame();

        tab.ShowMarkdownPreview.Should().BeFalse("ダブルクリックで編集モードへ切り替わるはず");
        pane.Editor.IsVisible.Should().BeTrue();
        pane.Editor.TextArea.Caret.Line.Should().Be(5, "「2行目の段落」はMarkdown内の5行目にあるはず");
    }

    // ------------------------------------------------------------------
    // 7. チェックリスト（.mdプレビューでは操作可能。方針変更の回帰）
    //
    // 【方針変更の経緯】当初「チェックリストは表示専用（クリックしてもファイルを書き換えない）」
    // という指示だったが、「チェックボックスのON/OFFができない」との利用者指摘を受けて撤回され、
    // .mdプレビューではクリック・キーボードで実際にON/OFFでき、編集中バッファ
    // （<c>DocumentSession.Document</c>）へ書き戻す方針に変わった（EditorPane.MarkdownPreview.cs
    // のOnMarkdownChecklistToggled参照）。取扱説明書（<see cref="ManualWindow"/>）は埋め込み
    // リソースで編集対象が無いため、引き続き表示専用のまま（別クラスManualWindowTests、および
    // ManualMarkdownRendererGfmTestsのレンダラ単体テストで確認する）。
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "チェックリストはCheckBoxとして描画され、初期状態は本文の[ ]/[x]と一致する")]
    public async Task チェックリストの初期状態が本文と一致する()
    {
        var original = "- [ ] 未完了の項目\n- [x] 完了した項目\n";
        var (window, pane, vm) = await OpenPaneAsync().ConfigureAwait(true);
        await OpenFileAsync(vm, "doc.md", original).ConfigureAwait(true);
        window.CaptureRenderedFrame();

        var checkboxes = pane.MarkdownPreviewHost.GetVisualDescendants().OfType<CheckBox>().ToList();
        checkboxes.Should().HaveCount(2);
        checkboxes.Should().Contain(c => c.IsChecked == false);
        checkboxes.Should().Contain(c => c.IsChecked == true);
    }

    [AvaloniaFact(DisplayName = "チェックリストをクリックすると編集中バッファが書き換わり、未保存になる")]
    public async Task チェックリストのクリックでバッファが書き換わり未保存になる()
    {
        var path = Path.Combine(_root, "doc.md");
        var original = "- [ ] 未完了の項目\n- [x] 完了した項目\n";
        var (window, pane, vm) = await OpenPaneAsync().ConfigureAwait(true);
        var tab = await OpenFileAsync(vm, "doc.md", original).ConfigureAwait(true);
        window.CaptureRenderedFrame();

        var uncheckedBox = pane.MarkdownPreviewHost.GetVisualDescendants().OfType<CheckBox>().Single(c => c.IsChecked == false);
        uncheckedBox.IsHitTestVisible.Should().BeTrue(".mdプレビューのチェックボックスは操作可能でなければならない");
        var point = uncheckedBox.TranslatePoint(
            new Point(uncheckedBox.Bounds.Width / 2, uncheckedBox.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        window.CaptureRenderedFrame();

        tab.Session.Document.Text.Should().Be(
            "- [x] 未完了の項目\n- [x] 完了した項目\n", "クリックした行の[ ]が[x]へ書き換わるはず");
        tab.Session.IsModified.Should().BeTrue("編集操作なので未保存マークが付くはず");
        (await File.ReadAllTextAsync(path).ConfigureAwait(true)).Should().Be(
            original, "自動保存はしない。ディスクはCtrl+Sまで変わらないはず");
    }

    [AvaloniaFact(DisplayName = "チェックリストのクリックはCtrl+Zで取り消せる")]
    public async Task チェックリストのクリックはCtrlZで取り消せる()
    {
        var original = "- [ ] 未完了の項目\n";
        var (window, pane, vm) = await OpenPaneAsync().ConfigureAwait(true);
        var tab = await OpenFileAsync(vm, "doc.md", original).ConfigureAwait(true);
        window.CaptureRenderedFrame();

        var checkbox = pane.MarkdownPreviewHost.GetVisualDescendants().OfType<CheckBox>().Single();
        var point = checkbox.TranslatePoint(new Point(checkbox.Bounds.Width / 2, checkbox.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        window.CaptureRenderedFrame();
        tab.Session.Document.Text.Should().Be("- [x] 未完了の項目\n");

        tab.Session.Document.UndoStack.Undo();

        tab.Session.Document.Text.Should().Be(original, "Ctrl+Zに相当するUndoStack.Undo()で元に戻るはず");
        tab.Session.IsModified.Should().BeFalse("元ファイルの状態まで戻れば未保存マークも消えるはず");
    }

    [AvaloniaFact(DisplayName = "チェックリストのクリック後、プレビューの表示も追従する")]
    public async Task チェックリストのクリック後プレビュー表示が追従する()
    {
        var original = "- [ ] 未完了の項目\n";
        var (window, pane, vm) = await OpenPaneAsync().ConfigureAwait(true);
        await OpenFileAsync(vm, "doc.md", original).ConfigureAwait(true);
        window.CaptureRenderedFrame();

        var checkbox = pane.MarkdownPreviewHost.GetVisualDescendants().OfType<CheckBox>().Single();
        var point = checkbox.TranslatePoint(new Point(checkbox.Bounds.Width / 2, checkbox.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        await PumpUntilAsync(() =>
            pane.MarkdownPreviewHost.GetVisualDescendants().OfType<CheckBox>().Any(c => c.IsChecked == true)).ConfigureAwait(true);
        window.CaptureRenderedFrame();

        pane.MarkdownPreviewHost.GetVisualDescendants().OfType<CheckBox>().Single().IsChecked.Should().BeTrue(
            "再描画後のチェックボックスも書き換え後の本文（[x]）と一致するはず");
    }

    [AvaloniaFact(DisplayName = "インデントした入れ子のチェックリストをクリックしても、インデントと後続テキストが壊れない")]
    public async Task ネストしたチェックリストのクリックでインデントと後続テキストが保たれる()
    {
        var original = "- [ ] 親項目\n  - [ ] 子項目 それでも残す末尾\n";
        var (window, pane, vm) = await OpenPaneAsync().ConfigureAwait(true);
        var tab = await OpenFileAsync(vm, "doc.md", original).ConfigureAwait(true);
        window.CaptureRenderedFrame();

        var checkboxes = pane.MarkdownPreviewHost.GetVisualDescendants().OfType<CheckBox>().ToList();
        checkboxes.Should().HaveCount(2, "親・子それぞれチェックボックスが描画されるはず");
        var childBox = checkboxes[1];
        var point = childBox.TranslatePoint(new Point(childBox.Bounds.Width / 2, childBox.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        window.CaptureRenderedFrame();

        tab.Session.Document.Text.Should().Be(
            "- [ ] 親項目\n  - [x] 子項目 それでも残す末尾\n",
            "子項目のインデント（2スペース）と末尾のテキストを保ったまま、その行のマークだけが変わるはず");
    }

    // ------------------------------------------------------------------
    // 8. サイズ上限を超えた場合のフォールバック
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "文字数上限を超えるMarkdownは編集モードで開き、理由が画面上に表示される")]
    public async Task 文字数上限超過時は編集モードで開き理由を表示する()
    {
        var longLine = new string('あ', 5000);
        var sb = new StringBuilder();
        for (var i = 0; i < 40; i++) sb.Append(longLine).Append('\n'); // 200,000字超・行数(40)は上限未満
        var (window, pane, vm) = await OpenPaneAsync().ConfigureAwait(true);
        var tab = await OpenFileAsync(vm, "big.md", sb.ToString()).ConfigureAwait(true);
        window.CaptureRenderedFrame();

        tab.MarkdownPreviewUnavailable.Should().BeTrue();
        tab.ShowMarkdownPreview.Should().BeFalse();
        pane.Editor.IsVisible.Should().BeTrue();
        pane.MarkdownPreviewHost.IsVisible.Should().BeFalse();
        pane.MarkdownModeToggleButton.IsEnabled.Should().BeFalse("上限超過時は切替ボタンを無効化する");

        var reasonShown = pane.MarkdownModeBar.GetVisualDescendants().OfType<TextBlock>()
            .Any(t => (t.Text ?? string.Empty).Contains("大きい"));
        reasonShown.Should().BeTrue("理由が画面上に表示されるはず");
    }

    [AvaloniaFact(DisplayName = "行数上限を超えるMarkdownは編集モードで開く")]
    public async Task 行数上限超過時は編集モードで開く()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 9000; i++) sb.Append('行').Append(i).Append('\n'); // 8,000行超・文字数は上限未満
        var (window, pane, vm) = await OpenPaneAsync().ConfigureAwait(true);
        var tab = await OpenFileAsync(vm, "manylines.md", sb.ToString()).ConfigureAwait(true);
        window.CaptureRenderedFrame();

        tab.MarkdownPreviewUnavailable.Should().BeTrue();
        tab.ShowMarkdownPreview.Should().BeFalse();
        pane.Editor.IsVisible.Should().BeTrue();
    }

    // ------------------------------------------------------------------
    // 9. テーマ切替時の再描画（実機検証で発覚した不具合の回帰）
    // ------------------------------------------------------------------

    /// <summary>
    /// 実機（Xvfb）検証で発覚した不具合の回帰テスト。プレビュー表示中に設定画面から
    /// テーマ（ライト/ダーク）を切り替えると、既に構築済みのブロックが再描画されず
    /// 文字が読めなくなる（前景色が旧テーマのまま、あるいは背景色と同化する）不具合があった。
    /// <see cref="EditorPane"/>がテーマ切替（<see cref="ThemeManager.ThemeChanged"/>）を購読して
    /// 表示中のプレビューを再構築することを、ブロックのControlインスタンスが再構築のたびに
    /// 入れ替わることで確認する（ヘッドレステストでは実際のピクセル色までは検証できないため、
    /// 「テーマ切替のたびに新しいテーマ色で組み立て直している」ことを instancing で代替確認する）。
    /// </summary>
    [AvaloniaFact(DisplayName = "実機不具合の回帰: テーマ切替時にプレビュー表示中のブロックが再構築される")]
    public async Task テーマ切替でプレビューが再描画される()
    {
        var (window, pane, vm) = await OpenPaneAsync().ConfigureAwait(true);
        await OpenFileAsync(vm, "doc.md", "# 見出し\n\n本文です。\n").ConfigureAwait(true);
        window.CaptureRenderedFrame();

        pane.MarkdownPreviewHost.IsVisible.Should().BeTrue();
        var beforeBlock = pane.MarkdownPreviewHost.GetVisualDescendants().OfType<SelectableTextBlock>().First();

        var originalTheme = ThemeManager.SelectedTheme;
        try
        {
            ThemeManager.SetTheme(originalTheme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark);
            window.CaptureRenderedFrame();

            var afterBlock = pane.MarkdownPreviewHost.GetVisualDescendants().OfType<SelectableTextBlock>().First();
            ReferenceEquals(beforeBlock, afterBlock).Should().BeFalse(
                "テーマ切替のたびにプレビューを再構築し、新しいテーマ色を反映させる必要がある"
                + "（実機で確認した不具合: 再構築しないと既存ブロックの文字色が更新されず読めなくなる）");
        }
        finally
        {
            ThemeManager.SetTheme(originalTheme);
        }
    }

    // ------------------------------------------------------------------
    // ヘルパ
    // ------------------------------------------------------------------

    private async Task<(Window Window, EditorPane Pane, EditorPaneViewModel Vm)> OpenPaneAsync()
    {
        var vm = new EditorPaneViewModel(new Settings(), new NullDialogService(), new AvaloniaUiServices());
        vm.SetProject(_root);
        var pane = new EditorPane { DataContext = vm };
        var window = _windows.Track(new Window { Width = 1000, Height = 800, Content = pane });
        window.Show();
        window.CaptureRenderedFrame().Should().NotBeNull();
        await Task.CompletedTask;
        return (window, pane, vm);
    }

    private async Task<EditorTabViewModel> OpenFileAsync(EditorPaneViewModel vm, string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content).ConfigureAwait(true);
        var result = await vm.OpenFileAsync(path).ConfigureAwait(true);
        result.IsSuccess.Should().BeTrue();
        return result.Value;
    }

    /// <summary>プレビュー内の全<see cref="SelectableTextBlock"/>の表示文字列（Text優先、無ければInlines.Text）。</summary>
    private static IEnumerable<string> FindTextBlocks(EditorPane pane)
        => pane.MarkdownPreviewHost.GetVisualDescendants().OfType<SelectableTextBlock>()
            .Select(b => b.Text ?? b.Inlines?.Text ?? string.Empty);

    /// <summary>
    /// ディスパッチャのジョブを出し切りながら、条件が満たされるかタイムアウトまで待つ。
    /// Button.Clickから発火する非同期処理（SafeHandler.RunAsync経由）の完了待ちに使う。
    /// </summary>
    private static async Task PumpUntilAsync(Func<bool> condition, int timeoutMs = 4000)
    {
        var elapsed = 0;
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return;
            await Task.Delay(10).ConfigureAwait(true);
            elapsed += 10;
            if (elapsed >= timeoutMs) return; // タイムアウトしても戻る。以降のAssertで失敗として顕在化させる。
        }
    }

    /// <summary>
    /// <see cref="IDialogService"/>のテスト用フェイク。呼び出し内容を記録し、
    /// <see cref="ConfirmResult"/>で確認ダイアログの結果を制御できる。
    /// </summary>
    private sealed class RecordingDialogService : IDialogService
    {
        public bool ConfirmResult { get; set; } = true;

        public List<(string Title, string Message)> ConfirmCalls { get; } = new();
        public List<(string Title, string Message)> MessageCalls { get; } = new();

        public Task<bool> ConfirmAsync(string title, string message)
        {
            ConfirmCalls.Add((title, message));
            return Task.FromResult(ConfirmResult);
        }

        public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel)
            => Task.FromResult<bool?>(ConfirmResult);

        public Task<string?> PromptAsync(string title, string message, string? initial = null)
            => Task.FromResult<string?>(initial);

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync(string title, IReadOnlyList<string>? extensions = null)
            => Task.FromResult<string?>(null);

        public Task<string?> SaveFileAsync(string title, string suggestedFileName, IReadOnlyList<string>? extensions = null)
            => Task.FromResult<string?>(null);

        public Task ShowMessageAsync(string title, string message)
        {
            MessageCalls.Add((title, message));
            return Task.CompletedTask;
        }
    }
}
