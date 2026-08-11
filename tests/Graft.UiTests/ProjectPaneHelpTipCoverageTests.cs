using System.Collections.Generic;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using FluentAssertions;
using Graft.UiTests.TestSupport;
using Graft.Views;

namespace Graft.UiTests;

/// <summary>
/// プロジェクトペイン改善（7項目まとめての要件）: 「追加する操作可能なコントロールには
/// すべてHelpTip.Standardを付ける」の取りこぼし防止テスト。
///
/// <see cref="SettingsHelpTipCoverageTests"/>は設定画面のみを対象にしており、プロジェクトペイン
/// （<see cref="ProjectPane"/>）は対象に入っていなかった。同じ方式（視覚ツリーを歩いて
/// Button/CheckBox/ComboBox/TextBox/ListBoxのうちHelpTip.Standard未設定のものが無いことを
/// 機械的に検証する）で、プロジェクトペイン用のテストをここに追加する。
///
/// 右クリックメニュー（<see cref="MenuItem"/>）はSettingsHelpTipCoverageTestsのTargetTypesに
/// 含まれていない（設定画面にContextMenuが無いため対象外だった）。プロジェクトペインには
/// 今回の改善で削除・ピン留め・表示名変更・タグ編集・場所の変更のMenuItemを追加したため、
/// ここでは個別にMenuItemも検証する。ContextMenuの子はPopup経由で遅延実現される場合がある
/// フレームワークもあるが、AvaloniaのContextMenuはXAMLに書いた時点でMenuItemインスタンス
/// 自体は生成済み（開いていなくてもGetLogicalDescendantsで辿れる。
/// EditorSelectionPromptTests.OpenContextMenuAndFindPromptItem参照）ため、
/// ウィンドウを実際に右クリックしなくても検証できる。
/// </summary>
public class ProjectPaneHelpTipCoverageTests : IDisposable
{
    private static readonly IReadOnlyList<Type> TargetTypes = new[]
    {
        typeof(Button), typeof(CheckBox), typeof(ComboBox), typeof(TextBox), typeof(ListBox),
    };

    private readonly ShownWindowTracker _windows = new();

    public void Dispose()
    {
        _windows.Dispose();
        GC.SuppressFinalize(this);
    }

    [AvaloniaFact(DisplayName = "プロジェクトペインの操作可能なコントロール（ボタン・一覧）は全てHelpTip.Standardを持つ")]
    public void プロジェクトペインのコントロールは全てHelpTipを持つ()
    {
        var pane = new ProjectPane();
        var window = _windows.Track(new Window { Content = pane });
        window.Show();

        // IsVisible==falseに限定する理由: ProjectPane.axamlが埋め込むEmptyStateView（8.8共通部品）は
        // GraftPanel等の別画面と兼用のため、ProjectPaneでは使わない副次アクション用ボタン
        // （SecondaryActionButton）を内部に持つ。SecondaryActionTextを一度も設定しないため
        // EmptyStateView.axaml.csのApplySecondaryActionTextにより恒久的にIsVisible=falseへ
        // 固定される（ProjectPane側のXAMLにこのボタンを使う経路が無く、利用者が触れることは
        // 無い）。「利用者が実際に触れられる操作可能なコントロール」を対象にする本来の趣旨に
        // 合わせ、常時非表示で構造的に到達不能なコントロールは対象から除く
        // （SettingsHelpTipCoverageTestsにはこの種の恒久非表示ボタンが無いため、この絞り込みを
        // 加えても既存テストの検出力は変わらない）。
        var missing = window.GetVisualDescendants().OfType<Control>()
            .Where(c => TargetTypes.Contains(c.GetType()) && c.IsVisible && HelpTip.GetStandard(c) is null)
            .Select(Describe)
            .ToList();

        missing.Should().BeEmpty(
            $"次のコントロールにHelpTip.Standardが設定されていません: {string.Join(", ", missing)}");
    }

    [AvaloniaFact(DisplayName = "プロジェクトペインの右クリックメニューの各項目は全てHelpTip.Standardを持つ")]
    public void プロジェクトペインの右クリックメニューは全てHelpTipを持つ()
    {
        var pane = new ProjectPane();
        // ContextMenuはListBoxへ添付プロパティとして設定されている（ProjectPane.axaml）。
        // 実際に右クリックで開かなくても、XAMLに書かれたMenuItemインスタンス自体は
        // ListBox構築の時点で既に生成済みのため、直接GetLogicalDescendantsで辿れる。
        var contextMenu = pane.ListBoxElement.ContextMenu;
        contextMenu.Should().NotBeNull("プロジェクト一覧には右クリックメニューが定義されているはず");

        var menuItems = contextMenu!.GetLogicalDescendants().OfType<MenuItem>().ToList();
        menuItems.Should().NotBeEmpty("右クリックメニューには削除等の項目が並んでいるはず");

        var missing = menuItems.Where(m => HelpTip.GetStandard(m) is null)
            .Select(m => $"MenuItem[{AutomationProperties.GetName(m) ?? m.Header?.ToString() ?? "(名前無し)"}]")
            .ToList();

        missing.Should().BeEmpty(
            $"次の右クリックメニュー項目にHelpTip.Standardが設定されていません: {string.Join(", ", missing)}");
    }

    private static string Describe(Control control)
    {
        var name = AutomationProperties.GetName(control);
        return $"{control.GetType().Name}[{name ?? "名前無し"}]";
    }
}
