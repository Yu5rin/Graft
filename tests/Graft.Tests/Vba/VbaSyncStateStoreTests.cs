using System.IO;
using FluentAssertions;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Features;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// <see cref="VbaSyncStateStore"/> のテスト（仕様書_VBA連携 4.3・10.1）。
/// 保存と読み込みの往復、壊れた JSON からの回復（<see cref="JsonFileStore"/> の挙動に合わせる）、
/// プロジェクトの Id が変わったときの付け替え。
/// </summary>
public class VbaSyncStateStoreTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 21, 10, 0, 0, TimeSpan.FromHours(9));

    private static VbaSyncState Sample() => new VbaSyncState { WorkbookFullName = @"C:\Work\計画.xlsm" }
        .WithModule("Modules/Module1.bas", VbaModuleState.Create(
            VbaModuleKind.Standard, "Module1", null, "m1", "e1", "l1", At))
        .WithModule("Documents/Sheet1.cls", VbaModuleState.Create(
            VbaModuleKind.Document, "Sheet1", "集計", "m2", "e2", "l2", At) with { DeletePending = true });

    [Fact(DisplayName = "ファイルが無ければ、ブックのパスを持つ空の状態が返る（警告なし）")]
    public async Task ファイルが無ければ空の状態()
    {
        using var ws = new TempWorkspace();
        var store = new VbaSyncStateStore(new AppPaths(ws.CreateDirectory("app")));

        var result = await store.LoadAsync("p_aaaaaa", @"C:\Work\計画.xlsm");

        result.IsSuccess.Should().BeTrue();
        result.Issues.Should().BeEmpty();
        result.Value.WorkbookFullName.Should().Be(@"C:\Work\計画.xlsm");
        result.Value.Modules.Should().BeEmpty();
        result.Value.Version.Should().Be(VbaSyncState.CurrentVersion);
    }

    [Fact(DisplayName = "保存して読み込むと、全項目（ハッシュ・削除保留・表示名・日時・種別）が往復する")]
    public async Task 往復する()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var store = new VbaSyncStateStore(paths);

        await store.SaveAsync("p_aaaaaa", Sample());
        var loaded = await store.LoadAsync("p_aaaaaa", "ignored");

        File.Exists(paths.GetVbaSyncFilePath("p_aaaaaa")).Should().BeTrue();
        loaded.Value.WorkbookFullName.Should().Be(@"C:\Work\計画.xlsm");
        loaded.Value.Modules.Should().HaveCount(2);

        var m1 = loaded.Value.Modules["Modules/Module1.bas"];
        m1.Should().Be(Sample().Modules["Modules/Module1.bas"]);

        var sheet = loaded.Value.Modules["Documents/Sheet1.cls"];
        sheet.Kind.Should().Be(VbaModuleKind.Document);
        sheet.Caption.Should().Be("集計");
        sheet.DeletePending.Should().BeTrue();
        sheet.MirrorHash.Should().Be("m2");
        sheet.ExcelHash.Should().Be("e2");
        sheet.ExcelLooseHash.Should().Be("l2");
        sheet.SyncedAt.Should().Be(At);
    }

    [Fact(DisplayName = "保存した JSON は仕様どおりの形（camelCase、種別は名前、version を持つ）")]
    public async Task JSONの形は仕様どおり()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        await new VbaSyncStateStore(paths).SaveAsync("p_aaaaaa", Sample());

        var json = await File.ReadAllTextAsync(paths.GetVbaSyncFilePath("p_aaaaaa"));

        json.Should().Contain("\"version\": 1");
        json.Should().Contain("\"workbookFullName\"");
        json.Should().Contain("\"modules\"");
        json.Should().Contain("\"kind\": \"Standard\"");
        json.Should().Contain("\"kind\": \"Document\"");
        json.Should().Contain("\"mirrorHash\"").And.Contain("\"excelHash\"").And.Contain("\"excelLooseHash\"");
        json.Should().Contain("\"deletePending\"").And.Contain("\"syncedAt\"").And.Contain("\"caption\": \"集計\"");
    }

    [Fact(DisplayName = "読み込んだ後のキーは大文字小文字を区別しない（ミラーのファイル名の比較に合わせる）")]
    public async Task キーは大文字小文字を区別しない()
    {
        using var ws = new TempWorkspace();
        var store = new VbaSyncStateStore(new AppPaths(ws.CreateDirectory("app")));
        await store.SaveAsync("p_aaaaaa", Sample());

        var loaded = await store.LoadAsync("p_aaaaaa", "x");

        loaded.Value.Modules.Should().ContainKey("modules/MODULE1.bas");
    }

    [Fact(DisplayName = "壊れた JSON は .corrupt.<日時> へ退避し、空の状態で回復する（JsonFileStore の共通復旧手順）")]
    public async Task 壊れたJSONから回復する()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        Directory.CreateDirectory(paths.VbaSyncDirectory);
        var file = paths.GetVbaSyncFilePath("p_aaaaaa");
        await File.WriteAllTextAsync(file, "{ これはJSONではない");

        var result = await new VbaSyncStateStore(paths).LoadAsync("p_aaaaaa", @"C:\Work\計画.xlsm");

        result.IsSuccess.Should().BeTrue();
        result.Value.Modules.Should().BeEmpty();
        result.Value.WorkbookFullName.Should().Be(@"C:\Work\計画.xlsm");
        Directory.GetFiles(paths.VbaSyncDirectory, "p_aaaaaa.json.corrupt.*").Should().ContainSingle();
    }

    [Fact(DisplayName = "modules が null と書かれていても落ちずに空として読む")]
    public async Task modulesがnullでも読める()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        Directory.CreateDirectory(paths.VbaSyncDirectory);
        await File.WriteAllTextAsync(
            paths.GetVbaSyncFilePath("p_aaaaaa"), "{ \"version\": 1, \"workbookFullName\": \"C:\\\\a.xlsm\", \"modules\": null }");

        var result = await new VbaSyncStateStore(paths).LoadAsync("p_aaaaaa", "x");

        result.IsSuccess.Should().BeTrue();
        result.Value.Modules.Should().BeEmpty();
        result.Value.WorkbookFullName.Should().Be(@"C:\a.xlsm");
    }

    [Fact(DisplayName = "Move: Id が変わったとき、同期基準のファイルと控えのフォルダが新しい Id へ付け替わる")]
    public async Task Id変更で付け替える()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var store = new VbaSyncStateStore(paths);
        await store.SaveAsync("p_old000", Sample());
        ws.WriteText("app/vba-sync/p_old000.rescue/Module1-20261121.txt", "控え");

        store.Move("p_old000", "p_new000");

        File.Exists(paths.GetVbaSyncFilePath("p_old000")).Should().BeFalse();
        Directory.Exists(paths.GetVbaRescueDirectory("p_old000")).Should().BeFalse();
        var loaded = await store.LoadAsync("p_new000", "x");
        loaded.Value.Modules.Should().HaveCount(2);
        File.ReadAllText(Path.Combine(paths.GetVbaRescueDirectory("p_new000"), "Module1-20261121.txt")).Should().Be("控え");
    }

    [Fact(DisplayName = "Move: 旧 Id のファイルが無ければ何もしない。同じ Id なら何もしない")]
    public async Task 旧Idが無ければ何もしない()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var store = new VbaSyncStateStore(paths);
        await store.SaveAsync("p_keep00", Sample());

        store.Move("p_none00", "p_new000");
        store.Move("p_keep00", "p_keep00");

        File.Exists(paths.GetVbaSyncFilePath("p_new000")).Should().BeFalse();
        File.Exists(paths.GetVbaSyncFilePath("p_keep00")).Should().BeTrue();
    }

    [Fact(DisplayName = "Move: 新しい Id に古い記録が残っていても、旧 Id の記録で置き換わる")]
    public async Task 新Idの既存を置き換える()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var store = new VbaSyncStateStore(paths);
        await store.SaveAsync("p_old000", Sample());
        await store.SaveAsync("p_new000", new VbaSyncState { WorkbookFullName = "stale" });

        store.Move("p_old000", "p_new000");

        (await store.LoadAsync("p_new000", "x")).Value.WorkbookFullName.Should().Be(@"C:\Work\計画.xlsm");
    }

    [Fact(DisplayName = "Delete: 同期基準のファイルを消す。控え（.rescue）は Excel から失われた本文を含みうるので残す")]
    public async Task Deleteは控えを残す()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var store = new VbaSyncStateStore(paths);
        await store.SaveAsync("p_aaaaaa", Sample());
        ws.WriteText("app/vba-sync/p_aaaaaa.rescue/Module1.txt", "控え");

        store.Delete("p_aaaaaa");
        store.Delete("p_aaaaaa"); // 無くても落ちない

        File.Exists(paths.GetVbaSyncFilePath("p_aaaaaa")).Should().BeFalse();
        Directory.Exists(paths.GetVbaRescueDirectory("p_aaaaaa")).Should().BeTrue();
    }

    [Fact(DisplayName = "WithModule／WithoutModule は元の状態を変えない（不変）")]
    public void 状態は不変()
    {
        var original = Sample();

        var removed = original.WithoutModule("modules/module1.bas");

        original.Modules.Should().HaveCount(2);
        removed.Modules.Should().HaveCount(1);
        removed.Modules.Should().NotContainKey("Modules/Module1.bas");
    }

    [Fact(DisplayName = "同期基準のパスは vba-sync/<プロジェクトId>.json、控えは vba-sync/<プロジェクトId>.rescue/")]
    public void パスの形()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "base"));

        paths.GetVbaSyncFilePath("p_abc123").Should().Be(Path.Combine(paths.BaseDirectory, "vba-sync", "p_abc123.json"));
        paths.GetVbaRescueDirectory("p_abc123").Should().Be(Path.Combine(paths.BaseDirectory, "vba-sync", "p_abc123.rescue"));
    }
}
