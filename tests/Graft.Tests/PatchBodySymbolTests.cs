using System.Linq;
using FluentAssertions;
using Graft.Core;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 本文の中にパッチの記号が現れる場合の扱いのテスト。次の2つをまとめて固定する。
///
/// 1. Graft独自形式の FENCE= 指定: 終了マーカーの行以外は、記号に似た行も含めて本文としてそのまま取り込む。
///    FENCE なしの既定（エスケープ必須・E006・E008）は変えない。
/// 2. 標準SR形式: 本文にパッチの記号が混じって途切れた場合は、E303 などの無関係なエラーではなく
///    原因が分かる E010 で失敗させる。実機の事故（handoff.md の NEW_FILE の本文に書き方の説明例が入っていた）
///    を縮めた入力を <c>std_sr_honbun_kigou_e010</c> に置いてある。
/// </summary>
public class PatchBodySymbolTests
{
    // ------------------------------------------------------------------
    // FENCE=（Graft独自形式）
    // ------------------------------------------------------------------

    [Fact(DisplayName = "FENCE付きのFULL本文は、素の終了マーカー・区切り・ブロック開始行・先頭の\\をそのまま取り込む")]
    public void FENCE付きFULLは本文をそのまま取り込む()
    {
        var result = new PatchParser().Parse(FixtureLoader.LoadPatch("fence_honbun_sonomama"));

        result.IsSuccess.Should().BeTrue("修正前は素の \">>>> END\" や \"=======\" でE006/E008になっていた");
        var block = result.Value.Blocks.Should().ContainSingle().Subject.Should().BeOfType<FullContentBlock>().Subject;
        block.Content.Should().Be(string.Join('\n',
            "# 書き方", "<<<< FILE: x", "=======", ">>>> END", ">>>>>>> REPLACE", "\\<<<< 行頭のバックスラッシュも残る"));
    }

    [Fact(DisplayName = "FENCE付きのAPPEND本文に「<<<< FILE:」や別のFENCE値の終了行があっても取り込める")]
    public void FENCE付きAPPENDは本文をそのまま取り込む()
    {
        var result = new PatchParser().Parse(FixtureLoader.LoadPatch("fence_append_honbun_sonomama"));

        result.IsSuccess.Should().BeTrue("修正前は \"<<<< FILE: x\" でE008になっていた");
        var block = result.Value.Blocks.Should().ContainSingle().Subject.Should().BeOfType<AppendBlock>().Subject;
        block.Content.Should().Be("<<<< FILE: x\n=======\n>>>> END:Qx");
    }

    [Fact(DisplayName = "FENCE付きのPREPEND本文も同様にそのまま取り込む")]
    public void FENCE付きPREPENDは本文をそのまま取り込む()
    {
        var text = "<<<< PREPEND: a.txt FENCE=Z\n>>>> END\n\\=======\n>>>> END:Z\n";
        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeTrue();
        result.Value.Blocks.Should().ContainSingle().Subject.Should().BeOfType<PrependBlock>()
            .Which.Content.Should().Be(">>>> END\n\\=======");
    }

    [Fact(DisplayName = "FENCE付きの後ろに別ブロックが続いても、終了行の次から通常どおり読む")]
    public void FENCE付きの次のブロックを読める()
    {
        var text = "<<<< FILE: a.txt MODE=FULL FENCE=Q\n=======\n>>>> END:Q\n<<<< FILE: b.txt MODE=FULL\nb\n>>>> END\n";
        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeTrue();
        result.Value.Blocks.Should().HaveCount(2);
    }

    [Fact(DisplayName = "FENCEなしの既定は変わらない: 素の終了マーカーはE006、ブロック開始行はE008")]
    public void FENCEなしの既存動作は変わらない()
    {
        var e006 = new PatchParser().Parse("<<<< FILE: a.txt MODE=FULL\nx\n=======\ny\n>>>> END\n");
        e006.IsSuccess.Should().BeFalse();
        e006.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E006);

        var e008 = new PatchParser().Parse("<<<< APPEND: a.txt\nx\n<<<< FILE: x\n>>>> END\n");
        e008.IsSuccess.Should().BeFalse();
        e008.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E008);

        var escaped = new PatchParser().Parse("<<<< FILE: a.txt MODE=FULL\n\\=======\n>>>> END\n");
        escaped.Value.Blocks.Single().Should().BeOfType<FullContentBlock>().Which.Content.Should().Be("=======");
    }

    // ------------------------------------------------------------------
    // 標準SR形式の E010
    // ------------------------------------------------------------------

    [Fact(DisplayName = "実機の形: NEW_FILEの本文に説明例のブロックがあると、E303ではなくE010で失敗する")]
    public void 本文の説明例はE010になる()
    {
        var text = FixtureLoader.LoadPatch("std_sr_honbun_kigou_e010");
        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeFalse("修正前は複数ブロックとして成功し、後段でE303になっていた");
        result.Issues.Should().ContainSingle().Which.Code.Should().Be(ErrorCode.E010);
        ErrorCatalog.RemedyOf(ErrorCode.E010).Should().Contain("FENCE=");
    }

    [Fact(DisplayName = "本文が途切れたあと、仮のパス path/to/ のブロックが続くパッチは適用させない")]
    public void 仮のパスのブロックはE010になる()
    {
        var text = "handoff.md\n<<<<<<< NEW_FILE\n説明\n>>>>>>> END_FILE\n\npath/to/file\n<<<<<<< SEARCH\na\n=======\nb\n>>>>>>> REPLACE\n";
        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeFalse();
        var issue = result.Issues.Should().ContainSingle().Subject;
        issue.Code.Should().Be(ErrorCode.E010);
        issue.Detail.Should().Contain("path/to/file");
    }

    [Fact(DisplayName = "仮のパスではない説明例でも、本文の中にNEW_FILEの開始行があればE010になる")]
    public void 本文の中のNEW_FILE開始行はE010になる()
    {
        var text = "doc.md\n<<<<<<< NEW_FILE\n例:\n<<<<<<< NEW_FILE\n>>>>>>> END_FILE\n";
        var result = new PatchParser().Parse(text);

        result.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E010);
    }

    [Fact(DisplayName = "正しい複数ブロックは弾かない（本文にSEARCH/REPLACEの例だけを含むNEW_FILEも通常どおり取り込む）")]
    public void 正しいパッチは弾かれない()
    {
        var multi = "a.py\n<<<<<<< SEARCH\nx\n=======\ny\n>>>>>>> REPLACE\n\nb.md\n<<<<<<< NEW_FILE\nhello\n>>>>>>> END_FILE\n\nc.txt\n<<<<<<< DELETE_FILE\n>>>>>>> END_FILE\n";
        var ok = new PatchParser().Parse(multi);
        ok.IsSuccess.Should().BeTrue();
        ok.Value.Blocks.Should().HaveCount(3);

        var example = new PatchParser().Parse(FixtureLoader.LoadPatch("std_sr_honbun_kigou_search_dake"));
        example.IsSuccess.Should().BeTrue("END_FILE を伴わない SEARCH/REPLACE の例だけの本文は、従来どおり取り込める");
        example.Value.Blocks.Should().ContainSingle().Which.Path.Should().Be("handoff.md");
    }
}
