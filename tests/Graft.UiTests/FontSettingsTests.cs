using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
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
using Graft.Views;
using Graft.Views.SettingsPanels;
using Xunit;

namespace Graft.UiTests;

/// <summary>
/// 検討書「フォント設定」の回帰テスト。<see cref="AppFontManager"/>による即時反映と、
/// <see cref="SettingsViewModel"/>のフォント選択欄（列挙成功／失敗どちらの経路も）を検証する。
/// フォント列挙・等幅判定そのものの単体テストはFontCatalogTests.cs参照。
///
/// 【v1.0.21での分割対応】
/// 利用者からの指摘（本文用に選んだフォントがメニュー・ボタン・一覧まで巻き込んでしまい、
/// 字の縦位置がずれて見える）を受けて、従来の「本文フォント」（実態はUI全体に効いていた）を
/// 「UIフォント」（<see cref="AppFontManager.SetUiFontFamily"/>・UiFontFamilyリソース）と
/// 新設の「本文フォント」（<see cref="AppFontManager.SetBodyTextFontFamily"/>・
/// BodyTextFontFamilyリソース。取扱説明書・Markdownプレビューの地の文専用）に分割した。
/// 以下を固定する:
/// 1. 既定（どちらも未設定）では両者が同じ既定値に解決されること（見た目が変わらないことの固定）。
/// 2. UIフォントだけを設定してもBodyTextFontFamilyは既定のままであること。
/// 3. 本文フォントだけを設定してもUiFontFamilyは既定のままであること。
/// 4. 空文字・nullに戻すとそれぞれ既定へ戻ること。
/// 5. CodeFontFamilyはどちらの変更の影響も受けないこと。
/// 6. 取扱説明書のManualContentPanelが本文用リソースへ束ねられ、コードブロックは
///    CodeFontFamilyのままであること（継承の上書き構造が壊れていないことの固定）。
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
        AppFontManager.SetUiFontFamily(null);
        AppFontManager.SetBodyTextFontFamily(null);
        AppFontManager.SetCodeFontFamily(null);
        _windows.Dispose();
        GC.SuppressFinalize(this);
    }

    [AvaloniaFact(DisplayName = "AppFontManager.SetUiFontFamilyでUiFontFamilyの解決値が即座に切り替わる")]
    public void UIフォントを設定すると即座に切り替わる()
    {
        Application.Current!.TryFindResource("UiFontFamily", null, out var before);

        AppFontManager.SetUiFontFamily("Comic Sans MS");
        Application.Current!.TryFindResource("UiFontFamily", null, out var after);

        after.Should().BeOfType<FontFamily>().Which.Name.Should().Be("Comic Sans MS");
        after.Should().NotBe(before);
    }

    [AvaloniaFact(DisplayName = "AppFontManager.SetBodyTextFontFamilyでBodyTextFontFamilyの解決値が即座に切り替わる")]
    public void 本文フォントを設定すると即座に切り替わる()
    {
        Application.Current!.TryFindResource("BodyTextFontFamily", null, out var before);

        AppFontManager.SetBodyTextFontFamily("Comic Sans MS");
        Application.Current!.TryFindResource("BodyTextFontFamily", null, out var after);

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

    [AvaloniaFact(DisplayName = "検証1: 既定（どちらも未設定）では、UIフォントと本文フォントが同じ既定値に解決される")]
    public void 既定ではUIフォントと本文フォントが同じ値に解決される()
    {
        Application.Current!.TryFindResource("UiFontFamily", null, out var uiDefault);
        Application.Current!.TryFindResource("BodyTextFontFamily", null, out var bodyDefault);

        uiDefault.Should().BeOfType<FontFamily>();
        bodyDefault.Should().BeOfType<FontFamily>();
        ((FontFamily)bodyDefault!).Name.Should().Be(
            ((FontFamily)uiDefault!).Name,
            "本文フォントの既定値はUIフォントと同じフォールバック列のため、未設定の利用者の見た目は今回の変更で変わらない");
    }

    [AvaloniaFact(DisplayName = "検証2: UIフォントだけを設定すると、UiFontFamilyだけが変わり本文フォントは既定のまま")]
    public void UIフォントだけ設定すると本文フォントは既定のまま()
    {
        Application.Current!.TryFindResource("BodyTextFontFamily", null, out var bodyDefault);

        AppFontManager.SetUiFontFamily("UiOnlyFont");

        Application.Current!.TryFindResource("UiFontFamily", null, out var ui);
        Application.Current!.TryFindResource("BodyTextFontFamily", null, out var body);

        ui.Should().BeOfType<FontFamily>().Which.Name.Should().Be("UiOnlyFont");
        body.Should().Be(bodyDefault, "UIフォントの変更が本文フォント側のリソースへ漏れてはならない");
    }

    [AvaloniaFact(DisplayName = "検証3: 本文フォントだけを設定すると、本文用リソースだけが変わりUiFontFamilyは既定のまま")]
    public void 本文フォントだけ設定するとUIフォントは既定のまま()
    {
        Application.Current!.TryFindResource("UiFontFamily", null, out var uiDefault);

        AppFontManager.SetBodyTextFontFamily("BodyOnlyFont");

        Application.Current!.TryFindResource("UiFontFamily", null, out var ui);
        Application.Current!.TryFindResource("BodyTextFontFamily", null, out var body);

        body.Should().BeOfType<FontFamily>().Which.Name.Should().Be("BodyOnlyFont");
        ui.Should().Be(uiDefault, "本文フォントの変更がUIフォント側のリソースへ漏れてはならない");
    }

    [AvaloniaFact(DisplayName = "検証4: null・空文字を渡すとUIフォント・本文フォントはそれぞれ既定へ戻る")]
    public void 未指定にするとそれぞれ既定へ戻る()
    {
        Application.Current!.TryFindResource("UiFontFamily", null, out var uiDefault);
        Application.Current!.TryFindResource("BodyTextFontFamily", null, out var bodyDefault);

        AppFontManager.SetUiFontFamily("Comic Sans MS");
        AppFontManager.SetBodyTextFontFamily("Comic Sans MS");
        Application.Current!.TryFindResource("UiFontFamily", null, out var uiOverridden);
        Application.Current!.TryFindResource("BodyTextFontFamily", null, out var bodyOverridden);
        uiOverridden.Should().NotBe(uiDefault);
        bodyOverridden.Should().NotBe(bodyDefault);

        AppFontManager.SetUiFontFamily(null);
        AppFontManager.SetBodyTextFontFamily("");
        Application.Current!.TryFindResource("UiFontFamily", null, out var uiReset);
        Application.Current!.TryFindResource("BodyTextFontFamily", null, out var bodyReset);
        uiReset.Should().Be(uiDefault, "nullは既定（Tokens.axamlの値）へ戻す扱い");
        bodyReset.Should().Be(bodyDefault, "空文字も既定へ戻す扱い");
    }

    [AvaloniaFact(DisplayName = "検証5: UIフォント・本文フォントのどちらを変えてもCodeFontFamilyは影響を受けない")]
    public void UIフォントも本文フォントもCodeFontFamilyへ影響しない()
    {
        AppFontManager.SetCodeFontFamily("FixedCodeFont");
        Application.Current!.TryFindResource("CodeFontFamily", null, out var codeBefore);

        AppFontManager.SetUiFontFamily("SomeUiFont");
        AppFontManager.SetBodyTextFontFamily("SomeBodyFont");
        Application.Current!.TryFindResource("CodeFontFamily", null, out var codeAfter);

        codeAfter.Should().Be(codeBefore);
        codeAfter.Should().BeOfType<FontFamily>().Which.Name.Should().Be("FixedCodeFont");
    }

    [AvaloniaFact(DisplayName = "検証6: 取扱説明書のManualContentPanelは本文フォントへ束ねられ、コードブロックはCodeFontFamilyのまま")]
    public void 取扱説明書のコンテナは本文フォントへコードブロックは等幅フォントへ束ねられる()
    {
        AppFontManager.SetBodyTextFontFamily("ManualBodyFont");
        AppFontManager.SetCodeFontFamily("ManualCodeFont");

        var window = _windows.Track(new ManualWindow());
        window.Show();

        var contentPanel = window.FindControl<StackPanel>("ManualContentPanel");
        contentPanel.Should().NotBeNull();
        // StackPanelはFontFamilyを自身のプロパティとして持たないため（GeneralSettingsView等の
        // TextBlock/ComboBoxと違いTemplatedControlではない）、ManualWindow.axaml側では
        // TextElement.FontFamily添付プロパティで束ねてある。TextElement.GetFontFamilyで
        // 実際に解決された値を読む。
        var contentPanelFontFamily = TextElement.GetFontFamily(contentPanel!);
        contentPanelFontFamily.Name.Should().Be(
            "ManualBodyFont", "ManualContentPanelはTextElement.FontFamily=\"{DynamicResource BodyTextFontFamily}\"で本文フォントへ束ねてある");

        // 3.1節のGraft形式コードブロックの中身（"type: fix"）で、実際のコードブロックの
        // SelectableTextBlockを特定する（ManualWindowTests.コードブロックは等幅フォントかつ
        // 背景色付きで表示されると同じ狙い方）。
        var codeBlock = window.GetVisualDescendants().OfType<SelectableTextBlock>()
            .Single(t => (t.Inlines?.Text ?? t.Text ?? string.Empty).Contains("type: fix"));

        codeBlock.FontFamily.Name.Should().Be(
            "ManualCodeFont", "コードブロックは自身にCodeFontFamilyを明示指定しており、本文フォントの継承を上書きするはずである");
        codeBlock.FontFamily.Name.Should().NotBe(contentPanelFontFamily.Name);
    }

    [AvaloniaFact(DisplayName = "フォント名に'や\\を含んでいてもAppFontManagerは例外を投げない")]
    public void フォント名に引用符やバックスラッシュを含んでいても壊れない()
    {
        var act = () => AppFontManager.SetUiFontFamily(@"O'Reilly\Mono""Font""");
        act.Should().NotThrow();
    }

    [AvaloniaFact(DisplayName = "フォント列挙に成功する環境では、設定画面のUIフォント・本文フォント欄がComboBoxで表示される")]
    public async Task フォント列挙に成功するとComboBoxが表示される()
    {
        var vm = await CreateViewModelAsync(fontCatalog: new FakeFontCatalog(
            all: new[] { "Alpha", "Beta" }, mono: new[] { "Beta" }));

        var view = new GeneralSettingsView { DataContext = vm };
        var window = _windows.Track(new Window { Content = view });
        window.Show();

        var uiCombo = view.GetVisualDescendants().OfType<ComboBox>()
            .SingleOrDefault(c => Equals(AutomationProperties.GetName(c), "UIフォント"));
        var uiFallback = view.GetVisualDescendants().OfType<TextBox>()
            .SingleOrDefault(c => Equals(AutomationProperties.GetName(c), "UIフォント（手入力）"));
        var bodyCombo = view.GetVisualDescendants().OfType<ComboBox>()
            .SingleOrDefault(c => Equals(AutomationProperties.GetName(c), "本文フォント"));
        var bodyFallback = view.GetVisualDescendants().OfType<TextBox>()
            .SingleOrDefault(c => Equals(AutomationProperties.GetName(c), "本文フォント（手入力）"));

        uiCombo.Should().NotBeNull();
        uiCombo!.IsVisible.Should().BeTrue();
        uiFallback.Should().NotBeNull("列挙成功時もフォールバック用TextBox自体はツリーに存在する（HelpTipカバレッジのため）");
        uiFallback!.IsVisible.Should().BeFalse();
        bodyCombo.Should().NotBeNull();
        bodyCombo!.IsVisible.Should().BeTrue();
        bodyFallback.Should().NotBeNull();
        bodyFallback!.IsVisible.Should().BeFalse();

        // 「(既定)」＋列挙された2件で計3項目。UIフォント・本文フォントは同じFontFamilyOptionsを共用する。
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

        var uiCombo = view.GetVisualDescendants().OfType<ComboBox>()
            .Single(c => Equals(AutomationProperties.GetName(c), "UIフォント"));
        var uiFallback = view.GetVisualDescendants().OfType<TextBox>()
            .Single(c => Equals(AutomationProperties.GetName(c), "UIフォント（手入力）"));
        var bodyCombo = view.GetVisualDescendants().OfType<ComboBox>()
            .Single(c => Equals(AutomationProperties.GetName(c), "本文フォント"));
        var bodyFallback = view.GetVisualDescendants().OfType<TextBox>()
            .Single(c => Equals(AutomationProperties.GetName(c), "本文フォント（手入力）"));
        var monoCombo = view.GetVisualDescendants().OfType<ComboBox>()
            .Single(c => Equals(AutomationProperties.GetName(c), "等幅フォント"));
        var monoFallback = view.GetVisualDescendants().OfType<TextBox>()
            .Single(c => Equals(AutomationProperties.GetName(c), "等幅フォント（手入力）"));

        uiCombo.IsVisible.Should().BeFalse();
        uiFallback.IsVisible.Should().BeTrue();
        bodyCombo.IsVisible.Should().BeFalse();
        bodyFallback.IsVisible.Should().BeTrue();
        monoCombo.IsVisible.Should().BeFalse();
        monoFallback.IsVisible.Should().BeTrue();

        // フォールバック中でも入力・保存経路自体は使える（検討書: 「失敗時は既定フォントの
        // ままにして、設定欄はテキスト入力へフォールバックする」）。
        vm.SelectedFontFamily = "手入力フォント";
        vm.SelectedFontFamily.Should().Be("手入力フォント");
        vm.SelectedBodyTextFontFamily = "手入力本文フォント";
        vm.SelectedBodyTextFontFamily.Should().Be("手入力本文フォント");
    }

    [AvaloniaFact(DisplayName = "UIフォントを選ぶと即座にAppFontManager経由で画面へ反映される（保存を待たない）")]
    public async Task UIフォントの選択が即座に反映される()
    {
        var vm = await CreateViewModelAsync(fontCatalog: new FakeFontCatalog(all: new[] { "PreviewFont" }, mono: Array.Empty<string>()));

        vm.SelectedFontFamily = "PreviewFont";

        Application.Current!.TryFindResource("UiFontFamily", null, out var value);
        value.Should().BeOfType<FontFamily>().Which.Name.Should().Be("PreviewFont");
    }

    [AvaloniaFact(DisplayName = "本文フォントを選ぶと即座にAppFontManager経由で画面へ反映される（保存を待たない）")]
    public async Task 本文フォントの選択が即座に反映される()
    {
        var vm = await CreateViewModelAsync(fontCatalog: new FakeFontCatalog(all: new[] { "BodyPreviewFont" }, mono: Array.Empty<string>()));

        vm.SelectedBodyTextFontFamily = "BodyPreviewFont";

        Application.Current!.TryFindResource("BodyTextFontFamily", null, out var value);
        value.Should().BeOfType<FontFamily>().Which.Name.Should().Be("BodyPreviewFont");
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
