using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Core;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;
using Graft.Platform.Null;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests;

/// <summary>
/// 機能改善（Ctrl+マウスホイールでの文字サイズ変更）の回帰テスト。
///
/// 検証する内容:
/// 1. エディタ本文・差分表示それぞれで、ホイール操作の結果が8〜32の範囲にクランプされること。
/// 2. どちらか一方での変更が、既存の設定即時保存の経路（SettingsViewModelの300msデバウンス
///    保存、LiveSettingsPropagationTests参照）に乗って settings.json（Editor.FontSize）へ
///    永続化されること。
/// 3. 一方での変更が、もう一方の表示（エディタ本文と差分表示は同じSettings.Editor.FontSizeを
///    共有する）へも再起動なしで同期すること。
/// 4. 設定画面（SettingsViewModel.EditorFontSizeText）を後から開いても、ホイール操作で
///    変えた値と食い違わないこと。
///
/// StartupCoordinator.StartAsync自体は実際のトレイ・ホットキー等に触れるため（他のテストと
/// 同様の理由で）ここでは呼ばない。代わりに、StartAsyncが行っているのと同じ配線
/// （ShellViewModel.EditorFontSizeChangeRequested → SettingsViewModel.SetEditorFontSizeLive、
/// SettingsViewModelのonLiveSettingsChanged → MainViewModel.UpdateSettings /
/// EditorPaneViewModel.UpdateSettings）をテスト側で再現する
/// （LiveSettingsPropagationTests.OpenShellAsyncと同じ考え方）。
/// </summary>
public class EditorFontSizeZoomTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-font-zoom", Guid.NewGuid().ToString("N"));

    private readonly string _appDirectory;
    private readonly string _projectDirectory;

    public EditorFontSizeZoomTests()
    {
        _appDirectory = Path.Combine(_root, "app");
        _projectDirectory = Path.Combine(_root, "project");
        Directory.CreateDirectory(_appDirectory);
        Directory.CreateDirectory(_projectDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 後始末の失敗は検証結果に影響しない。
        }

        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------------
    // 1. クランプ（8〜32）
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "エディタ本文: Ctrl+マウスホイールでのフォントサイズは8〜32にクランプされる")]
    public void エディタ本文のフォントサイズは8から32にクランプされる()
    {
        var vm = new EditorPaneViewModel(new Settings(), new NullDialogService(), new AvaloniaUiServices());
        vm.FontSize.Should().Be(13, "既定値（Settings.Editor.FontSize）から始まるはず");

        vm.AdjustFontSize(-100);
        vm.FontSize.Should().Be(8, "下限を下回る操作は8にクランプされるはず");

        vm.AdjustFontSize(1000);
        vm.FontSize.Should().Be(32, "上限を上回る操作は32にクランプされるはず");
    }

    [AvaloniaFact(DisplayName = "差分表示: Ctrl+マウスホイールでのフォントサイズは8〜32にクランプされる")]
    public void 差分表示のフォントサイズは8から32にクランプされる()
    {
        var vm = new DiffViewModel(new Settings(), new AvaloniaUiServices());
        vm.CodeFontSize.Should().Be(13, "既定値（Settings.Editor.FontSize）から始まるはず");

        vm.AdjustCodeFontSize(-100);
        vm.CodeFontSize.Should().Be(8);

        vm.AdjustCodeFontSize(1000);
        vm.CodeFontSize.Should().Be(32);
    }

    [AvaloniaFact(DisplayName = "AdjustFontSize/AdjustCodeFontSizeは変更確定のたびにFontSizeChangeCommittedを発火する")]
    public void 確定のたびにイベントが発火する()
    {
        var editorVm = new EditorPaneViewModel(new Settings(), new NullDialogService(), new AvaloniaUiServices());
        double? editorRaised = null;
        editorVm.FontSizeChangeCommitted += (_, size) => editorRaised = size;
        editorVm.AdjustFontSize(2);
        editorRaised.Should().Be(15);

        var diffVm = new DiffViewModel(new Settings(), new AvaloniaUiServices());
        double? diffRaised = null;
        diffVm.FontSizeChangeCommitted += (_, size) => diffRaised = size;
        diffVm.AdjustCodeFontSize(-2);
        diffRaised.Should().Be(11);
    }

    // ------------------------------------------------------------------
    // 2〜4. 永続化・相互同期・設定画面との整合
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "エディタ本文でのCtrl+マウスホイールが、設定即時保存の経路でsettings.jsonへ永続化され、差分表示にも同期する")]
    public async Task エディタでの変更が永続化され差分表示にも同期する()
    {
        var appPaths = new AppPaths(_appDirectory);
        appPaths.EnsureCoreDirectoriesExist();
        var (shell, settingsVm) = await OpenShellWithLiveSettingsAsync(appPaths).ConfigureAwait(true);

        shell.Editor.FontSize.Should().Be(13);
        shell.Graft.Diff.CodeFontSize.Should().Be(13);

        // 実際のView（EditorPane.axaml.cs）と同じ経路: AdjustFontSize経由でホイール操作を模擬する。
        shell.Editor.AdjustFontSize(5);
        shell.Editor.FontSize.Should().Be(18, "ローカルの表示はホイール操作の直後に即座に反映される");

        var store = new SettingsStore(appPaths);
        await WaitUntilAsync(async () => (await store.LoadAsync().ConfigureAwait(true)).Value.Editor.FontSize == 18)
            .ConfigureAwait(true);

        var reloaded = await store.LoadAsync().ConfigureAwait(true);
        reloaded.Value.Editor.FontSize.Should().Be(18, "設定画面のフォントサイズ欄と同じ経路で settings.json へ保存される必要がある");

        // 保存成功後にonLiveSettingsChanged経由でMainViewModel.UpdateSettings/EditorPaneViewModel.
        // UpdateSettingsへ伝播し、差分表示のフォントサイズも再起動なしで追従するはず。
        shell.Graft.Diff.CodeFontSize.Should().Be(18, "エディタ本文と差分表示は同じSettings.Editor.FontSizeを共有するはず");

        // 後から設定画面を開いても（新しいSettingsViewModelインスタンスでも）食い違わない。
        settingsVm.EditorFontSizeText.Should().Be("18");
    }

    [AvaloniaFact(DisplayName = "差分表示でのCtrl+マウスホイールが、永続化されエディタ本文にも同期する（逆方向）")]
    public async Task 差分表示での変更が永続化されエディタにも同期する()
    {
        var appPaths = new AppPaths(_appDirectory);
        appPaths.EnsureCoreDirectoriesExist();
        var (shell, _) = await OpenShellWithLiveSettingsAsync(appPaths).ConfigureAwait(true);

        shell.Graft.Diff.AdjustCodeFontSize(-3);
        shell.Graft.Diff.CodeFontSize.Should().Be(10);

        var store = new SettingsStore(appPaths);
        await WaitUntilAsync(async () => (await store.LoadAsync().ConfigureAwait(true)).Value.Editor.FontSize == 10)
            .ConfigureAwait(true);

        shell.Editor.FontSize.Should().Be(10, "差分表示側の変更もエディタ本文へ同期するはず");
    }

    [AvaloniaFact(DisplayName = "settings.jsonに既に保存されていた値は、設定画面を一度も開かなくてもホイール保存で消えない")]
    public async Task 既存の設定値はホイール保存で失われない()
    {
        var appPaths = new AppPaths(_appDirectory);
        appPaths.EnsureCoreDirectoriesExist();

        // あらかじめ「非既定」の値をいくつか保存しておく（設定画面をまだ一度も開いていない状況を再現）。
        var preset = new Settings
        {
            Theme = "dark",
            Git = new GitSettings { AutoCommit = true },
            Editor = new EditorSettings { FontSize = 13 },
        };
        await new SettingsStore(appPaths).SaveAsync(preset).ConfigureAwait(true);

        var (shell, _) = await OpenShellWithLiveSettingsAsync(appPaths, loadExisting: true).ConfigureAwait(true);
        shell.Editor.AdjustFontSize(4);

        var store = new SettingsStore(appPaths);
        await WaitUntilAsync(async () => (await store.LoadAsync().ConfigureAwait(true)).Value.Editor.FontSize == 17)
            .ConfigureAwait(true);

        var reloaded = (await store.LoadAsync().ConfigureAwait(true)).Value;
        reloaded.Editor.FontSize.Should().Be(17);
        reloaded.Theme.Should().Be("dark", "ホイール操作での保存が、設定画面を開かずに保存した他の項目を既定値へ巻き戻してはならない");
        reloaded.Git.AutoCommit.Should().BeTrue("同上（並行実装の保存経路だと、常駐SettingsViewModelを未初期化のまま使い既定値で上書きしてしまう恐れがある）");
    }

    // ------------------------------------------------------------------
    // ヘルパ
    // ------------------------------------------------------------------

    /// <summary>
    /// StartupCoordinator.StartAsyncが行っている配線（常駐SettingsViewModel・
    /// EditorFontSizeChangeRequestedの購読・onLiveSettingsChangedからのUpdateSettings伝播）を
    /// テスト側で再現する。
    /// </summary>
    private async Task<(ShellViewModel Shell, SettingsViewModel SettingsVm)> OpenShellWithLiveSettingsAsync(
        AppPaths appPaths, bool loadExisting = false)
    {
        var settingsStore = new SettingsStore(appPaths);
        if (!loadExisting)
        {
            await settingsStore.SaveAsync(new Settings { ShowPreview = false }).ConfigureAwait(true);
        }

        var initial = (await settingsStore.LoadAsync().ConfigureAwait(true)).Value;

        var shell = StartupCoordinator.BuildShellViewModel(
            appPaths, initial, settingsStore, new PatchQueue(appPaths),
            new ProjectStore(appPaths), new RevisionStore(appPaths), new RevisionRestorer(appPaths),
            new NullDialogService(), new AvaloniaUiServices(), openSettings: () => { });
        await shell.Graft.InitializeAsync().ConfigureAwait(true);

        var settingsVm = new SettingsViewModel(
            appPaths, new NullDialogService(), new AvaloniaUiServices(),
            onLiveSettingsChanged: updated =>
            {
                shell.Graft.UpdateSettings(updated);
                shell.Editor.UpdateSettings(updated);
            });
        await settingsVm.InitializeAsync().ConfigureAwait(true);

        shell.EditorFontSizeChangeRequested += (_, size) => settingsVm.SetEditorFontSizeLive(size);

        return (shell, settingsVm);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 200; i++)
        {
            if (await condition().ConfigureAwait(true)) return;
            await Task.Delay(10).ConfigureAwait(true);
        }

        // 最終試行。失敗すればAssertion側のShould()で明確な失敗として表れる。
    }
}
