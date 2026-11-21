using FluentAssertions;
using Graft.Core;
using Graft.Platform;
using Graft.UiTests.Vba;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// 「疑わしい点」の修正: <c>OpenProject</c>（読み書きの前提検査）でも、保護ビュー・URL のブックを選択画面と同じ判定で弾く。
/// 修正前は選択画面だけが弾き、追加した後にそうなったブックは <c>VBProject</c> の取得で失敗して E802
/// （トラストセンターの案内）と誤診される見込みだった。
/// </summary>
public class VbaWorkbookAvailabilityFixTests
{
    private const string Book = @"C:\Work\計画.xlsm";

    [Theory(DisplayName = "前提検査: URL・保護ビューは E801 に、選択画面と同じ理由の文言で弾く。それ以外の状態は別の前提検査の仕事なので null")]
    [InlineData(VbaWorkbookAvailability.UrlLocation, true)]
    [InlineData(VbaWorkbookAvailability.ProtectedView, true)]
    [InlineData(VbaWorkbookAvailability.Available, false)]
    [InlineData(VbaWorkbookAvailability.AccessNotTrusted, false)]
    [InlineData(VbaWorkbookAvailability.Locked, false)]
    [InlineData(VbaWorkbookAvailability.Unresponsive, false)]
    [InlineData(VbaWorkbookAvailability.NoMacroFormat, false)]
    public void 弾く状態(VbaWorkbookAvailability availability, bool blocked)
    {
        var issue = VbaWorkbookAvailabilityIssues.ToIssue(availability, Book);

        if (!blocked)
        {
            issue.Should().BeNull();
            return;
        }

        issue!.Code.Should().Be(ErrorCode.E801);
        issue.Detail.Should().Contain(availability == VbaWorkbookAvailability.UrlLocation
            ? VbaWorkbookAvailabilityIssues.UrlLocationReason
            : VbaWorkbookAvailabilityIssues.ProtectedViewReason);
        issue.Path.Should().Be(Book);
    }

    [Fact(DisplayName = "前提検査: 追加したあとに保護ビューになったブックの読み取りは、E802 ではなく保護ビューの理由で止まる（Fake も本物と同じ検査）")]
    public async Task 保護ビューは読み取りでも弾く()
    {
        var bridge = new FakeVbaBridge();
        bridge.AddWorkbook(Book, FakeVbaBridge.Std("Module1", "x")).Availability = VbaWorkbookAvailability.ProtectedView;

        var read = await bridge.ReadComponentsAsync(Book, null);
        var write = await bridge.WriteComponentsAsync(Book, new[] { new VbaComponentWrite("Module1", Graft.Core.Vba.VbaModuleKind.Standard, "y", false) });

        read.IsSuccess.Should().BeFalse();
        read.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E801 && i.Detail!.Contains("保護ビュー"));
        write.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E801);
        bridge.Get(Book)!.Find("Module1")!.Code.Should().Be("x");
    }
}
