using System.IO;
using FluentAssertions;
using Graft.Core;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// <see cref="Settings.Vba"/>（<see cref="VbaSettings"/>）のテスト（仕様書_VBA連携 8章・10.1）。
/// 既定値、範囲検証（<c>ComTimeoutMs</c> 1000〜30000、<c>CallTimeoutMs</c> 5000〜120000 かつ ComTimeoutMs 以上）、
/// JSON の往復、旧形式の settings.json（vba キー無し）の後方互換。
/// </summary>
public class VbaSettingsTests
{
    private static AppPaths MakePaths(TempWorkspace ws) => new(ws.CreateDirectory("app"));

    [Fact(DisplayName = "既定値: 有効・コンテキスト収集と解析前に自動 Pull・再試行 5 秒・呼び出し全体 30 秒")]
    public void 既定値()
    {
        var vba = new Settings().Vba;

        vba.Enabled.Should().BeTrue();
        vba.PullOnContextOpen.Should().BeTrue();
        vba.PullBeforeDryRun.Should().BeTrue();
        vba.ComTimeoutMs.Should().Be(5000);
        vba.CallTimeoutMs.Should().Be(30000);
    }

    [Fact(DisplayName = "settings.json が無ければ既定値、vba キーの無い旧形式でも既定値で読める（警告なし）")]
    public async Task 旧形式の設定でも読める()
    {
        using var ws = new TempWorkspace();
        var paths = MakePaths(ws);
        var store = new SettingsStore(paths);

        (await store.LoadAsync()).Value.Vba.Should().Be(new VbaSettings());

        await File.WriteAllTextAsync(paths.SettingsFilePath, "{ \"theme\": \"dark\" }");
        var loaded = await store.LoadAsync();

        loaded.Value.Theme.Should().Be("dark");
        loaded.Value.Vba.Should().Be(new VbaSettings());
        loaded.Issues.Should().BeEmpty();
    }

    [Fact(DisplayName = "JSON の往復: camelCase の vba キーで保存され、読み直すと同じ値になる")]
    public async Task JSONの往復()
    {
        using var ws = new TempWorkspace();
        var paths = MakePaths(ws);
        var store = new SettingsStore(paths);
        var custom = new VbaSettings
        {
            Enabled = false, PullOnContextOpen = false, PullBeforeDryRun = false, ComTimeoutMs = 8000, CallTimeoutMs = 45000,
        };

        await store.SaveAsync(new Settings { Vba = custom });
        var json = await File.ReadAllTextAsync(paths.SettingsFilePath);
        var loaded = await store.LoadAsync();

        json.Should().Contain("\"vba\":");
        json.Should().Contain("\"comTimeoutMs\": 8000").And.Contain("\"callTimeoutMs\": 45000");
        json.Should().Contain("\"pullOnContextOpen\": false").And.Contain("\"pullBeforeDryRun\": false");
        json.Should().NotContain("pullHotkey", "ホットキーは初版から設けない");
        loaded.Value.Vba.Should().Be(custom);
        loaded.Issues.Should().BeEmpty();
    }

    [Fact(DisplayName = "vba が JSON 上で null でも落ちず、既定値に置き換わる")]
    public async Task nullのvbaは既定値()
    {
        using var ws = new TempWorkspace();
        var paths = MakePaths(ws);
        await File.WriteAllTextAsync(paths.SettingsFilePath, "{ \"vba\": null }");

        var loaded = await new SettingsStore(paths).LoadAsync();

        loaded.IsSuccess.Should().BeTrue();
        loaded.Value.Vba.Should().Be(new VbaSettings());
    }

    [Theory(DisplayName = "ComTimeoutMs が範囲（1000〜30000）外なら既定値 5000 に戻り、E404 の警告が付く")]
    [InlineData(0)]
    [InlineData(999)]
    [InlineData(30001)]
    [InlineData(-5)]
    public async Task ComTimeoutMsの範囲外(int value)
    {
        using var ws = new TempWorkspace();
        var paths = MakePaths(ws);
        await File.WriteAllTextAsync(paths.SettingsFilePath, $"{{ \"vba\": {{ \"comTimeoutMs\": {value} }} }}");

        var loaded = await new SettingsStore(paths).LoadAsync();

        loaded.Value.Vba.ComTimeoutMs.Should().Be(5000);
        loaded.Issues.Should().Contain(i => i.Code == ErrorCode.E404 && i.Detail!.Contains("vba.comTimeoutMs"));
    }

