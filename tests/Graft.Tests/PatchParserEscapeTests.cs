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

    [Fact(DisplayName = "未エスケープの \"<<<<\" 始まりの行が本文中に現れるとE008（新しいブロックの開始）になる")]
    public void 未エスケープマーカーはE008になる()
    {
        // mimikaeshi_marker_e006.txt の4行目「<<<< THIS LOOKS LIKE A HEADER」は"<<<<"始まりのため、
        // 「新しいブロックが始まった」と解釈できる行として扱われE008になる（従来はE006だった。
        // 詳細はPatchParser.BrokenBodyFailureのコメントと、実際の事故を再現した
        // PatchParserUnclosedBlockTests参照）。ファイル名は変更しない（フィクスチャの使い回し）。
        var text = FixtureLoader.LoadPatch("mimikaeshi_marker_e006");
        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeFalse("本文中の未エスケープのマーカー様の行は構文破損として扱われるはず");
        result.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E008);
        result.Issues.Single(i => i.Code == ErrorCode.E008).LineNumber.Should().Be(4);
    }

    [Fact(DisplayName = "\">>>>\" や \"=======\" だけの未エスケープ行はE006のまま（新しいブロックの開始ではないため）")]
    public void 閉じマーカー様の未エスケープ行はE006のまま()
    {
        // FULL本文の途中に、どの終了マーカーにも一致しない ">>>> " 始まりの行が現れるケース。
        // "<<<<"始まりではないため「次のブロックが始まった」とは判定せず、従来どおり
        // エスケープ忘れとしてE006を返す（PatchParser.BrokenBodyFailure参照）。
        var text =
            "<<<< FILE: src/broken.py MODE=FULL\n" +
            "def foo():\n" +
            ">>>> NOT A REAL TERMINATOR\n" +
            ">>>> END\n";
        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeFalse();
        result.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E006);
        result.Issues.Single(i => i.Code == ErrorCode.E006).LineNumber.Should().Be(3);
    }
}
