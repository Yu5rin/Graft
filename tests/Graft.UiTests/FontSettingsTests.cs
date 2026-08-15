using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using FluentAssertions;
using Graft.Editor;
using Graft.Infra;
using Graft.Platform;
using Graft.Themes;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;
using Graft.Views.SettingsPanels;
using Xunit;

namespace Graft.UiTests;

/// <summary>
/// 検討書「フォント設定」の回帰テスト。<see cref="AppFontManager"/>による即時反映と、
/// <see cref="SettingsViewModel"/>のフォント選択欄（列挙成功／失敗どちらの経路も）を検証する。
/// フォント列挙・等幅判定そのものの単体テストはFontCatalogTests.cs参照。
/// </summary>
public class FontSettingsTests : IDisposable
{
    private readonly ShownWindowTracker _windows = new();

    public void Dispose()
    {
        // AppFontManagerが書き換えるApplication.Resourcesの直接キーは、テーマ辞書と違い
        // ThemeManagerのような「差し替え」ではなく「上書き→削除」の形を取るため、次のテストへ
        // 影響しないよう必ず既定へ戻す（テスト間で状態を持ち越さないため。ThemeTests/
        // HelpTipTestsと同じ考え方）。
        AppFontManager.SetBodyFontFamily(null);
        AppFontManager.SetCodeFontFamily(null);
        _windows.Dispose();
        GC.SuppressFinalize(this);
    }

    [AvaloniaFact(DisplayName = "AppFontManager.SetBodyFontFamilyでUiFontFamilyの解決値が即座に切り替わる")]
    public void 本文フォントを設定すると即座に切り替わる()
    {
        Application.Current!.TryFindResource("UiFontFamily", null, out var before);

        AppFontManager.SetBodyFontFamily("Comic Sans MS");
        Application.Current!.TryFindResource("UiFontFamily", null, out var after);

        after.Should().BeOfType<FontFamily>().Which.Name.Should().Be("Comic Sans MS");
        after.Should().NotBe(before);
    }

    [AvaloniaFact(DisplayName = "AppFontManager.SetCodeFontFamilyでCodeFontFamilyの解決値が即座に切り替わる")]
    public void 等幅フォントを設定すると即座に切り替わる()
    {
        AppFontManager.SetCodeFontFamily("Fira Code");
        Application.Current!.TryFindResource("CodeFontFamily", null, out var value);

        value.Should().BeOfType<FontFamily>().Which.Name.Should().Be("Fira Code");
    }

    [AvaloniaFact(DisplayName = "null・空文字・空白を渡すとTokens.axaml側の既定フォントへ戻る")]
    public void 未指定にすると既定へ戻る()
    {
        Application.Current!.TryFindResource("UiFontFamily", null, out var defaultValue);

        AppFontManager.SetBodyFontFamily("Comic Sans MS");
        Application.Current!.TryFindResource("UiFontFamily", null, out var overridden);
        overridden.Should().NotBe(defaultValue);

        AppFontManager.SetBodyFontFamily(null);
        Application.Current!.TryFindResource("UiFontFamily", null, out var resetToNull);
        resetToNull.Should().Be(defaultValue, "nullは既定（Tokens.axamlの値）へ戻す扱い");

        AppFontManager.SetBodyFontFamily("  ");
        Application.Current!.TryFindResource("UiFontFamily", null, out var resetToWhitespace);
        resetToWhitespace.Should().Be(defaultValue, "空白のみも既定へ戻す扱い");
    }

    [AvaloniaFact(DisplayName = "フォント名に'や\\を含んでいてもAppFontManagerは例外を投げない")]
    public void フォント名に引用符やバックスラッシュを含んでいても壊れない()
    {
        var act = () => AppFontManager.SetBodyFontFamily(@"O'Reilly\Mono""Font""");
        act.Should().NotThrow();
    }

    [AvaloniaFact(DisplayName = "フォント列挙に成功する環境では、設定画面のフォント欄がComboBoxで表示される")]
    public async Task フォント列挙に成功するとComboBoxが表示される()
    {
        var vm = await CreateViewModelAsync(fontCatalog: new FakeFontCatalog(
            all: new[] { "Alpha", "Beta" }, mono: new[] { "Beta" }));

        var view = new GeneralSettingsView { DataContext = vm };
        var window = _windows.Track(new Window { Content = view });
        window.Show();

        var bodyCombo = view.GetVisualDescendants().OfType<ComboBox>()
            .SingleOrDefault(c => Equals(AutomationProperties.GetName(c), "本文フォント"));
        var bodyFallback = view.GetVisualDescendants().OfType<TextBox>()
            .SingleOrDefault(c => Equals(AutomationProperties.GetName(c), "本文フォント（手入力）"));

        bodyCombo.Should().NotBeNull();
        bodyCombo!.IsVisible.Should().BeTrue();
        bodyFallback.Should().NotBeNull("列挙成功時もフォールバック用TextBox自体はツリーに存在する（HelpTipカバレッジのため）");
        bodyFallback!.IsVisible.Should().BeFalse();

        // 「(既定)」＋列挙された2件で計3項目。
        vm.FontFamilyOptions.Select(o => o.Value).Should().Equal("", "Alpha", "Beta");
        vm.MonospaceFontFamilyOptions.Select(o => o.Value).Should().Equal("", "Beta");
    }

