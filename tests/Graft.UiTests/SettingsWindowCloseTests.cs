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
/// 即時反映方式（14章）における設定画面のクローズ動作の回帰テスト。
///
/// かつては「保存」ボタンを押すまでどの設定も反映されず、テーマだけが例外的に
/// setter経由で<see cref="ThemeManager.SetTheme"/>を呼んで即時プレビューされていた。
/// この不統一が「保存せずに閉じるとテーマだけプレビューのまま残り、次に開くと巻き戻る」
/// 不具合の真因であり、直前のラウンドでは「未保存の変更があれば確認する」という
/// 対症療法で塞いでいた（このファイルの旧版）。利用者から「そもそも全項目が即時反映される
/// べきではないか」という指摘を受け、今回は全項目を即時反映方式へ移行したため、
/// 「未保存の変更」という状態自体が存在しなくなり、確認ダイアログそのものが不要になった。
///
/// このテストクラスの関心は次の2点に絞られる。
///   1. 変更した状態でも、どの経路でも確認ダイアログなしに閉じられること。
///   2. 閉じた後も変更がsettings.jsonへ反映されたまま残っていること。特にTextBox以外の
///      即時反映項目（ここではテーマ）は変更直後にディスクへの保存が
///      <see cref="SettingsViewModel"/>内部の短いデバウンス待ちになるため、その保存予約が
///      待たずに確定してから閉じること（<see cref="SettingsViewModel.FlushPendingSaveAsync"/>）
///      を確認する。
/// 「閉じる」ボタン・Escapeキー・ウィンドウの×（Closing）の3経路が同じ挙動であることの
/// 検証には引き続き価値があるため、3経路それぞれをテストする。
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

    [AvaloniaFact(DisplayName = "変更が無くてもEscapeキーで確認ダイアログを出さずに閉じられる")]
    public async Task 変更が無くても確認なしで閉じられる()
    {
        var (vm, dialogs, _) = await BuildViewModelAsync();
        var window = new SettingsWindow(vm);
        window.Show();
        await SettleAsync();

        var closed = false;
        window.Closed += (_, _) => closed = true;
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);

        await WaitUntilAsync(() => closed);

        closed.Should().BeTrue();
        dialogs.ConfirmCallCount.Should().Be(
            0, "即時反映方式では未保存の変更という状態自体が無く、確認ダイアログを出す理由が無い");
    }

    [AvaloniaFact(DisplayName = "テーマを変更してEscapeキーで閉じても確認なしに閉じ、変更がsettings.jsonへ保持される")]
    public async Task Escapeキーで確認なしに閉じられ変更が保持される()
    {
        var (vm, dialogs, appPaths) = await BuildViewModelAsync();
        var window = new SettingsWindow(vm);
        window.Show();
        await SettleAsync();

        // ComboBoxの選択が変わった瞬間の即時反映を模す。ここでは保存のデバウンス
        // （300ms）が経過する前にすぐ閉じ、FlushPendingSaveAsyncが待たずに確定させる
        // ことを検証する。
        vm.SelectedTheme = "dark";

        var closed = false;
        window.Closed += (_, _) => closed = true;
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);

        await WaitUntilAsync(() => closed);

        closed.Should().BeTrue("即時反映方式では確認なしにEscapeで閉じられる必要がある");
        dialogs.ConfirmCallCount.Should().Be(0);
        ThemeManager.SelectedTheme.Should().Be(AppTheme.Dark, "テーマのプレビューは閉じても取り消されない");

        var reloaded = await new SettingsStore(appPaths).LoadAsync();
        reloaded.Value.Theme.Should().Be(
            "dark", "デバウンス待ちだった保存もFlushPendingSaveAsyncで確定してから閉じる必要がある");
    }

    [AvaloniaFact(DisplayName = "テーマを変更してウィンドウの×で閉じても確認なしに閉じ、変更がsettings.jsonへ保持される")]
    public async Task ウィンドウのクローズで確認なしに閉じられ変更が保持される()
    {
        var (vm, dialogs, appPaths) = await BuildViewModelAsync();
        var window = new SettingsWindow(vm);
        window.Show();
        await SettleAsync();

        vm.SelectedTheme = "light";

        var closed = false;
        window.Closed += (_, _) => closed = true;
        window.Close(); // タイトルバー×相当。同期的にClosingを発火する。

        await WaitUntilAsync(() => closed);

        closed.Should().BeTrue("即時反映方式では確認なしに×で閉じられる必要がある");
        dialogs.ConfirmCallCount.Should().Be(0);

        var reloaded = await new SettingsStore(appPaths).LoadAsync();
        reloaded.Value.Theme.Should().Be("light");
    }

    [AvaloniaFact(DisplayName = "テーマを変更して「閉じる」ボタンで閉じても確認なしに閉じ、変更がsettings.jsonへ保持される")]
    public async Task 閉じるボタンで確認なしに閉じられ変更が保持される()
    {
        var (vm, dialogs, appPaths) = await BuildViewModelAsync();
        var window = new SettingsWindow(vm);
        window.Show();
        await SettleAsync();

        vm.SelectedTheme = "dark";

        var closed = false;
        window.Closed += (_, _) => closed = true;

        var closeButton = window.GetVisualDescendants().OfType<Button>()
            .Single(b => Equals(b.Content, "閉じる"));
        closeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        await WaitUntilAsync(() => closed);

        closed.Should().BeTrue("即時反映方式では確認なしに「閉じる」ボタンで閉じられる必要がある");
        dialogs.ConfirmCallCount.Should().Be(0);

        var reloaded = await new SettingsStore(appPaths).LoadAsync();
        reloaded.Value.Theme.Should().Be("dark");
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

    /// <summary>Escape/Closingの非同期な閉じる処理（fire-and-forgetなTask）が終わるまで待つ。</summary>
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
    /// ダイアログ呼び出し回数を記録するダイアログ。「閉じる操作で確認ダイアログが
    /// 出ていないか」（0回であるべき）の検証に呼び出し回数を使う。
    /// </summary>
    private sealed class RecordingDialogService : IDialogService
    {
        public int ConfirmCallCount { get; private set; }

        public Task<bool> ConfirmAsync(string title, string message)
        {
            ConfirmCallCount++;
            return Task.FromResult(true);
        }

        public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel)
        {
            ConfirmCallCount++;
            return Task.FromResult<bool?>(true);
        }

        public Task<string?> PromptAsync(string title, string message, string? initial = null)
            => Task.FromResult(initial);

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync(string title, IReadOnlyList<string>? extensions = null) => Task.FromResult<string?>(null);
        public Task<string?> SaveFileAsync(string title, string suggestedFileName, IReadOnlyList<string>? extensions = null) => Task.FromResult<string?>(null);

        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
    }
}
