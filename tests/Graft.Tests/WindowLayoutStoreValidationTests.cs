using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Graft.ViewModels;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 異常系点検「低」7件目（layout.json のペイン系の値に検証が無い）の回帰テスト。
///
/// 実測: <c>codeFontSize=100000</c>、<c>sideViewWidth=-100</c>、<c>graftPanelHeight=1e9</c>、
/// <c>leftPaneSplitRatio=5.0</c>、<c>caretLine=-5</c>、<c>graftPanelPlacement="??"</c>が
/// そのまま読み込まれた。ウィンドウ位置・サイズは<see cref="WindowLayoutStore.ResolveWindowBounds"/>が、
/// <c>sideViewWidth</c>は<c>ShellWindow.axaml.cs</c>の<c>SafeLength</c>が実際に使う時点で
/// 個別に守っているため、ここでは指示どおり「守られていなかった」<c>codeFontSize</c>・
/// <c>leftPaneSplitRatio</c>・<c>caretLine</c>・<c>graftPanelPlacement</c>のみを対象にする。
/// </summary>
public class WindowLayoutStoreValidationTests
{
    private static async Task<WindowLayoutState> LoadFromRawJsonAsync(TempWorkspace ws, string json)
    {
        var appDir = ws.CreateDirectory("app");
        var paths = new AppPaths(appDir);
        await File.WriteAllTextAsync(Path.Combine(appDir, "layout.json"), json);
        var store = new WindowLayoutStore(paths);
        return await store.LoadAsync();
    }

    [Fact(DisplayName = "不具合回帰: codeFontSize=100000は通常操作の上限(32)へクランプされる")]
    public async Task 巨大なCodeFontSizeはクランプされる()
    {
        using var ws = new TempWorkspace();
        var json = """
        { "projectPaneWidths": { "p1": { "codeFontSize": 100000 } } }
        """;

        var state = await LoadFromRawJsonAsync(ws, json);

        state.ProjectPaneWidths["p1"].CodeFontSize.Should().Be(32);
    }

    [Fact(DisplayName = "不具合回帰: codeFontSize=0（下限未満）は通常操作の下限(8)へクランプされる")]
    public async Task 小さすぎるCodeFontSizeはクランプされる()
    {
        using var ws = new TempWorkspace();
        var json = """
        { "projectPaneWidths": { "p1": { "codeFontSize": 0 } } }
        """;

        var state = await LoadFromRawJsonAsync(ws, json);

        state.ProjectPaneWidths["p1"].CodeFontSize.Should().Be(8);
    }

    [Fact(DisplayName = "codeFontSizeがNaN相当（負の無限大等）でも既定値13へ倒れる（Math.Clampの非数扱いを避ける安全策）")]
    public async Task 非数のCodeFontSizeは既定値になる()
    {
        using var ws = new TempWorkspace();
        var json = """
        { "projectPaneWidths": { "p1": { "codeFontSize": "NaN" } } }
        """;

        var state = await LoadFromRawJsonAsync(ws, json);

        state.ProjectPaneWidths["p1"].CodeFontSize.Should().Be(13);
    }

    [Fact(DisplayName = "不具合回帰: leftPaneSplitRatio=5.0は0〜1の範囲外のため既定値0.55へフォールバックする")]
    public async Task 範囲外のLeftPaneSplitRatioは既定値になる()
    {
        using var ws = new TempWorkspace();
        var json = """{ "leftPaneSplitRatio": 5.0 }""";

        var state = await LoadFromRawJsonAsync(ws, json);

        state.LeftPaneSplitRatio.Should().Be(0.55);
    }

    [Fact(DisplayName = "leftPaneSplitRatio=-1.0（負値）も既定値0.55へフォールバックする")]
    public async Task 負のLeftPaneSplitRatioは既定値になる()
    {
        using var ws = new TempWorkspace();
        var json = """{ "leftPaneSplitRatio": -1.0 }""";

        var state = await LoadFromRawJsonAsync(ws, json);

        state.LeftPaneSplitRatio.Should().Be(0.55);
    }

