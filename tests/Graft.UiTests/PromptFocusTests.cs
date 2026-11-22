using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Core.PromptFocus;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;
using Graft.Platform.Null;
using Graft.UiTests.TestSupport;
using Graft.UiTests.Vba;
using Graft.ViewModels;

namespace Graft.UiTests;

/// <summary>
/// 依頼の観点（チップ）の選択・共有・記憶・依頼文への反映のテスト。
/// 仕様書_ブックの情報と依頼の観点 6.2・11.2。
/// </summary>
public class PromptFocusTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-promptfocus", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
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

    private static string[] Titles(PromptFocusSelectionViewModel focus) => focus.Groups.Select(g => g.Title).ToArray();

    private static PromptFocusChipViewModel Chip(PromptFocusSelectionViewModel focus, string id)
        => focus.Groups.SelectMany(g => g.Chips).Single(c => c.Id == id);

    private static async Task<ShellHarness> HarnessWithProjectAsync(params (string Path, string Text)[] files)
    {
        var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        foreach (var (path, text) in files)
        {
            var full = h.ProjectPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, text).ConfigureAwait(true);
        }

        await h.RegisterProjectAsync().ConfigureAwait(true);
        return h;
    }

    // ------------------------------------------------------------------
    // 並びと既定
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "VBA でないプロジェクトでは、4つの見出しに14チップが最初から並び、VBA の見出しは出ない。何も選ばれていない")]
    public async Task 通常のプロジェクトでは14チップが並ぶ()
    {
        using var h = await HarnessWithProjectAsync(("a.py", "x = 1\n")).ConfigureAwait(true);
        var focus = h.Shell.Graft.ContextCollect!.Focus;

        Titles(focus).Should().Equal("きれいに", "正しく", "品質", "デザイン");
        focus.Groups.Sum(g => g.Chips.Count).Should().Be(14);
        focus.Groups.SelectMany(g => g.Chips).Should().NotContain(c => c.Id.StartsWith("focus-vba-"));
        focus.SelectedCount.Should().Be(0, "既定は何も選ばない");
        focus.SelectedIds.Should().BeEmpty();
        focus.ComposeText().Should().BeEmpty();
        focus.HeaderText.Should().Be("依頼の観点");
    }

    [AvaloniaFact(DisplayName = "VBA プロジェクトでは5つの見出しに17チップが並び、VBA の見出しに3つ出る")]
    public async Task VBAプロジェクトでは17チップが並ぶ()
    {
        using var rig = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        var focus = rig.Graft.ContextCollect!.Focus;

        Titles(focus).Should().Equal("きれいに", "正しく", "品質", "デザイン", "VBA");
        focus.Groups.Sum(g => g.Chips.Count).Should().Be(17);
        focus.Groups.Last().Chips.Select(c => c.Name).Should().Equal("64bit対応", "参照設定なし", "操作ミス対策");
        focus.SelectedCount.Should().Be(0);
    }

    [AvaloniaFact(DisplayName = "全チップに一言の説明（ツールチップ）があり、読み上げ名は「名前（選択中／未選択）」")]
    public async Task ツールチップと読み上げ名()
    {
        using var rig = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        var focus = rig.Graft.ContextCollect!.Focus;

        focus.Groups.SelectMany(g => g.Chips).Should().OnlyContain(c => !string.IsNullOrWhiteSpace(c.Tooltip));
        var chip = Chip(focus, "focus-readable");
        chip.AutomationName.Should().Be("読みやすく（未選択）");
        chip.IsSelected = true;
        chip.AutomationName.Should().Be("読みやすく（選択中）");
    }

    // ------------------------------------------------------------------
    // 共有・再計算・すべて解除
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "依頼文コピーとコンテキスト収集の窓は同じ選択状態を共有する")]
    public async Task 二つの画面で選択を共有する()
    {
        using var h = await HarnessWithProjectAsync(("a.py", "x = 1\n")).ConfigureAwait(true);
        var graft = h.Shell.Graft;

        graft.PromptCopy!.Context.Should().BeSameAs(graft.ContextCollect);
        graft.PromptCopy.Context.Focus.Should().BeSameAs(graft.ContextCollect!.Focus, "同じインスタンスなので片方で選べばもう片方にも出る");

        Chip(graft.ContextCollect.Focus, "focus-bugfix").IsSelected = true;

        graft.PromptCopy.Context.Focus.SelectedIds.Should().Equal("focus-bugfix");
        graft.ContextCollect.BuildFocusText().Should().Contain("## 不具合を直す");
    }

    [AvaloniaFact(DisplayName = "「すべて解除」で選択がすべて外れ、見出しの数も消える")]
    public async Task すべて解除()
    {
        using var h = await HarnessWithProjectAsync(("a.py", "x = 1\n")).ConfigureAwait(true);
        var focus = h.Shell.Graft.ContextCollect!.Focus;
        Chip(focus, "focus-readable").IsSelected = true;
        Chip(focus, "focus-speed").IsSelected = true;
        focus.HeaderText.Should().Be("依頼の観点 2");
        focus.ClearAllCommand.CanExecute(null).Should().BeTrue();

        focus.ClearAllCommand.Execute(null);

        focus.SelectedCount.Should().Be(0);
        focus.Groups.SelectMany(g => g.Chips).Should().OnlyContain(c => !c.IsSelected);
        focus.HeaderText.Should().Be("依頼の観点");
        focus.ClearAllCommand.CanExecute(null).Should().BeFalse("何も選んでいないときは押せない");
    }

    [AvaloniaFact(DisplayName = "チップを切り替えると、依頼文の推定トークン数が再計算される（選んでいるテンプレートは変わらない）")]
    public async Task 切り替えでトークン推定が再計算される()
    {
        using var h = await HarnessWithProjectAsync(("a.py", "x = 1\n")).ConfigureAwait(true);
        var copy = h.Shell.Graft.PromptCopy!;
        copy.IsOpen = true;
        await ShellHarness.WaitUntilAsync(() => copy.Templates.Count > 0 && copy.SelectedTemplate is not null).ConfigureAwait(true);
        var selectedId = copy.SelectedTemplate!.Template.Id;
        var before = copy.Templates.Single(t => t.Template.Id == selectedId).EstimatedTokens;

        Chip(copy.Context.Focus, "focus-maintainable").IsSelected = true;
        await ShellHarness.WaitUntilAsync(
            () => copy.Templates.Single(t => t.Template.Id == selectedId).EstimatedTokens > before).ConfigureAwait(true);

        copy.Templates.Single(t => t.Template.Id == selectedId).EstimatedTokens.Should().BeGreaterThan(before);
        copy.SelectedTemplate!.Template.Id.Should().Be(selectedId, "切り替えでテンプレートの選択は変わらない");

        copy.Context.Focus.ClearAllCommand.Execute(null);
        await ShellHarness.WaitUntilAsync(
            () => copy.Templates.Single(t => t.Template.Id == selectedId).EstimatedTokens == before).ConfigureAwait(true);
        copy.Templates.Single(t => t.Template.Id == selectedId).EstimatedTokens.Should().Be(before, "解除すれば元の数字に戻る");
    }

    // ------------------------------------------------------------------
    // 依頼文・コンテキスト収集への反映
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "観点を選んで「コピー」すると、共通の決まりが1回だけ入り、「# 対象ファイル」の直前に観点が入る。何も選ばなければ今と同じ文")]
    public async Task 依頼文へ反映される()
    {
        using var h = await HarnessWithProjectAsync(("a.py", "A_MARK = 1\n")).ConfigureAwait(true);
        var copy = h.Shell.Graft.PromptCopy!;
        copy.AppendSelectedFiles = true; // 指示文の後ろに選んだファイルを付ける（{{files}} の無い「初回用」でも「# 対象ファイル」が付く）。

        async Task<string> CopyOnceAsync()
        {
            copy.IsOpen = true;
            await ShellHarness.WaitUntilAsync(() => copy.Templates.Count > 0 && copy.SelectedTemplate is not null).ConfigureAwait(true);
            copy.SelectedTemplate = copy.Templates.Single(t => t.Template.Id == "builtin-full");
            await ShellHarness.ExecuteAsync(copy.CopyCommand).ConfigureAwait(true);
            return h.Clipboard.Text!;
        }

        var baseline = await CopyOnceAsync().ConfigureAwait(true);
        baseline.Should().NotContain("依頼の観点");
        baseline.Should().Contain("# 対象ファイル").And.Contain("A_MARK = 1");

        var focus = copy.Context.Focus;
        Chip(focus, "focus-readable").IsSelected = true;
        Chip(focus, "focus-bugfix").IsSelected = true;
        var withFocus = await CopyOnceAsync().ConfigureAwait(true);

        System.Text.RegularExpressions.Regex.Matches(withFocus, "# 依頼の観点").Count.Should().Be(1);
        System.Text.RegularExpressions.Regex.Matches(withFocus, "## 進め方の決まり").Count.Should().Be(1, "共通の決まりは1回だけ");
        withFocus.Should().Contain("## 読みやすく").And.Contain("## 不具合を直す");
        withFocus.IndexOf("# 依頼の観点", StringComparison.Ordinal)
            .Should().BeLessThan(withFocus.IndexOf("# 対象ファイル", StringComparison.Ordinal), "観点は「# 対象ファイル」の直前");
        withFocus.Should().Contain("- コードの前に、変更点と理由を 5 行以内で書く。\n\n# 対象ファイル\n");

        focus.ClearAllCommand.Execute(null);
        var after = await CopyOnceAsync().ConfigureAwait(true);
        after.Should().Be(baseline, "観点をすべて外せば、依頼文は従来と1文字も変わらない");
    }

    [AvaloniaFact(DisplayName = "VBA プロジェクトでは、依頼文に VBA の前提が1回と、チップごとの VBA 追記が入る")]
    public async Task VBAの依頼文()
    {
        using var rig = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        var copy = rig.Graft.PromptCopy!;
        var focus = copy.Context.Focus;
        Chip(focus, "focus-readable").IsSelected = true;
        Chip(focus, "focus-vba-guard").IsSelected = true;

        copy.IsOpen = true;
        await ShellHarness.WaitUntilAsync(() => copy.Templates.Count > 0 && copy.SelectedTemplate is not null).ConfigureAwait(true);
        await ShellHarness.ExecuteAsync(copy.CopyCommand).ConfigureAwait(true);

        var text = rig.H.Clipboard.Text!;
        System.Text.RegularExpressions.Regex.Matches(text, "## VBA の前提").Count.Should().Be(1);
        text.Should().Contain("- 字下げは 4 スペースにそろえてください。");
        text.Should().Contain("## 利用者の操作ミスに備える");
        text.Should().Contain("- シート名・テーブル名・列は、確かめられないものを推測で使わず、質問してください。",
            "ブックの情報はまだ依頼文に入らないので、推測せず質問させる文になる");
    }

    [AvaloniaFact(DisplayName = "コンテキスト収集のコピーでは、「# 前提」の直後に観点が入る。何も選ばなければ観点の見出しは出ない")]
    public async Task コンテキスト収集へ反映される()
    {
        var clipboard = new FakeClipboard();
        var (vm, _, _) = await BuildContextAsync("collect", clipboard, standing: "Python 3.12").ConfigureAwait(true);

        await ShellHarness.ExecuteAsync(vm.CopyCommand).ConfigureAwait(true);
        clipboard.Text.Should().NotContain("依頼の観点");

        Chip(vm.Focus, "focus-speed").IsSelected = true;
        await ShellHarness.ExecuteAsync(vm.CopyCommand).ConfigureAwait(true);

        var text = clipboard.Text!;
        var standing = text.IndexOf("# 前提", StringComparison.Ordinal);
        var focusAt = text.IndexOf("# 依頼の観点", StringComparison.Ordinal);
        var tree = text.IndexOf("# プロジェクト構成", StringComparison.Ordinal);
        (standing >= 0 && standing < focusAt && focusAt < tree).Should().BeTrue("前提 → 観点 → 構成");
        text.Should().Contain("## 速く");
    }

    // ------------------------------------------------------------------
    // 記憶と復元
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "選択は変えてから約500ms後に projects.json へ保存され、開き直すと復元される（古い Project を渡しても最新が使われる）")]
    public async Task 選択が保存され復元される()
    {
        var clipboard = new FakeClipboard();
        var (vm, appPaths, project) = await BuildContextAsync("persist", clipboard).ConfigureAwait(true);
        var store = new ProjectStore(appPaths);

        Chip(vm.Focus, "focus-docs").IsSelected = true;
        Chip(vm.Focus, "focus-readable").IsSelected = true;
        await ShellHarness.WaitUntilAsync(
            () => store.LoadAsync().GetAwaiter().GetResult().Value.Single().Overrides.PromptFocusIds.Count == 2, 3000).ConfigureAwait(true);

        var saved = (await store.LoadAsync().ConfigureAwait(true)).Value.Single();
        saved.Overrides.PromptFocusIds.Should().Equal("focus-readable", "focus-docs");

        // プロジェクトを切り替えて戻る状況: 一覧側が持つ古い Project（記憶が空）で作り直しても、最新が復元される。
        var reopened = new ContextCollectViewModel(appPaths, store, project, new Settings(), new FakeUiServices(clipboard), new NullDialogService());
        await reopened.ReloadFocusAsync().ConfigureAwait(true);

        reopened.Focus.SelectedIds.Should().Equal("focus-readable", "focus-docs");
        Chip(reopened.Focus, "focus-readable").IsSelected.Should().BeTrue();
    }

    [AvaloniaFact(DisplayName = "保存待ちのままプロジェクトを切り替えて破棄しても、最後の選択は失われない")]
    public async Task 破棄の時に保存待ちを書き出す()
    {
        var clipboard = new FakeClipboard();
        var (vm, appPaths, _) = await BuildContextAsync("flush", clipboard).ConfigureAwait(true);
        var store = new ProjectStore(appPaths);

        Chip(vm.Focus, "focus-security").IsSelected = true;
        vm.Dispose(); // 500ms を待たずに破棄（プロジェクトの切り替え）。
        await ShellHarness.WaitUntilAsync(
            () => store.LoadAsync().GetAwaiter().GetResult().Value.Single().Overrides.PromptFocusIds.Count == 1, 3000).ConfigureAwait(true);

        (await store.LoadAsync().ConfigureAwait(true)).Value.Single().Overrides.PromptFocusIds.Should().Equal("focus-security");
    }

    [AvaloniaFact(DisplayName = "削除された自作の観点と、VBA でないプロジェクトの VBA 専用の観点は、読み込み時に黙って捨てられる。残っている自作は残る")]
    public async Task 無効なIdは黙って捨てられる()
    {
        var clipboard = new FakeClipboard();
        var (vm, appPaths, project) = await BuildContextAsync("stale", clipboard).ConfigureAwait(true);
        var store = new ProjectStore(appPaths);
        await new PromptFocusStore(appPaths).SaveAsync(new[]
        {
            new PromptFocus { Id = "custom-keep", Group = PromptFocusGroups.Mine, Name = "残る", Body = "## 残る\n- x" },
        }).ConfigureAwait(true);
        await store.UpdateAsync(project.Id, p => p with
        {
            Overrides = p.Overrides with
            {
                PromptFocusIds = new[] { "custom-gone", "focus-vba-64bit", "custom-keep", "focus-docs" },
            },
        }).ConfigureAwait(true);

        var reopened = new ContextCollectViewModel(appPaths, store, project, new Settings(), new FakeUiServices(clipboard), new NullDialogService());
        await reopened.ReloadFocusAsync().ConfigureAwait(true);

        reopened.Focus.SelectedIds.Should().Equal("focus-docs", "custom-keep");
        Titles(reopened.Focus).Should().Contain("自分用", "自作の観点があれば末尾に「自分用」の見出しが出る");
        reopened.Focus.Groups.Last().Chips.Single().Name.Should().Be("残る");
    }

    [AvaloniaFact(DisplayName = "focus.json が読めなくても、組み込みの観点は並び、選択も消えない")]
    public async Task 読み込み失敗でも組み込みは出る()
    {
        var clipboard = new FakeClipboard();
        var (vm, appPaths, project) = await BuildContextAsync("broken", clipboard).ConfigureAwait(true);
        await File.WriteAllTextAsync(appPaths.FocusFilePath, "{ 壊れている").ConfigureAwait(true);
        await new ProjectStore(appPaths).UpdateAsync(project.Id, p => p with
        {
            Overrides = p.Overrides with { PromptFocusIds = new[] { "focus-docs" } },
        }).ConfigureAwait(true);

        var reopened = new ContextCollectViewModel(appPaths, new ProjectStore(appPaths), project, new Settings(), new FakeUiServices(clipboard), new NullDialogService());
        await reopened.ReloadFocusAsync().ConfigureAwait(true);

        reopened.Focus.Groups.Sum(g => g.Chips.Count).Should().Be(14);
        reopened.Focus.SelectedIds.Should().Equal("focus-docs");
    }

    // ------------------------------------------------------------------

    private async Task<(ContextCollectViewModel Vm, AppPaths AppPaths, Project Project)> BuildContextAsync(
        string caseName, IClipboardAccess clipboard, string? standing = null)
    {
        var appPaths = new AppPaths(Path.Combine(_root, caseName, "app"));
        appPaths.EnsureCoreDirectoriesExist();
        var projectDir = Path.Combine(_root, caseName, "project");
        Directory.CreateDirectory(projectDir);
        await File.WriteAllTextAsync(Path.Combine(projectDir, "main.py"), "print(1)\n").ConfigureAwait(true);

        var store = new ProjectStore(appPaths);
        var registered = (await store.RegisterAsync(projectDir, caseName).ConfigureAwait(true)).Value;
        if (standing is not null)
        {
            registered = (await store.UpdateAsync(registered.Id, p => p with { StandingContext = standing }).ConfigureAwait(true)).Value;
        }

        var vm = new ContextCollectViewModel(appPaths, store, registered, new Settings(), new FakeUiServices(clipboard), new NullDialogService());
        await vm.InitializeAsync().ConfigureAwait(true);
        return (vm, appPaths, registered);
    }
}
