using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Graft.Core.PromptFocus;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;
using Graft.Platform.Null;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;
using Graft.Views;
using Graft.Views.SettingsPanels;

namespace Graft.UiTests;

/// <summary>
/// 依頼の観点のパネル（<see cref="PromptFocusPanel"/>）と、設定の「依頼の観点」タブ（<see cref="FocusSettingsView"/>）のテスト。
/// 仕様書_ブックの情報と依頼の観点 6.2・6.6・11.2。
/// </summary>
public class PromptFocusPanelAndSettingsTests : IDisposable
{
    private readonly ShownWindowTracker _windows = new();

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-promptfocus-ui", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _windows.Dispose();
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 後片付けの失敗は検証結果に影響しない。
        }

        GC.SuppressFinalize(this);
    }

    private static PromptFocusSelectionViewModel NewSelection(bool isVba)
        => new(PromptFocusStore.BuiltIns, isVba, Array.Empty<string>());

    private PromptFocusPanel ShowPanel(PromptFocusSelectionViewModel vm)
    {
        var panel = new PromptFocusPanel { DataContext = vm };
        var window = _windows.Track(new Window { Content = panel, Width = 420, Height = 360 });
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return panel;
    }

    // ------------------------------------------------------------------
    // パネル
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "パネル: 全チップが最初から並び（折りたたまない）、各チップに HelpTip と読み上げ名がある")]
    public void パネルにチップが並ぶ()
    {
        var panel = ShowPanel(NewSelection(isVba: true));

        var chips = panel.GetVisualDescendants().OfType<ToggleButton>().ToList();
        chips.Should().HaveCount(17);
        chips.Should().OnlyContain(c => HelpTip.GetStandard(c) != null && HelpTip.GetDetailed(c) != null,
            "操作できるコントロールには HelpTip を付ける（SettingsHelpTipCoverageTests と同じ規約）");
        chips.Should().OnlyContain(c => c.IsVisible && c.IsEffectivelyVisible, "折りたたまれていない");
        chips.Select(c => Avalonia.Automation.AutomationProperties.GetName(c)).Should().Contain("読みやすく（未選択）");
        panel.GetVisualDescendants().OfType<Button>().Single(b => b.GetType() == typeof(Button)).Content.Should().Be("すべて解除");
    }

    [AvaloniaFact(DisplayName = "パネル: VBA でないプロジェクトでは14チップで、VBA のチップは出ない")]
    public void VBAでなければVBAのチップは出ない()
    {
        var panel = ShowPanel(NewSelection(isVba: false));

        var names = panel.GetVisualDescendants().OfType<ToggleButton>().Select(c => c.Content?.ToString()).ToList();
        names.Should().HaveCount(14);
        names.Should().NotContain("64bit対応").And.NotContain("参照設定なし").And.NotContain("操作ミス対策");
    }

    [AvaloniaFact(DisplayName = "パネル: チップを押すと選択状態に反映され、見出しに数が出る。「すべて解除」で戻る")]
    public void パネルの操作が選択へ反映される()
    {
        var vm = NewSelection(isVba: false);
        var panel = ShowPanel(vm);
        var chip = panel.GetVisualDescendants().OfType<ToggleButton>().First(c => Equals(c.Content, "速く"));

        chip.IsChecked = true;
        Dispatcher.UIThread.RunJobs();

        vm.SelectedIds.Should().Equal("focus-speed");
        panel.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).Should().Contain("依頼の観点 1");

        var clear = panel.GetVisualDescendants().OfType<Button>().Single(b => b.GetType() == typeof(Button));
        clear.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        chip.IsChecked.Should().BeFalse();
        vm.SelectedCount.Should().Be(0);
    }

    [AvaloniaFact(DisplayName = "パネル: 外から選択が変わる（もう片方の画面での操作）と、チップの表示も追随する")]
    public void 外からの変更が表示に出る()
    {
        var vm = NewSelection(isVba: false);
        var panel = ShowPanel(vm);

        vm.Groups.SelectMany(g => g.Chips).Single(c => c.Id == "focus-docs").IsSelected = true;
        Dispatcher.UIThread.RunJobs();

        panel.GetVisualDescendants().OfType<ToggleButton>().Single(c => Equals(c.Content, "説明を書く"))
            .IsChecked.Should().BeTrue();
    }

    [AvaloniaFact(DisplayName = "コンテキスト収集の窓と依頼文コピーのドロップダウンに、同じパネルが置かれている")]
    public async Task 二つの画面にパネルがある()
    {
        using var h = await ShellHarness.CreateAsync(withWindow: true).ConfigureAwait(true);
        Directory.CreateDirectory(h.ProjectDirectory);
        await File.WriteAllTextAsync(Path.Combine(h.ProjectDirectory, "a.py"), "x = 1\n").ConfigureAwait(true);
        await h.RegisterProjectAsync().ConfigureAwait(true);
        var context = h.Shell.Graft.ContextCollect!;

        // ドロップダウン（ShellWindow）
        h.Shell.Graft.PromptCopy!.IsOpen = true;
        Dispatcher.UIThread.RunJobs();
        var popupPanels = h.Window!.GetVisualDescendants().OfType<PromptFocusPanel>().ToList();
        popupPanels.Should().NotBeEmpty("ドロップダウンの中にパネルがある");
        popupPanels.Should().OnlyContain(p => ReferenceEquals(p.DataContext, context.Focus));

        // コンテキスト収集の窓
        var window = _windows.Track(new ContextCollectWindow(context));
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var windowPanel = window.GetVisualDescendants().OfType<PromptFocusPanel>().Single();
        windowPanel.DataContext.Should().BeSameAs(context.Focus, "2つの画面は同じ選択状態を見る");
        windowPanel.GetVisualDescendants().OfType<ToggleButton>().Should().HaveCount(14);
    }

    // ------------------------------------------------------------------
    // 設定のタブ
    // ------------------------------------------------------------------

    private (PromptFocusSettingsViewModel Vm, AppPaths Paths, ScriptedDialogs Dialogs) NewSettings(string name)
    {
        var paths = new AppPaths(Path.Combine(_root, name));
        paths.EnsureCoreDirectoriesExist();
        var dialogs = new ScriptedDialogs();
        return (new PromptFocusSettingsViewModel(paths, dialogs), paths, dialogs);
    }

    [AvaloniaFact(DisplayName = "設定: 組み込み17件が並び、組み込みは編集も削除も保存もできない")]
    public async Task 組み込みは編集できない()
    {
        var (vm, _, _) = NewSettings("builtin");
        await vm.InitializeAsync().ConfigureAwait(true);

        vm.Focuses.Should().HaveCount(17);
        vm.SelectedFocus = vm.Focuses.First();

        vm.SelectedFocus.IsBuiltIn.Should().BeTrue();
        vm.SelectedFocus.IsEditable.Should().BeFalse();
        vm.SaveEditCommand.CanExecute(null).Should().BeFalse();
        vm.DeleteCommand.CanExecute(null).Should().BeFalse();
        vm.DuplicateCommand.CanExecute(null).Should().BeTrue("組み込みも複製はできる");
    }

    [AvaloniaFact(DisplayName = "設定: 組み込みを複製して編集・保存でき、focus.json に自作だけが書かれる")]
    public async Task 複製して編集して保存できる()
    {
        var (vm, paths, _) = NewSettings("dup");
        await vm.InitializeAsync().ConfigureAwait(true);
        vm.SelectedFocus = vm.Focuses.Single(f => f.Id == "focus-readable");

        await ShellHarness.ExecuteAsync(vm.DuplicateCommand).ConfigureAwait(true);

        var copy = vm.SelectedFocus!;
        copy.IsBuiltIn.Should().BeFalse();
        copy.Id.Should().StartWith("custom-");
        copy.Name.Should().Be("読みやすく（複製）");
        copy.Body.Should().Be(PromptFocusStore.BuiltIns.Single(f => f.Id == "focus-readable").Body, "指示文は引き継ぐ");
        copy.VbaAddendum.Should().Contain("字下げは 4 スペース");
        vm.Focuses.Should().HaveCount(18);
        vm.Focuses.Last().Should().BeSameAs(copy, "自作は組み込みの後ろ（追加順）");

        copy.Name = "私の読みやすさ";
        copy.Group = PromptFocusGroups.Mine;
        copy.Tooltip = "自分用の説明";
        copy.Body = "- 本文だけ（見出しが無い）";
        await ShellHarness.ExecuteAsync(vm.SaveEditCommand).ConfigureAwait(true);

        copy.Source.Body.Should().Be("## 私の読みやすさ\n- 本文だけ（見出しが無い）", "先頭が「## 」でなければ名前の見出しを補う");
        var json = await File.ReadAllTextAsync(paths.FocusFilePath).ConfigureAwait(true);
        json.Should().Contain("私の読みやすさ").And.Contain("自分用の説明");
        json.Should().NotContain("focus-readable", "組み込みは書き出さない");

        var reloaded = new PromptFocusSettingsViewModel(paths, new ScriptedDialogs());
        await reloaded.InitializeAsync().ConfigureAwait(true);
        reloaded.Focuses.Should().HaveCount(18);
        reloaded.Focuses.Last().Name.Should().Be("私の読みやすさ");
    }

    [AvaloniaFact(DisplayName = "設定: 追加は「自分用」の見出しで末尾に入り、削除は確認のうえで消える")]
    public async Task 追加と削除()
    {
        var (vm, paths, dialogs) = NewSettings("adddel");
        await vm.InitializeAsync().ConfigureAwait(true);
        dialogs.PromptAnswer = (_, _, _) => "チェック観点";

        await ShellHarness.ExecuteAsync(vm.AddCommand).ConfigureAwait(true);

        var added = vm.SelectedFocus!;
        added.Name.Should().Be("チェック観点");
        added.Group.Should().Be("自分用");
        added.Body.Should().Be("## チェック観点");
        File.Exists(paths.FocusFilePath).Should().BeTrue();

        dialogs.ConfirmAnswer = (_, _) => false;
        await ShellHarness.ExecuteAsync(vm.DeleteCommand).ConfigureAwait(true);
        vm.Focuses.Should().Contain(added, "確認で「いいえ」なら消えない");

        dialogs.ConfirmAnswer = (_, _) => true;
        await ShellHarness.ExecuteAsync(vm.DeleteCommand).ConfigureAwait(true);
        vm.Focuses.Should().NotContain(added);
        (await File.ReadAllTextAsync(paths.FocusFilePath).ConfigureAwait(true)).Should().NotContain("チェック観点");
    }

    [AvaloniaFact(DisplayName = "設定: 「VBA」の見出しは、VBA 専用の観点でないと保存できない")]
    public async Task VBAの見出しはVBA専用だけ()
    {
        var (vm, _, _) = NewSettings("vbagroup");
        await vm.InitializeAsync().ConfigureAwait(true);
        vm.SelectedFocus = vm.Focuses.Single(f => f.Id == "focus-readable");
        await ShellHarness.ExecuteAsync(vm.DuplicateCommand).ConfigureAwait(true);
        var copy = vm.SelectedFocus!;

        copy.Group = PromptFocusGroups.Vba;
        copy.VbaOnly = false;
        await ShellHarness.ExecuteAsync(vm.SaveEditCommand).ConfigureAwait(true);
        vm.StatusMessage.Should().Contain("VBA 専用");
        copy.Source.Group.Should().NotBe(PromptFocusGroups.Vba, "保存されていない");

        copy.VbaOnly = true;
        await ShellHarness.ExecuteAsync(vm.SaveEditCommand).ConfigureAwait(true);
        copy.Source.Group.Should().Be(PromptFocusGroups.Vba);
        copy.Source.VbaOnly.Should().BeTrue();
    }

    [AvaloniaFact(DisplayName = "設定: 指示文に {{ が含まれると変数は使えない旨の警告が出る")]
    public async Task 変数の警告()
    {
        var (vm, _, _) = NewSettings("warn");
        await vm.InitializeAsync().ConfigureAwait(true);
        vm.SelectedFocus = vm.Focuses.First();
        await ShellHarness.ExecuteAsync(vm.DuplicateCommand).ConfigureAwait(true);
        var copy = vm.SelectedFocus!;
        copy.HasVariableWarning.Should().BeFalse();

        copy.Body = "## x\n- {{files}} を見て";

        copy.HasVariableWarning.Should().BeTrue();
    }

    [AvaloniaFact(DisplayName = "設定: タブが設定ウィンドウに並び、中の操作できるコントロールに HelpTip が付いている")]
    public async Task タブが並びHelpTipが付く()
    {
        var paths = new AppPaths(Path.Combine(_root, "tab"));
        paths.EnsureCoreDirectoriesExist();
        var vm = new SettingsViewModel(paths, new NullDialogService(), new AvaloniaUiServices());
        await vm.InitializeAsync().ConfigureAwait(true);
        var window = _windows.Track(new SettingsWindow(vm) { Width = 1000, Height = 720 });
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var tabs = window.GetVisualDescendants().OfType<TabControl>().First(t => t.Name == "CategoryTabControl");
        var tab = tabs.Items.OfType<TabItem>().Single(t => Equals(t.Header, "依頼の観点"));
        HelpTip.GetStandard(tab).Should().NotBeNull();
        tabs.SelectedItem = tab;
        Dispatcher.UIThread.RunJobs();

        var view = window.GetVisualDescendants().OfType<FocusSettingsView>().Single();
        var targets = view.GetVisualDescendants().OfType<Control>()
            // SettingsHelpTipCoverageTests と同じく、種類は型そのもので選ぶ（ToggleButton・RepeatButton などの派生は含めない）。
            .Where(c => c.GetType() == typeof(Button) || c.GetType() == typeof(CheckBox) || c.GetType() == typeof(ComboBox)
                        || c.GetType() == typeof(TextBox) || c.GetType() == typeof(ListBox))
            .ToList();
        targets.Should().NotBeEmpty();
        targets.Where(c => HelpTip.GetStandard(c) is null)
            .Select(c => c.GetType().Name + "[" + Avalonia.Automation.AutomationProperties.GetName(c) + "]")
            .Should().BeEmpty();
        view.GetVisualDescendants().OfType<ListBox>().First().ItemCount.Should().Be(17);
    }
}