    [Theory(DisplayName = "ComTimeoutMs の境界（1000・30000）は有効")]
    [InlineData(1000)]
    [InlineData(30000)]
    public async Task ComTimeoutMsの境界は有効(int value)
    {
        using var ws = new TempWorkspace();
        var paths = MakePaths(ws);
        await File.WriteAllTextAsync(paths.SettingsFilePath, $"{{ \"vba\": {{ \"comTimeoutMs\": {value}, \"callTimeoutMs\": 60000 }} }}");

        var loaded = await new SettingsStore(paths).LoadAsync();

        loaded.Value.Vba.ComTimeoutMs.Should().Be(value);
        loaded.Issues.Should().BeEmpty();
    }

    [Theory(DisplayName = "CallTimeoutMs が範囲（5000〜120000）外なら既定値 30000 に戻り、E404 の警告が付く")]
    [InlineData(4999)]
    [InlineData(120001)]
    [InlineData(0)]
    public async Task CallTimeoutMsの範囲外(int value)
    {
        using var ws = new TempWorkspace();
        var paths = MakePaths(ws);
        await File.WriteAllTextAsync(paths.SettingsFilePath, $"{{ \"vba\": {{ \"callTimeoutMs\": {value} }} }}");

        var loaded = await new SettingsStore(paths).LoadAsync();

        loaded.Value.Vba.CallTimeoutMs.Should().Be(30000);
        loaded.Issues.Should().Contain(i => i.Code == ErrorCode.E404 && i.Detail!.Contains("vba.callTimeoutMs"));
    }

    [Theory(DisplayName = "CallTimeoutMs の境界（5000・120000）は有効（ComTimeoutMs 以下に収まる場合）")]
    [InlineData(5000)]
    [InlineData(120000)]
    public async Task CallTimeoutMsの境界は有効(int value)
    {
        using var ws = new TempWorkspace();
        var paths = MakePaths(ws);
        await File.WriteAllTextAsync(paths.SettingsFilePath, $"{{ \"vba\": {{ \"comTimeoutMs\": 5000, \"callTimeoutMs\": {value} }} }}");

        var loaded = await new SettingsStore(paths).LoadAsync();

        loaded.Value.Vba.CallTimeoutMs.Should().Be(value);
        loaded.Issues.Should().BeEmpty();
    }

    [Fact(DisplayName = "CallTimeoutMs が ComTimeoutMs 未満なら（再試行の途中で全体が諦められてしまうので）既定値 30000 に戻る")]
    public async Task CallTimeoutMsがComTimeoutMs未満()
    {
        using var ws = new TempWorkspace();
        var paths = MakePaths(ws);
        await File.WriteAllTextAsync(paths.SettingsFilePath, "{ \"vba\": { \"comTimeoutMs\": 20000, \"callTimeoutMs\": 10000 } }");

        var loaded = await new SettingsStore(paths).LoadAsync();

        loaded.Value.Vba.ComTimeoutMs.Should().Be(20000);
        loaded.Value.Vba.CallTimeoutMs.Should().Be(30000);
        loaded.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E404 && i.Detail!.Contains("vba.callTimeoutMs"));
    }

    [Fact(DisplayName = "CallTimeoutMs が ComTimeoutMs と同じなら有効（以上、の境界）")]
    public async Task CallTimeoutMsがComTimeoutMsと同じ()
    {
        using var ws = new TempWorkspace();
        var paths = MakePaths(ws);
        await File.WriteAllTextAsync(paths.SettingsFilePath, "{ \"vba\": { \"comTimeoutMs\": 10000, \"callTimeoutMs\": 10000 } }");

        var loaded = await new SettingsStore(paths).LoadAsync();

        loaded.Value.Vba.CallTimeoutMs.Should().Be(10000);
        loaded.Issues.Should().BeEmpty();
    }

    [Fact(DisplayName = "保存前の検証（ValidateOnly）でも同じ範囲検証が働く")]
    public void 保存前の検証でも働く()
    {
        var invalid = new Settings { Vba = new VbaSettings { ComTimeoutMs = 10 } };

        var issues = SettingsStore.ValidateOnly(invalid).Issues;

        issues.Should().Contain(i => i.Code == ErrorCode.E406 && i.Detail!.Contains("vba.comTimeoutMs"));
    }
}
