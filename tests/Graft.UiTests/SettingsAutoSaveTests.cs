using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using FluentAssertions;
using Graft.Core;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;
using Graft.Themes;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests;

/// <summary>
/// 14章 設定画面の即時反映方式（保存ボタンの廃止）の回帰テスト。
/// <see cref="SettingsWindowCloseTests"/>がクローズ経路の一貫性を扱うのに対し、
/// こちらは即時反映の中身（確定タイミング・検証・既定に戻す）を扱う。
///
/// 特に次の3点は不具合の実害が大きいため重点的に検証する。
///   1. TextBoxは打ち替えの途中では確定しない（フォーカスを外した時／Enterキーでのみ確定）。
///      1文字ごとに保存すると「100」を「50」に打ち替える途中の「10」や空文字が
///      settings.jsonへ書き込まれてしまう。
///   2. バリデーションに失敗した値は保存しない。<see cref="SettingsStore.LoadAsync"/>のように
///      黙って既定値へ差し替えて保存すると、画面に見えている値と実際の保存内容が食い違う。
///   3. 「既定に戻す」はsettings.jsonのみを対象とし、projects.json（プロジェクト定義）には
///      触れない。
/// </summary>
public class SettingsAutoSaveTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-settingsautosave", Guid.NewGuid().ToString("N"));

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

    [AvaloniaFact(DisplayName = "TextBoxを打ち替えている途中は保存されず、フォーカスを外した時点の値だけが保存される")]
    public async Task テキスト入力はフォーカスを外すまで保存されない()
    {
        var (vm, appPaths, window) = await BuildWindowAsync();

        var maxRevisions = FindTextBox(window, "最大保持リビジョン数");
        var hotkey = FindTextBox(window, "グローバルホットキー");

        maxRevisions.Focus();
        // 「100」を「50」に打ち替える途中を模す。空文字・「5」を経由するが、
        // どちらもフォーカスを外すまではViewModelへ届かないはず。
        maxRevisions.Text = "";
        vm.MaxRevisionsText.Should().Be("100", "空にした直後はまだ確定していないはず");
        maxRevisions.Text = "5";
        vm.MaxRevisionsText.Should().Be("100", "打ち替えの途中の「5」で確定してはならない");
        maxRevisions.Text = "50";
        vm.MaxRevisionsText.Should().Be("100", "打ち替えの途中の「50」はまだ確定していない（フォーカスが外れていない）");

        // デバウンス時間を過ぎても、そもそも確定していないので保存されていないはず。
        await Task.Delay(400);
        var midway = await new SettingsStore(appPaths).LoadAsync();
        midway.Value.Backup.MaxRevisions.Should().Be(100, "確定前の入力はディスクへ書き込まれてはならない");

        // 別のTextBoxへフォーカスを移す＝LostFocus。ここで初めて確定する。
        hotkey.Focus();
        vm.MaxRevisionsText.Should().Be("50", "フォーカスを外した時点で確定するはず");

        await WaitUntilAsync(async () =>
        {
            var reloaded = await new SettingsStore(appPaths).LoadAsync();
            return reloaded.Value.Backup.MaxRevisions == 50;
        });

        var saved = await new SettingsStore(appPaths).LoadAsync();
        saved.Value.Backup.MaxRevisions.Should().Be(50, "フォーカスを外した時点の最終値だけが保存されている必要がある");
    }

    [AvaloniaFact(DisplayName = "TextBoxはEnterキーでも、フォーカスを外さずに値を確定できる")]
    public async Task テキスト入力はEnterキーでも確定できる()
    {
        var (vm, appPaths, window) = await BuildWindowAsync();

        var maxRevisions = FindTextBox(window, "最大保持リビジョン数");
        maxRevisions.Focus();
        maxRevisions.Text = "50";
        vm.MaxRevisionsText.Should().Be("100", "Enterを押すまではまだ確定していないはず");

        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);

        vm.MaxRevisionsText.Should().Be("50", "Enterキーでフォーカスを外さずに確定できる必要がある");

        await WaitUntilAsync(async () =>
        {
            var reloaded = await new SettingsStore(appPaths).LoadAsync();
            return reloaded.Value.Backup.MaxRevisions == 50;
        });
    }

    [AvaloniaFact(DisplayName = "不正な値（負数）を確定しても保存されず、ValidationIssuesに警告が表示される")]
    public async Task 不正な値は保存されず警告が表示される()
    {
        var (vm, appPaths, window) = await BuildWindowAsync();

        var maxRevisions = FindTextBox(window, "最大保持リビジョン数");
        maxRevisions.Focus();
        maxRevisions.Text = "-5"; // backup.maxRevisionsは0以上が条件
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);

        vm.MaxRevisionsText.Should().Be("-5", "画面上の入力値そのものは書き換えない（黙って既定値へ戻すと入力内容が消えて驚かせる）");

        await WaitUntilAsync(() => Task.FromResult(vm.ValidationIssues.Count > 0));

        vm.ValidationIssues.Should().Contain(
            i => i.Code == ErrorCode.E406 && i.Detail != null && i.Detail.Contains("maxRevisions"),
            "不正な項目が何か分かるよう、既存のValidationIssues表示の仕組みで伝える必要がある");

        // デバウンス時間を過ぎても、不正な値のままでは保存されていないはず。
        await Task.Delay(400);
        var saved = await new SettingsStore(appPaths).LoadAsync();
        saved.Value.Backup.MaxRevisions.Should().Be(
            100, "不正な値を黙って既定値へ差し替えて保存するのも、不正なまま保存するのも避ける必要がある");
    }

    [AvaloniaFact(DisplayName = "不正な値を直してから確定すると、通常どおり保存されValidationIssuesが消える")]
    public async Task 不正な値を直すと保存されValidationIssuesが消える()
    {
        var (vm, appPaths, window) = await BuildWindowAsync();

        var maxRevisions = FindTextBox(window, "最大保持リビジョン数");
        maxRevisions.Focus();
        maxRevisions.Text = "-5";
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        await WaitUntilAsync(() => Task.FromResult(vm.ValidationIssues.Count > 0));

        maxRevisions.Text = "50";
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);

        await WaitUntilAsync(async () =>
        {
            var reloaded = await new SettingsStore(appPaths).LoadAsync();
            return reloaded.Value.Backup.MaxRevisions == 50;
        });

        vm.ValidationIssues.Should().BeEmpty("直った時点でValidationIssuesは消える必要がある");
    }

    [AvaloniaFact(DisplayName = "チェックボックスを切り替えた瞬間に（デバウンス後）settings.jsonへ反映される")]
    public async Task チェックボックスは変更した瞬間に保存が予約される()
    {
        var (vm, appPaths, _) = await BuildWindowAsync();

        vm.UseRecycleBin.Should().BeTrue("既定値はtrue");
        vm.UseRecycleBin = false;

        await WaitUntilAsync(async () =>
        {
            var reloaded = await new SettingsStore(appPaths).LoadAsync();
            return !reloaded.Value.Backup.UseRecycleBin;
        });
    }

    [AvaloniaFact(DisplayName = "「既定に戻す」は確認ダイアログを経て、settings.jsonのみを既定値に戻しprojects.jsonは変更しない")]
    public async Task 既定に戻すは設定のみを対象にする()
    {
        var appPaths = new AppPaths(Path.Combine(_root, Guid.NewGuid().ToString("N")));
        appPaths.EnsureCoreDirectoriesExist();

        // settings.jsonへ既定値と異なる値を仕込んでおく。
        var settingsStore = new SettingsStore(appPaths);
        await settingsStore.SaveAsync(new Settings { Theme = "dark", Backup = new BackupSettings { MaxRevisions = 7 } });

        // projects.jsonにもプロジェクトを1件登録しておき、「既定に戻す」の影響が及ばないことを確認する。
        var projectDir = Path.Combine(_root, "project");
        Directory.CreateDirectory(projectDir);
        var projectStore = new ProjectStore(appPaths);
        var registered = await projectStore.RegisterAsync(projectDir, "対象外プロジェクト");
        registered.IsSuccess.Should().BeTrue();

        var dialogs = new ConfirmingDialogService();
        var vm = new SettingsViewModel(appPaths, dialogs, new AvaloniaUiServices());
        await vm.InitializeAsync();

        vm.SelectedTheme.Should().Be("dark");
        ThemeManager.SelectedTheme.Should().Be(AppTheme.Dark);

        await ExecuteAsync(vm.ResetToDefaultsCommand);

        dialogs.ConfirmCallCount.Should().Be(1, "既定に戻すは破壊的操作なので必ず確認ダイアログを経る必要がある");
        vm.SelectedTheme.Should().Be("system", "画面の入力欄も既定値まで戻る必要がある");
        ThemeManager.SelectedTheme.Should().Be(AppTheme.System, "テーマも即座に既定値へ反映される必要がある");

        var reloadedSettings = await settingsStore.LoadAsync();
        reloadedSettings.Value.Theme.Should().Be("system", "settings.jsonは既定値に戻っている必要がある");
        reloadedSettings.Value.Backup.MaxRevisions.Should().Be(100);

        var reloadedProjects = await projectStore.LoadAsync();
        reloadedProjects.Value.Should().ContainSingle(
            "「既定に戻す」の対象はsettings.jsonのみであり、projects.jsonの内容は変更されてはならない");
        reloadedProjects.Value[0].Name.Should().Be("対象外プロジェクト");
    }

    [AvaloniaFact(DisplayName = "「既定に戻す」の確認ダイアログでキャンセルすると何も変わらない")]
    public async Task 既定に戻すをキャンセルすると変更されない()
    {
        var appPaths = new AppPaths(Path.Combine(_root, Guid.NewGuid().ToString("N")));
        appPaths.EnsureCoreDirectoriesExist();
        var settingsStore = new SettingsStore(appPaths);
        await settingsStore.SaveAsync(new Settings { Theme = "dark" });

        var dialogs = new ConfirmingDialogService { ConfirmResponse = false };
        var vm = new SettingsViewModel(appPaths, dialogs, new AvaloniaUiServices());
        await vm.InitializeAsync();

        await ExecuteAsync(vm.ResetToDefaultsCommand);

        vm.SelectedTheme.Should().Be("dark", "キャンセルした場合は既定値へ戻してはならない");
        var reloaded = await settingsStore.LoadAsync();
        reloaded.Value.Theme.Should().Be("dark");
    }

    private async Task<(SettingsViewModel ViewModel, AppPaths AppPaths, SettingsWindow Window)> BuildWindowAsync()
    {
        var appPaths = new AppPaths(Path.Combine(_root, Guid.NewGuid().ToString("N")));
        appPaths.EnsureCoreDirectoriesExist();
        var vm = new SettingsViewModel(appPaths, new ConfirmingDialogService(), new AvaloniaUiServices());
        var window = new SettingsWindow(vm);
        window.Show();
        await SettleAsync();
        return (vm, appPaths, window);
    }

    private static TextBox FindTextBox(Window window, string automationName)
        => window.GetVisualDescendants().OfType<TextBox>()
            .Single(t => Equals(AutomationProperties.GetName(t), automationName));

    /// <summary>ディスパッチャに積まれた非同期継続（Window.LoadedのInitializeAsync等）を消化させる。</summary>
    private static async Task SettleAsync()
    {
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// デバウンス保存やValidationIssuesの更新など、非同期に進む状態変化の完了を待つ。
    /// 保存には300msのデバウンスを挟むため、CIのように遅い環境でも余裕を持って
    /// 待てるよう5秒（500回×10ms）まで待つ。
    /// </summary>
    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 500; i++)
        {
            if (await condition().ConfigureAwait(true)) return;
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// AsyncRelayCommand.ExecuteはICommand.Execute(void)の制約上async voidになっており、
    /// 呼び出し側からawaitできない。IsExecutingが下がるまで待つことでテストから完了を待機する
    /// （HookSettingsViewModelTestsと同じ手法）。
    /// </summary>
    private static async Task ExecuteAsync(System.Windows.Input.ICommand command)
    {
        command.Execute(null);
        if (command is AsyncRelayCommand async)
        {
            while (async.IsExecuting)
            {
                await Task.Delay(10);
            }
        }
    }

    /// <summary>確認ダイアログに常に既定の応答を返すダイアログ。呼び出し回数も記録する。</summary>
    private sealed class ConfirmingDialogService : IDialogService
    {
        public bool ConfirmResponse { get; set; } = true;

        public int ConfirmCallCount { get; private set; }

        public Task<bool> ConfirmAsync(string title, string message)
        {
            ConfirmCallCount++;
            return Task.FromResult(ConfirmResponse);
        }

        public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel)
            => Task.FromResult<bool?>(true);

        public Task<string?> PromptAsync(string title, string message, string? initial = null)
            => Task.FromResult(initial);

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync(string title, IReadOnlyList<string>? extensions = null) => Task.FromResult<string?>(null);

        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
    }
}
