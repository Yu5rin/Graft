using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
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
/// 利用者からの指摘対応: 「失敗を再依頼」ボタンは無効時にグレーアウトして押せないと
/// 分かるのに、接ぎ木パネルの「破棄」「プレビュー」「適用」等、他のボタンは無効でも
/// 見た目が変わらなかった。
///
/// 【原因】Button/ToggleButton/CheckBox/ComboBoxのControlTheme（Controls.axaml・
/// Controls.Input.axaml）はいずれも:disabledで自分自身のForegroundをtext.disabledへ
/// 変えるだけだった。「失敗を再依頼」はContentへ文字列をそのまま渡しているため
/// ContentPresenterが暗黙に作るTextBlockがボタンのForegroundをそのまま受け取り正しく
/// 灰色になっていたが、「破棄」「プレビュー」「適用」はContentへ
/// StackPanel（IconGlyph＋TextBlock）を組んでおり、内側のTextBlockはControls.Base.axaml側の
/// 暗黙スタイル（Foreground: text.primary固定）を持つため、ボタン自身のForeground変更を
/// 受け取れなかった。同様にIconGlyphは自分のStrokeプロパティ（既定text.primary）を持ち、
/// ボタンのForegroundとは無関係だったため、履歴ペインのようなアイコンのみのボタンも
/// 無効時にアイコンの色が変わらなかった。
///
/// 【対応】個々のボタンへ直接色を継ぎ足すのではなく、共通コントロールテーマ側で
/// 一括して直す。
/// - Controls.Base.axaml: TextBlock:disabled { Foreground: text.disabled }
///   AvaloniaのIsEnabledは祖先を考慮した実効的な有効状態（IsEffectivelyEnabled）で
///   :disabled疑似クラスへ反映されるため、disabledなボタンの内側のTextBlockにも
///   :disabledは伝播している。個別のボタンではなくTextBlock自身に対するスタイルを
///   足すことで、ボタンに限らずあらゆる場所で一括して効く。
/// - Icons.axaml: IconGlyphのControlThemeへ ^:disabled { Stroke: text.disabled } を追加。
///   同じ理由でIconGlyph自身にも:disabledは伝播する。
/// - Controls.Input.axaml: CheckBoxの箱（PART_Box）の枠線も:disabledで灰色にする。
///
/// このテストは、実際にShellWindowを開いた状態（接ぎ木パネル・履歴ペインの実物のXAML）で
/// 無効時の前景色が有効時と異なることを検証する回帰テスト。
/// </summary>
public class DisabledAppearanceTests : IDisposable
{
    private readonly string _appDirectory =
        Path.Combine(Path.GetTempPath(), "graft-disabled-appearance", Guid.NewGuid().ToString("N"));
    private readonly ShownWindowTracker _windows = new();

    public void Dispose()
    {
        _windows.Dispose();
        try
        {
            if (Directory.Exists(_appDirectory)) Directory.Delete(_appDirectory, recursive: true);
        }
        catch (IOException)
        {
            // 後始末の失敗は検証結果に影響しない。
        }
        GC.SuppressFinalize(this);
    }

    private ShellWindow OpenFreshShellWindow()
    {
        var appPaths = new AppPaths(_appDirectory);
        appPaths.EnsureCoreDirectoriesExist();
        var shell = StartupCoordinator.BuildShellViewModel(
            appPaths, new Settings(), new SettingsStore(appPaths), new PatchQueue(appPaths),
            new ProjectStore(appPaths), new RevisionStore(appPaths), new RevisionRestorer(appPaths),
            new NullDialogService(), new AvaloniaUiServices(), openSettings: () => { });

        var window = _windows.Track(new ShellWindow(shell) { Width = 1280, Height = 800 });
        window.Show();
        ShellWindowLoadWaiter.WaitForLayoutApplied(window);
        return window;
    }

    /// <summary>
    /// コマンドにバインドされたButtonの「押せるか」は<see cref="Button.IsEnabled"/>ではなく
    /// <see cref="InputElement.IsEffectivelyEnabled"/>で判定する必要がある。Avalonia の
    /// Buttonは、CanExecute(null)がfalseでも自分の<c>IsEnabled</c>プロパティ自体は既定のtrueの
    /// ままにし、代わりに内部の<c>IsEnabledCore</c>（延いてはIsEffectivelyEnabled、および
    /// このテストが検証したい:disabled疑似クラス）の側でコマンドの状態を織り込む実装になっている
    /// （decompileで確認済み）。これは本テストが見つけたAvalonia側の既存の仕様であり、
    /// 8.14の指摘そのものとは無関係だが、正しいプロパティで検証しないと「無効時の見た目」を
    /// 検証したことにならないため、ここに一括してまとめておく。
    /// </summary>
    private static bool IsEffectivelyDisabled(Button button) => !button.IsEffectivelyEnabled;

