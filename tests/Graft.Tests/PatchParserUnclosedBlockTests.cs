using FluentAssertions;
using Graft.Core;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 案件1の回帰テスト。実際に起きた事故（PATCHメタが二重に出力され、1つ目が"&gt;&gt;&gt;&gt;"で
/// 閉じられていない出力）を、当時と同じ入力で再現し、エラーメッセージが「閉じられていない
/// ブロックの開始行」と「次のブロックが始まった行」の両方を伝えることを確認する。
/// 併せて、FULL本文・SEARCH本文の途中で新しいブロックが始まった場合も同様に確認する。
/// </summary>
public class PatchParserUnclosedBlockTests
{
    [Fact(DisplayName = "回帰_実際の事故（PATCHメタの二重出力・1つ目が未閉鎖）で両方の行番号を伝える")]
    public void PATCHメタが二重に出力され1つ目が未閉鎖の場合()
    {
        // docs化された実際の事故入力（1行目のPATCHメタが>>>>で閉じられないまま、
        // 5行目で2つ目のPATCHメタが始まる）をそのまま使う。
        var text = FixtureLoader.LoadPatch("patch_meta_nijuu_mikaiho");
        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeFalse("1つ目のPATCHメタが閉じられていないため構文破損として扱われるはず");
        result.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E008,
            "「新しいブロックが始まった」ことを伝える専用コードE008になるはず（従来はE006で原因が伝わらなかった）");

        var issue = result.Issues.Single(i => i.Code == ErrorCode.E008);
        issue.LineNumber.Should().Be(5, "新しいブロックが始まった行（2つ目の\"<<<< PATCH\"）");
        issue.Detail.Should().NotBeNull();
        issue.Detail!.Should().Contain("1行目", "閉じられていないブロックの開始行が伝わっているはず");
        issue.Detail!.Should().Contain("5行目", "次のブロックが始まった行が伝わっているはず");
        issue.Detail!.Should().Contain("<<<< PATCH", "どのブロックの話かが分かるよう、マーカーの内容も含まれているはず");
        issue.Detail!.Should().Contain("\\", "エスケープでの回避策の案内も失われていないはず（E006の案内を引き継ぐ）");
    }

    [Fact(DisplayName = "回帰_FULL本文の途中で新しいブロックが始まった場合も両方の行番号を伝える")]
    public void FULL本文の途中で新しいブロックが始まった場合()
    {
        var text =
            "<<<< FILE: src/new_module.py MODE=FULL\n" +   // 1行目: 開始
            "def foo():\n" +                                // 2行目
            "    pass\n" +                                  // 3行目
            "<<<< FILE: src/other.py MODE=FULL\n" +          // 4行目: 閉じ忘れたまま次のブロックが開始
            "def bar():\n" +
            "    pass\n" +
            ">>>> END\n";
        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeFalse();
        result.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E008);
        var issue = result.Issues.Single(i => i.Code == ErrorCode.E008);
        issue.LineNumber.Should().Be(4);
        issue.Detail!.Should().Contain("1行目");
        issue.Detail!.Should().Contain("4行目");
        issue.Detail!.Should().Contain(">>>> END", "FULL本文の期待する終了マーカーが案内されているはず");
    }

    [Fact(DisplayName = "回帰_SEARCH本文の途中で新しいブロックが始まった場合も両方の行番号を伝える")]
    public void SEARCH本文の途中で新しいブロックが始まった場合()
    {
        var text =
            "<<<< FILE: src/app.py\n" +
            "<<<<<<< SEARCH\n" +              // 2行目: SEARCH開始
            "def greet(name):\n" +             // 3行目
            "<<<< FILE: src/other.py\n" +      // 4行目: SEARCHが閉じ忘れたまま次のブロックが開始
            "<<<<<<< SEARCH\n" +
            "old\n" +
            "=======\n" +
            "new\n" +
            ">>>>>>> REPLACE\n";
        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeFalse();
        result.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E008);
        var issue = result.Issues.Single(i => i.Code == ErrorCode.E008);
        issue.LineNumber.Should().Be(4);
        issue.Detail!.Should().Contain("2行目", "SEARCHが開始した行が伝わっているはず");
        issue.Detail!.Should().Contain("4行目");
        issue.Detail!.Should().Contain("=======", "SEARCH本文が期待する終了マーカーが案内されているはず");
    }

    [Fact(DisplayName = "回帰_REPLACE本文の途中で新しいブロックが始まった場合はSEARCH/REPLACEの区切り行を開始行として伝える")]
    public void REPLACE本文の途中で新しいブロックが始まった場合()
    {
        var text =
            "<<<< FILE: src/app.py\n" +
            "<<<<<<< SEARCH\n" +
            "old\n" +
            "=======\n" +                       // 4行目: REPLACE開始（区切り行）
            "new\n" +                            // 5行目
            "<<<< FILE: src/other.py MODE=FULL\n" + // 6行目: REPLACEが閉じ忘れたまま次のブロックが開始
            "content\n" +
            ">>>> END\n";
        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeFalse();
        result.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E008);
        var issue = result.Issues.Single(i => i.Code == ErrorCode.E008);
        issue.LineNumber.Should().Be(6);
        issue.Detail!.Should().Contain("4行目", "REPLACE本文が開始した行（=======）が伝わっているはず");
        issue.Detail!.Should().Contain("6行目");
    }
}
