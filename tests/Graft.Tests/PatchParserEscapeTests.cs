using FluentAssertions;
using Graft.Core;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// <see cref="PatchParser"/> のエスケープ規則（仕様書4.9）・FENCE指定に関する単体テスト。
/// </summary>
public class PatchParserEscapeTests
{
    [Fact(DisplayName = "エスケープされたマーカーは解除されマーカーとして再解釈されない")]
    public void エスケープ規則が解除される()
    {
        var text = FixtureLoader.LoadPatch("escape_kaijo");
        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeTrue("エスケープされた行はマーカーとして扱われず1つのFULLブロックのままのはず");
        result.Value.Blocks.Should().HaveCount(1, "エスケープ解除後の内容が新たなブロックとして再解釈されてはならない");

        var block = result.Value.Blocks[0].Should().BeOfType<FullContentBlock>().Subject;
        block.Content.Should().Contain("<<<< FILE: 相対パス", "先頭の\\が1つ取り除かれ本来の文字列が残るはず");
        block.Content.Should().Contain("<<<<<<< SEARCH");
        block.Content.Should().Contain("=======");
        block.Content.Should().Contain(">>>>>>> REPLACE");
        block.Content.Should().NotContain("\\<<<<", "エスケープ用のバックスラッシュは取り除かれているはず");
        block.Content.Should().NotContain("\\=======");
        block.Content.Should().NotContain("\\>>>>");
    }

    [Fact(DisplayName = "FENCE指定により終了マーカーを変更できる")]
    public void FENCE指定で終了マーカーを変更できる()
    {
        var text = FixtureLoader.LoadPatch("fence_shitei");
        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeTrue("FENCE指定に対応する終了マーカーで正しく閉じられているはず");
        result.Value.Blocks.Should().HaveCount(1);
        var block = result.Value.Blocks[0].Should().BeOfType<FullContentBlock>().Subject;
        block.Fence.Should().Be("abc123");
        block.Content.Should().Be("def hello():\n    print(\"hello\")");
    }

    [Fact(DisplayName = "未エスケープのマーカーらしき行が本文中に現れるとE006になる")]
    public void 未エスケープマーカーはE006になる()
    {
        var text = FixtureLoader.LoadPatch("mimikaeshi_marker_e006");
        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeFalse("本文中の未エスケープのマーカー様の行は構文破損として扱われるはず");
        result.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E006);
        result.Issues.Single(i => i.Code == ErrorCode.E006).LineNumber.Should().Be(4);
    }
}