    private static Color DisabledTextColor()
    {
        Application.Current!.TryFindResource("TextDisabledColor", null, out var value);
        return (Color)value!;
    }

    /// <summary>
    /// 接ぎ木パネル（GraftPanel）ツールバーの中からボタンを探す。「適用」等はコマンドバー
    /// （ShellWindow.axaml上部）にも同名（同じAutomationProperties.Name・同じApplyCommand）の
    /// ボタンが並んでおり、ウィンドウ全体から探すと重複してSingle()が失敗するため、
    /// GraftPanel配下に絞り込む。
    /// </summary>
    private static Button FindButtonByName(Window window, string automationName)
        => window.GetVisualDescendants().OfType<GraftPanel>().Single()
            .GetVisualDescendants().OfType<Button>()
            .Single(b => AutomationProperties.GetName(b) == automationName);

    [AvaloniaFact(DisplayName = "起動直後（パッチ未解析）は「破棄」「プレビュー」「適用」「失敗を再依頼」がいずれも無効である")]
    public void 起動直後は接ぎ木ツールバーのボタンがいずれも無効である()
    {
        // MainViewModelの既定状態（_currentPatch is null・_dryRun is null・Blocks空）では
        // Discard/Preview/Apply/CopyRecoveryPromptのいずれもCanExecuteがfalseになる
        // （MainViewModel.cs参照）。以降のテストが「無効時の見た目」を検証する前提を確認する。
        var window = OpenFreshShellWindow();
        var vm = (ShellViewModel)window.DataContext!;
        vm.Graft.DiscardCommand.CanExecute(null).Should().BeFalse("ビューモデル側は最初から無効のはず");

        IsEffectivelyDisabled(FindButtonByName(window, "解析結果を破棄")).Should().BeTrue();
        IsEffectivelyDisabled(FindButtonByName(window, "プレビューを再実行")).Should().BeTrue();
        IsEffectivelyDisabled(FindButtonByName(window, "適用を実行")).Should().BeTrue();
        IsEffectivelyDisabled(FindButtonByName(window, "失敗ブロックの再依頼プロンプトをコピー")).Should().BeTrue();
    }

    [AvaloniaFact(DisplayName = "「破棄」ボタン（アイコン＋文字）は無効時にアイコンと文字の両方がtext.disabledへ変わる")]
    public void 破棄ボタンは無効時にアイコンと文字がグレーアウトする()
    {
        var window = OpenFreshShellWindow();
        var discard = FindButtonByName(window, "解析結果を破棄");
        IsEffectivelyDisabled(discard).Should().BeTrue("起動直後は解析結果が無く無効のはず");

        var label = discard.GetVisualDescendants().OfType<TextBlock>().Single(t => Equals(t.Text, "破棄"));
        var icon = discard.GetVisualDescendants().OfType<IconGlyph>().Single();

        var disabledColor = DisabledTextColor();
        ((ISolidColorBrush)label.Foreground!).Color.Should().Be(disabledColor,
            "内側のTextBlockもボタンが無効なら灰色になる必要がある（8.14の指摘の直接原因）");
        ((ISolidColorBrush)icon.Stroke!).Color.Should().Be(disabledColor,
            "アイコンもボタンが無効なら灰色になる必要がある");
    }

    [AvaloniaFact(DisplayName = "「破棄」の無効時の文字色は「失敗を再依頼」の無効時の文字色と一致する（利用者が基準に挙げた見た目に揃っている）")]
    public void 破棄と失敗を再依頼の無効時の色が一致する()
    {
        var window = OpenFreshShellWindow();

        var discard = FindButtonByName(window, "解析結果を破棄");
        var discardLabel = discard.GetVisualDescendants().OfType<TextBlock>().Single(t => Equals(t.Text, "破棄"));

        var recovery = FindButtonByName(window, "失敗ブロックの再依頼プロンプトをコピー");
        // Content="失敗を再依頼"（文字列）はContentPresenterが暗黙にTextBlockを生成する。
        var recoveryLabel = recovery.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => Equals(t.Text, "失敗を再依頼"));

