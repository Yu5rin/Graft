using FluentAssertions;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Platform;
using Graft.Platform.Null;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// <see cref="NullVbaBridge"/> と <see cref="NullPlatformServices"/> の Vba のテスト
/// （仕様書_VBA連携 3.1・5.1）。利用できない環境では例外を投げず、IsSupported=false を表明し、
/// 呼ばれたときは E706（この環境では使えない機能です）の失敗を返す。UI はこれを見て機能ごと隠す。
/// </summary>
public class NullVbaBridgeTests
{
    [Fact(DisplayName = "IsSupported は false で、理由（日本語）を持つ")]
    public void 非対応を表明する()
    {
        IVbaBridge bridge = new NullVbaBridge();

        bridge.IsSupported.Should().BeFalse();
        bridge.UnsupportedReason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact(DisplayName = "全メソッドが例外を投げず、E706 の失敗を返す")]
    public async Task 全メソッドがE706で失敗する()
    {
        IVbaBridge bridge = new NullVbaBridge();

        var list = await bridge.ListOpenWorkbooksAsync();
        var read = await bridge.ReadComponentsAsync(@"C:\a.xlsm", null);
        var write = await bridge.WriteComponentsAsync(
            @"C:\a.xlsm", new[] { new VbaComponentWrite("Module1", VbaModuleKind.Standard, "x", true) });
        var save = await bridge.SaveWorkbookAsync(@"C:\a.xlsm");
        var show = await bridge.ShowInVbeAsync(@"C:\a.xlsm", "Module1", 3);
        var activate = await bridge.ActivateWorkbookAsync(@"C:\a.xlsm");
        var info = await bridge.ReadWorkbookInfoAsync(
            @"C:\a.xlsm", new VbaWorkbookInfoOptions(true, VbaSheetViewSet.AllStructureOnly, 30, 60, 100, TimeSpan.FromSeconds(20)));

        foreach (var issues in new[] { list.Issues, read.Issues, write.Issues, save.Issues, show.Issues, activate.Issues, info.Issues })
        {
            issues.Should().ContainSingle().Which.Code.Should().Be(ErrorCode.E706);
        }

        new[] { list.IsSuccess, read.IsSuccess, write.IsSuccess, save.IsSuccess, show.IsSuccess, activate.IsSuccess, info.IsSuccess }
            .Should().AllBeEquivalentTo(false);
    }

    [Fact(DisplayName = "NullPlatformServices.Vba は NullVbaBridge で、DescribeEnvironment に Excel VBA連携 が含まれる")]
    public void NullPlatformServicesの配線()
    {
        var services = new NullPlatformServices();

        services.Vba.Should().BeOfType<NullVbaBridge>();
        services.Vba.IsSupported.Should().BeFalse();
        services.DescribeEnvironment().Should().Contain("Excel VBA連携");
    }

    [Fact(DisplayName = "書き込み結果の型: 失敗（RolledBack／Failed）には理由を持たせられる")]
    public void 書き込み結果の型()
    {
        var issue = GraftIssue.Of(ErrorCode.E809, "書き戻しに失敗");

        var outcome = new VbaWriteOutcome("Module1", VbaWriteStatus.Failed, null, issue);

        outcome.Issue!.Code.Should().Be(ErrorCode.E809);
        Enum.GetValues<VbaWriteStatus>().Should().BeEquivalentTo(
            new[] { VbaWriteStatus.Written, VbaWriteStatus.Created, VbaWriteStatus.RolledBack, VbaWriteStatus.Failed });
        Enum.GetValues<VbaWorkbookAvailability>().Should().BeEquivalentTo(new[]
        {
            VbaWorkbookAvailability.Available, VbaWorkbookAvailability.NoMacroFormat, VbaWorkbookAvailability.Locked,
            VbaWorkbookAvailability.AccessNotTrusted, VbaWorkbookAvailability.UrlLocation, VbaWorkbookAvailability.ProtectedView,
            VbaWorkbookAvailability.Unresponsive, // P2 で追加（一覧の取得中に Excel が応答しないブック）。
        });
    }
}
