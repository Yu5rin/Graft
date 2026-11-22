using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Core;
using Graft.Core.PromptFocus;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;
using Graft.Platform.Null;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;

namespace Graft.UiTests;

/// <summary>
/// 依頼の観点（機能 B）の不具合修正（Q1〜Q4 の点検で見つかったもの）の回帰テスト。
/// DisplayName の先頭の「不具合 N」「疑わしい N」は、点検の報告の番号。いずれも修正前のコードでは失敗する。
/// </summary>
public class PromptFocusFixTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-promptfocus-fix", Guid.NewGuid().ToString("N"));

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

    private static PromptFocus Custom(string id, string name) => new()
    {
        Id = id, Group = PromptFocusGroups.Mine, Name = name, Body = $"## {name}\n- {name}の指示",
    };

    private async Task<(AppPaths Paths, ProjectStore Store, Project Project)> BuildAsync(string caseName, params string[] selectedIds)
    {
        var paths = new AppPaths(Path.Combine(_root, caseName, "app"));
        paths.EnsureCoreDirectoriesExist();
        var projectDir = Path.Combine(_root, caseName, "project");
        Directory.CreateDirectory(projectDir);
        await File.WriteAllTextAsync(Path.Combine(projectDir, "main.py"), "print(1)\n").ConfigureAwait(true);
        var store = new ProjectStore(paths);
        var project = (await store.RegisterAsync(projectDir, caseName).ConfigureAwait(true)).Value;
        project = (await store.UpdateAsync(project.Id, p => p with
        {
            Overrides = p.Overrides with { PromptFocusIds = selectedIds },
        }).ConfigureAwait(true)).Value;
        return (paths, store, project);
    }

    private static ContextCollectViewModel Open(AppPaths paths, ProjectStore store, Project project)
        => new(paths, store, project, new Settings(), new FakeUiServices(new FakeClipboard()), new NullDialogService());

    [AvaloniaFact(DisplayName = "不具合 3: focus.json を他のアプリが掴んでいて読めない（E402）間も、自作の観点の選択は消えず、保存される Id からも落ちない")]
    public async Task 不具合3_掴まれて読めなくても選択は消えない()
    {
        var (paths, store, project) = await BuildAsync("locked", "custom-1", "focus-docs").ConfigureAwait(true);
        await new PromptFocusStore(paths).SaveAsync(new[] { Custom("custom-1", "夜の観点") }).ConfigureAwait(true);
        var vm = Open(paths, store, project);

        await using (new FileStream(paths.FocusFilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await vm.ReloadFocusAsync().ConfigureAwait(true);

            vm.Focus.IdsToPersist.Should().Contain("custom-1", "読めなかっただけで、自作の観点の選択を捨ててはならない");
            vm.Focus.IdsToPersist.Should().Contain("focus-docs");
        }

        // 利用者が別のチップを触っても、読めていない自作の Id は保存される Id に残る。
        vm.Focus.Groups.SelectMany(g => g.Chips).First(c => c.Id == "focus-security").IsSelected = true;
        vm.Focus.IdsToPersist.Should().Contain("custom-1");

        // 読めるようになったら、自作の観点が戻り、選択も生きている。
        await vm.ReloadFocusAsync().ConfigureAwait(true);
        vm.Focus.SelectedIds.Should().Contain("custom-1").And.Contain("focus-docs");
        vm.Focus.Groups.SelectMany(g => g.Chips).Should().Contain(c => c.Id == "custom-1");
    }

    [AvaloniaFact(DisplayName = "不具合 3: focus.json が壊れている（E404）ときも、自作の観点の選択は消えない")]
    public async Task 不具合3_壊れていても選択は消えない()
    {
        var (paths, store, project) = await BuildAsync("corrupt", "custom-1").ConfigureAwait(true);
        await File.WriteAllTextAsync(paths.FocusFilePath, "{ 壊れている").ConfigureAwait(true);
        var vm = Open(paths, store, project);

        await vm.ReloadFocusAsync().ConfigureAwait(true);

        vm.Focus.IdsToPersist.Should().Contain("custom-1");
        (await store.LoadAsync().ConfigureAwait(true)).Value.Single().Overrides.PromptFocusIds.Should().Contain("custom-1");
    }

    [AvaloniaFact(DisplayName = "不具合 3: いったん確定した一覧は、あとから読めなかった一覧（組み込みだけ）で差し替えない")]
    public async Task 不具合3_確定した一覧は読めなかった一覧で差し替えない()
    {
        var (paths, store, project) = await BuildAsync("keep", "custom-1").ConfigureAwait(true);
        await new PromptFocusStore(paths).SaveAsync(new[] { Custom("custom-1", "夜の観点") }).ConfigureAwait(true);
        var vm = Open(paths, store, project);
        await vm.ReloadFocusAsync().ConfigureAwait(true);
        vm.Focus.SelectedIds.Should().Equal("custom-1");

        await using (new FileStream(paths.FocusFilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await vm.ReloadFocusAsync().ConfigureAwait(true); // 窓やドロップダウンを開くたびの再読み込み。
        }

        vm.Focus.SelectedIds.Should().Equal(new[] { "custom-1" }, "読めなかった再読み込みで、自作のチップが一覧から消えてはならない");
        vm.Focus.Groups.SelectMany(g => g.Chips).Should().Contain(c => c.Id == "custom-1");
    }

    [AvaloniaFact(DisplayName = "疑わしい 6: 実際の経路（ShellHarness）でも、起動時の保存先の focus.json の自作の観点がチップに出て、依頼文に入る")]
    public async Task 疑わしい6_保存先の自作の観点が実際の経路で出る()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        await File.WriteAllTextAsync(h.ProjectPath("a.py"), "x = 1\n").ConfigureAwait(true);
        await h.RegisterProjectAsync().ConfigureAwait(true);
        await new PromptFocusStore(h.AppPaths).SaveAsync(new[] { Custom("custom-1", "夜の観点") }).ConfigureAwait(true);
        var context = h.Shell.Graft.ContextCollect!;

        await context.ReloadFocusAsync().ConfigureAwait(true);

        var chip = context.Focus.Groups.SelectMany(g => g.Chips).SingleOrDefault(c => c.Id == "custom-1");
        chip.Should().NotBeNull("起動時の保存先（ハーネスでは AppDirectory）の focus.json を見る。実行ファイルの隣の data ではない");
        chip!.IsSelected = true;
        await h.Shell.Graft.PromptCopy!.QuickCopyAsync().ConfigureAwait(true);
        h.Clipboard.Text.Should().Contain("## 夜の観点").And.Contain("夜の観点の指示");
    }

    [AvaloniaFact(DisplayName = "疑わしい 7: focus.json を書けなかったとき（E402 の保護中）は、「複製しました」と出さず、失敗を伝えて一覧も元に戻す")]
    public async Task 疑わしい7_書けなければ追加したと出さない()
    {
        var paths = new AppPaths(Path.Combine(_root, "settings", "app"));
        paths.EnsureCoreDirectoriesExist();
        await new PromptFocusStore(paths).SaveAsync(new[] { Custom("custom-1", "既存") }).ConfigureAwait(true);
        var before = await File.ReadAllTextAsync(paths.FocusFilePath).ConfigureAwait(true);
        var vm = new PromptFocusSettingsViewModel(paths, new ScriptedDialogs());

        await using (new FileStream(paths.FocusFilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await vm.InitializeAsync().ConfigureAwait(true);
            vm.Focuses.Should().HaveCount(17, "前提: 読み込みは警告つきで、組み込みだけが出る");
            vm.SelectedFocus = vm.Focuses.Single(f => f.Id == "focus-readable");

            await ShellHarness.ExecuteAsync(vm.DuplicateCommand).ConfigureAwait(true);
        }

        vm.StatusMessage.Should().NotContain("複製しました").And.Contain("できませんでした").And.Contain("E402");
        vm.ErrorIssue!.Code.Should().Be(ErrorCode.E402);
        vm.Focuses.Should().HaveCount(17, "書けなかった観点が、追加されたように一覧に残ってはならない");
        (await File.ReadAllTextAsync(paths.FocusFilePath).ConfigureAwait(true)).Should().Be(before, "読めなかった実体は上書きしない");
    }

    [AvaloniaFact(DisplayName = "不具合 8: 不正な値（body が null）の自作の観点を選んでも依頼文の組み立てで落ちない。設定画面の初期化も止まらない")]
    public async Task 不具合8_不正な値でも落ちない()
    {
        var (paths, store, project) = await BuildAsync("nulls", "custom-1").ConfigureAwait(true);
        await File.WriteAllTextAsync(paths.FocusFilePath, """
            { "focuses": [ { "id": "custom-1", "group": null, "name": null, "body": null },
                           { "id": "custom-2", "group": "自分用", "name": "夜", "body": null } ] }
            """).ConfigureAwait(true);
        var vm = Open(paths, store, project);

        await vm.ReloadFocusAsync().ConfigureAwait(true);
        vm.Focus.Groups.SelectMany(g => g.Chips).First(c => c.Id == "custom-2").IsSelected = true;

        var text = () => vm.Focus.ComposeText();
        text.Should().NotThrow();
        vm.Focus.ComposeText().Should().Contain("## 夜");

        await File.WriteAllTextAsync(paths.FocusFilePath, "{ \"focuses\": null }").ConfigureAwait(true);
        var settings = new PromptFocusSettingsViewModel(paths, new ScriptedDialogs());
        await settings.InitializeAsync().ConfigureAwait(true);
        settings.Focuses.Should().HaveCount(17);
    }
}
