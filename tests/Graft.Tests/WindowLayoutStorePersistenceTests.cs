using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Infra;
using Graft.Platform;
using Graft.Tests.TestSupport;
using Graft.ViewModels;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// バグ1回帰: WindowLayoutState.Left/Top のNaNがJSON直列化できず、layout.jsonの復旧・保存
/// そのものが壊れていた不具合。<see cref="WindowLayoutStore"/> の読み書きを実ファイル上で検証する。
/// </summary>
public class WindowLayoutStorePersistenceTests
{
    [Fact(DisplayName = "破損layout.jsonはLoadAsyncで退避され、既定値のlayout.jsonが再生成される（例外なし）")]
    public async Task 破損layout_jsonを退避して既定値で再生成する()
    {
        using var ws = new TempWorkspace();
        var appDir = ws.CreateDirectory("app");
        var paths = new AppPaths(appDir);
        var layoutPath = Path.Combine(appDir, "layout.json");
        await File.WriteAllTextAsync(layoutPath, "{ これはJSONではない");

        var store = new WindowLayoutStore(paths);

        Func<Task<WindowLayoutState>> act = () => store.LoadAsync();
        var loaded = await act.Should().NotThrowAsync("破損していても例外を投げず既定値へ復旧するはず");
        var state = loaded.Subject;

        state.Left.Should().BeNull("既定値は未保存を表すnullのはず");
        state.Top.Should().BeNull();
        state.Width.Should().Be(1280);

        var quarantined = Directory.GetFiles(appDir, "layout.json.corrupt.*");
        quarantined.Should().ContainSingle("壊れた内容は消さずに退避しておく必要がある");
        File.Exists(layoutPath).Should().BeTrue("既定値のlayout.jsonが再生成されているはず");
    }

    [Fact(DisplayName = "Left/Topが未保存（null）のままでもSaveAsyncは例外にならず成功する")]
    public async Task 未保存のLeftTopでもSaveAsyncが成功する()
    {
        // 初回起動時や、一度もWindowState.Normalにならないまま終了した場合（最大化のまま終了等）に
        // Left/Topがnullのまま保存されるケースの回帰テスト。旧実装ではdouble.NaNが既定値のため
        // System.Text.Jsonが「NaNはJSONとして書けない」例外を投げ、layout.json自体が消滅していた。
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var store = new WindowLayoutStore(paths);
        var state = new WindowLayoutState { IsMaximized = true };
        state.Left.Should().BeNull();
        state.Top.Should().BeNull();

        var act = async () => await store.SaveAsync(state);
        await act.Should().NotThrowAsync();

        var reloaded = await store.LoadAsync();
        reloaded.Left.Should().BeNull();
        reloaded.Top.Should().BeNull();
        reloaded.IsMaximized.Should().BeTrue();
    }

    [Fact(DisplayName = "旧形式（\"NaN\"文字列リテラルを含むlayout.json）が例外なく読める")]
    public async Task 旧形式のNaN文字列リテラルを読める()
    {
        using var ws = new TempWorkspace();
        var appDir = ws.CreateDirectory("app");
        var paths = new AppPaths(appDir);
        var layoutPath = Path.Combine(appDir, "layout.json");
        await File.WriteAllTextAsync(layoutPath, """
            {
              "left": "NaN",
              "top": "NaN",
              "width": 1024,
              "height": 768,
              "isMaximized": false
            }
            """);

        var store = new WindowLayoutStore(paths);

        Func<Task<WindowLayoutState>> act = () => store.LoadAsync();
        var loaded = await act.Should().NotThrowAsync("旧形式のNaN文字列リテラルも読めるはず");
        var state = loaded.Subject;

        state.Width.Should().Be(1024);
        state.Height.Should().Be(768);
    }

    [Fact(DisplayName = "旧形式のNaN文字列リテラルはResolveWindowBoundsで画面中央へ補正される")]
    public void 旧形式のNaNはResolveWindowBoundsで中央補正される()
    {
        var state = new WindowLayoutState { Left = double.NaN, Top = double.NaN, Width = 1280, Height = 800 };
        var screens = new FakeScreenInfo(new UiRect(0, 0, 1920, 1080), new UiRect(0, 0, 1920, 1040));

        var bounds = WindowLayoutStore.ResolveWindowBounds(state, 960, 600, screens);

        bounds.Left.Should().Be((1920 - 1280) / 2.0);
        bounds.Top.Should().Be((1040 - 800) / 2.0);
    }

    private sealed class FakeScreenInfo : IScreenInfo
    {
        public FakeScreenInfo(UiRect virtualScreen, UiRect primaryWorkArea)
        {
            VirtualScreen = virtualScreen;
            PrimaryWorkArea = primaryWorkArea;
        }

        public UiRect VirtualScreen { get; }
        public UiRect PrimaryWorkArea { get; }
        public bool IsAnimationEnabled => true;
    }
}
