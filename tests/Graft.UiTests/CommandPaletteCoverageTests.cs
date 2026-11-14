using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Features;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;

namespace Graft.UiTests;

/// <summary>
/// コマンドパレットの網羅性（ツールバー・接ぎ木パネルの見出し・メニュー・ショートカットにある操作が、
/// すべてパレットから実行できること）と、ショートカット一覧（<see cref="ShortcutCatalog"/>）との
/// 食い違いが無いことの検証。
/// </summary>
public class CommandPaletteCoverageTests
{
    private static async Task<(ShellHarness Harness, IReadOnlyList<CommandPaletteItem> Items)> OpenPaletteAsync()
    {
        var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        h.Shell.CommandPalette.Open();
        return (h, h.Shell.CommandPalette.Results.ToList());
    }

    [AvaloniaFact(DisplayName = "コマンドパレット: ツールバー・接ぎ木パネルの見出し・メニューにある操作が、すべて載っている")]
    public async Task 画面にある操作はすべてパレットにある()
    {
        var (h, items) = await OpenPaletteAsync().ConfigureAwait(true);
        using var _ = h;

        // ツールバー: ファイルを選ぶ・依頼文をコピー（本体と▼）・貼り付けて解析（本体と▼のファイルから解析）・
        //            キュー・履歴・設定・？のメニュー。
        // 接ぎ木パネルの見出し: 修正を依頼・破棄・キューへ追加・再チェック・適用。
        // 履歴のメニュー: 変更を見る・要約をコピー・バックアップフォルダを開く・取り消す・ここまで戻す。
        // エクスプローラのメニュー: 新規ファイル・新規フォルダ・追加・削除を元に戻す。ほか、ショートカットのある操作。
        var required = new[]
        {
            "AIに渡すファイルを選ぶ", "依頼文をコピー", "依頼文のテンプレートを選んでコピー",
            "貼り付けて解析（クリップボードの回答を解析）", "ファイルから解析", "キューを開く", "履歴を開く", "設定を開く",
            "キーボードショートカット一覧を開く", "取扱説明書を開く", "使い方を学ぶ（画面上のチュートリアル）",
            "修正を依頼（失敗した変更の修正依頼文をコピー）", "解析結果を破棄", "キューへ追加",
            "再チェック（適用できるかをもう一度調べる）", "適用",
            "選択中の履歴の変更を見る", "選択中の履歴の要約をコピー", "選択中の履歴のバックアップフォルダを開く",
            "選択中の適用を取り消す", "選択中の履歴までまとめて戻す", "直前の適用を取り消す",
            "新規ファイルを作る", "新規フォルダを作る", "既存のファイルをプロジェクトに追加", "削除したファイルを元に戻す",
            "ファイル名であいまい検索して開く（クイックオープン）", "現在のファイルを保存", "開いているファイルをすべて保存",
            "現在のタブを閉じる", "直前に閉じたタブを開き直す", "ファイル内を検索", "ファイル内を置換", "行番号を指定して移動",
            "接ぎ木パネルを開閉", "接ぎ木パネルの配置を切り替え（下／右）", "クリップボード監視を一時停止／再開",
        };
        items.Select(i => i.Title).Should().Contain(required);
    }

    [AvaloniaFact(DisplayName = "コマンドパレット: 分類ごとに並び、各行に分類名があり、キーのある操作はキーが出る")]
    public async Task 分類ごとに並び分類名が付く()
    {
        var (h, items) = await OpenPaletteAsync().ConfigureAwait(true);
        using var _ = h;

        items.Should().OnlyContain(i => PaletteCategories.Order.Contains(i.Category), "すべての行に決まった分類名がある");
        var ranks = items.Select(i => PaletteCategories.Order.ToList().IndexOf(i.Category)).ToList();
        ranks.Should().BeInAscendingOrder("分類の順（ファイル→編集→表示→AIに渡す→AIから受け取る→履歴→設定とヘルプ）に並ぶ");
        PaletteCategories.Order.Should().BeEquivalentTo(items.Select(i => i.Category).Distinct(), "7つの分類がすべて使われている");

        items.Single(i => i.Title == "適用").Category.Should().Be("AIから受け取る");
        items.Single(i => i.Title == "適用").GestureText.Should().Be("Ctrl+Enter");
        items.Single(i => i.Title == "依頼文をコピー").GestureText.Should().Be("Ctrl+Shift+C");
        items.Single(i => i.Title == "履歴を開く").Category.Should().Be("履歴");
        items.Single(i => i.Title == "設定を開く").Category.Should().Be("設定とヘルプ");
        items.Single(i => i.Title == "現在のファイルを保存").GestureText.Should().Be("Ctrl+S");
        items.Single(i => i.Title == "現在のファイルを保存").AutomationName.Should().StartWith("ファイル、");
    }

