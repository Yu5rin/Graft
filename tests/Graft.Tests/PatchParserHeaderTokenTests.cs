using System.Linq;
using FluentAssertions;
using Graft.Core;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 実機で報告された事故の再現テスト（PatchParser.ParseHeaderTokens関連）。
///
/// 【事故の要約】利用者が "&lt;&lt;&lt;&lt; FILE: graft-indent-probe.txt MODE=DELETE" という
/// （Graftに存在しない）ヘッダを貼り付けたところ、未知の属性トークン "MODE=DELETE" が
/// 黙って捨てられ、isFull=false のまま SEARCH ペアを探し続けて入力が尽き、
/// 「パッチが途中で切れている」（E005・TruncatedSignal）という誤った診断になっていた。
/// 実際には1文字も欠けていないため、利用者は「AIに続きを依頼する」という誤った対処へ
/// 誘導されてしまう。このファイルは、未知の属性トークンをE002として即座に・正しい案内文で
/// 打ち切るようにした修正を固定する。
/// </summary>
public class PatchParserHeaderTokenTests
{
    [Fact(DisplayName = "MODE=DELETEは切断ではなくE002になり案内にDELETE:ヘッダを含む")]
    public void MODE_DELETEはE002になり案内にDELETEヘッダを含む()
    {
        var text = """
            <<<< PATCH
            summary: インデント保全の検証用ファイルを削除する
            type: chore
            >>>>

            <<<< FILE: graft-indent-probe.txt MODE=DELETE
            >>>> END
            """;

        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeFalse("MODE=DELETEという未知の値は、切断ではなく即座にE002で打ち切られるはず");
        result.Issues.Should().NotContain(i => i.Code == ErrorCode.E005, "切断扱い（切断のはずが本当は切れていない）に逃げてはならない");
        result.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E002);
        var issue = result.Issues.Single(i => i.Code == ErrorCode.E002);
        issue.Detail.Should().Contain("<<<< DELETE:", "利用者が次に使うべき正しいヘッダをその場で案内するはず");
    }

    [Fact(DisplayName = "DELETE_ヘッダは従来どおりDeleteBlockとして解析される")]
    public void DELETEヘッダは従来どおり解析される()
    {
        var text = """
            <<<< PATCH
            summary: インデント保全の検証用ファイルを削除する
            type: chore
            >>>>

            <<<< DELETE: graft-indent-probe.txt
            """;

        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsTruncated.Should().BeFalse();
        result.Value.Blocks.Should().HaveCount(1);
        result.Value.Blocks[0].Should().BeOfType<DeleteBlock>()
            .Which.Path.Should().Be("graft-indent-probe.txt");
    }

    [Fact(DisplayName = "MODE_fullは小文字の綴りとしてE002になり案内に大文字表記を含む")]
    public void MODEが小文字の場合はE002になり大文字の案内を含む()
    {
        var text = """
            <<<< FILE: src/new_module.py MODE=full
            def new_function():
                return 42
            >>>> END
            """;

        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeFalse();
        result.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E002);
        var issue = result.Issues.Single(i => i.Code == ErrorCode.E002);
        issue.Detail.Should().Contain("大文字", "別の操作との取り違えではなく綴りの問題だと伝えるはず");
        issue.Detail.Should().Contain("MODE=FULL");
    }

    [Fact(DisplayName = "未知のキーはE002になり使える属性の一覧とトークン自体を含む")]
    public void 未知のキーはE002になり属性の一覧を含む()
    {
        var text = """
            <<<< FILE: src/a.py
            <<<<<<< SEARCH OCCURENCE=2
            foo
            =======
            bar
            >>>>>>> REPLACE
            """;

        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeFalse();
        result.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E002);
        var issue = result.Issues.Single(i => i.Code == ErrorCode.E002);
        issue.Detail.Should().Contain("OCCURENCE=2", "綴り間違いに気づけるよう見つかったトークンをそのまま含めるはず");
        issue.Detail.Should().Contain("MODE=FULL");
        issue.Detail.Should().Contain("FENCE=");
        issue.Detail.Should().Contain("OCCURRENCE=");
    }

    [Fact(DisplayName = "空白を含むパスはE002になり案内に空白の注意を含む")]
    public void 空白を含むパスはE002になり空白の案内を含む()
    {
        var text = """
            <<<< FILE: My Folder/a.js
            <<<<<<< SEARCH
            foo
            =======
            bar
            >>>>>>> REPLACE
            """;

        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeFalse();
        result.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E002);
        var issue = result.Issues.Single(i => i.Code == ErrorCode.E002);
        issue.Detail.Should().Contain("空白", "先頭トークンだけがパスとして扱われるため、空白入りパスの可能性を案内するはず");
    }

    [Fact(DisplayName = "SEARCHマーカー行の未知の属性はE002になりパスの空白案内にはならない")]
    public void SEARCHマーカー行の未知属性はE002になり空白案内にはならない()
    {
        var text = """
            <<<< FILE: src/a.py
            <<<<<<< SEARCH FOO
            foo
            =======
            bar
            >>>>>>> REPLACE
            """;

        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeFalse();
        result.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E002);
        var issue = result.Issues.Single(i => i.Code == ErrorCode.E002);
        issue.Detail.Should().NotContain("空白", "SEARCHマーカー行はパスを持たないため、空白の案内は成立しないはず");
        issue.Detail.Should().Contain("MODE=FULL");
    }

    [Fact(DisplayName = "MODE=FULL_FENCE=_OCCURRENCE=_説明文は従来どおり解析できる")]
    public void 既知の属性は従来どおり解析できる()
    {
        var fullText = """
            <<<< FILE: src/generated.py MODE=FULL FENCE=abc123 # 生成ファイル
            def hello():
                print("hello")
            >>>> END:abc123
            """;
        var fullResult = new PatchParser().Parse(fullText);
        fullResult.IsSuccess.Should().BeTrue();
        var fullBlock = fullResult.Value.Blocks[0].Should().BeOfType<FullContentBlock>().Subject;
        fullBlock.Fence.Should().Be("abc123");
        fullBlock.Description.Should().Be("生成ファイル");

        var occurrence2Text = """
            <<<< FILE: src/a.py
            <<<<<<< SEARCH OCCURRENCE=2 # 2件目だけ
            foo
            =======
            bar
            >>>>>>> REPLACE
            """;
        var occurrence2Result = new PatchParser().Parse(occurrence2Text);
        occurrence2Result.IsSuccess.Should().BeTrue();
        var occurrence2Block = occurrence2Result.Value.Blocks[0].Should().BeOfType<SearchReplaceBlock>().Subject;
        occurrence2Block.Occurrence.Index.Should().Be(2);
        occurrence2Block.Pairs[0].Description.Should().Be("2件目だけ");

        var occurrenceAllText = """
            <<<< FILE: src/a.py
            <<<<<<< SEARCH OCCURRENCE=ALL
            foo
            =======
            bar
            >>>>>>> REPLACE
            """;
        var occurrenceAllResult = new PatchParser().Parse(occurrenceAllText);
        occurrenceAllResult.IsSuccess.Should().BeTrue();
        occurrenceAllResult.Value.Blocks[0].Should().BeOfType<SearchReplaceBlock>()
            .Which.Occurrence.All.Should().BeTrue();
    }

    [Fact(DisplayName = "説明文に_#_を付け忘れた場合はE002の案内に_#_の付け方を含む")]
    public void 説明文のシャープ忘れはEに案内を含む()
    {
        // 【なぜこの案内が必要か】既定テンプレートは説明文を
        // "<<<<<<< SEARCH  # このペアの変更内容を1行で" と "#" 付きで書くよう指示しているが、
        // AIが "#" を落として出力することは十分ありうる。以前は未知のトークンを黙って
        // 捨てていたためこの形でも通っていたので、今回の修正はここだけ後方互換を狭めている。
        // 「綴りを確認してください」だけでは "#" を付ければよいと気づけないため、
        // 案内に必ず "#" の付け方を含めることを固定する。
        var searchText = """
            <<<< FILE: src/a.py
            <<<<<<< SEARCH ここを直す
            foo
            =======
            bar
            >>>>>>> REPLACE
            """;

        var searchResult = new PatchParser().Parse(searchText);

        searchResult.IsSuccess.Should().BeFalse();
        var searchIssue = searchResult.Issues.Single(i => i.Code == ErrorCode.E002);
        searchIssue.Detail.Should().Contain("#", "\"#\" を付ければ説明文として通ることを案内するはず");

        // パスを取るヘッダ側でも同じ取り違えが起こりうるため、そちらにも案内を含める。
        var fileText = """
            <<<< FILE: src/a.py ここを直す
            <<<<<<< SEARCH
            foo
            =======
            bar
            >>>>>>> REPLACE
            """;

        var fileResult = new PatchParser().Parse(fileText);

        fileResult.IsSuccess.Should().BeFalse();
        var fileIssue = fileResult.Issues.Single(i => i.Code == ErrorCode.E002);
        fileIssue.Detail.Should().Contain("空白", "パスを取るヘッダではパスの空白が最も多い原因のため、まずそれを案内するはず");
        fileIssue.Detail.Should().Contain("#", "説明文を書きたかった場合に備えて \"#\" の付け方も案内するはず");
    }
}
