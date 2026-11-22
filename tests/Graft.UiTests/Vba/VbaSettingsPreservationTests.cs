using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Infra;
using Graft.Platform;
using Graft.ViewModels;

namespace Graft.UiTests.Vba;

/// <summary>
/// 設定画面で別の項目を保存しても、<c>Settings.Vba</c> が既定値へ戻らないことの固定
/// （docs/仕様書_VBA連携.md 8章・10.2）。
/// <para>
/// 【なぜ必要か】 <see cref="SettingsViewModel"/> の保存は <c>new Settings { … }</c> で全項目を組み立て直す
/// （<c>BuildSettingsFromFields</c>）。そこへ <c>Vba</c> を引き継ぐ処理を足さないと、設定画面で何か 1 つ
/// 保存するたびに、<c>settings.json</c> に書かれている VBA 連携の値が黙って既定値へ戻る。VBA 連携の設定パネルは
/// 後の段階で作るので、今は画面に編集欄が無く、値が消えても利用者には見えない。それだけに、保存で消えないことを
/// 先にテストで固定しておく。
/// </para>
/// </summary>
public class VbaSettingsPreservationTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-vba-settings", Guid.NewGuid().ToString("N"));

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

    private AppPaths NewAppPaths()
    {
        var appPaths = new AppPaths(Path.Combine(_root, "app"));
        appPaths.EnsureCoreDirectoriesExist();
        return appPaths;
    }

    private static readonly VbaSettings Customized = new()
    {
        Enabled = false,
        PullOnContextOpen = false,
        PullBeforeDryRun = false,
        ComTimeoutMs = 8000,
        CallTimeoutMs = 45000,
        CollectWorkbookInfo = false,
        WorkbookInfoSampleRows = 12,
        WorkbookInfoMaxColumns = 50,
        WorkbookInfoMaxCellChars = 120,
        WorkbookInfoMaxChars = 40000,
    };

    [AvaloniaFact(DisplayName = "設定画面で別の項目を保存しても、Vba の値は保たれる（BuildSettingsFromFields の往復）")]
    public async Task 別の項目の保存でVbaが消えない()
    {
        var appPaths = NewAppPaths();
        await new SettingsStore(appPaths).SaveAsync(new Settings { Vba = Customized });

        var vm = new SettingsViewModel(appPaths, new FakeDialogService(), new AvaloniaUiServices());
        await vm.InitializeAsync();
        vm.ShowPreview = !vm.ShowPreview; // 無関係な項目を変えて保存を起こす。
        await vm.FlushPendingSaveAsync();

        var saved = (await new SettingsStore(appPaths).LoadAsync()).Value;
        saved.ShowPreview.Should().BeFalse("保存が実際に行われたことの確認（既定は true）");
        saved.Vba.Should().Be(Customized, "保存のたびに Vba が既定へ戻ってはならない");
    }

    [AvaloniaFact(DisplayName = "vba キーが無い既存の settings.json は、保存後も既定値の Vba で読める")]
    public async Task 旧形式の設定でも保存できる()
    {
        var appPaths = NewAppPaths();
        await File.WriteAllTextAsync(appPaths.SettingsFilePath, "{ \"theme\": \"dark\" }");

        var vm = new SettingsViewModel(appPaths, new FakeDialogService(), new AvaloniaUiServices());
        await vm.InitializeAsync();
        vm.ShowPreview = !vm.ShowPreview;
        await vm.FlushPendingSaveAsync();

        var saved = (await new SettingsStore(appPaths).LoadAsync()).Value;
        saved.Theme.Should().Be("dark");
        saved.Vba.Should().Be(new VbaSettings());
    }

    [AvaloniaFact(DisplayName = "保存を重ねても Vba の値は変わらず、読み直した画面からの保存でも保たれる")]
    public async Task 読み直した画面からの保存でも保たれる()
    {
        var appPaths = NewAppPaths();
        await new SettingsStore(appPaths).SaveAsync(new Settings { Vba = Customized });

        var first = new SettingsViewModel(appPaths, new FakeDialogService(), new AvaloniaUiServices());
        await first.InitializeAsync();
        first.ShowPreview = !first.ShowPreview;
        await first.FlushPendingSaveAsync();

        var second = new SettingsViewModel(appPaths, new FakeDialogService(), new AvaloniaUiServices());
        await second.InitializeAsync();
        second.RequireSummary = !second.RequireSummary;
        await second.FlushPendingSaveAsync();

        (await new SettingsStore(appPaths).LoadAsync()).Value.Vba.Should().Be(Customized);
    }

    /// <summary>ダイアログを出さない最小のフェイク（このテストでは確認を求められない）。</summary>
    private sealed class FakeDialogService : IDialogService
    {
        public Task<bool> ShowActionMessageAsync(string title, string message, string actionLabel) => Task.FromResult(false);

        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;

        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(false);

        public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel)
            => Task.FromResult<bool?>(false);

        public Task<string?> PromptAsync(string title, string message, string? initial = null)
            => Task.FromResult<string?>(initial);

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync(string title, IReadOnlyList<string>? extensions = null)
            => Task.FromResult<string?>(null);

        public Task<string?> SaveFileAsync(string title, string suggestedFileName, IReadOnlyList<string>? extensions = null)
            => Task.FromResult<string?>(null);
    }
}
