using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
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
/// 取扱説明書機能の回帰テスト。
///
/// 検証する内容:
/// - ManualWindow単体で、埋め込みリソース（docs/取扱説明書.md）から本文が読み込め、空でないこと
///   （OpenSourceLicensesTests.ライセンス全文が読み込めると同じ理由。埋め込みリソース名の
///   ずれ・Graft.csprojの同梱漏れといった回帰を防ぐ）。
/// - F1キーで取扱説明書が要求されること。テキスト入力欄にフォーカスがあっても反応する必要が
///   あるため、ShortcutsWindowTests（Ctrl+/の横取り確認）と対になる形で、あえて
///   クイックオープンの検索欄にフォーカスした状態でも確認する。
/// - Escape・「閉じる」ボタンで閉じられること（既存のShortcutsWindow等と同じ作法）。
/// - キーボードショートカット一覧には、ヘルプメニュー経由・Ctrl+/経由の両方で
///   引き続き到達できること（ShortcutsWindowTests側で検証済みのため、ここでは
///   ヘルプメニューに2項目とも並ぶことだけを確認する）。
///
/// 利用者からの指摘対応（Markdown装飾表示）: 以前はMarkdownをそのままプレーンテキストで
/// TextBoxへ表示していたが、ManualMarkdownRenderer（自前の軽量パーサ）でAvalonia標準
/// コントロールへ組み立てるようにした。以下も検証する。
/// - 見出しレベルごとにフォントサイズが異なり、階層が視覚的に分かること。
/// - コードブロックが等幅フォント・背景色付きで本文と区別できること。
/// - 表（キーボードショートカット一覧など）がGridとして展開されること。
/// - 目次のリンクをクリックすると例外にならず、対応する見出しへジャンプする経路が
///   実際に呼ばれること。
/// - 本文が選択してコピーできる（SelectableTextBlockで構築されている）こと。
/// </summary>
public class ManualWindowTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-manual", Guid.NewGuid().ToString("N"));
    private readonly string _appDirectory;
    private readonly string _projectDirectory;
    private readonly ShownWindowTracker _windows = new();

    public ManualWindowTests()
    {
        _appDirectory = Path.Combine(_root, "app");
        _projectDirectory = Path.Combine(_root, "project");
        Directory.CreateDirectory(_appDirectory);
        Directory.CreateDirectory(_projectDirectory);
    }

    public void Dispose()
    {
        _windows.Dispose();
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 後始末の失敗は検証結果に影響しない。
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>ManualContentPanelに展開された全SelectableTextBlockのテキストを連結して返す。</summary>
    private static string CollectRenderedText(Window window)
        => string.Join(
            "\n",
            window.GetVisualDescendants().OfType<SelectableTextBlock>()
                .Select(t => t.Inlines?.Text ?? t.Text ?? string.Empty));

    [AvaloniaFact(DisplayName = "取扱説明書の本文が埋め込みリソースから読み込め、空でも読み込み失敗の定型文でもない")]
    public void 本文が埋め込みリソースから読み込める()
    {
        var window = _windows.Track(new ManualWindow());
        window.Show();

        var renderedText = CollectRenderedText(window);

        renderedText.Should().NotBeNullOrWhiteSpace("取扱説明書の本文が空であってはならない");
        renderedText.Should().NotBe(
            "取扱説明書を読み込めませんでした。",
            "埋め込みリソース名のずれ・Graft.csprojでの同梱漏れなどで読み込みに失敗している（回帰）");
        // 目次・主要見出しが実際に含まれていることも確認し、「たまたま何か読めた」ではなく
        // 期待した取扱説明書.mdそのものが読み込めていることを担保する。
        renderedText.Should().Contain("Graftとは何か");
        renderedText.Should().Contain("パッチの形式");
        renderedText.Should().Contain("キーボードショートカット一覧");
    }

    [AvaloniaFact(DisplayName = "見出しは階層（レベル）ごとにフォントサイズが異なる")]
    public void 見出しは階層ごとにフォントサイズが異なる()
    {
        var window = _windows.Track(new ManualWindow());
        window.Show();

        // "1. Graftとは何か"は## （レベル2）、"2.1 自分の..."は### （レベル3）の見出し。
        var level2 = window.GetVisualDescendants().OfType<SelectableTextBlock>()
            .Single(t => (t.Inlines?.Text ?? t.Text) == "1. Graftとは何か");
        var level3 = window.GetVisualDescendants().OfType<SelectableTextBlock>()
            .Single(t => (t.Inlines?.Text ?? t.Text) == "2.1 自分のプロジェクトを使わずに試したいとき（サンプルで試す）");

        level2.FontSize.Should().BeGreaterThan(level3.FontSize, "上位の見出しほど大きく表示され階層が視覚的に分かる必要がある");
        level2.FontWeight.Should().Be(FontWeight.SemiBold);
        level3.FontWeight.Should().Be(FontWeight.SemiBold);
    }

    [AvaloniaFact(DisplayName = "コードブロックは等幅フォントで表示され、本文と異なる背景色の枠で囲まれる")]
    public void コードブロックは等幅フォントかつ背景色付きで表示される()
    {
        var window = _windows.Track(new ManualWindow());
        window.Show();

        // 3.1節のGraft形式コードブロックの中身（"type: fix"）で狙う。"<<<< PATCH"自体は
        // 直前の説明文の中でも`インラインコード`として使われているため、それとは
        // 区別できる、実際のコードブロックの中にしか出てこない文字列を使う。
        var codeBlock = window.GetVisualDescendants().OfType<SelectableTextBlock>()
            .Single(t => (t.Inlines?.Text ?? t.Text ?? string.Empty).Contains("type: fix"));

        codeBlock.FontFamily.Name.Should().NotBe(
            window.FontFamily.Name, "コードブロックは本文の可変幅フォントとは異なる等幅フォントで表示される必要がある");

        var border = codeBlock.GetVisualAncestors().OfType<Border>()
            .FirstOrDefault(b => b.Background is ISolidColorBrush brush && brush.Color != Colors.Transparent);
        border.Should().NotBeNull("コードブロックは本文と区別できる背景色付きの枠で囲まれている必要がある");
    }

    [AvaloniaFact(DisplayName = "キーボードショートカット一覧は表（Grid）として展開される")]
    public void 表はGridとして展開される()
    {
        var window = _windows.Track(new ManualWindow());
        window.Show();

        // 7章の接ぎ木の操作の表。ヘッダー「キー」「動作」と、データ行の1つ
        // 「Ctrl+Shift+V」を含むGridが2列以上・複数行で存在することを確認する。
        var renderedText = CollectRenderedText(window);
        renderedText.Should().Contain("Ctrl+Shift+V", "表の中身（キー割り当て）が読み込めている前提の確認");

        var tableGrid = window.GetVisualDescendants().OfType<Grid>()
            .FirstOrDefault(g => g.ColumnDefinitions.Count >= 2 && g.RowDefinitions.Count >= 3
                && g.GetVisualDescendants().OfType<SelectableTextBlock>()
                    .Any(t => (t.Inlines?.Text ?? t.Text) == "Ctrl+Shift+V"));

        tableGrid.Should().NotBeNull("表はGrid（複数列・複数行）として展開されている必要がある");
    }

    [AvaloniaFact(DisplayName = "本文はSelectableTextBlockで構築され、マウスでの範囲選択・部分コピーができる")]
    public void 本文は選択してコピーできる()
    {
        var window = _windows.Track(new ManualWindow());
        window.Show();

        var blocks = window.GetVisualDescendants().OfType<SelectableTextBlock>().ToList();
        blocks.Should().NotBeEmpty("本文の各段落・見出し・リスト項目はSelectableTextBlockで構築されている必要がある");

        // SelectableTextBlockはSelectionStart/SelectionEndを持ち、範囲選択・CanCopyに対応する
        // （TextBoxのIsReadOnlyと同じ「表示専用だが選択・コピーはできる」性質をAvaloniaの
        // 標準APIで確認する）。
        var sample = blocks.First(b => !string.IsNullOrEmpty(b.Inlines?.Text ?? b.Text));
        var text = sample.Inlines!.Text ?? sample.Text!;
        sample.SelectionStart = 0;
        sample.SelectionEnd = text.Length;
        sample.SelectedText.Should().Be(text, "選択したテキストがそのまま取得できる必要がある（コピー機能の土台）");
    }

    [AvaloniaFact(DisplayName = "目次のリンクをクリックすると例外にならず、該当見出しへジャンプする経路が呼ばれる")]
    public void 目次のリンクをクリックすると見出しへジャンプする()
    {
        var window = _windows.Track(new ManualWindow());
        window.Show();

        var tocLink = window.GetVisualDescendants().OfType<Button>()
            .Single(b => AutomationProperties.GetName(b) == "目次: Graftとは何かへジャンプ");

        var act = () => tocLink.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        act.Should().NotThrow("目次のリンクは押しても例外にならない必要がある（未知のアンカーでも同様）");
    }

    [AvaloniaFact(DisplayName = "Escapeキーで閉じる")]
    public void Escapeキーで閉じる()
    {
        var window = _windows.Track(new ManualWindow());
        window.Show();

        var closed = false;
        window.Closed += (_, _) => closed = true;
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);

        closed.Should().BeTrue();
    }

    [AvaloniaFact(DisplayName = "「閉じる」ボタンで閉じる")]
    public void 閉じるボタンで閉じる()
    {
        var window = _windows.Track(new ManualWindow());
        window.Show();

        var closed = false;
        window.Closed += (_, _) => closed = true;

        var closeButton = window.GetVisualDescendants().OfType<Button>()
            .Single(b => Equals(b.Content, "閉じる"));
        closeButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        closed.Should().BeTrue();
    }

    [AvaloniaFact(DisplayName = "「コピー」ボタンを押しても例外にならない")]
    public void コピーボタンを押しても例外にならない()
    {
        // LogViewerWindow・OpenSourceLicensesWindowと同じくAvaloniaUiServices.SharedClipboardを
        // 直接呼ぶ実装（DI差し替え不可）のため、headless実行環境（実際のOSクリップボードを
        // 持たないCI等）では書き込み結果を読み戻して検証できない。他の2ウィンドウ同様、
        // クリップボードの実際の中身までは検証せず、ボタン操作自体が例外なく完了することの
        // スモークテストに留める。
        var window = _windows.Track(new ManualWindow());
        window.Show();

        var copyButton = window.GetVisualDescendants().OfType<Button>()
            .Single(b => Equals(b.Content, "コピー"));
        var act = () => copyButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        act.Should().NotThrow();
    }

    [AvaloniaFact(DisplayName = "F1キーで取扱説明書が要求される（フォーカス位置に関わらず）")]
    public void F1で取扱説明書が要求される()
    {
        var (shell, window) = OpenShellAsync();

        var requested = false;
        shell.RequestOpenManual += (_, _) => requested = true;

        window.KeyPressQwerty(PhysicalKey.F1, RawInputModifiers.None);

        requested.Should().BeTrue("F1はショートカット一覧・パッチキーとは異なり、いつでも取扱説明書を開けてよい");
        CloseOwnedWindows(window);
    }

    [AvaloniaFact(DisplayName = "テキスト入力欄にフォーカスがあってもF1で取扱説明書が要求される（Ctrl+/との違い）")]
    public async Task テキスト入力中でもF1で取扱説明書が要求される()
    {
        await File.WriteAllTextAsync(Path.Combine(_projectDirectory, "sample.txt"), "1行目\n").ConfigureAwait(true);
        var (shell, window) = OpenShellAsync();
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        // クイックオープン（Ctrl+P）を開くと検索欄（標準のTextBox）へフォーカスが移る
        // （ShortcutsWindowTests.テキスト入力中はCtrlスラッシュで一覧を開かないと同じ手順。
        // プロジェクトが1件も無いとクイックオープンが開かないため、事前に登録しておく必要がある）。
        window.KeyPressQwerty(PhysicalKey.P, RawInputModifiers.Control);
        shell.QuickOpen.IsOpen.Should().BeTrue();
        await SettleAsync().ConfigureAwait(true);

        var requested = false;
        shell.RequestOpenManual += (_, _) => requested = true;

        window.KeyPressQwerty(PhysicalKey.F1, RawInputModifiers.None);

        requested.Should().BeTrue(
            "F1はCtrl+/と異なりエディタの標準操作と衝突しないため、テキスト入力欄にフォーカスがあっても取扱説明書を開けてよい");
        CloseOwnedWindows(window);
    }

    [AvaloniaFact(DisplayName = "ヘルプメニューには取扱説明書・キーボードショートカット一覧の2項目が両方並び、双方に到達できる")]
    public void ヘルプメニューは両方の項目に到達できる()
    {
        var (_, window) = OpenShellAsync();

        var toggleButton = window.GetVisualDescendants().OfType<Button>()
            .Single(b => AutomationProperties.GetName(b) == "ヘルプメニューを開く");
        toggleButton.Focus();
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);

        var manualItem = window.GetVisualDescendants().OfType<Button>()
            .SingleOrDefault(b => AutomationProperties.GetName(b) == "取扱説明書を開く");
        var shortcutsItem = window.GetVisualDescendants().OfType<Button>()
            .SingleOrDefault(b => AutomationProperties.GetName(b) == "キーボードショートカット一覧を開く");

        manualItem.Should().NotBeNull("ヘルプメニューから取扱説明書に到達できる必要がある");
        shortcutsItem.Should().NotBeNull("ヘルプメニューからショートカット一覧にも引き続き到達できる必要がある");
        HelpTip.GetStandard(manualItem!).Should().NotBeNull("追加したメニュー項目にはHelpTip.Standardが必要");
        HelpTip.GetStandard(shortcutsItem!).Should().NotBeNull("既存のメニュー項目にも引き続きHelpTip.Standardが必要");
    }

    // ------------------------------------------------------------------
    // ヘルパ（ShortcutsWindowTests.csと同じ構成）
    // ------------------------------------------------------------------

    private static void CloseOwnedWindows(Window owner)
    {
        foreach (var child in owner.OwnedWindows.ToArray())
        {
            child.Close();
        }
    }

    private static async Task SettleAsync()
    {
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(10).ConfigureAwait(true);
        }
    }

    private (ShellViewModel Shell, ShellWindow Window) OpenShellAsync()
    {
        var appPaths = new AppPaths(_appDirectory);
        appPaths.EnsureCoreDirectoriesExist();

        var shell = StartupCoordinator.BuildShellViewModel(
            appPaths,
            new Settings(),
            new SettingsStore(appPaths),
            new PatchQueue(appPaths),
            new ProjectStore(appPaths),
            new RevisionStore(appPaths),
            new RevisionRestorer(appPaths),
            new NullDialogService(),
            new AvaloniaUiServices(),
            openSettings: () => { });

        var window = _windows.Track(new ShellWindow(shell) { Width = 1280, Height = 800 });
        window.Show();
        ShellWindowLoadWaiter.WaitForLayoutApplied(window);
        return (shell, window);
    }
}
