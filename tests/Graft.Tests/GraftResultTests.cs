using FluentAssertions;
using Graft.Core;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// GraftResult&lt;T&gt;.HasIssueの検証。
///
/// 過去に、Severity.Warningとして発行されるE301（適用後の変更検出）を、呼び出し側
/// （HistoryPaneViewModel.RestoreAsync/RestoreThroughAsync）が
/// <c>result.Errors.Any(i =&gt; i.Code == ErrorCode.E301)</c> で検出しようとしていた。
/// しかしErrorsはSeverity.Errorのみを対象とするため、Warningとして発行されたE301は
/// 決して見つからず、「上書きして続行しますか？」の確認ダイアログへ到達できない
/// （＝分岐が死んでいた）不具合があった。
///
/// この不具合を再発させないため、Errorsでは検出できないこと・HasIssueなら深刻度を問わず
/// 検出できることの両方を直接検証する。
/// </summary>
public class GraftResultTests
{
    [Fact(DisplayName = "根本原因の再発防止: Severity.Warningで発行された問題はErrorsでは検出できないが、HasIssueなら検出できる")]
    public void Warning問題はHasIssueで検出できるがErrorsでは検出できない()
    {
        var result = GraftResult<string>.Fail(
            GraftIssue.Of(ErrorCode.E301, "適用後にさらに変更されています", severity: Severity.Warning));

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().NotContain(i => i.Code == ErrorCode.E301,
            "ErrorsはSeverity.Errorのみを対象とするため、Warningで発行されたE301は含まれないはず" +
            "（この前提を見落として.Errors.Any(...)で分岐すると、その分岐は決して真にならず死ぬ）");
        result.HasIssue(ErrorCode.E301).Should().BeTrue(
            "HasIssueは深刻度を問わずコードの有無を判定するため、Warningでも検出できるはず");
    }

    [Fact(DisplayName = "HasIssueは深刻度がErrorの問題も検出できる")]
    public void Error問題もHasIssueで検出できる()
    {
        var result = GraftResult<string>.Fail(GraftIssue.Of(ErrorCode.E405, "実体が見つかりません"));

        result.HasIssue(ErrorCode.E405).Should().BeTrue();
        result.Errors.Should().Contain(i => i.Code == ErrorCode.E405);
    }

    [Fact(DisplayName = "HasIssueは該当コードが無ければfalseを返す")]
    public void 該当コードが無ければfalse()
    {
        var result = GraftResult<string>.Fail(GraftIssue.Of(ErrorCode.E405, "実体が見つかりません"));

        result.HasIssue(ErrorCode.E301).Should().BeFalse();
    }
}