    [AvaloniaFact(DisplayName = "コマンドパレット: 分類名でも絞り込める")]
    public async Task 分類名でも絞り込める()
    {
        var (h, _) = await OpenPaletteAsync().ConfigureAwait(true);
        using var _h = h;

        h.Shell.CommandPalette.Query = "履歴 取り消";
        h.Shell.CommandPalette.Results.Select(r => r.Title).Should().Contain("選択中の適用を取り消す");
    }

    [AvaloniaFact(DisplayName = "ショートカット一覧とパレットが食い違わない: 一覧に操作名のあるキーは、パレットの同じ操作に同じキー表記が出る")]
    public async Task 一覧とパレットのキーが一致する()
    {
        var (h, items) = await OpenPaletteAsync().ConfigureAwait(true);
        using var _ = h;

        // パレットにある操作のキーは、すべて一覧（ShortcutCatalog）にある値か、折りたたみレベル2〜5のリテラル。
        var catalogGestures = ShortcutCatalog.Entries.Select(e => e.Gesture).ToHashSet();
        var allowedLiterals = new HashSet<string> { "Ctrl+Shift+2", "Ctrl+Shift+3", "Ctrl+Shift+4", "Ctrl+Shift+5" };
        foreach (var item in items.Where(i => i.HasGesture))
        {
            (catalogGestures.Contains(item.GestureText) || allowedLiterals.Contains(item.GestureText))
                .Should().BeTrue($"パレットの「{item.Title}」のキー「{item.GestureText}」は、ショートカット一覧に無い");
        }

        // 一覧のうち CommandKey を持つもの（パレットで逆引きできる操作）は、パレットのどこかの行に出ている。
        // 逆引き先を持たないキー（コマンドパレット自身・エクスプローラ内のキー・プロジェクト番号など）は除く。
        var notInPalette = new HashSet<string>
        {
            "OpenCommandPalette", "OpenNode", "RenameNode", "DeleteNode",
        };
        foreach (var entry in ShortcutCatalog.Entries.Where(e => e.CommandKey is not null && !notInPalette.Contains(e.CommandKey)))
        {
            items.Should().Contain(i => i.GestureText == entry.Gesture,
                $"一覧にあるキー「{entry.Gesture}」（{entry.CommandKey}）に対応する操作が、パレットに無い");
        }
    }

    [AvaloniaFact(DisplayName = "ショートカット一覧: 実際に効くキー（ShellWindow.Keyboard.cs・エクスプローラのKeyBindings）がすべて載っている")]
    public void 実際に効くキーが一覧に載っている()
    {
        // ShellWindow.Keyboard.cs の HandleCtrlShiftShortcut / HandleCtrlAltShortcut / HandlePlainCtrl* と、
        // ExplorerView.axaml の KeyBindings、EditorPane.axaml.cs のキー処理にある組み合わせを、ここに書き出してある。
        // キーを足したのに一覧へ載せ忘れると、このテストが失敗する（逆に、載せたのに実装が無い、は
        // ShellKeyboardTests・ShortcutsWindowTests の実際のキー操作テストが検出する）。
        var implemented = new[]
        {
            "Ctrl+Shift+C", "Ctrl+Shift+V", "Ctrl+Shift+H", "Ctrl+Shift+E", "Ctrl+Shift+F", "Ctrl+Shift+S",
            "Ctrl+Shift+T", "Ctrl+Shift+P", "Ctrl+Alt+Z", "Ctrl+Alt+1〜9", "Ctrl+J", "Ctrl+S", "Ctrl+,", "Ctrl+Enter",
            "Ctrl+P", "Ctrl+F", "Ctrl+H", "Ctrl+G", "Ctrl+W", "Ctrl+Tab", "Ctrl+/", "F1", "F6", "Escape", "Space",
            "Alt+↑ / Alt+↓", "Shift+Alt+↓", "Ctrl+Shift+K", "Ctrl+Space", "Ctrl+Shift+1〜5", "Ctrl+Shift+/", "Ctrl+Shift+[",
            "Enter", "F2", "Delete", "Ctrl+Z",
        };
        var listed = ShortcutCatalog.Entries.Select(e => e.Gesture).ToHashSet();
        implemented.Where(k => !listed.Contains(k)).Should().BeEmpty("実際に効くキーが、ショートカット一覧に載っていない");
    }
}
