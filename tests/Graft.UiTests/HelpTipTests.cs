using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using FluentAssertions;
using Graft.Infra;
using Graft.Platform;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;
using Graft.Views;
using Graft.Views.SettingsPanels;

namespace Graft.UiTests;

/// <summary>
/// 「操作の説明」（ツールチップ）の3段階表示レベルの回帰テスト（課題1）。
///
/// <see cref="HelpTip"/>はテーマ（<see cref="Graft.Themes.ThemeManager"/>）と同じ設計で、
/// アプリ全体に1つの静的な状態（<see cref="HelpTip.CurrentLevel"/>）を持つ。ThemeTestsと同様、
/// 各テストの冒頭で明示的にレベルをそろえてから検証する（テスト間で状態を持ち越さないため）。
/// </summary>
public class HelpTipTests : IDisposable
{
    private readonly ShownWindowTracker _windows = new();

    public void Dispose()
    {
        // 表示したウィンドウを後始末する（ShownWindowTracker参照。閉じ忘れると
        // 「Unable to locate 'Avalonia.Platform.IFontManagerImpl'」がCIで不定期に出る）。
        _windows.Dispose();
        GC.SuppressFinalize(this);
    }

    [AvaloniaFact(DisplayName = "標準の説明を選ぶと標準の文言だけがツールチップになる")]
    public void 標準の説明を選ぶと標準の文言になる()
    {
        HelpTip.SetLevel(TooltipDetailLevel.Standard);
        var button = CreateTaggedButton();

        var tip = ToolTip.GetTip(button);
        tip.Should().BeOfType<TextBlock>().Which.Text.Should().Be("標準の説明");
    }

    [AvaloniaFact(DisplayName = "くわしい説明を選ぶとくわしい文言がツールチップになる")]
    public void くわしい説明を選ぶとくわしい文言になる()
    {
        HelpTip.SetLevel(TooltipDetailLevel.Detailed);
        var button = CreateTaggedButton();

        var tip = ToolTip.GetTip(button);
        tip.Should().BeOfType<TextBlock>().Which.Text.Should().Be("くわしい説明");
    }

    [AvaloniaFact(DisplayName = "表示しないを選ぶとツールチップ自体が割り当てられない（空文字ではない）")]
    public void 表示しないを選ぶとツールチップが出ない()
    {
        HelpTip.SetLevel(TooltipDetailLevel.Off);
        var button = CreateTaggedButton();

        // 空文字のTextBlockではなく、Tip自体がnullであること（Avaloniaのツールチップサービスは
        // Tipがnullなら開かない。空文字だと「何も書かれていない吹き出し」が出てしまいうる）。
        ToolTip.GetTip(button).Should().BeNull();
    }

    [AvaloniaFact(DisplayName = "くわしい説明が未設定のコントロールでは、くわしい説明を選んでも標準の説明にフォールバックする")]
    public void くわしい説明未設定なら標準へフォールバックする()
    {
        HelpTip.SetLevel(TooltipDetailLevel.Detailed);
        var button = new Button();
        HelpTip.SetStandard(button, "標準のみ");

        var tip = ToolTip.GetTip(button);
        tip.Should().BeOfType<TextBlock>().Which.Text.Should().Be("標準のみ");
    }

    [AvaloniaFact(DisplayName = "設定を変えた瞬間、既に生成済みのコントロールのツールチップも切り替わる（再起動不要）")]
    public void レベル変更は既存のコントロールへ即時反映される()
    {
        HelpTip.SetLevel(TooltipDetailLevel.Standard);
        var button = CreateTaggedButton();
        ToolTip.GetTip(button).Should().BeOfType<TextBlock>().Which.Text.Should().Be("標準の説明");

        // ボタン側では何も操作していない。設定側の変更（SetLevel）だけで切り替わる必要がある
        // （課題1の要件: 「設定を変えた瞬間、すでに開いているウィンドウすべてのツールチップが
        // 切り替わること」）。
        HelpTip.SetLevel(TooltipDetailLevel.Detailed);
        ToolTip.GetTip(button).Should().BeOfType<TextBlock>().Which.Text.Should().Be("くわしい説明");

        HelpTip.SetLevel(TooltipDetailLevel.Off);
        ToolTip.GetTip(button).Should().BeNull();

        HelpTip.SetLevel(TooltipDetailLevel.Standard);
        ToolTip.GetTip(button).Should().BeOfType<TextBlock>().Which.Text.Should().Be("標準の説明");
    }

