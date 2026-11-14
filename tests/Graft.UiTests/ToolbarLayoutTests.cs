using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Graft.UiTests.TestSupport;
using Graft.Views;

namespace Graft.UiTests;

/// <summary>
/// ツールバーの「作業の流れ」の並びと、「適用」「キューへ追加」を接ぎ木パネルの見出しへ移した
/// 配置の検証。利用者の選択（「作業の流れで並べ直す」）が崩れたら気付けるようにする。
/// </summary>
public class ToolbarLayoutTests
{
    private static Rect BoundsIn(Control control, Visual root)
        => new(control.TranslatePoint(new Point(0, 0), root) ?? default, control.Bounds.Size);

    [AvaloniaFact(DisplayName = "ツールバーは左から ファイルを選ぶ → 依頼文をコピー → 貼り付けて解析 → キュー の順で、履歴・設定・？は右端にある")]
    public async Task ツールバーは作業の流れで並ぶ()
    {
        using var harness = await ShellHarness.CreateAsync(withWindow: true).ConfigureAwait(true);
        var window = harness.Window!;
        Dispatcher.UIThread.RunJobs();

        var names = new[] { "ContextButton", "PromptButton", "PromptTemplateButton", "ParseButton", "ParseMenuButton", "QueueButton" };
        var lefts = names.Select(n => BoundsIn(window.GetControl<Button>(n), window).Left).ToList();
        lefts.Should().BeInAscendingOrder("ツールバーの並びは作業の流れ（渡す → 受け取る）");

        var history = BoundsIn(window.GetControl<Button>("HistoryButton"), window);
        var settings = BoundsIn(window.GetControl<Button>("SettingsButton"), window);
        var help = BoundsIn(window.GetControl<Button>("ShortcutsButton"), window);
        history.Left.Should().BeGreaterThan(lefts[^1], "履歴は作業の流れの外にあり、右端グループにある");
        settings.Left.Should().BeGreaterThan(history.Left);
        help.Left.Should().BeGreaterThan(settings.Left, "並びは 履歴・設定・？");

        window.GetControl<Button>("ContextButton").Content.Should().Be("ファイルを選ぶ");
        window.GetControl<Button>("PromptButton").Content.Should().Be("依頼文をコピー");
        window.GetControl<Button>("ParseButton").Content.Should().Be("貼り付けて解析");
    }

    [AvaloniaFact(DisplayName = "「適用」「キューへ追加」はツールバーに無く、接ぎ木パネルの見出しにある。適用だけが主ボタンである")]
    public async Task 適用は接ぎ木パネルの見出しにありツールバーには無い()
    {
        using var harness = await ShellHarness.CreateAsync(withWindow: true).ConfigureAwait(true);
        var window = harness.Window!;

        var commandBar = window.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "CommandBarGrid");
        var toolbarNames = commandBar.GetVisualDescendants().OfType<Button>()
            .Select(b => AutomationProperties.GetName(b)).ToList();
        toolbarNames.Should().NotContain("適用を実行", "適用は解析結果に対する操作なので接ぎ木パネルの見出しへ移した");
        toolbarNames.Should().NotContain("現在の解析結果をパッチキューへ追加");