    [Theory(DisplayName = "0〜1の範囲内のleftPaneSplitRatioはそのまま維持される（回帰: 正常系を壊していないこと）")]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(0.3)]
    public async Task 範囲内のLeftPaneSplitRatioはそのまま(double value)
    {
        using var ws = new TempWorkspace();
        var json = $$"""{ "leftPaneSplitRatio": {{value}} }""";

        var state = await LoadFromRawJsonAsync(ws, json);

        state.LeftPaneSplitRatio.Should().Be(value);
    }

    [Fact(DisplayName = "不具合回帰: caretLine=-5のような負のカーソル行は1行目へフォールバックする")]
    public async Task 負のCaretLineは1へフォールバックする()
    {
        using var ws = new TempWorkspace();
        var json = """
        {
          "projectPaneWidths": {
            "p1": {
              "openTabs": [ { "relativePath": "a.txt", "caretLine": -5, "caretColumn": -3 } ]
            }
          }
        }
        """;

        var state = await LoadFromRawJsonAsync(ws, json);

        var tab = state.ProjectPaneWidths["p1"].OpenTabs.Should().ContainSingle().Subject;
        tab.CaretLine.Should().Be(1);
        tab.CaretColumn.Should().Be(1);
    }

    [Fact(DisplayName = "0のcaretLine/caretColumnも1へフォールバックする（1始まりのため0は存在しない位置）")]
    public async Task ゼロのCaretLineは1へフォールバックする()
    {
        using var ws = new TempWorkspace();
        var json = """
        {
          "projectPaneWidths": {
            "p1": {
              "openTabs": [ { "relativePath": "a.txt", "caretLine": 0, "caretColumn": 0 } ]
            }
          }
        }
        """;

        var state = await LoadFromRawJsonAsync(ws, json);

        var tab = state.ProjectPaneWidths["p1"].OpenTabs.Should().ContainSingle().Subject;
        tab.CaretLine.Should().Be(1);
        tab.CaretColumn.Should().Be(1);
    }

    [Fact(DisplayName = "不具合回帰: graftPanelPlacement=\"??\"のような未知の値は既定の\"bottom\"へフォールバックする")]
    public async Task 未知のGraftPanelPlacementは既定値になる()
    {
        using var ws = new TempWorkspace();
        var json = """
        { "projectPaneWidths": { "p1": { "graftPanelPlacement": "??" } } }
        """;

        var state = await LoadFromRawJsonAsync(ws, json);

        state.ProjectPaneWidths["p1"].GraftPanelPlacement.Should().Be("bottom");
    }

    [Fact(DisplayName = "graftPanelPlacement=\"right\"は妥当な値としてそのまま維持される（回帰: 正常系を壊していないこと）")]
    public async Task rightのGraftPanelPlacementは維持される()
    {
        using var ws = new TempWorkspace();
        var json = """
        { "projectPaneWidths": { "p1": { "graftPanelPlacement": "right" } } }
        """;

        var state = await LoadFromRawJsonAsync(ws, json);

        state.ProjectPaneWidths["p1"].GraftPanelPlacement.Should().Be("right");
    }

    [Fact(DisplayName = "妥当な値のみのlayout.jsonは、読み込み後も値が変化しない（回帰: 検証追加が正常系を壊していないこと）")]
    public async Task 妥当な値は変化しない()
    {
        using var ws = new TempWorkspace();
        var json = """
        {
          "width": 1000,
          "height": 700,
          "leftPaneSplitRatio": 0.4,
          "projectPaneWidths": {
            "p1": {
              "sideViewWidth": 300,
              "codeFontSize": 16,
              "graftPanelPlacement": "right",
              "openTabs": [ { "relativePath": "a.txt", "caretLine": 10, "caretColumn": 4 } ]
            }
          }
        }
        """;

        var state = await LoadFromRawJsonAsync(ws, json);

        state.Width.Should().Be(1000);
        state.LeftPaneSplitRatio.Should().Be(0.4);
        var layout = state.ProjectPaneWidths["p1"];
        layout.SideViewWidth.Should().Be(300);
        layout.CodeFontSize.Should().Be(16);
        layout.GraftPanelPlacement.Should().Be("right");
        var tab = layout.OpenTabs.Should().ContainSingle().Subject;
        tab.CaretLine.Should().Be(10);
        tab.CaretColumn.Should().Be(4);
    }
}
