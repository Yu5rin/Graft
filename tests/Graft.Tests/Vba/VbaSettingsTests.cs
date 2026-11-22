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

    [Fact(DisplayName = "ブックの情報の既定値: 取得する・見出し＋N 行の N は 5・30 列・1 セル 60 文字・全体 20000 文字・全部は 500 行 50 列")]
    public void ブックの情報の既定値()
    {
        var vba = new Settings().Vba;

        vba.CollectWorkbookInfo.Should().BeTrue();
        vba.WorkbookInfoSampleRows.Should().Be(5);
        vba.WorkbookInfoMaxColumns.Should().Be(30);
        vba.WorkbookInfoMaxCellChars.Should().Be(60);
        vba.WorkbookInfoMaxChars.Should().Be(20000);
        vba.WorkbookInfoAllMaxRows.Should().Be(500);
        vba.WorkbookInfoAllMaxColumns.Should().Be(50);
    }

    [Fact(DisplayName = "リボン（customUI）の XML の最大文字数の既定は 30000")]
    public void リボンの既定値()
    {
        new Settings().Vba.RibbonMaxChars.Should().Be(30000);
    }

    [Fact(DisplayName = "控えの既定値: 1 モジュール 20 世代・全体 50MB")]
    public void 控えの既定値()
    {
        var vba = new Settings().Vba;

        vba.RescueMaxGenerations.Should().Be(20);
        vba.RescueMaxTotalMB.Should().Be(50);
    }

    [Fact(DisplayName = "控えの 2 項目は JSON の往復で保たれる（camelCase）。このキーの無い旧形式は既定値で読める")]
    public async Task 控えの往復()
    {
        using var ws = new TempWorkspace();
        var paths = MakePaths(ws);
        var store = new SettingsStore(paths);
        var custom = new VbaSettings { RescueMaxGenerations = 50, RescueMaxTotalMB = 200 };

        await store.SaveAsync(new Settings { Vba = custom });
        var json = await File.ReadAllTextAsync(paths.SettingsFilePath);
        var loaded = await store.LoadAsync();

        json.Should().Contain("\"rescueMaxGenerations\": 50").And.Contain("\"rescueMaxTotalMB\": 200");
        loaded.Value.Vba.Should().Be(custom);
        loaded.Issues.Should().BeEmpty();

        await File.WriteAllTextAsync(paths.SettingsFilePath, "{ \"vba\": { \"comTimeoutMs\": 7000 } }");
        var old = await store.LoadAsync();
        old.Value.Vba.RescueMaxGenerations.Should().Be(20);
        old.Value.Vba.RescueMaxTotalMB.Should().Be(50);
        old.Issues.Should().BeEmpty();
    }

    [Theory(DisplayName = "控えの数値は範囲外なら既定値に戻り、E404 の警告が付く（世代 1〜200・容量 5〜1000）。境界は有効")]
    [InlineData("rescueMaxGenerations", 0, 20, true)]
    [InlineData("rescueMaxGenerations", 201, 20, true)]
    [InlineData("rescueMaxGenerations", 1, 1, false)]
    [InlineData("rescueMaxGenerations", 200, 200, false)]
    [InlineData("rescueMaxTotalMB", 4, 50, true)]
    [InlineData("rescueMaxTotalMB", 1001, 50, true)]
    [InlineData("rescueMaxTotalMB", 5, 5, false)]
    [InlineData("rescueMaxTotalMB", 1000, 1000, false)]
    public async Task 控えの範囲(string key, int value, int expected, bool warns)
    {
        using var ws = new TempWorkspace();
        var paths = MakePaths(ws);
        await File.WriteAllTextAsync(paths.SettingsFilePath, $"{{ \"vba\": {{ \"{key}\": {value} }} }}");

        var loaded = await new SettingsStore(paths).LoadAsync();

        var actual = key == "rescueMaxGenerations" ? loaded.Value.Vba.RescueMaxGenerations : loaded.Value.Vba.RescueMaxTotalMB;
        actual.Should().Be(expected);
        if (warns) loaded.Issues.Should().Contain(i => i.Code == ErrorCode.E404 && i.Detail!.Contains("vba." + key));
        else loaded.Issues.Should().BeEmpty();
    }

    [Fact(DisplayName = "ブックの情報の項目は JSON の往復で保たれる（camelCase）。このキーの無い旧形式は既定値で読める")]
    public async Task ブックの情報の往復()
    {
        using var ws = new TempWorkspace();
        var paths = MakePaths(ws);
        var store = new SettingsStore(paths);
        var custom = new VbaSettings
        {
            CollectWorkbookInfo = false, WorkbookInfoSampleRows = 12, WorkbookInfoMaxColumns = 50,
            WorkbookInfoMaxCellChars = 120, WorkbookInfoMaxChars = 40000, WorkbookInfoAllMaxRows = 800, WorkbookInfoAllMaxColumns = 70,
            RibbonMaxChars = 60000,
            RescueMaxGenerations = 33, RescueMaxTotalMB = 77,
        };

        await store.SaveAsync(new Settings { Vba = custom });
        var json = await File.ReadAllTextAsync(paths.SettingsFilePath);
        var loaded = await store.LoadAsync();

        json.Should().Contain("\"collectWorkbookInfo\": false").And.Contain("\"workbookInfoSampleRows\": 12");
        json.Should().Contain("\"workbookInfoMaxColumns\": 50").And.Contain("\"workbookInfoMaxCellChars\": 120");
        json.Should().Contain("\"workbookInfoMaxChars\": 40000");
        json.Should().Contain("\"workbookInfoAllMaxRows\": 800").And.Contain("\"workbookInfoAllMaxColumns\": 70");
        json.Should().Contain("\"ribbonMaxChars\": 60000");
        loaded.Value.Vba.Should().Be(custom);
        loaded.Issues.Should().BeEmpty();

        await File.WriteAllTextAsync(paths.SettingsFilePath, "{ \"vba\": { \"comTimeoutMs\": 7000 } }");
        var old = await store.LoadAsync();
        old.Value.Vba.ComTimeoutMs.Should().Be(7000);
        old.Value.Vba.CollectWorkbookInfo.Should().BeTrue();
        old.Value.Vba.WorkbookInfoSampleRows.Should().Be(5);
        old.Value.Vba.WorkbookInfoAllMaxRows.Should().Be(500, "このキーの無い旧形式は既定値で読める");
        old.Value.Vba.WorkbookInfoAllMaxColumns.Should().Be(50);
        old.Value.Vba.RibbonMaxChars.Should().Be(30000, "このキーの無い旧形式は既定値で読める");
        old.Issues.Should().BeEmpty();
    }

    [Theory(DisplayName = "ブックの情報の数値は範囲外なら既定値に戻り、E404 の警告が付く（N 1〜20・列 5〜100・文字 10〜500・全体 2000〜100000・全部の行 10〜5000・列 5〜200）")]
    [InlineData("workbookInfoSampleRows", 0, 5)]
    [InlineData("workbookInfoSampleRows", 21, 5)]
    [InlineData("workbookInfoMaxColumns", 4, 30)]
    [InlineData("workbookInfoMaxColumns", 101, 30)]
    [InlineData("workbookInfoMaxCellChars", 9, 60)]
    [InlineData("workbookInfoMaxCellChars", 501, 60)]
    [InlineData("workbookInfoMaxChars", 1999, 20000)]
    [InlineData("workbookInfoMaxChars", 100001, 20000)]
    [InlineData("workbookInfoAllMaxRows", 9, 500)]
    [InlineData("workbookInfoAllMaxRows", 5001, 500)]
    [InlineData("workbookInfoAllMaxColumns", 4, 50)]
    [InlineData("workbookInfoAllMaxColumns", 201, 50)]
    [InlineData("ribbonMaxChars", 1999, 30000)]
    [InlineData("ribbonMaxChars", 200001, 30000)]
    public async Task ブックの情報の範囲外(string key, int value, int fallback)
    {
        using var ws = new TempWorkspace();
        var paths = MakePaths(ws);
        await File.WriteAllTextAsync(paths.SettingsFilePath, $"{{ \"vba\": {{ \"{key}\": {value} }} }}");

        var loaded = await new SettingsStore(paths).LoadAsync();

        var vba = loaded.Value.Vba;
        var actual = key switch
        {
            "workbookInfoSampleRows" => vba.WorkbookInfoSampleRows,
            "workbookInfoMaxColumns" => vba.WorkbookInfoMaxColumns,
            "workbookInfoMaxCellChars" => vba.WorkbookInfoMaxCellChars,
            "workbookInfoAllMaxRows" => vba.WorkbookInfoAllMaxRows,
            "workbookInfoAllMaxColumns" => vba.WorkbookInfoAllMaxColumns,
            "ribbonMaxChars" => vba.RibbonMaxChars,
            _ => vba.WorkbookInfoMaxChars,
        };
        actual.Should().Be(fallback);
        loaded.Issues.Should().Contain(i => i.Code == ErrorCode.E404 && i.Detail!.Contains("vba." + key));
    }

    [Theory(DisplayName = "ブックの情報の数値の境界は有効")]
    [InlineData("workbookInfoSampleRows", 1)]
    [InlineData("workbookInfoSampleRows", 20)]
    [InlineData("workbookInfoMaxColumns", 5)]
    [InlineData("workbookInfoMaxColumns", 100)]
    [InlineData("workbookInfoMaxCellChars", 10)]
    [InlineData("workbookInfoMaxCellChars", 500)]
    [InlineData("workbookInfoMaxChars", 2000)]
    [InlineData("workbookInfoMaxChars", 100000)]
    [InlineData("workbookInfoAllMaxRows", 10)]
    [InlineData("workbookInfoAllMaxRows", 5000)]
    [InlineData("workbookInfoAllMaxColumns", 5)]
    [InlineData("workbookInfoAllMaxColumns", 200)]
    [InlineData("ribbonMaxChars", 2000)]
    [InlineData("ribbonMaxChars", 200000)]
    public async Task ブックの情報の境界は有効(string key, int value)
    {
        using var ws = new TempWorkspace();
        var paths = MakePaths(ws);
        await File.WriteAllTextAsync(paths.SettingsFilePath, $"{{ \"vba\": {{ \"{key}\": {value} }} }}");

        var loaded = await new SettingsStore(paths).LoadAsync();

        loaded.Issues.Should().BeEmpty();
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