    [AvaloniaFact(DisplayName = "レベル変更は購読中の全コントロールへ同時に反映される")]
    public void レベル変更は複数コントロールへ同時に反映される()
    {
        HelpTip.SetLevel(TooltipDetailLevel.Standard);
        var button1 = CreateTaggedButton();
        var button2 = CreateTaggedButton();

        HelpTip.SetLevel(TooltipDetailLevel.Detailed);

        ToolTip.GetTip(button1).Should().BeOfType<TextBlock>().Which.Text.Should().Be("くわしい説明");
        ToolTip.GetTip(button2).Should().BeOfType<TextBlock>().Which.Text.Should().Be("くわしい説明");
    }

    [AvaloniaFact(DisplayName = "ツールチップの中身は折り返し設定と最大幅を持つ（長文が画面外へはみ出さないため）")]
    public void ツールチップは折り返しと最大幅を持つ()
    {
        HelpTip.SetLevel(TooltipDetailLevel.Detailed);
        var button = CreateTaggedButton();

        var tip = ToolTip.GetTip(button).Should().BeOfType<TextBlock>().Subject;
        tip.TextWrapping.Should().Be(TextWrapping.Wrap, "「くわしい説明」は長文になるため折り返しが必要");
        tip.MaxWidth.Should().BeGreaterThan(0).And.BeLessThan(double.PositiveInfinity,
            "最大幅が指定されていないと画面外へはみ出しうる");
    }

    [AvaloniaFact(DisplayName = "settings.jsonのtooltipDetailは3値へ読み替えられ、逆変換もできる")]
    public void 設定値の読み替えと逆変換ができる()
    {
        HelpTip.ParseLevel("off").Should().Be(TooltipDetailLevel.Off);
        HelpTip.ParseLevel("standard").Should().Be(TooltipDetailLevel.Standard);
        HelpTip.ParseLevel("detailed").Should().Be(TooltipDetailLevel.Detailed);
        HelpTip.ParseLevel(null).Should().Be(TooltipDetailLevel.Standard, "未知の値は標準として扱う");
        HelpTip.ParseLevel("なにか").Should().Be(TooltipDetailLevel.Standard);

        HelpTip.ToSettingsValue(TooltipDetailLevel.Off).Should().Be("off");
        HelpTip.ToSettingsValue(TooltipDetailLevel.Standard).Should().Be("standard");
        HelpTip.ToSettingsValue(TooltipDetailLevel.Detailed).Should().Be("detailed");
    }

