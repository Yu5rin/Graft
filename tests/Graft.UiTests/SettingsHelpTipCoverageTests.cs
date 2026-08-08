using System.Collections.Generic;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using Graft.Views;
using Graft.Views.SettingsPanels;

namespace Graft.UiTests;

/// <summary>
/// 設定画面の項目取りこぼし防止テスト（要望2）。
///
/// 「設定画面の全項目にHelpTip.Standard/Detailedを付ける」という要件は、1件ずつ手作業で
/// 数えて確認すると、後日項目が増えたときに付け忘れても気付けない。そこでHelpTipTestsのように
/// 個々の文言を確認するのではなく、各設定画面の視覚ツリーを実際に歩いて「操作可能なコントロールに
/// HelpTip.Standardが1つも設定されていない」ものが無いことを機械的に検証する。新しい設定項目を
/// 追加してHelpTipを付け忘れると、このテストが失敗して気付ける（回帰防止）。
///
/// DataContextを与えなくてもXAMLの添付プロパティ自体は解決される（HelpTipTestsの
/// 「設定変更で開いているウィンドウのツールチップが切り替わる」テストのコメント参照）ため、
/// ここでは各Viewを既定コンストラクタのまま構築するだけでよい。
/// </summary>
public class SettingsHelpTipCoverageTests
{
    /// <summary>
    /// HelpTipの対象とみなすコントロール種別。ラベルのTextBlockそのものは対象に含めない
    /// （隣接するTextBox/ComboBox等の側にHelpTipを付ける既存の流儀に合わせるため）。
    /// </summary>
    private static readonly IReadOnlyList<Type> TargetTypes = new[]
    {
        typeof(Button), typeof(CheckBox), typeof(ComboBox), typeof(TextBox), typeof(ListBox),
    };

    [AvaloniaFact(DisplayName = "設定画面「一般」の操作可能なコントロールは全てHelpTip.Standardを持つ")]
    public void 一般設定のコントロールは全てHelpTipを持つ() => AssertAllControlsHaveHelpTip(new GeneralSettingsView());

    [AvaloniaFact(DisplayName = "設定画面「エディタ」の操作可能なコントロールは全てHelpTip.Standardを持つ")]
    public void エディタ設定のコントロールは全てHelpTipを持つ() => AssertAllControlsHaveHelpTip(new EditorSettingsView());

    [AvaloniaFact(DisplayName = "設定画面「マッチング」の操作可能なコントロールは全てHelpTip.Standardを持つ")]
    public void マッチング設定のコントロールは全てHelpTipを持つ() => AssertAllControlsHaveHelpTip(new MatchingSettingsView());

    [AvaloniaFact(DisplayName = "設定画面「安全機構」の操作可能なコントロールは全てHelpTip.Standardを持つ")]
    public void 安全機構設定のコントロールは全てHelpTipを持つ() => AssertAllControlsHaveHelpTip(new SafetySettingsView());

    [AvaloniaFact(DisplayName = "設定画面「表示・コンテキスト」の操作可能なコントロールは全てHelpTip.Standardを持つ")]
    public void 表示コンテキスト設定のコントロールは全てHelpTipを持つ() => AssertAllControlsHaveHelpTip(new DiffSettingsView());

    [AvaloniaFact(DisplayName = "設定画面「プロンプトテンプレート」の操作可能なコントロールは全てHelpTip.Standardを持つ")]
    public void プロンプトテンプレート設定のコントロールは全てHelpTipを持つ() => AssertAllControlsHaveHelpTip(new TemplateSettingsView());

    [AvaloniaFact(DisplayName = "設定画面「適用後フック」の操作可能なコントロールは全てHelpTip.Standardを持つ")]
    public void 適用後フック設定のコントロールは全てHelpTipを持つ() => AssertAllControlsHaveHelpTip(new HookSettingsView());

    [AvaloniaFact(DisplayName = "設定画面「JSON編集」の操作可能なコントロールは全てHelpTip.Standardを持つ")]
    public void JSON編集設定のコントロールは全てHelpTipを持つ() => AssertAllControlsHaveHelpTip(new RawJsonSettingsView());

    [AvaloniaFact(DisplayName = "設定ウィンドウ本体（タブ・トークン統計・下部ボタン）は全てHelpTip.Standardを持つ")]
    public void 設定ウィンドウ本体のコントロールは全てHelpTipを持つ()
    {
        var window = new SettingsWindow();
        window.Show();

        // TabItemはButton/CheckBox等ではないため対象種別に含めていないが、要望2で
        // タブ自体にも説明を付けた（トークン統計・バージョン情報など項目名だけでは
        // 分かりにくいタブがあるため）。個別に確認する。
        var missingTabs = window.GetVisualDescendants().OfType<TabItem>()
            .Where(t => HelpTip.GetStandard(t) is null)
            .Select(t => AutomationProperties.GetName(t) ?? t.Header?.ToString() ?? "(名前無し)")
            .ToList();
        missingTabs.Should().BeEmpty("設定ウィンドウの全タブにHelpTip.Standardが必要");

        AssertAllControlsHaveHelpTip(window);
    }

    /// <summary>
    /// 指定コントロール配下（自分自身を含む）の対象種別コントロールをすべて洗い出し、
    /// HelpTip.Standardが設定されていないものが無いことを検証する。
    /// ウィンドウに載せて実際にShow()することで、DataTemplate展開前の静的なXAML構造を
    /// 漏れなく視覚ツリーへ反映させる。
    /// </summary>
    private static void AssertAllControlsHaveHelpTip(Control control)
    {
        Window window;
        if (control is Window w)
        {
            window = w;
        }
        else
        {
            window = new Window { Content = control };
        }
        window.Show();

        var missing = window.GetVisualDescendants().OfType<Control>()
            .Where(c => TargetTypes.Contains(c.GetType()) && HelpTip.GetStandard(c) is null)
            .Select(Describe)
            .ToList();

        missing.Should().BeEmpty(
            $"次のコントロールにHelpTip.Standardが設定されていません: {string.Join(", ", missing)}");
    }

    private static string Describe(Control control)
    {
        var name = AutomationProperties.GetName(control);
        return $"{control.GetType().Name}[{name ?? "名前無し"}]";
    }
}
