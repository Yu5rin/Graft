using System.IO;
using FluentAssertions;
using Graft.Core.Vba;
using Graft.Features;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// リボン（customUI）の出し方（<see cref="VbaLink.RibbonState"/>）の記憶と、projects.json の後方互換のテスト
/// （仕様書_ブックの情報と依頼の観点 4.1・15.5・15.8）。
/// </summary>
public class VbaRibbonStateTests
{
    [Fact(DisplayName = "ribbonState を持たない既存の vba は、「内容も出す」（既定）で読める。ブックの情報の出し方とは独立")]
    public async Task 旧形式は既定で読める()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var root = ws.CreateDirectory("proj");
        await File.WriteAllTextAsync(paths.ProjectsFilePath, $$"""
            {
              "projects": [
                { "id": "p_000001", "name": "旧VBA", "root": "{{root.Replace("\\", "\\\\")}}",
                  "vba": { "workbookFullName": "C:\\Work\\計画.xlsm", "workbookInfoState": "Hidden" } }
              ]
            }
            """);

        var vba = (await new ProjectStore(paths).LoadAsync()).Value.Should().ContainSingle().Subject.Vba!;

        vba.RibbonState.Should().Be("Full");
        VbaWorkbookInfoLevels.Parse(vba.RibbonState).Should().Be(VbaWorkbookInfoLevel.Full);
        VbaWorkbookInfoLevels.Parse(vba.WorkbookInfoState).Should().Be(VbaWorkbookInfoLevel.Hidden, "ブックの情報の出し方は変わらない");
    }

    [Fact(DisplayName = "リボンの出し方は保存して読み直すと往復する。不明な文字列は Full として読まれる")]
    public async Task 往復する()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var root = ws.CreateDirectory("app/vba-mirrors/計画-1a2b3c4d");
        var store = new ProjectStore(paths);
        var registered = await store.RegisterAsync(root, null);
        await store.SaveAsync(new[] { registered.Value with { Vba = new VbaLink { WorkbookFullName = @"C:\Work\計画.xlsm", RibbonState = "StructureOnly" } } });

        var json = await File.ReadAllTextAsync(paths.ProjectsFilePath);
        var reloaded = (await new ProjectStore(paths).LoadAsync()).Value.Should().ContainSingle().Subject.Vba!;

        json.Should().Contain("\"ribbonState\": \"StructureOnly\"");
        reloaded.RibbonState.Should().Be("StructureOnly");
        VbaWorkbookInfoLevels.Parse("壊れた値").Should().Be(VbaWorkbookInfoLevel.Full);
    }

    [Fact(DisplayName = "別のブックへ結び直しても、リボンの出し方は変えない（利用者の好みで、ブックの中身ではない）")]
    public void 結び直しでは変わらない()
    {
        var link = new VbaLink { WorkbookFullName = @"C:\Work\A.xlsm", RibbonState = "Hidden" };

        link.WithWorkbook(@"C:\Work\B.xlsm").RibbonState.Should().Be("Hidden");
    }
}
