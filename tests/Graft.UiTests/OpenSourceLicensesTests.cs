using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using FluentAssertions;
using Graft.UiTests.TestSupport;
using Graft.Views;

namespace Graft.UiTests;

/// <summary>
/// 製品としての体裁3件のうち機能1（オープンソースライセンス表記）の回帰テスト。
///
/// 依存関係の実際の調べ方（dotnet list packageでの列挙・nuspec/リポジトリLICENSEの突き合わせ）
/// はOpenSourceLicensesWindow.axaml.csのクラスコメントに記載済み。ここでは「一覧が空でない」
/// 「主要な依存（Avalonia・AvaloniaEdit・DiffPlex）が実際に含まれる」「各項目のライセンス全文が
/// 埋め込みリソースから読み込めている（読み込み失敗の定型文になっていない）」ことを機械的に
/// 検証し、将来リストが誤って空になったり、埋め込みリソース側のファイル名だけずれて
/// 読み込みに失敗したりする回帰を防ぐ。
/// </summary>
public class OpenSourceLicensesTests : IDisposable
{
    private readonly ShownWindowTracker _windows = new();

    public void Dispose()
    {
        _windows.Dispose();
        GC.SuppressFinalize(this);
    }

    [AvaloniaFact(DisplayName = "ライセンス一覧が空でなく、主要な依存パッケージを含む")]
    public void ライセンス一覧が空でなく主要パッケージを含む()
    {
        var window = _windows.Track(new OpenSourceLicensesWindow());
        window.Show();

        var entriesList = window.GetVisualDescendants().OfType<ItemsControl>()
            .Single(c => c.Name == "EntriesList");
        var items = ((IEnumerable<object>)entriesList.ItemsSource!).ToList();

        items.Should().NotBeEmpty("使用しているオープンソースソフトウェアの一覧が空であってはならない");

        var names = window.GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text ?? string.Empty).ToList();
        names.Should().Contain(n => n.Contains("Avalonia"), "UIフレームワーク本体が一覧に含まれる必要がある");
        names.Should().Contain(n => n.Contains("AvaloniaEdit"), "エディタ本体が一覧に含まれる必要がある");
        names.Should().Contain(n => n.Contains("DiffPlex"), "差分計算ライブラリが一覧に含まれる必要がある");
    }

    [AvaloniaFact(DisplayName = "各項目のライセンス全文が埋め込みリソースから正しく読み込めている")]
    public void ライセンス全文が読み込める()
    {
        var window = _windows.Track(new OpenSourceLicensesWindow());
        window.Show();

        var entriesList = window.GetVisualDescendants().OfType<ItemsControl>()
            .Single(c => c.Name == "EntriesList");
        var items = ((IEnumerable<object>)entriesList.ItemsSource!)
            .Cast<OpenSourceLicensesWindowEntry>().ToList();

        items.Should().NotBeEmpty();
        foreach (var item in items)
        {
            item.LicenseText.Should().NotBe(
                "ライセンスファイルを読み込めませんでした。",
                $"{item.Name}の埋め込みリソース読み込みに失敗している（ファイル名のずれ等の回帰）");
            item.LicenseText.Should().NotBeNullOrWhiteSpace();
        }
    }

    [AvaloniaFact(DisplayName = "「バージョン情報」タブから開くボタンにHelpTip.Standardが付いている")]
    public void バージョン情報のボタンにHelpTipが付いている()
    {
        var view = new AboutView();
        var window = _windows.Track(new Window { Content = view });
        window.Show();

        var button = window.GetVisualDescendants().OfType<Button>()
            .Single(b => AutomationProperties.GetName(b) == "オープンソースライセンスを表示");

        HelpTip.GetStandard(button).Should().NotBeNull("機能1の要件どおりHelpTip.Standardが必要");
    }

    [AvaloniaFact(DisplayName = "Escapeキーで閉じる")]
    public void Escapeキーで閉じる()
    {
        var window = _windows.Track(new OpenSourceLicensesWindow());
        window.Show();

        var closed = false;
        window.Closed += (_, _) => closed = true;
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);

        closed.Should().BeTrue();
    }
}