    [AvaloniaFact(DisplayName = "検討書「失敗時は…設定欄はテキスト入力へフォールバックする」: 列挙結果が空ならTextBoxへ切り替わる")]
    public async Task フォント列挙に失敗するとテキスト入力へフォールバックする()
    {
        var vm = await CreateViewModelAsync(fontCatalog: new EmptyFontCatalog());

        var view = new GeneralSettingsView { DataContext = vm };
        var window = _windows.Track(new Window { Content = view });
        window.Show();

        var bodyCombo = view.GetVisualDescendants().OfType<ComboBox>()
            .Single(c => Equals(AutomationProperties.GetName(c), "本文フォント"));
        var bodyFallback = view.GetVisualDescendants().OfType<TextBox>()
            .Single(c => Equals(AutomationProperties.GetName(c), "本文フォント（手入力）"));
        var monoCombo = view.GetVisualDescendants().OfType<ComboBox>()
            .Single(c => Equals(AutomationProperties.GetName(c), "等幅フォント"));
        var monoFallback = view.GetVisualDescendants().OfType<TextBox>()
            .Single(c => Equals(AutomationProperties.GetName(c), "等幅フォント（手入力）"));

        bodyCombo.IsVisible.Should().BeFalse();
        bodyFallback.IsVisible.Should().BeTrue();
        monoCombo.IsVisible.Should().BeFalse();
        monoFallback.IsVisible.Should().BeTrue();

        // フォールバック中でも入力・保存経路自体は使える（検討書: 「失敗時は既定フォントの
        // ままにして、設定欄はテキスト入力へフォールバックする」）。
        vm.SelectedFontFamily = "手入力フォント";
        vm.SelectedFontFamily.Should().Be("手入力フォント");
    }

    [AvaloniaFact(DisplayName = "本文フォントを選ぶと即座にAppFontManager経由で画面へ反映される（保存を待たない）")]
    public async Task 本文フォントの選択が即座に反映される()
    {
        var vm = await CreateViewModelAsync(fontCatalog: new FakeFontCatalog(all: new[] { "PreviewFont" }, mono: Array.Empty<string>()));

        vm.SelectedFontFamily = "PreviewFont";

        Application.Current!.TryFindResource("UiFontFamily", null, out var value);
        value.Should().BeOfType<FontFamily>().Which.Name.Should().Be("PreviewFont");
    }

    [AvaloniaFact(DisplayName = "等幅フォントを選ぶと即座にAppFontManager経由で画面へ反映される（保存を待たない）")]
    public async Task 等幅フォントの選択が即座に反映される()
    {
        var vm = await CreateViewModelAsync(fontCatalog: new FakeFontCatalog(all: Array.Empty<string>(), mono: new[] { "MonoPreview" }));

        vm.SelectedMonospaceFontFamily = "MonoPreview";

        Application.Current!.TryFindResource("CodeFontFamily", null, out var value);
        value.Should().BeOfType<FontFamily>().Which.Name.Should().Be("MonoPreview");
    }

    private async Task<SettingsViewModel> CreateViewModelAsync(IFontCatalog fontCatalog)
    {
        var root = Path.Combine(Path.GetTempPath(), "graft-font-settings", Guid.NewGuid().ToString("N"));
        var appPaths = new AppPaths(root);
        appPaths.EnsureCoreDirectoriesExist();
        var vm = new SettingsViewModel(
            appPaths, new NullDialogService(), new AvaloniaUiServices(), fontCatalog: fontCatalog);
        await vm.InitializeAsync();
        return vm;
    }

    /// <summary>テスト用の固定リストを返す<see cref="IFontCatalog"/>。実際の列挙成功を模擬する。</summary>
    private sealed class FakeFontCatalog : IFontCatalog
    {
        public FakeFontCatalog(IReadOnlyList<string> all, IReadOnlyList<string> mono)
        {
            AllFamilyNames = all;
            MonospaceFamilyNames = mono;
        }

        public IReadOnlyList<string> AllFamilyNames { get; }

        public IReadOnlyList<string> MonospaceFamilyNames { get; }
    }

    /// <summary>何もしない最小のダイアログ実装（HelpTipTests.NullDialogServiceと同じ形）。</summary>
    private sealed class NullDialogService : IDialogService
    {
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);

        public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel)
            => Task.FromResult<bool?>(true);

        public Task<string?> PromptAsync(string title, string message, string? initial = null)
            => Task.FromResult(initial);

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync(string title, IReadOnlyList<string>? extensions = null)
            => Task.FromResult<string?>(null);

        public Task<string?> SaveFileAsync(string title, string suggestedFileName, IReadOnlyList<string>? extensions = null)
            => Task.FromResult<string?>(null);

        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
    }
}
