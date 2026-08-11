using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Core;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests;

/// <summary>
/// プロジェクトペイン改善（利用者からの明示的な要望7項目）の通しシナリオ。
/// <see cref="ScenarioTests"/>と同じ作法（本物のShellViewModel・ShellWindowを画面ありで
/// 動かし、利用者の操作の流れを追う）で、それぞれの回帰を確認する。
///
/// 「削除でフォルダが消えないこと」は最重要の安全要件のため、実ファイルを作ったうえで
/// 削除操作の前後でフォルダ・ファイルが残っていることを直接確認する。
/// 「再起動後も保たれること」は、同じAppPathsに対して新しいProjectStoreを作って読み直す
/// （プロセス再起動を模す。他の永続化テスト（ProjectStorePaneOperationsTests等）と同じ作法）
/// ことで確認する。
/// </summary>
public class ProjectPaneOperationsScenarioTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-projectpane-ops", Guid.NewGuid().ToString("N"));
    private readonly string _appDirectory;
    private readonly ShownWindowTracker _windows = new();

    public ProjectPaneOperationsScenarioTests()
    {
        _appDirectory = Path.Combine(_root, "app");
        Directory.CreateDirectory(_appDirectory);
    }

    public void Dispose()
    {
        _windows.Dispose();
        TempDirectoryCleanup.TryDeleteRecursive(_root);
        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------------
    // 要望1: 削除
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "削除（履歴は残す）は登録情報だけを消し、プロジェクトフォルダとファイルは一切消えない")]
    public async Task 削除は履歴を残す場合もフォルダを消さない()
    {
        var projectDir = CreateProjectDir("keep-folder");
        var filePath = Path.Combine(projectDir, "important.txt");
        await File.WriteAllTextAsync(filePath, "消えたら困る内容");

        // 不具合2点検（実機報告の横断チェック）: 「履歴も削除する」は不可逆な破壊的操作のため
        // ConfirmThreeWayAsyncのyesLabel（既定ボタン）からnoLabelへ移した
        // （ProjectPaneViewModel.DeleteSelectedProjectAsync参照）。trueが「履歴は残す」になる。
        var dialogs = new ScriptedDialogService { ThreeWayResult = true }; // 「履歴は残す」
        var (shell, _) = await OpenShellAsync(dialogs).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(projectDir).ConfigureAwait(true);
        shell.Graft.ProjectPane.Items.Should().ContainSingle();

        await ExecuteAsync(shell.Graft.ProjectPane.DeleteProjectCommand).ConfigureAwait(true);

        shell.Graft.ProjectPane.Items.Should().BeEmpty("削除後は一覧から消えているはず");
        Directory.Exists(projectDir).Should().BeTrue("フォルダ自体は絶対に消してはいけない");
        File.Exists(filePath).Should().BeTrue("フォルダ内のファイルも消してはいけない");
        (await File.ReadAllTextAsync(filePath)).Should().Be("消えたら困る内容");

        // 実機不具合の回帰確認: 既定ボタン（yesLabel）は不可逆な「履歴も削除する」ではなく、
        // 非破壊的な「履歴は残す」でなければならない。
        dialogs.LastThreeWayLabels.Should().Be(("履歴は残す", "履歴も削除する"),
            "既定ボタン（Enterで実行される）に破壊的な選択肢を渡してはいけない");

        // 再起動を模して読み直しても消えたままであること（永続化の確認）。
        var reloadedStore = new ProjectStore(new AppPaths(_appDirectory));
        (await reloadedStore.LoadAsync()).Value.Should().BeEmpty();
    }

    [AvaloniaFact(DisplayName = "削除（履歴も削除）を選んだ場合もプロジェクトフォルダとファイルは一切消えない")]
    public async Task 削除は履歴も削除する場合もフォルダを消さない()
    {
        var projectDir = CreateProjectDir("keep-folder2");
        var filePath = Path.Combine(projectDir, "important2.txt");
        await File.WriteAllTextAsync(filePath, "これも消えたら困る");

        var dialogs = new ScriptedDialogService { ThreeWayResult = false }; // 「履歴も削除する」（falseがnoLabel）
        var (shell, _) = await OpenShellAsync(dialogs).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(projectDir).ConfigureAwait(true);

        await ExecuteAsync(shell.Graft.ProjectPane.DeleteProjectCommand).ConfigureAwait(true);

        Directory.Exists(projectDir).Should().BeTrue("履歴を削除する選択でも、プロジェクトフォルダ自体は安全性のため絶対に消さない");
        File.Exists(filePath).Should().BeTrue();
    }

    [AvaloniaFact(DisplayName = "削除確認ダイアログでキャンセルすると何も削除されない")]
    public async Task 削除確認をキャンセルすると何も起きない()
    {
        var projectDir = CreateProjectDir("cancel-delete");
        var dialogs = new ScriptedDialogService { ThreeWayResult = null }; // キャンセル
        var (shell, _) = await OpenShellAsync(dialogs).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(projectDir).ConfigureAwait(true);

        await ExecuteAsync(shell.Graft.ProjectPane.DeleteProjectCommand).ConfigureAwait(true);

        shell.Graft.ProjectPane.Items.Should().ContainSingle("キャンセルされたので一覧から消えてはいけない");
        Directory.Exists(projectDir).Should().BeTrue();
    }

    [AvaloniaFact(DisplayName = "削除で一覧が空になると、エディタのタブが閉じエクスプローラが未選択状態に戻る")]
    public async Task 最後のプロジェクトを削除すると未選択状態に戻る()
    {
        var projectDir = CreateProjectDir("last-project");
        var filePath = Path.Combine(projectDir, "open-me.txt");
        await File.WriteAllTextAsync(filePath, "開いてから消す");

        var dialogs = new ScriptedDialogService { ThreeWayResult = true }; // 「履歴は残す」（この項目自体は履歴の扱いを検証対象にしない）
        var (shell, _) = await OpenShellAsync(dialogs).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(projectDir).ConfigureAwait(true);
        await shell.Editor.OpenFileAsync(filePath).ConfigureAwait(true);
        shell.Editor.Tabs.Should().ContainSingle();

        await ExecuteAsync(shell.Graft.ProjectPane.DeleteProjectCommand).ConfigureAwait(true);

        shell.Graft.ProjectPane.SelectedItem.Should().BeNull("選べるプロジェクトが無いので未選択に戻るはず");
        shell.Editor.Tabs.Should().BeEmpty("プロジェクト未選択に戻ったら開いていたタブは閉じるはず");
        shell.Explorer.HasProject.Should().BeFalse();
        File.Exists(filePath).Should().BeTrue("タブを閉じてもファイル自体は消えない");
    }

    [AvaloniaFact(DisplayName = "削除しても他にプロジェクトが残っていれば、自動的に別のプロジェクトが選択される")]
    public async Task 削除後に他のプロジェクトが残っていれば自動選択される()
    {
        var projectA = CreateProjectDir("multi-a");
        var projectB = CreateProjectDir("multi-b");

        var dialogs = new ScriptedDialogService { ThreeWayResult = true }; // 「履歴は残す」（この項目自体は履歴の扱いを検証対象にしない）
        var (shell, _) = await OpenShellAsync(dialogs).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(projectA).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(projectB).ConfigureAwait(true);
        shell.Graft.ProjectPane.Items.Should().HaveCount(2);

        // 現在選択中（直近に登録したB）を削除する。
        await ExecuteAsync(shell.Graft.ProjectPane.DeleteProjectCommand).ConfigureAwait(true);

        shell.Graft.ProjectPane.Items.Should().ContainSingle();
        shell.Graft.ProjectPane.SelectedItem.Should().NotBeNull("Aが残っているので自動的に選ばれるはず");
        shell.Graft.ProjectPane.SelectedItem!.Project.Root.Should().Be(projectA);
    }

    // ------------------------------------------------------------------
    // 要望2: ダブルクリックでエクスプローラへ切り替え
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "プロジェクトのダブルクリック（NotifyActivated）でサイドビューがエクスプローラへ切り替わる")]
    public async Task ダブルクリックでエクスプローラへ切り替わる()
    {
        var projectDir = CreateProjectDir("dbl-click");
        var (shell, _) = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(projectDir).ConfigureAwait(true);

        // 別のサイドビュー（履歴）を表示している状態から始める。
        shell.SelectSideView(SideViewKind.History);
        shell.SelectedSideView.Should().Be(SideViewKind.History);

        var project = shell.Graft.ProjectPane.SelectedItem!.Project;
        shell.Graft.ProjectPane.NotifyActivated(project);

        shell.SelectedSideView.Should().Be(SideViewKind.Explorer, "ダブルクリックでエクスプローラへ切り替わる必要がある");
        shell.IsSideViewCollapsed.Should().BeFalse("折りたたまれていたら展開されるはず");
    }

    [AvaloniaFact(DisplayName = "サイドビューが折りたたまれていても、ダブルクリックで展開されエクスプローラになる")]
    public async Task ダブルクリックは折りたたみを展開する()
    {
        var projectDir = CreateProjectDir("dbl-click-collapsed");
        var (shell, _) = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(projectDir).ConfigureAwait(true);

        // 既定で選択中のプロジェクトビュー（ShellViewModelの初期値）を再クリックして折りたたむ
        // （SelectSideViewの既存仕様: 表示中のビューを再クリックすると折りたたむ）。
        shell.SelectedSideView.Should().Be(SideViewKind.Project, "ShellViewModelの既定値のはず");
        shell.SelectSideView(SideViewKind.Project);
        shell.IsSideViewCollapsed.Should().BeTrue();

        var project = shell.Graft.ProjectPane.SelectedItem!.Project;
        shell.Graft.ProjectPane.NotifyActivated(project);

        shell.SelectedSideView.Should().Be(SideViewKind.Explorer);
        shell.IsSideViewCollapsed.Should().BeFalse();
    }

    [AvaloniaFact(DisplayName = "単クリック相当（SelectedItemの変更）はサイドビューを切り替えない（既存挙動を変えない）")]
    public async Task 単クリックはサイドビューを切り替えない()
    {
        var projectA = CreateProjectDir("single-click-a");
        var projectB = CreateProjectDir("single-click-b");
        var (shell, _) = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(projectA).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(projectB).ConfigureAwait(true);

        shell.SelectSideView(SideViewKind.History);
        var itemA = shell.Graft.ProjectPane.Items.Single(i => i.Project.Root == projectA);

        shell.Graft.ProjectPane.SelectedItem = itemA; // 単クリックと同じ経路。

        shell.SelectedSideView.Should().Be(SideViewKind.History, "単クリックでの選択だけではサイドビューを変えてはいけない");
    }

    // ------------------------------------------------------------------
    // 要望3: ピン留め
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "ピン留めを切り替えると一覧の並び順・IsPinnedへ反映され、永続化される")]
    public async Task ピン留めの切替が一覧と永続化へ反映される()
    {
        // 不具合3対応: ピン留め無し・どちらも一度もパッチを適用していない（NextRevision=1）
        // プロジェクト同士は、最終適用日時ではなく登録順で安定して並ぶ（ProjectStore.Sort参照）。
        // そのため先に登録したBが既定では先頭に来る。Aを後から登録し、Aをピン留めすることで
        // 「ピン留めが先頭へ移動させる」ことを検証する。
        var projectA = CreateProjectDir("pin-a"); // 後に登録＝ピン留めするまでは先頭に来ない。
        var projectB = CreateProjectDir("pin-b"); // 先に登録＝ピン留め無しの既定では先頭。

        var (shell, _) = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(projectB).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(projectA).ConfigureAwait(true);
        shell.Graft.ProjectPane.Items[0].Project.Root.Should().Be(projectB,
            "ピン留め無し・双方未適用なら登録順（先に登録したB）が先頭のはず");

        var itemA = shell.Graft.ProjectPane.Items.Single(i => i.Project.Root == projectA);
        shell.Graft.ProjectPane.SelectedItem = itemA;

        await ExecuteAsync(shell.Graft.ProjectPane.TogglePinCommand).ConfigureAwait(true);

        shell.Graft.ProjectPane.Items[0].Project.Root.Should().Be(projectA, "ピン留めしたAが先頭に来るはず");
        shell.Graft.ProjectPane.Items[0].IsPinned.Should().BeTrue();

        // 再起動を模して永続化を確認する。
        var reloadedStore = new ProjectStore(new AppPaths(_appDirectory));
        var reloaded = await reloadedStore.LoadAsync().ConfigureAwait(true);
        reloaded.Value.Single(p => p.Root == projectA).Pinned.Should().BeTrue();

        // もう一度切り替えると解除される。
        var itemAAgain = shell.Graft.ProjectPane.Items.Single(i => i.Project.Root == projectA);
        shell.Graft.ProjectPane.SelectedItem = itemAAgain;
        await ExecuteAsync(shell.Graft.ProjectPane.TogglePinCommand).ConfigureAwait(true);
        shell.Graft.ProjectPane.Items.Single(i => i.Project.Root == projectA).IsPinned.Should().BeFalse();
    }

    [AvaloniaFact(DisplayName = "複数をピン留めするとピン留めした順に並び、解除して再度ピン留めすると最後尾に来る")]
    public async Task 複数ピン留めの順序と再ピン留めの実際の挙動()
    {
        // 要望対応（ピン留め同士は「ピン留めした順」に並ぶ）: TogglePinCommand（実際の右クリック
        // メニュー→ProjectStore更新の経路）を使って、C→A→Bの順にピン留めしたときに
        // 一覧がその順（C, A, B）で並ぶこと、さらにAを解除して再度ピン留めすると
        // ピン留め済みグループの最後尾（C, B, A）に来ることを確認する。
        var projectA = CreateProjectDir("multi-pin-a");
        var projectB = CreateProjectDir("multi-pin-b");
        var projectC = CreateProjectDir("multi-pin-c");

        var (shell, _) = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(projectA).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(projectB).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(projectC).ConfigureAwait(true);

        async Task TogglePin(string root)
        {
            var item = shell.Graft.ProjectPane.Items.Single(i => i.Project.Root == root);
            shell.Graft.ProjectPane.SelectedItem = item;
            await ExecuteAsync(shell.Graft.ProjectPane.TogglePinCommand).ConfigureAwait(true);
            // PinnedAtはDateTimeOffset.Nowを使うため、連続で呼ぶと同時刻になり得る環境差を避ける
            // ための最小限のウェイト。
            await Task.Delay(10).ConfigureAwait(true);
        }

        await TogglePin(projectC).ConfigureAwait(true); // 1番目にピン留め
        await TogglePin(projectA).ConfigureAwait(true); // 2番目にピン留め
        await TogglePin(projectB).ConfigureAwait(true); // 3番目にピン留め

        shell.Graft.ProjectPane.Items.Select(i => i.Project.Root).Should().ContainInOrder(
            new[] { projectC, projectA, projectB },
            "ピン留めした順（C→A→B）に並ぶはず");

        await TogglePin(projectA).ConfigureAwait(true); // Aを解除
        shell.Graft.ProjectPane.Items.Single(i => i.Project.Root == projectA).IsPinned.Should().BeFalse();

        await TogglePin(projectA).ConfigureAwait(true); // Aを再度ピン留め

        shell.Graft.ProjectPane.Items.Select(i => i.Project.Root).Should().ContainInOrder(
            new[] { projectC, projectB, projectA },
            "解除して再度ピン留めしたAは新しいPinnedAtを持つため、ピン留め済みの最後尾に来るはず");
    }

    // ------------------------------------------------------------------
    // 要望4: 表示名の変更
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "表示名の変更が一覧・永続化へ反映される")]
    public async Task 表示名の変更が反映される()
    {
        var projectDir = CreateProjectDir("rename-me");
        var dialogs = new ScriptedDialogService { PromptResult = "新しい表示名" };
        var (shell, _) = await OpenShellAsync(dialogs).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(projectDir).ConfigureAwait(true);

        await ExecuteAsync(shell.Graft.ProjectPane.RenameProjectCommand).ConfigureAwait(true);

        shell.Graft.ProjectPane.SelectedItem!.DisplayName.Should().Be("新しい表示名");

        var reloadedStore = new ProjectStore(new AppPaths(_appDirectory));
        var reloaded = await reloadedStore.LoadAsync().ConfigureAwait(true);
        reloaded.Value.Single().Name.Should().Be("新しい表示名", "再起動を模しても保たれているはず");
    }

    [AvaloniaFact(DisplayName = "表示名を空にすると、フォルダ名から自動生成される既定の名前へ戻る")]
    public async Task 表示名を空にすると既定へ戻る()
    {
        var projectDir = CreateProjectDir("reset-name-target");
        var dialogs = new ScriptedDialogService { PromptResult = "カスタム名" };
        var (shell, _) = await OpenShellAsync(dialogs).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(projectDir).ConfigureAwait(true);
        await ExecuteAsync(shell.Graft.ProjectPane.RenameProjectCommand).ConfigureAwait(true);
        shell.Graft.ProjectPane.SelectedItem!.DisplayName.Should().Be("カスタム名");

        dialogs.PromptResult = string.Empty; // 空でOK。
        await ExecuteAsync(shell.Graft.ProjectPane.RenameProjectCommand).ConfigureAwait(true);

        shell.Graft.ProjectPane.SelectedItem!.DisplayName.Should().Be("reset-name-target",
            "空にした場合はフォルダ名（ProjectNameFormatter.Normalize）由来の既定へ戻るはず");
    }

    [AvaloniaFact(DisplayName = "表示名の変更ダイアログをキャンセルすると変わらない")]
    public async Task 表示名の変更はキャンセルできる()
    {
        var projectDir = CreateProjectDir("rename-cancel");
        var dialogs = new ScriptedDialogService { PromptResult = null }; // キャンセル
        var (shell, _) = await OpenShellAsync(dialogs).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(projectDir).ConfigureAwait(true);
        var before = shell.Graft.ProjectPane.SelectedItem!.DisplayName;

        await ExecuteAsync(shell.Graft.ProjectPane.RenameProjectCommand).ConfigureAwait(true);

        shell.Graft.ProjectPane.SelectedItem!.DisplayName.Should().Be(before);
    }

    // ------------------------------------------------------------------
    // 要望5: タグの編集
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "タグの編集がカンマ区切りで解釈され、前後の空白を落として空要素を除いて反映・永続化される")]
    public async Task タグの編集が反映される()
    {
        var projectDir = CreateProjectDir("tag-me");
        var dialogs = new ScriptedDialogService { PromptResult = " web , backend ,,  " };
        var (shell, _) = await OpenShellAsync(dialogs).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(projectDir).ConfigureAwait(true);

        await ExecuteAsync(shell.Graft.ProjectPane.EditTagsCommand).ConfigureAwait(true);

        shell.Graft.ProjectPane.SelectedItem!.TagsText.Should().Be("web / backend");

        var reloadedStore = new ProjectStore(new AppPaths(_appDirectory));
        var reloaded = await reloadedStore.LoadAsync().ConfigureAwait(true);
        reloaded.Value.Single().Tags.Should().BeEquivalentTo(new[] { "web", "backend" });
    }

    // ------------------------------------------------------------------
    // 要望6: 場所の変更（行方不明プロジェクトの再結び付け）
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "場所を変更すると、未接続プロジェクトが同じプロジェクトのまま新しい場所へ再接続され、履歴が引き継がれる")]
    public async Task 場所の変更で未接続プロジェクトが再接続される()
    {
        var oldRoot = CreateProjectDir("relocate-old");
        var dialogs = new ScriptedDialogService();
        var (shell, _) = await OpenShellAsync(dialogs).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(oldRoot).ConfigureAwait(true);
        var oldId = shell.Graft.ProjectPane.SelectedItem!.Project.Id;

        // back/<projectId>/ に履歴フォルダがある状態を用意する（実際の適用を模す）。
        var appPaths = new AppPaths(_appDirectory);
        var oldBackupDir = appPaths.GetProjectBackupDirectory(oldId);
        Directory.CreateDirectory(Path.Combine(oldBackupDir, "r1_20260101_000000"));

        // フォルダを移動・リネームして未接続を再現する。
        var newRoot = Path.Combine(_root, "relocate-new");
        Directory.Move(oldRoot, newRoot);
        await shell.Graft.ProjectPane.LoadAsync().ConfigureAwait(true);
        shell.Graft.ProjectPane.Items.Single().IsDisconnected.Should().BeTrue("フォルダが移動したので未接続になるはず");

        dialogs.PickFolderResult = newRoot;

        await ExecuteAsync(shell.Graft.ProjectPane.RelocateProjectCommand).ConfigureAwait(true);

        var relocated = shell.Graft.ProjectPane.SelectedItem!.Project;
        relocated.Root.Should().Be(newRoot);
        relocated.IsDisconnected.Should().BeFalse();
        relocated.Id.Should().NotBe(oldId, "Rootが変わるとIdも変わるはず");

        var newBackupDir = appPaths.GetProjectBackupDirectory(relocated.Id);
        Directory.Exists(Path.Combine(newBackupDir, "r1_20260101_000000")).Should().BeTrue(
            "履歴フォルダが新しいIdの場所へ引き継がれ、履歴が切り離されていないはず");
    }

    // ------------------------------------------------------------------
    // 要望7: D&D登録（複数フォルダの一括登録）
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "複数フォルダを続けて登録すると、いずれも一覧に反映され永続化される")]
    public async Task 複数フォルダの登録がいずれも反映される()
    {
        var projectA = CreateProjectDir("dnd-a");
        var projectB = CreateProjectDir("dnd-b");
        var projectC = CreateProjectDir("dnd-c");

        var (shell, _) = await OpenShellAsync().ConfigureAwait(true);
        // OnDrop内部の複数件ループと同じ形（順に登録）。
        foreach (var folder in new[] { projectA, projectB, projectC })
        {
            await shell.Graft.ProjectPane.RegisterFolderAsync(folder).ConfigureAwait(true);
        }

        shell.Graft.ProjectPane.Items.Should().HaveCount(3);

        var reloadedStore = new ProjectStore(new AppPaths(_appDirectory));
        var reloaded = await reloadedStore.LoadAsync().ConfigureAwait(true);
        reloaded.Value.Select(p => p.Root).Should().BeEquivalentTo(new[] { projectA, projectB, projectC });
    }

    // ------------------------------------------------------------------
    // ヘルパ
    // ------------------------------------------------------------------

    private string CreateProjectDir(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private async Task<(ShellViewModel Shell, Window Window)> OpenShellAsync(IDialogService? dialogs = null)
    {
        var appPaths = new AppPaths(_appDirectory);
        appPaths.EnsureCoreDirectoriesExist();

        var settingsStore = new SettingsStore(appPaths);
        await settingsStore.SaveAsync(new Settings { ShowPreview = false }).ConfigureAwait(true);

        var usedDialogs = dialogs ?? new ScriptedDialogService();
        var shell = StartupCoordinator.BuildShellViewModel(
            appPaths,
            new Settings { ShowPreview = false },
            settingsStore,
            new PatchQueue(appPaths),
            new ProjectStore(appPaths),
            new RevisionStore(appPaths),
            new RevisionRestorer(appPaths),
            usedDialogs,
            new AvaloniaUiServices(),
            openSettings: () => { });

        var window = _windows.Track(new ShellWindow(shell) { Width = 1280, Height = 800 });
        window.Show();
        await WaitForShellInitializedAsync(shell).ConfigureAwait(true);
        return (shell, window);
    }

    private static async Task WaitForShellInitializedAsync(ShellViewModel shell)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            while (shell.Graft.ProjectPane.State == ProjectPaneState.Loading)
            {
                await Task.Delay(10, cts.Token).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException ex)
        {
            throw new TimeoutException(
                "ShellWindow.OnLoaded経由の初期化が30秒以内に完了しませんでした（ProjectPane.StateがLoadingのまま）。", ex);
        }
    }

    private static async Task ExecuteAsync(System.Windows.Input.ICommand command)
    {
        command.Execute(null);
        if (command is AsyncRelayCommand async)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (async.IsExecuting)
            {
                if (cts.IsCancellationRequested) throw new TimeoutException("コマンドの完了待ちがタイムアウトしました。");
                await Task.Delay(10).ConfigureAwait(true);
            }
        }
    }

    /// <summary>各ダイアログの戻り値をテストごとに差し替えられるスクリプト可能なダイアログ実装。</summary>
    private sealed class ScriptedDialogService : IDialogService
    {
        public bool ConfirmResult { get; set; } = true;
        public bool? ThreeWayResult { get; set; } = true;
        public string? PromptResult { get; set; } = "テスト";
        public string? PickFolderResult { get; set; }

        /// <summary>直近のConfirmThreeWayAsync呼び出しのyesLabel/noLabel（不具合2の回帰確認用）。</summary>
        public (string YesLabel, string NoLabel)? LastThreeWayLabels { get; private set; }

        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(ConfirmResult);

        public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel)
        {
            LastThreeWayLabels = (yesLabel, noLabel);
            return Task.FromResult(ThreeWayResult);
        }

        public Task<string?> PromptAsync(string title, string message, string? initial = null)
            => Task.FromResult(PromptResult);

        public Task<string?> PickFolderAsync(string title) => Task.FromResult(PickFolderResult);

        public Task<string?> PickFileAsync(string title, IReadOnlyList<string>? extensions = null) => Task.FromResult<string?>(null);
        public Task<string?> SaveFileAsync(string title, string suggestedFileName, IReadOnlyList<string>? extensions = null) => Task.FromResult<string?>(null);

        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
    }
}
