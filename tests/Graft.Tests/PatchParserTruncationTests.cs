using FluentAssertions;
using Graft.Core;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// <see cref="PatchParser"/> の切断検出（仕様書4.10・E005）に関する単体テスト。
/// </summary>
public class PatchParserTruncationTests
{
    [Fact(DisplayName = "終了マーカー欠落は切断(E005)として扱われ途中までのブロックが保持される")]
    public void 終了マーカー欠落は切断として扱われる()
    {
        var text = FixtureLoader.LoadPatch("setsudan_e005");
        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeTrue("切断は警告として扱われ、解析自体は成功として返るはず（4.10）");
        result.Value.IsTruncated.Should().BeTrue();
        result.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E005);
        result.Issues.Single(i => i.Code == ErrorCode.E005).Severity.Should().Be(Severity.Warning);

        result.Value.Blocks.Should().HaveCount(1, "切断より前に完全に解析済みだったブロックは保持されるはず");
        result.Value.Blocks[0].Path.Should().Be("src/complete.py");

        result.Value.TailLines.Should().NotBeEmpty("継続依頼プロンプト用の末尾行が記録されるはず");
    }
}