        ((ISolidColorBrush)discardLabel.Foreground!).Color.Should().Be(
            ((ISolidColorBrush)recoveryLabel.Foreground!).Color,
            "アイコン付きボタンも文字列のみのボタンも、無効時は同じ色で揃う必要がある");
    }

    [AvaloniaFact(DisplayName = "アイコン＋文字のボタンは有効/無効を切り替えると見た目も追従する（元に戻る）")]
    public void アイコンと文字のボタンは有効に戻すと色も戻る()
    {
        // 「破棄」ボタン（Views/GraftPanel.axaml）と同じContent構成
        // （StackPanel内にIconGlyph＋TextBlock）を再現する。
        var icon = new IconGlyph { Data = Geometry.Parse("M4,4 L12,12") };
        var label = new TextBlock { Text = "破棄" };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(icon);
        content.Children.Add(label);
        var button = new Button { Content = content };
        var window = _windows.Track(new Window { Width = 200, Height = 100, Content = button });
        window.Show();
        window.CaptureRenderedFrame().Should().NotBeNull();

        var enabledLabelColor = ((ISolidColorBrush)label.Foreground!).Color;
        var enabledIconColor = ((ISolidColorBrush)icon.Stroke!).Color;
        var disabledColor = DisabledTextColor();

        button.IsEnabled = false;
        window.CaptureRenderedFrame().Should().NotBeNull();
        ((ISolidColorBrush)label.Foreground!).Color.Should().Be(disabledColor, "無効時は灰色になる必要がある");
        ((ISolidColorBrush)icon.Stroke!).Color.Should().Be(disabledColor, "無効時はアイコンも灰色になる必要がある");

        button.IsEnabled = true;
        window.CaptureRenderedFrame().Should().NotBeNull();
        ((ISolidColorBrush)label.Foreground!).Color.Should().Be(enabledLabelColor,
            "再び有効にしたら元の文字色へ戻る必要がある");
        ((ISolidColorBrush)icon.Stroke!).Color.Should().Be(enabledIconColor,
            "再び有効にしたら元のアイコン色へ戻る必要がある");
    }

    [AvaloniaFact(DisplayName = "アイコンのみのボタン（履歴ペインのツールバーと同じContent構成）は無効時にアイコンがグレーアウトする")]
    public void アイコンのみのボタンは無効時にアイコンがグレーアウトする()
    {
        // Views/HistoryPane.axamlの「このリビジョンを取り消す」等と同じContent構成
        // （IconGlyph1個のみ）をそのまま再現する。IconGlyph:disabledの効果を検証する。
        var icon = new IconGlyph { Data = Geometry.Parse("M4,4 L12,12") };
        var button = new Button
        {
            Content = icon,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
        };
        var window = _windows.Track(new Window { Width = 200, Height = 100, Content = button });
        window.Show();
        window.CaptureRenderedFrame().Should().NotBeNull();

        var enabledColor = ((ISolidColorBrush)icon.Stroke!).Color;

        button.IsEnabled = false;
        window.CaptureRenderedFrame().Should().NotBeNull();
        var disabledColor = ((ISolidColorBrush)icon.Stroke!).Color;

        disabledColor.Should().NotBe(enabledColor, "無効になったらアイコンの色が変わる必要がある");
        disabledColor.Should().Be(DisabledTextColor(), "無効時のアイコンはtext.disabledで揃う必要がある");
    }

    [AvaloniaFact(DisplayName = "チェックボックスは無効時に箱の枠線もグレーアウトする")]
    public void チェックボックスは無効時に箱の枠線がグレーアウトする()
    {
        var checkBox = new CheckBox { Content = "サンプル" };
        var window = _windows.Track(new Window { Width = 200, Height = 100, Content = checkBox });
        window.Show();
        window.CaptureRenderedFrame().Should().NotBeNull();

        var box = checkBox.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_Box");
        var enabledBorder = ((ISolidColorBrush)box.BorderBrush!).Color;

        checkBox.IsEnabled = false;
        window.CaptureRenderedFrame().Should().NotBeNull();
        var disabledBorder = ((ISolidColorBrush)box.BorderBrush!).Color;

        disabledBorder.Should().NotBe(enabledBorder, "無効になったら箱の枠線の色が変わる必要がある");
        disabledBorder.Should().Be(DisabledTextColor(), "無効時の箱の枠線はtext.disabledで揃う必要がある");
    }
}
