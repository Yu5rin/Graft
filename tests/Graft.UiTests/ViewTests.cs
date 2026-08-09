using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Views;

namespace Graft.UiTests;

/// <summary>
/// 移植した各Viewが例外なく構築・描画できることを検証する（仕様書v2.1 附録A.7、20章L3）。
/// XAMLの構文誤り・リソースキーの解決失敗は、いずれも構築時か描画時に例外として現れるため、
/// このテストが通ることで「画面が開いた瞬間に落ちる」種類の不具合を機械的に防げる。
/// v2.0のWPF版で実際に発生した StaticResource 解決失敗・型変換失敗と同種の不具合が対象。
/// </summary>
public class ViewTests
{
    [AvaloniaFact(DisplayName = "空状態ビューを構築して描画できる")]
    public void 空状態ビューを描画できる()
    {
        var view = new EmptyStateView { Message = "テスト", ActionText = "開く" };
        RenderInWindow(view);
    }

    [AvaloniaFact(DisplayName = "空状態ビューは状態に応じて表示するパネルを切り替える")]
    public void 空状態ビューは状態に応じてパネルを切り替える()
    {
        var view = new EmptyStateView();
        var window = RenderInWindow(view);

        view.State = EmptyStateMode.Empty;
        window.CaptureRenderedFrame().Should().NotBeNull();

        view.State = EmptyStateMode.Error;
        view.Issue = Graft.Core.GraftIssue.Of(Graft.Core.ErrorCode.E101, "テスト");
        window.CaptureRenderedFrame().Should().NotBeNull();
    }

    [AvaloniaFact(DisplayName = "ステータスバーを構築して描画できる")]
    public void ステータスバーを描画できる() => RenderInWindow(new StatusBarView());

    [AvaloniaFact(DisplayName = "サイドバーを構築して描画できる")]
    public void サイドバーを描画できる() => RenderInWindow(new SideBar());

    [AvaloniaFact(DisplayName = "プロジェクトペインを構築して描画できる")]
    public void プロジェクトペインを描画できる() => RenderInWindow(new ProjectPane());

    [AvaloniaFact(DisplayName = "履歴ペインを構築して描画できる")]
    public void 履歴ペインを描画できる() => RenderInWindow(new HistoryPane());

    // 課題2-2回帰テスト: 種別絞り込みが未選択のとき、空欄ではなく「すべての種別」が
    // 選択済みの状態で表示されることを確認する（DataContextはHistoryPaneViewModelそのもの、
    // TypeFilterは一切設定しない既定状態のまま検証）。
    [AvaloniaFact(DisplayName = "履歴の種別絞り込みは未選択時に「すべての種別」を選択済みで表示する")]
    public void 履歴の種別絞り込みは未選択時にすべての種別が選択済みになる()
    {
        var pane = new HistoryPane
        {
            DataContext = new Graft.ViewModels.HistoryPaneViewModel(
                new Graft.Core.RevisionStore(new Graft.Infra.AppPaths(
                    Path.Combine(Path.GetTempPath(), "graft-viewtests-history", Guid.NewGuid().ToString("N")))),
                new Graft.Core.RevisionRestorer(new Graft.Infra.AppPaths(
                    Path.Combine(Path.GetTempPath(), "graft-viewtests-history", Guid.NewGuid().ToString("N")))),
                new Graft.Features.ProjectStore(new Graft.Infra.AppPaths(
                    Path.Combine(Path.GetTempPath(), "graft-viewtests-history", Guid.NewGuid().ToString("N")))),
                new Graft.Platform.Null.NullDialogService()),
        };
        RenderInWindow(pane);

        pane.TypeComboElement.SelectedItem.Should().Be(Graft.ViewModels.HistoryPaneViewModel.AllTypesOption,
            "未選択時に空欄のままでは何を選ぶドロップダウンか分からないため、既定で「すべての種別」を選択済みにする");
    }

    [AvaloniaFact(DisplayName = "エクスプローラビューを構築して描画できる")]
    public void エクスプローラビューを描画できる() => RenderInWindow(new ExplorerView());

    [AvaloniaFact(DisplayName = "検索ビューを構築して描画できる")]
    public void 検索ビューを描画できる() => RenderInWindow(new SearchView());

    [AvaloniaFact(DisplayName = "エディタ内検索オーバーレイを構築して描画できる")]
    public void 検索オーバーレイを描画できる() => RenderInWindow(new SearchOverlay());

    [AvaloniaFact(DisplayName = "差分ビューを構築して描画できる")]
    public void 差分ビューを描画できる() => RenderInWindow(new DiffView());

