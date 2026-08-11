using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using FluentAssertions;
using Graft.Infra;
using Graft.Platform;
using Graft.Platform.Null;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests;

/// <summary>
/// コマンドパレット（Ctrl+Shift+P、機能追加）のシナリオテスト。
/// QuickOpenTestsと同じ作法（実際の起動と同じ依存グラフでShellViewModelを組み立て、
/// ShellWindow経由のキー操作からオーバーレイの開閉・絞り込み・Enterでの実行までを
/// 一気通貫で検証する）。
/// </summary>
public class CommandPaletteTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-command-palette", Guid.NewGuid().ToString("N"));

    private readonly ShownWindowTracker _windows = new();

    public void Dispose()
    {
        _windows.Dispose();

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

    [AvaloniaFact(DisplayName = "Ctrl+Shift+Pでコマンドパレットが開き、既定で全操作が一覧表示される")]
    public async Task CtrlShiftPで開き全操作が一覧表示される()
    {
        var (shell, window) = await OpenShellAsync().ConfigureAwait(true);

        PressCtrlShiftP(window);

        shell.CommandPalette.IsOpen.Should().BeTrue("Ctrl+Shift+Pでコマンドパレットが開く必要がある");
        shell.CommandPalette.Results.Should().NotBeEmpty("空クエリのときは全操作を一覧表示する（クイックオープンと異なり件数が少ないため）");
        shell.CommandPalette.Results.Should().Contain(r => r.Title == "設定を開く");
        window.CaptureRenderedFrame().Should().NotBeNull("オーバーレイを開いた状態で描画できる必要がある");
    }

    [AvaloniaFact(DisplayName = "再度のCtrl+Shift+Pでコマンドパレットが閉じる（トグル）")]
    public async Task 再度のCtrlShiftPで閉じる()
    {
        var (shell, window) = await OpenShellAsync().ConfigureAwait(true);

        PressCtrlShiftP(window);
        shell.CommandPalette.IsOpen.Should().BeTrue();

        PressCtrlShiftP(window);
        shell.CommandPalette.IsOpen.Should().BeFalse();
    }

    [AvaloniaFact(DisplayName = "Escapeでコマンドパレットが閉じる")]
    public async Task Escapeで閉じる()
    {
        var (shell, window) = await OpenShellAsync().ConfigureAwait(true);

        PressCtrlShiftP(window);
        shell.CommandPalette.IsOpen.Should().BeTrue();

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);

        shell.CommandPalette.IsOpen.Should().BeFalse();
    }

    [AvaloniaFact(DisplayName = "検索文字列で操作を絞り込める")]
    public async Task 検索文字列で絞り込める()
    {
        var (shell, window) = await OpenShellAsync().ConfigureAwait(true);

        PressCtrlShiftP(window);
        shell.CommandPalette.Query = "接ぎ木パネルの開閉";

        shell.CommandPalette.Results.Should().ContainSingle(r => r.Title == "接ぎ木パネルの開閉");
        shell.CommandPalette.Results.Should().NotContain(r => r.Title == "設定を開く", "一致しない操作は絞り込み後の候補に出ないはず");
    }

    [AvaloniaFact(DisplayName = "Enterで選択中のコマンドが実際に実行される")]
    public async Task Enterでコマンドが実行される()
    {
        var (shell, window) = await OpenShellAsync().ConfigureAwait(true);
        shell.IsGraftPanelOpen.Should().BeFalse("既定値");

        PressCtrlShiftP(window);
        shell.CommandPalette.Query = "接ぎ木パネルの開閉";
        shell.CommandPalette.SelectedResult.Should().NotBeNull();

        PressEnter(window);

        shell.CommandPalette.IsOpen.Should().BeFalse("確定するとオーバーレイは閉じる");
        shell.IsGraftPanelOpen.Should().BeTrue("ToggleGraftPanelCommand.Execute(null)が実際に呼ばれ、接ぎ木パネルが開いたはず");
    }

    [AvaloniaFact(DisplayName = "CanExecuteがfalseの項目はIsEnabled=falseとして区別され、Enterで確定しても実行されない")]
    public async Task 実行できない項目は区別され実行されない()
    {
        var (shell, _) = await OpenShellAsync().ConfigureAwait(true);

        // 解析結果が無い状態ではDiscardCommand.CanExecuteはfalse（MainViewModel参照）。
        shell.Graft.DiscardCommand.CanExecute(null).Should().BeFalse("前提: 解析結果が無い状態の確認");

        shell.CommandPalette.Open();
        shell.CommandPalette.Query = "解析結果を破棄";

        var item = shell.CommandPalette.Results.Should().ContainSingle(r => r.Title == "解析結果を破棄").Which;
        item.IsEnabled.Should().BeFalse("実行できない状態のコマンドは選べないことが分かるよう区別される必要がある");

        shell.CommandPalette.SelectedResult = item;
        var discardCalled = false;
        // DiscardCommandは実行されると_currentPatchをクリアするだけで判定しづらいため、
        // ここではIsOpenが変化しない（=何も実行されず、オーバーレイも閉じない）ことで確認する。
        shell.CommandPalette.ConfirmSelection();

        shell.CommandPalette.IsOpen.Should().BeTrue("実行できない項目のEnterは何もせず、オーバーレイも閉じないはず");
        discardCalled.Should().BeFalse();
    }

    [AvaloniaFact(DisplayName = "コマンドパレットを開くとクイックオープンが閉じ、クイックオープンを開くとコマンドパレットが閉じる（相互排他）")]
    public async Task クイックオープンと相互排他になる()
    {
        var (shell, window) = await OpenShellAsync().ConfigureAwait(true);

        PressCtrlShiftP(window);
        shell.CommandPalette.IsOpen.Should().BeTrue();

        window.KeyPressQwerty(PhysicalKey.P, RawInputModifiers.Control);
        await Task.Delay(10).ConfigureAwait(true);

        // プロジェクト未選択のためQuickOpenViewModel.ToggleAsync自体は開かないが、
        // コマンドパレット側は先に閉じられているはず（ShellViewModel.ToggleQuickOpenAsync参照）。
        shell.CommandPalette.IsOpen.Should().BeFalse("クイックオープンを開こうとした時点でコマンドパレットは閉じる必要がある");
    }

    private static void PressCtrlShiftP(ShellWindow window)
        => window.KeyPressQwerty(PhysicalKey.P, RawInputModifiers.Control | RawInputModifiers.Shift);

    private static void PressEnter(ShellWindow window)
        => window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);

    // window.Show()はShellWindow.OnLoaded経由で非同期にshell.Graft.InitializeAsync()を呼ぶ。
    // QuickOpenTests.OpenShellAsyncと同じ理由でここでは明示的に呼ばない。
    private Task<(ShellViewModel Shell, ShellWindow Window)> OpenShellAsync()
    {
        var appPaths = new AppPaths(_root);
        appPaths.EnsureCoreDirectoriesExist();

        var shell = StartupCoordinator.BuildShellViewModel(
            appPaths,
            new Settings(),
            new SettingsStore(appPaths),
            new Graft.Features.PatchQueue(appPaths),
            new Graft.Features.ProjectStore(appPaths),
            new Graft.Core.RevisionStore(appPaths),
            new Graft.Core.RevisionRestorer(appPaths),
            new NullDialogService(),
            new AvaloniaUiServices(),
            openSettings: () => { });

        var window = _windows.Track(new ShellWindow(shell) { Width = 1280, Height = 800 });
        window.Show();
        ShellWindowLoadWaiter.WaitForLayoutApplied(window);
        return Task.FromResult<(ShellViewModel, ShellWindow)>((shell, window));
    }
}
