using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using FluentAssertions;
using Graft.Views;

namespace Graft.UiTests;

/// <summary>
/// シェルウィンドウの構築・描画テスト（仕様書v2.1 附録A.7）。
/// 「主要画面が例外なく構築・描画できること」を保証し、
/// リソース解決の失敗やレイアウト崩れをここで検出する。
/// </summary>
public class ShellWindowTests
{
    [AvaloniaFact(DisplayName = "シェルウィンドウを例外なく構築して表示できる")]
    public void シェルウィンドウを構築できる()
    {
        var window = new ShellWindow();
        window.Show();

        window.IsVisible.Should().BeTrue();
        window.Title.Should().Be("Graft");
    }

    [AvaloniaFact(DisplayName = "シェルウィンドウを実際に描画してフレームを取得できる")]
    public void シェルウィンドウを描画できる()
    {
        var window = new ShellWindow { Width = 1000, Height = 700 };
        window.Show();

        using var frame = window.CaptureRenderedFrame();
        frame.Should().NotBeNull("リソース解決に失敗すると描画そのものができない");
        frame!.PixelSize.Width.Should().BeGreaterThan(0);
    }

    [AvaloniaFact(DisplayName = "最小サイズは仕様どおり960x600である")]
    public void 最小サイズが仕様どおりである()
    {
        var window = new ShellWindow();
        window.MinWidth.Should().Be(960);
        window.MinHeight.Should().Be(600);
    }

    // ------------------------------------------------------------------
    // 課題3: コマンドバーのボタン左詰め。
    //
    // 以前はColumnDefinitions="Auto,*,Auto"で、プロジェクト選択ドロップダウン（列0）と
    // ボタン群（列2＝右端）の間に列1の余白（*）が挟まっていた。ボタン群をドロップダウンの
    // すぐ右（列1）へ寄せ、余白は列2へ追い出し、「?」ショートカット一覧ボタンだけを
    // 列3として右端に残す構成（ColumnDefinitions="Auto,Auto,*,Auto"）にした。
    // Grid.Column値はXAMLパース時に確定するため、レイアウト（Measure/Arrange）を経ずに
    // 検証できる。
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "課題3: プロジェクト選択は列0、操作ボタン群は列1（ドロップダウンのすぐ右）、ショートカット一覧は列3（右端）にある")]
    public void コマンドバーのボタンは左詰めでショートカットのみ右端にある()
    {
        var window = new ShellWindow();
        window.Show();

        var projectCombo = window.GetVisualDescendants().OfType<ComboBox>()
            .Single(c => Equals(AutomationProperties.GetName(c), "プロジェクトを選択"));
        var analyzeButton = window.GetVisualDescendants().OfType<Button>()
            .Single(b => Equals(AutomationProperties.GetName(b), "クリップボードのパッチを解析"));
        var shortcutsButton = window.GetVisualDescendants().OfType<Button>()
            .Single(b => Equals(AutomationProperties.GetName(b), "キーボードショートカット一覧を開く"));

        Grid.GetColumn(projectCombo).Should().Be(0, "プロジェクト選択ドロップダウンは常に左端に見える位置を維持する");
        Grid.GetColumn((Control)analyzeButton.Parent!).Should().Be(
            1, "操作ボタン群はプロジェクト選択のすぐ右（左詰め）へ寄せる");
        Grid.GetColumn(shortcutsButton).Should().Be(
            3, "ショートカット一覧はワークフローの操作ボタン群と性質が異なる補助機能のため、右端に独立させる");
    }

    [AvaloniaFact(DisplayName = "課題3: 最小幅まで狭めてもコマンドバーのボタンが重ならない")]
    public void 最小幅でもコマンドバーのボタンが重ならない()
    {
        var window = new ShellWindow { Width = 960, Height = 600 };
        window.Show();
        window.Measure(new Avalonia.Size(960, 600));
        window.Arrange(new Avalonia.Rect(0, 0, 960, 600));

        // 接ぎ木パネル側にも同名（「適用を実行」）のボタンがあるため、コマンドバーの
        // Grid（x:Name="CommandBarGrid"）配下だけに絞って探す。
        var commandBar = window.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "CommandBarGrid");
        var buttons = commandBar.GetVisualDescendants().OfType<Button>()
            .Where(b => AutomationProperties.GetName(b) is
                "プロジェクトのファイル一覧とコンテキスト収集を開く" or "クリップボードのパッチを解析" or
                "現在の解析結果をパッチキューへ追加" or "パッチキューを開く" or "適用を実行" or
                "プロンプトテンプレートを選んでコピー" or "履歴ビューを開く" or "設定を開く" or
                "キーボードショートカット一覧を開く")
            .Select(b => (Name: AutomationProperties.GetName(b)!, Bounds: b.Bounds))
            .OrderBy(x => x.Bounds.Left)
            .ToList();

        buttons.Should().HaveCount(9, "コマンドバーの主要ボタン9個すべてが見つかる必要がある");

        // 最小幅960pxでも、幅0（測定されていない＝レイアウトから外れた）ボタンが無いこと、
        // かつX方向の範囲が互いに重ならないことを確認する（欠け・重なりの実機確認の裏付け）。
        foreach (var button in buttons)
        {
            button.Bounds.Width.Should().BeGreaterThan(0, $"「{button.Name}」の幅が0＝表示されていない可能性がある");
        }

        for (var i = 1; i < buttons.Count; i++)
        {
            buttons[i].Bounds.Left.Should().BeGreaterOrEqualTo(
                buttons[i - 1].Bounds.Right,
                $"「{buttons[i - 1].Name}」と「{buttons[i].Name}」が重なってはならない");
        }

        // ショートカット一覧（「?」）は右端に寄せた列にあるため、最も右側に描画されるはず。
        buttons[^1].Name.Should().Be("キーボードショートカット一覧を開く");
    }
}
