using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using FluentAssertions;
using Graft.Infra;
using Graft.Platform;
using Graft.Themes;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests;

/// <summary>
/// バグ2（設定画面を保存せずに閉じるとテーマが既定へ巻き戻る）の回帰テスト。
///
/// 真因は、<see cref="SettingsViewModel.SelectedTheme"/> のsetterが<see cref="ThemeManager.SetTheme"/>を
/// 呼んで即時プレビュー反映する一方、「閉じる」ボタン・Escape・ウィンドウの×（Closing）が
/// 単にウィンドウを閉じるだけでプレビューを取り消さなかったことにある。次に開いた瞬間、
/// <c>PopulateEditableFields</c>が保存済みの値へ<c>SelectedTheme</c>を戻すため、
/// 利用者には「テーマが既定へ戻った」ように見えていた。
///
/// 対応は<see cref="SettingsViewModel.RequestCloseAsync"/>への一本化: 未保存の変更が無ければ
/// 確認なしで閉じてよい、あれば「保存する／破棄して閉じる／キャンセル」を確認し、破棄なら
/// プレビューを保存済みの状態へ戻す。「閉じる」ボタン・Escape・ウィンドウの×はすべて
/// <see cref="SettingsWindow"/>の同じ入口（コードビハインドのRequestCloseAsync）を通るため、
/// ここではViewModel単体の分岐（保存/破棄/キャンセル）と、Escape・×の2経路が実際に
/// その入口へ到達することの両方を検証する（「閉じる」ボタンはコードビハインド上Escapeと
/// 全く同じ1行呼び出しであることに加え、ボタン自体のクリックもここで直接検証する）。
/// </summary>
public class SettingsWindowCloseTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-settingsclose", Guid.NewGuid().ToString("N"));

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

    [AvaloniaFact(DisplayName = "変更が無ければ確認ダイアログを出さずに閉じてよいと判定する")]
    public async Task 変更なしなら確認なしで閉じてよい()
    {
        var (vm, dialogs, _) = await BuildViewModelAsync();

        var shouldClose = await vm.RequestCloseAsync();

        shouldClose.Should().BeTrue("未保存の変更が無いのに確認を挟むと、毎回確認が出て煩わしくなる");
        dialogs.ConfirmThreeWayCallCount.Should().Be(0, "変更が無い操作で確認ダイアログを出してはならない");
    }

    [AvaloniaFact(DisplayName = "テーマを変更して「破棄して閉じる」を選ぶと、プレビューが保存済みの値へ戻る")]
    public async Task 破棄するとテーマのプレビューが戻る()
    {
        var (vm, dialogs, appPaths) = await BuildViewModelAsync();
        dialogs.ConfirmThreeWayResponse = false; // 「破棄して閉じる」

        // 既定（system）からダークへ切り替える。setter経由でThemeManagerへ即時プレビュー反映される。
        vm.SelectedTheme = "dark";
        ThemeManager.SelectedTheme.Should().Be(AppTheme.Dark, "テーマ選択は即時プレビューされる仕様（9.3）");

        var shouldClose = await vm.RequestCloseAsync();

        shouldClose.Should().BeTrue();
        dialogs.ConfirmThreeWayCallCount.Should().Be(1, "未保存の変更があるので確認を1回だけ出す必要がある");
        ThemeManager.SelectedTheme.Should().Be(
            AppTheme.System, "破棄した場合、保存されていた設定（system）へプレビューを戻す必要がある");

        // 保存はされていないこと（ディスク上は既定のまま）。
        var reloaded = await new SettingsStore(appPaths).LoadAsync();
        reloaded.Value.Theme.Should().Be("system");
    }

    [AvaloniaFact(DisplayName = "テーマを変更して「保存する」を選ぶと、変更が永続化されテーマも維持される")]
    public async Task 保存すると変更が永続化されテーマも維持される()
    {
        var (vm, dialogs, appPaths) = await BuildViewModelAsync();
        dialogs.ConfirmThreeWayResponse = true; // 「保存する」

        vm.SelectedTheme = "dark";

        var shouldClose = await vm.RequestCloseAsync();

        shouldClose.Should().BeTrue();
        ThemeManager.SelectedTheme.Should().Be(AppTheme.Dark, "保存した場合はプレビューをそのまま維持する");

        var reloaded = await new SettingsStore(appPaths).LoadAsync();
        reloaded.Value.Theme.Should().Be("dark", "保存を選んだ以上、ディスク上のsettings.jsonへ反映されている必要がある");
    }

    [AvaloniaFact(DisplayName = "「キャンセル」を選ぶと閉じずに、変更内容もプレビューもそのまま残る")]
    public async Task キャンセルすると閉じずに変更が残る()
    {
        var (vm, dialogs, appPaths) = await BuildViewModelAsync();
        dialogs.ConfirmThreeWayResponse = null; // 「キャンセル」

        vm.SelectedTheme = "light";

        var shouldClose = await vm.RequestCloseAsync();

        shouldClose.Should().BeFalse("キャンセルした場合はウィンドウを閉じてはならない");
        vm.SelectedTheme.Should().Be("light", "キャンセルなら入力中の値をそのまま残す必要がある");
        ThemeManager.SelectedTheme.Should().Be(AppTheme.Light, "キャンセルならプレビューもそのまま残す必要がある");

        var reloaded = await new SettingsStore(appPaths).LoadAsync();
        reloaded.Value.Theme.Should().Be("system", "キャンセルなので保存はされていない");
    }

    [AvaloniaFact(DisplayName = "Escapeキーでも同じ確認フローを通り、破棄すればプレビューが戻ってウィンドウが閉じる")]
    public async Task Escapeキーでも確認フローを通る()
    {
        var (vm, dialogs, _) = await BuildViewModelAsync();
        dialogs.ConfirmThreeWayResponse = false; // 「破棄して閉じる」
        var window = new SettingsWindow(vm);
        window.Show();
        // SettingsWindowはLoadedで自前にInitializeAsyncをもう一度呼ぶ（本番の初回表示と同じ経路）。
        // それが完了する前にSelectedThemeを書き換えると、後から終わるPopulateEditableFieldsに
        // 上書きされて競合するため、ここで一旦落ち着くのを待つ。
        await SettleAsync();

        vm.SelectedTheme = "dark";

        var closed = false;
        window.Closed += (_, _) => closed = true;
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);

        await WaitUntilAsync(() => closed);

        closed.Should().BeTrue("未保存の変更があっても、破棄を選べばEscapeで閉じられる必要がある");
        dialogs.ConfirmThreeWayCallCount.Should().Be(1);
        ThemeManager.SelectedTheme.Should().Be(AppTheme.System, "Escapeで破棄した場合もテーマのプレビューを戻す必要がある");
    }

    [AvaloniaFact(DisplayName = "ウィンドウの×（Closing）でも同じ確認フローを通り、キャンセルなら開いたままになる")]
    public async Task ウィンドウのクローズでも確認フローを通る()
    {
        var (vm, dialogs, _) = await BuildViewModelAsync();
        dialogs.ConfirmThreeWayResponse = null; // 「キャンセル」
        var window = new SettingsWindow(vm);
        window.Show();
        // SettingsWindowはLoadedで自前にInitializeAsyncをもう一度呼ぶ（本番の初回表示と同じ経路）。
        // それが完了する前にSelectedThemeを書き換えると、後から終わるPopulateEditableFieldsに
        // 上書きされて競合するため、ここで一旦落ち着くのを待つ。
        await SettleAsync();

        vm.SelectedTheme = "dark";

        var closed = false;
        window.Closed += (_, _) => closed = true;

        // タイトルバー×相当。Window.Close()は同期的にClosingイベントを発火する
        // （SettingsWindow.axaml.csのOnClosingが一旦e.Cancel=trueで止め、非同期確認を挟む）。
        window.Close();
        await WaitUntilAsync(() => dialogs.ConfirmThreeWayCallCount > 0);

        closed.Should().BeFalse("キャンセルを選んだのにウィンドウが閉じてはならない");
        window.IsVisible.Should().BeTrue("キャンセル後は設定画面を開いたままにする必要がある");

        // 同じウィンドウで、今度は破棄を選べばちゃんと閉じられることも確認する
        // （キャンセル後に再度×を押した場合の挙動）。
        dialogs.ConfirmThreeWayResponse = false;
        window.Close();
        await WaitUntilAsync(() => closed);

        closed.Should().BeTrue("破棄を選べば×からもウィンドウが閉じる必要がある");
    }

    [AvaloniaFact(DisplayName = "「閉じる」ボタンでも同じ確認フローを通る")]
    public async Task 閉じるボタンでも確認フローを通る()
    {
        var (vm, dialogs, _) = await BuildViewModelAsync();
        dialogs.ConfirmThreeWayResponse = false; // 「破棄して閉じる」
        var window = new SettingsWindow(vm);
        window.Show();
        // SettingsWindowはLoadedで自前にInitializeAsyncをもう一度呼ぶ（本番の初回表示と同じ経路）。
        // それが完了する前にSelectedThemeを書き換えると、後から終わるPopulateEditableFieldsに
        // 上書きされて競合するため、ここで一旦落ち着くのを待つ。
        await SettleAsync();

        vm.SelectedTheme = "dark";

        var closed = false;
        window.Closed += (_, _) => closed = true;

        var closeButton = window.GetVisualDescendants().OfType<Button>()
            .Single(b => Equals(b.Content, "閉じる"));
        closeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        await WaitUntilAsync(() => closed);

        closed.Should().BeTrue();
        dialogs.ConfirmThreeWayCallCount.Should().Be(1);
        ThemeManager.SelectedTheme.Should().Be(AppTheme.System);
    }

    /// <summary>
    /// AppPaths・SettingsViewModelを本番と同じ依存で組み立てる（settings.jsonは未作成＝既定値）。
    /// テーマの既定は"system"であり、本テストの多くがそこからの変化を検証する。
    /// </summary>
    private async Task<(SettingsViewModel ViewModel, RecordingDialogService Dialogs, AppPaths AppPaths)> BuildViewModelAsync()
    {
        var appPaths = new AppPaths(Path.Combine(_root, Guid.NewGuid().ToString("N")));
        appPaths.EnsureCoreDirectoriesExist();
        var dialogs = new RecordingDialogService();
        var vm = new SettingsViewModel(appPaths, dialogs, new Graft.Platform.AvaloniaUiServices());
        await vm.InitializeAsync();
        return (vm, dialogs, appPaths);
    }

    /// <summary>Escape/Closingの非同期確認フロー（fire-and-forgetなTask）が終わるまで待つ。</summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// ディスパッチャに積まれた非同期継続（Window.LoadedのInitializeAsync等）を
    /// 一通り消化させるための待ち。Task.Delayをはさむことでheadlessのディスパッチャが
    /// ポンプされる（他のUiTestsのポーリングと同じ手法）。
    /// </summary>
    private static async Task SettleAsync()
    {
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// ConfirmThreeWayAsyncの応答をテストごとに差し替えられ、呼び出し回数を記録するダイアログ。
    /// 「変更が無いのに確認が出ていないか」（0回であるべき）の検証に呼び出し回数を使う。
    /// </summary>
    private sealed class RecordingDialogService : IDialogService
    {
        public bool? ConfirmThreeWayResponse { get; set; }

        public int ConfirmThreeWayCallCount { get; private set; }

        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);

        public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel)
        {
            ConfirmThreeWayCallCount++;
            return Task.FromResult(ConfirmThreeWayResponse);
        }

        public Task<string?> PromptAsync(string title, string message, string? initial = null)
            => Task.FromResult<string?>(initial);

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync(string title, IReadOnlyList<string>? extensions = null) => Task.FromResult<string?>(null);

        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
    }
}
