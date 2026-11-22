using System.IO;
using FluentAssertions;
using Graft.Core;
using Graft.Features;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// <see cref="Project.Vba"/>（<see cref="VbaLink"/>）と projects.json の後方互換のテスト
/// （仕様書_VBA連携 4.1・10.1）。<c>vba</c> キーを持たない既存の projects.json が変更なしで読めること、
/// <c>vba</c> ありで往復できることを、実際の <see cref="ProjectStore"/> を通して確かめる。
/// </summary>
public class ProjectVbaLinkTests
{
    [Fact(DisplayName = "vba キーを持たない既存の projects.json はそのまま読めて、Vba は null（ファイルプロジェクト）")]
    public async Task 旧形式のprojects_jsonが読める()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var root = ws.CreateDirectory("proj");
        await File.WriteAllTextAsync(paths.ProjectsFilePath, $$"""
            {
              "projects": [
                { "id": "p_000001", "name": "旧プロジェクト", "root": "{{root.Replace("\\", "\\\\")}}", "nextRevision": 4 }
              ]
            }
            """);

        var result = await new ProjectStore(paths).LoadAsync();

        result.IsSuccess.Should().BeTrue();
        var project = result.Value.Should().ContainSingle().Subject;
        project.Name.Should().Be("旧プロジェクト");
        project.NextRevision.Should().Be(4);
        project.Vba.Should().BeNull();
    }

    [Fact(DisplayName = "vba を持つプロジェクトは、保存して読み直すと全項目が往復する")]
    public async Task Vbaありで往復する()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        // 既定の置き場（データ保存先の vba-mirrors/ の配下）に作ったミラーでも登録できる（仕様 4.2）。
        var root = ws.CreateDirectory("app/vba-mirrors/計画-1a2b3c4d");
        var store = new ProjectStore(paths);
        var registered = await store.RegisterAsync(root, null);
        registered.IsSuccess.Should().BeTrue();

        var withVba = registered.Value with
        {
            Vba = new VbaLink { WorkbookFullName = @"C:\Work\計画.xlsm", AutoPushAfterApply = false, SaveWorkbookAfterPush = true },
        };
        await store.SaveAsync(new[] { withVba });

        var reloaded = (await new ProjectStore(paths).LoadAsync()).Value.Should().ContainSingle().Subject;
        reloaded.Vba.Should().NotBeNull();
        reloaded.Vba!.WorkbookFullName.Should().Be(@"C:\Work\計画.xlsm");
        reloaded.Vba.AutoPushAfterApply.Should().BeFalse();
        reloaded.Vba.SaveWorkbookAfterPush.Should().BeTrue();
    }

    [Fact(DisplayName = "JSON のキーは camelCase の vba で、VbaLink の既定は「適用後に自動反映する・ブックは保存しない」")]
    public async Task JSONのキーと既定値()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var root = ws.CreateDirectory("mirror");
        var store = new ProjectStore(paths);
        var registered = (await store.RegisterAsync(root, null)).Value;

        await store.SaveAsync(new[] { registered with { Vba = new VbaLink { WorkbookFullName = @"C:\a.xlsm" } } });
        var json = await File.ReadAllTextAsync(paths.ProjectsFilePath);

        json.Should().Contain("\"vba\":");
        json.Should().Contain("\"workbookFullName\"");
        json.Should().Contain("\"autoPushAfterApply\": true");
        json.Should().Contain("\"saveWorkbookAfterPush\": false");
    }

    [Fact(DisplayName = "vba を持たないプロジェクトを保存しても、読み直して Vba は null のまま")]
    public async Task Vbaなしの往復()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var root = ws.CreateDirectory("plain");
        var store = new ProjectStore(paths);
        await store.RegisterAsync(root, null);

        (await new ProjectStore(paths).LoadAsync()).Value.Should().ContainSingle().Which.Vba.Should().BeNull();
    }

    [Fact(DisplayName = "ブックの情報の項目（出し方・値の見本・数式）を持たない既存の vba は、既定値（内容も出す・見本オフ・数式オフ）で読める")]
    public async Task ブックの情報の項目が無い旧形式は既定値()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var root = ws.CreateDirectory("proj");
        await File.WriteAllTextAsync(paths.ProjectsFilePath, $$"""
            {
              "projects": [
                { "id": "p_000002", "name": "旧VBA", "root": "{{root.Replace("\\", "\\\\")}}",
                  "vba": { "workbookFullName": "C:\\Work\\計画.xlsm", "autoPushAfterApply": false } }
              ]
            }
            """);

        var vba = (await new ProjectStore(paths).LoadAsync()).Value.Should().ContainSingle().Subject.Vba!;

        vba.WorkbookInfoState.Should().Be("Full");
        vba.WorkbookInfoSampleValues.Should().BeFalse("値の見本は既定オフ");
        vba.WorkbookInfoIncludeFormulas.Should().BeFalse();
        vba.AutoPushAfterApply.Should().BeFalse("既存の項目は変わらない");
        Graft.Core.Vba.VbaWorkbookInfoLevels.Parse(vba.WorkbookInfoState).Should().Be(Graft.Core.Vba.VbaWorkbookInfoLevel.Full);
    }

    [Fact(DisplayName = "ブックの情報の項目は、保存して読み直すと往復する。不明な出し方の文字列は Full として読まれる")]
    public async Task ブックの情報の項目が往復する()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var root = ws.CreateDirectory("app/vba-mirrors/計画-1a2b3c4d");
        var store = new ProjectStore(paths);
        var registered = await store.RegisterAsync(root, null);
        var withVba = registered.Value with
        {
            Vba = new VbaLink
            {
                WorkbookFullName = @"C:\Work\計画.xlsm", WorkbookInfoState = "StructureOnly",
                WorkbookInfoSampleValues = true, WorkbookInfoIncludeFormulas = true,
            },
        };
        await store.SaveAsync(new[] { withVba });

        var json = await File.ReadAllTextAsync(paths.ProjectsFilePath);
        var reloaded = (await new ProjectStore(paths).LoadAsync()).Value.Should().ContainSingle().Subject.Vba!;

        json.Should().Contain("\"workbookInfoState\": \"StructureOnly\"").And.Contain("\"workbookInfoSampleValues\": true");
        reloaded.WorkbookInfoState.Should().Be("StructureOnly");
        reloaded.WorkbookInfoSampleValues.Should().BeTrue();
        reloaded.WorkbookInfoIncludeFormulas.Should().BeTrue();
        Graft.Core.Vba.VbaWorkbookInfoLevels.Parse("壊れた値").Should().Be(Graft.Core.Vba.VbaWorkbookInfoLevel.Full);
    }

    [Fact(DisplayName = "保存時の自動反映（autoPushOnSave）を持たない既存の vba は、オフで読める。オンにすると往復する")]
    public async Task 保存時の自動反映は既定オフで往復する()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var root = ws.CreateDirectory("proj");
        await File.WriteAllTextAsync(paths.ProjectsFilePath, $$"""
            {
              "projects": [
                { "id": "p_000003", "name": "旧VBA", "root": "{{root.Replace("\\", "\\\\")}}",
                  "vba": { "workbookFullName": "C:\\Work\\計画.xlsm" } }
              ]
            }
            """);
        var store = new ProjectStore(paths);

        var old = (await store.LoadAsync()).Value.Should().ContainSingle().Subject;
        old.Vba!.AutoPushOnSave.Should().BeFalse("既定はオフ（キーが無い既存の projects.json を、勝手にオンにしない）");

        await store.SaveAsync(new[] { old with { Vba = old.Vba with { AutoPushOnSave = true } } });
        var json = await File.ReadAllTextAsync(paths.ProjectsFilePath);
        var reloaded = (await new ProjectStore(paths).LoadAsync()).Value.Should().ContainSingle().Subject.Vba!;

        json.Should().Contain("\"autoPushOnSave\": true");
        reloaded.AutoPushOnSave.Should().BeTrue();
        reloaded.AutoPushAfterApply.Should().BeTrue("ほかの項目の既定は変わらない");
    }
}