    [AvaloniaFact(DisplayName = "設定画面の「操作の説明」を変えた瞬間、すでに開いているシェルウィンドウのツールチップが切り替わる（再起動不要）")]
    public async Task 設定変更で開いているウィンドウのツールチップが切り替わる()
    {
        HelpTip.SetLevel(TooltipDetailLevel.Standard);

        // ShellWindowはコマンドバーの各ボタンにHelpTip.Standard/Detailedを付けている
        // （ShellWindow.axaml）。DataContextを与えなくても、XAMLで宣言された添付プロパティ自体は
        // 通常どおり設定される（{Binding ...}するプロパティだけがDataContext無しでは解決しない）。
        var shell = _windows.Track(new ShellWindow());
        shell.Show();
        var analyzeButton = shell.GetVisualDescendants().OfType<Button>()
            .Single(b => Equals(AutomationProperties.GetName(b), "クリップボードのパッチを解析"));

        ToolTip.GetTip(analyzeButton).Should().BeOfType<TextBlock>()
            .Which.Text.Should().Contain("ここが接ぎ木の入口です");

        var root = Path.Combine(Path.GetTempPath(), "graft-helptip", Guid.NewGuid().ToString("N"));
        var appPaths = new AppPaths(root);
        appPaths.EnsureCoreDirectoriesExist();
        var vm = new SettingsViewModel(appPaths, new NullDialogService(), new AvaloniaUiServices());
        await vm.InitializeAsync();

        // ウィンドウを再構築せず、設定画面の操作だけでシェルウィンドウ側のツールチップが
        // 切り替わる必要がある（課題1の要件）。
        vm.SelectedTooltipDetail = "detailed";
        ToolTip.GetTip(analyzeButton).Should().BeOfType<TextBlock>()
            .Which.Text.Should().Contain("ChatGPTやClaudeなどのAIに修正を頼んで");

        vm.SelectedTooltipDetail = "off";
        ToolTip.GetTip(analyzeButton).Should().BeNull();

        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // 後始末の失敗は検証結果に影響しない。
        }
    }

    [AvaloniaFact(DisplayName = "課題2・3: 「閉じたときの動作」「PC起動時に自動で起動する」のツールチップも操作の説明レベルに追従する")]
    public async Task 課題2_3の設定項目もツールチップが切り替わる()
    {
        HelpTip.SetLevel(TooltipDetailLevel.Standard);

        var root = Path.Combine(Path.GetTempPath(), "graft-helptip-general", Guid.NewGuid().ToString("N"));
        var appPaths = new AppPaths(root);
        appPaths.EnsureCoreDirectoriesExist();
        var vm = new SettingsViewModel(appPaths, new NullDialogService(), new AvaloniaUiServices());
        await vm.InitializeAsync();

        var view = new GeneralSettingsView { DataContext = vm };
        var window = _windows.Track(new Window { Content = view });
        window.Show();

        var closeBehaviorCombo = view.GetVisualDescendants().OfType<ComboBox>()
            .Single(c => Equals(AutomationProperties.GetName(c), "ウィンドウを閉じたときの動作"));
        var launchAtStartupCheck = view.GetVisualDescendants().OfType<CheckBox>()
            .Single(c => Equals(AutomationProperties.GetName(c), "PC起動時に自動で起動する"));

        ToolTip.GetTip(closeBehaviorCombo).Should().BeOfType<TextBlock>()
            .Which.Text.Should().Contain("常駐する");
        ToolTip.GetTip(launchAtStartupCheck).Should().BeOfType<TextBlock>()
            .Which.Text.Should().Contain("PCの起動時");

        // 「くわしい説明」に切り替えると、両方ともくわしい文言へ即時に切り替わる必要がある
        // （課題1のHelpTip機構に相乗りしているだけであることの確認。再構築は不要）。
        HelpTip.SetLevel(TooltipDetailLevel.Detailed);

        ToolTip.GetTip(closeBehaviorCombo).Should().BeOfType<TextBlock>()
            .Which.Text.Should().Contain("お使いの環境がタスクトレイに対応していない場合");
        ToolTip.GetTip(launchAtStartupCheck).Should().BeOfType<TextBlock>()
            .Which.Text.Should().Contain("XDG autostart");

        HelpTip.SetLevel(TooltipDetailLevel.Off);
        ToolTip.GetTip(closeBehaviorCombo).Should().BeNull();
        ToolTip.GetTip(launchAtStartupCheck).Should().BeNull();

        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // 後始末の失敗は検証結果に影響しない。
        }
    }

    private static Button CreateTaggedButton()
    {
        var button = new Button();
        HelpTip.SetStandard(button, "標準の説明");
        HelpTip.SetDetailed(button, "くわしい説明");
        return button;
    }

    /// <summary>何もしない最小のダイアログ実装。この画面ではダイアログを起動しない操作しか使わない。</summary>
    private sealed class NullDialogService : IDialogService
    {
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);

        public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel)
            => Task.FromResult<bool?>(true);

        public Task<string?> PromptAsync(string title, string message, string? initial = null)
            => Task.FromResult(initial);

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync(string title, IReadOnlyList<string>? extensions = null)
            => Task.FromResult<string?>(null);

        public Task<string?> SaveFileAsync(string title, string suggestedFileName, IReadOnlyList<string>? extensions = null)
            => Task.FromResult<string?>(null);

        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
    }
}