        var panel = window.GetVisualDescendants().OfType<GraftPanel>().Single();
        var apply = panel.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ApplyButtonElement");
        apply.Classes.Should().Contain("primary", "適用は接ぎ木パネルの主ボタン");
        panel.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("primary"))
            .Should().Be(1, "主ボタンは原則1つ");

        // 見出しの並びは 修正を依頼 → 破棄 → キューへ追加 → 再チェック → 適用。
        var header = new[]
        {
            "失敗ブロックの修正依頼文をコピー", "解析結果を破棄", "現在の解析結果をパッチキューへ追加",
            "適用できるかを再チェック", "適用を実行",
        };
        var buttons = panel.GetVisualDescendants().OfType<Button>()
            .Where(b => header.Contains(AutomationProperties.GetName(b))).ToList();
        buttons.Select(b => AutomationProperties.GetName(b)).Should().BeEquivalentTo(header);
        buttons.OrderBy(b => BoundsIn(b, window).Left).Select(b => AutomationProperties.GetName(b))
            .Should().Equal(header);

        // 「プレビュー」という名前は、この見出しからは無くなった（解析し直しは「再チェック」）。
        panel.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).Should().Contain("再チェック")
            .And.NotContain("プレビュー");
    }

    [AvaloniaFact(DisplayName = "適用ボタンの文言は、解析後はチェックの付いた適用可能な件数「適用 (N件)」になり、チェックを外すと減る")]
    public async Task 適用ボタンの件数が追従する()
    {
        using var harness = await ShellHarness.CreateAsync(withWindow: true).ConfigureAwait(true);
        File.WriteAllText(harness.ProjectPath("a.txt"), "one\ntwo\n");
        await harness.RegisterProjectAsync().ConfigureAwait(true);

        harness.Shell.Graft.ApplyButtonText.Should().Be("適用", "解析前は件数が無意味なので出さない");

        await harness.ParseAsync("a.txt", "one", "ONE").ConfigureAwait(true);
        harness.Shell.Graft.ApplyButtonText.Should().Be("適用 (1件)");

        harness.Shell.Graft.Blocks[0].IsSelected = false;
        harness.Shell.Graft.ApplyButtonText.Should().Be("適用 (0件)", "チェックを外した分は書き込まれないので数えない");
    }

    [AvaloniaFact(DisplayName = "接ぎ木パネルが畳まれていても、解析結果が出たらパネルが開く")]
    public async Task 解析結果が出たらパネルが開く()
    {
        using var harness = await ShellHarness.CreateAsync(withWindow: true).ConfigureAwait(true);
        File.WriteAllText(harness.ProjectPath("a.txt"), "one\ntwo\n");
        await harness.RegisterProjectAsync().ConfigureAwait(true);

        harness.Shell.IsGraftPanelOpen = false;
        await harness.ParseAsync("a.txt", "one", "ONE").ConfigureAwait(true);
        harness.Shell.IsGraftPanelOpen.Should().BeTrue("適用ボタンが見えない状態にしない");

        // 再解析のときも、畳んであれば開き直す。
        harness.Shell.IsGraftPanelOpen = false;
        await harness.ParseAsync("a.txt", "one", "ONE").ConfigureAwait(true);
        harness.Shell.IsGraftPanelOpen.Should().BeTrue();
    }

    [AvaloniaFact(DisplayName = "幅が狭いときはグループの見出しを先に隠し、ボタンは残す。最小幅でもボタンは欠けない")]
    public void 狭いと見出しだけが隠れる()
    {
        var wide = new ShellWindow { Width = 1400, Height = 700 };
        var narrow = new ShellWindow { Width = 960, Height = 600 };
        try
        {
            wide.Show();
            narrow.Show();
            Dispatcher.UIThread.RunJobs();

            wide.GetControl<TextBlock>("HandoffGroupLabel").IsVisible.Should().BeTrue();
            wide.GetControl<TextBlock>("ReceiveGroupLabel").IsVisible.Should().BeTrue();
            narrow.GetControl<TextBlock>("HandoffGroupLabel").IsVisible.Should().BeFalse("幅が足りないときは見出しを先に隠す");
            narrow.GetControl<TextBlock>("ReceiveGroupLabel").IsVisible.Should().BeFalse();

            // 最小幅でも、作業の流れのボタンは右端グループと重ならない。
            var queueRight = BoundsIn(narrow.GetControl<Button>("QueueButton"), narrow).Right;
            var historyLeft = BoundsIn(narrow.GetControl<Button>("HistoryButton"), narrow).Left;
            queueRight.Should().BeLessThanOrEqualTo(historyLeft);
        }
        finally
        {
            wide.Close();
            narrow.Close();
        }
    }

    [AvaloniaFact(DisplayName = "解析の▼メニューには「クリップボードから解析」「ファイルから解析…」があり、キーは見出しではなくInputGestureで出す")]
    public void 解析の下矢印メニューの中身()
    {
        var window = new ShellWindow { Width = 1280, Height = 800 };
        try
        {
            window.Show();
            var items = MenuTestExtensions.FlyoutItems(window.GetControl<Button>("ParseMenuButton"));
            items.Select(i => i.Header).Should().Equal(new object[] { "クリップボードから解析", "ファイルから解析…" });
            items[0].InputGesture.Should().NotBeNull();
            items.Should().OnlyContain(i => !i.Header!.ToString()!.Contains("Ctrl"));
        }
        finally
        {
            window.Close();
        }
    }
}
