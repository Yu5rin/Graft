using FluentAssertions;
using Graft.Core;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// <see cref="ErrorCode"/>を1つ追加するたびに<see cref="ErrorCatalog"/>への登録を忘れる事故
/// （追加すればコンパイルは通るが、実際に使われた瞬間にErrorCatalog.SummaryOf/RemedyOfが
/// KeyNotFoundExceptionで落ちる）を機械的に検出する。依頼1〜3（E705・E706・E707）はいずれも
/// この形で登録漏れが無いことを保証されている。
/// </summary>
public class ErrorCatalogTests
{
    [Fact(DisplayName = "全てのErrorCodeがErrorCatalogに登録済みで、要約・対処のいずれも空でない")]
    public void 全エラーコードがカタログに登録されている()
    {
        foreach (var code in Enum.GetValues<ErrorCode>())
        {
            var summary = () => ErrorCatalog.SummaryOf(code);
            var remedy = () => ErrorCatalog.RemedyOf(code);

            summary.Should().NotThrow($"{code} はErrorCatalogに登録されている必要がある");
            remedy.Should().NotThrow($"{code} はErrorCatalogに登録されている必要がある");

            ErrorCatalog.SummaryOf(code).Should().NotBeNullOrWhiteSpace($"{code} の要約は空であってはならない");
            ErrorCatalog.RemedyOf(code).Should().NotBeNullOrWhiteSpace($"{code} の対処は空であってはならない");
        }
    }

    [Theory(DisplayName = "依頼1〜3で追加したエラーコード（E705・E706・E707）の内容が期待どおり")]
    [InlineData(ErrorCode.E705, "フォント")]
    [InlineData(ErrorCode.E706, "利用できない")]
    [InlineData(ErrorCode.E707, "ハイコントラスト")]
    public void 追加した3件のエラーコードの内容が期待どおり(ErrorCode code, string expectedSummaryFragment)
    {
        ErrorCatalog.SummaryOf(code).Should().Contain(expectedSummaryFragment);
    }
}
