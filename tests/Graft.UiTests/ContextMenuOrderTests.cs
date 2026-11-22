using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Graft.UiTests.TestSupport;
using Graft.Views;

namespace Graft.UiTests;

/// <summary>
/// 右クリックメニューの並びと言い方の回帰テスト。次の約束が崩れたら気付けるようにする。
/// <list type="bullet">
/// <item>よく使う安全な操作を上、移動・名前の変更を中、削除・取り消しなど危険な操作を一番下（区切り線つき）に置く。</item>
/// <item>キーは見出しの文字へ埋め込まず、メニューの右側に出す InputGesture で表す。</item>
/// <item>同じ操作は、どの画面でも同じ言い方（<see cref="MenuText"/>）にする。</item>
/// </list>
/// </summary>
public class ContextMenuOrderTests
{
    /// <summary>メニューの子を、区切り線を「---」として並べた見出しの列にする。</summary>
    private static List<string> Layout(ContextMenu menu)
        => menu.Items.Select(i => i switch
        {
            MenuItem m => m.Header?.ToString() ?? string.Empty,
            Separator => "---",
            _ => i?.ToString() ?? string.Empty,
        }).ToList();

    [AvaloniaFact(DisplayName = "エクスプローラの右クリックメニュー: 削除は区切り線のあとの一番下、パスの項目は共通の言い方、キーは見出しに入れない")]
    public void エクスプローラのメニューの並び()
    {
        var view = new ExplorerView();
        var layout = Layout(view.GetControl<TreeView>("FileTreeView").ContextMenu!);

        layout.Should().Equal(
            "開く", MenuText.AddToContext, "このファイルの変更履歴",
            "---", "新規ファイル…", "新規フォルダ…", "ここにファイルを追加…",
            "---", MenuText.CopyPath, MenuText.CopyRelativePath, MenuText.RevealInFileManager,
            "---", "名前の変更", "削除を元に戻す",
            "---", "削除");
        layout[^1].Should().Be("削除", "危険な操作は一番下");
        layout[^2].Should().Be("---", "危険な操作は区切り線で分ける");
    }

    [AvaloniaFact(DisplayName = "履歴の右クリックメニュー: 見る・コピーが先で、取り消す操作（この適用を取り消す・ここまで戻す）が区切り線のあとの一番下にある")]
    public void 履歴のメニューは危険な操作が一番下()
    {
        var view = new HistoryPane();
        var list = view.GetLogicalDescendants().OfType<ListBox>().First(l => l.ContextMenu is not null);
        var layout = Layout(list.ContextMenu!);

        layout.Should().Equal(
            "変更を見る", "要約をコピー",
            "---", "バックアップフォルダを開く",
            "---", "この適用を取り消す", "ここまで戻す");
        layout[0].Should().NotBe("この適用を取り消す", "以前は取り消す操作が先頭にあった");
    }

    [AvaloniaFact(DisplayName = "プロジェクトの右クリックメニュー: ピン留めが先頭、パスのコピー・表示は共通の言い方で変更系の前、削除は区切り線のあとの一番下")]
    public void プロジェクトのメニューは削除が一番下()
    {
        var view = new ProjectPane();
        var list = view.GetLogicalDescendants().OfType<ListBox>().First(l => l.ContextMenu is not null);
        var layout = Layout(list.ContextMenu!);

        // 先頭の見出しは選択中のプロジェクトで変わる（ピン留めする／解除する）ため、並びの比較からは外す。
        layout.Skip(1).Should().Equal(
            "---", MenuText.CopyPath, MenuText.RevealInFileManager,
            "---", "タグを編集…", "表示名を変更…", "場所を変更…",
            // Excel VBA 連携の項目（VBA プロジェクトのときだけ表示。ProjectPane.axaml）。削除より前の独立した区画。
            "---", "Excel から取込", "Excel へ反映", "Excel から復元", "ブックを前面化", "Excel 側の控えを見る…",
            "適用後に Excel へ自動反映する", "保存したらそのモジュールを Excel へ反映する", "反映後にブックを保存する",
            "---", "削除…");
        layout.Last().Should().Be("削除…");
        layout[^2].Should().Be("---");
    }

    [AvaloniaFact(DisplayName = "キューの項目の右クリックメニュー: 項目を取り除く操作がある（右側の削除ボタンと同じ）")]
    public void キューの項目にメニューがある()
    {
        var window = new QueueWindow();
        try
        {
            window.Show();
            var menus = window.GetVisualDescendants().OfType<Grid>().Where(g => g.ContextMenu is not null).ToList();
            // 項目が無いと行は実体化しない。メニューの定義はXAMLにあるため、テンプレートの文字列として確かめる。
            _ = menus;
            File.ReadAllText(FindSource("QueueWindow.axaml")).Should().Contain("キューから削除");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(DisplayName = "エディタのタブの右クリックメニュー: 相対パスをコピー・AIに渡すファイルに追加があり、パスの言い方は共通")]
    public async Task タブのメニューの中身()
    {
        using var harness = await ShellHarness.CreateAsync(withWindow: true).ConfigureAwait(true);
        var path = harness.ProjectPath("a.txt");
        await File.WriteAllTextAsync(path, "A\n").ConfigureAwait(true);
        await harness.RegisterProjectAsync().ConfigureAwait(true);
        var tab = (await harness.Shell.Editor.OpenFileAsync(path).ConfigureAwait(true)).Value;
        Dispatcher.UIThread.RunJobs();

        var grid = harness.Window!.GetVisualDescendants().OfType<Grid>()
            .Single(g => ReferenceEquals(g.DataContext, tab) && g.ContextMenu is not null);
        var layout = Layout(grid.ContextMenu!);

        layout.Should().Equal(
            "閉じる", "他のタブを閉じる", "右側のタブを閉じる", "すべてのタブを閉じる",
            "---", MenuText.AddToContext,
            "---", MenuText.CopyPath, MenuText.CopyRelativePath, MenuText.RevealInFileManager);
    }

    [AvaloniaFact(DisplayName = "同じ操作の言い方は共通: エクスプローラーで表示はWindowsだけ、それ以外はファイルマネージャーで表示")]
    public void ファイルマネージャの言い方はOSで出し分ける()
    {
        MenuText.RevealInFileManager.Should().Be(
            OperatingSystem.IsWindows() ? "エクスプローラーで表示" : "ファイルマネージャーで表示");
        MenuText.CopyPath.Should().Be("パスをコピー");
        MenuText.CopyRelativePath.Should().Be("相対パスをコピー");
    }

    [AvaloniaFact(DisplayName = "どの右クリックメニューにも、見出しへキーを埋め込んだ項目（「開く (Enter)」のような形）が無い")]
    public void 見出しにキーを埋め込まない()
    {
        var menus = new List<ContextMenu>
        {
            new ExplorerView().GetControl<TreeView>("FileTreeView").ContextMenu!,
            new HistoryPane().GetLogicalDescendants().OfType<ListBox>().First(l => l.ContextMenu is not null).ContextMenu!,
            new ProjectPane().GetLogicalDescendants().OfType<ListBox>().First(l => l.ContextMenu is not null).ContextMenu!,
        };

        var headers = menus.SelectMany(m => m.GetLogicalDescendants().OfType<MenuItem>())
            .Select(m => m.Header?.ToString() ?? string.Empty);
        headers.Should().NotContain(h => h.Contains("(Ctrl") || h.Contains("(F2)") || h.Contains("(Enter)")
            || h.Contains("(Delete)") || h.Contains("(Space)"));
    }

    private static string FindSource(string fileName)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "src", "Graft", "Views", fileName);
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException(fileName);
    }
}