    [AvaloniaFact(DisplayName = "インライン編集パネルを構築して描画できる")]
    public void インライン編集パネルを描画できる() => RenderInWindow(new InlineEditPanel());

    [AvaloniaFact(DisplayName = "エディタペインを構築して描画できる")]
    public void エディタペインを描画できる() => RenderInWindow(new EditorPane());

    [AvaloniaFact(DisplayName = "接ぎ木パネルを構築して描画できる")]
    public void 接ぎ木パネルを描画できる() => RenderInWindow(new GraftPanel());

    [AvaloniaFact(DisplayName = "パッチキュー画面を構築して描画できる")]
    public void パッチキュー画面を描画できる() => RenderWindow(new QueueWindow());

    // 課題1: 適用前プレビュー画面（ShowPreview有効時）。DataContextを与えず構造だけ確認する
    // （QueueWindow等と同じ、パラメータ無しコンストラクタでのXAML描画スモークテスト）。
    [AvaloniaFact(DisplayName = "適用前プレビュー画面を構築して描画できる")]
    public void 適用前プレビュー画面を描画できる() => RenderWindow(new ApplyPreviewWindow());

    [AvaloniaFact(DisplayName = "コンテキスト収集画面を構築して描画できる")]
    public void コンテキスト収集画面を描画できる() => RenderWindow(new ContextCollectWindow());

    [AvaloniaFact(DisplayName = "設定画面を構築して描画できる")]
    public void 設定画面を描画できる() => RenderWindow(new SettingsWindow());

    [AvaloniaFact(DisplayName = "初回起動ガイドを構築して描画できる")]
    public void 初回起動ガイドを描画できる() => RenderWindow(new OnboardingWindow());

    [AvaloniaFact(DisplayName = "キーボードショートカット一覧を構築して描画できる")]
    public void キーボードショートカット一覧を描画できる() => RenderWindow(new ShortcutsWindow());

    [AvaloniaFact(DisplayName = "バージョン情報を構築して描画できる")]
    public void バージョン情報を描画できる() => RenderInWindow(new AboutView());

    [AvaloniaFact(DisplayName = "バージョン情報に製作者と著作権表示が出る")]
    public void バージョン情報に製作者と著作権が表示される()
    {
        var view = new AboutView();
        RenderInWindow(view);

        // Graft.csprojの<Company>/<Copyright>から生成されるアセンブリ属性を読んで
        // 表示していることを検証する（ハードコード文字列ではないことの確認）。
        var assembly = typeof(AboutView).Assembly;
        var company = assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company;
        var copyright = assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright;

        var authorText = view.FindControl<TextBlock>("AuthorText");
        var copyrightText = view.FindControl<TextBlock>("CopyrightText");

        authorText.Should().NotBeNull();
        copyrightText.Should().NotBeNull();
        authorText!.IsVisible.Should().BeTrue();
        copyrightText!.IsVisible.Should().BeTrue();
        authorText.Text.Should().Be($"製作者: {company}");
        copyrightText.Text.Should().Be(copyright);
    }

    [AvaloniaFact(DisplayName = "設定画面の各タブを構築して描画できる")]
    public void 設定タブを描画できる()
    {
        // 設定画面のタブ7枚。1枚でもXAMLやリソース参照を誤ると、ここで描画に失敗する。
        RenderInWindow(new Graft.Views.SettingsPanels.GeneralSettingsView());
        RenderInWindow(new Graft.Views.SettingsPanels.EditorSettingsView());
        RenderInWindow(new Graft.Views.SettingsPanels.MatchingSettingsView());
        RenderInWindow(new Graft.Views.SettingsPanels.SafetySettingsView());
        RenderInWindow(new Graft.Views.SettingsPanels.DiffSettingsView());
        RenderInWindow(new Graft.Views.SettingsPanels.TemplateSettingsView());
        RenderInWindow(new Graft.Views.SettingsPanels.RawJsonSettingsView());
    }

    /// <summary>ウィンドウそのものを表示して描画する（Window派生の画面用）。</summary>
    private static void RenderWindow(Window window)
    {
        window.Show();

        using var frame = window.CaptureRenderedFrame();
        frame.Should().NotBeNull("リソース解決に失敗すると描画そのものができない");
    }

    /// <summary>
    /// コントロールをウィンドウに載せて実際に描画する。UserControl単体では描画パスに
    /// 乗らずリソース解決の失敗を検出できないため、必ずウィンドウ経由で確認する。
    /// </summary>
    private static Window RenderInWindow(Control view)
    {
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();

        using var frame = window.CaptureRenderedFrame();
        frame.Should().NotBeNull("リソース解決に失敗すると描画そのものができない");
        return window;
    }
}
