using System.Linq;
using FluentAssertions;
using Graft.Core;
using Graft.Features;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// AIが「このファイルも見せて」と求める合図（<c>NEED_MORE_CONTEXT</c>）の判定のテスト。
///
/// 【このテストを足した経緯】Graft独自形式のプロンプトテンプレートは、AIに
/// 「NEED_MORE_CONTEXT: &lt;ファイルパス&gt;」の1行を返すよう指示していた。ところが判定は
/// 「NEED_MORE_CONTEXT という語だけの1行」にしか反応せず、指示どおりパス付きで返ってきた
/// 回答はE710にならずE001（ブロックが存在しない）になっていた。クリップボード監視でも
/// 検知されなかった。パス付き・複数行・各種の書き癖を受け付けつつ、誤検知の防止
/// （ほかの内容が1行でも混じれば検知しない／テンプレート本文を検知しない）を
/// 保っていることをここで固定する。
/// </summary>
public class NeedMoreContextTests
{
    // ------------------------------------------------------------------
    // 検知する形
    // ------------------------------------------------------------------

    [Theory(DisplayName = "NEED_MORE_CONTEXTは語だけ・パス付き1行・複数行・バッククォート囲み・フェンス付きのどれでも検知する")]
    [InlineData("NEED_MORE_CONTEXT")]
    [InlineData("NEED_MORE_CONTEXT\n")]
    [InlineData("NEED_MORE_CONTEXT: src/Graft/Core/PatchParser.cs")]
    [InlineData("NEED_MORE_CONTEXT: src/Graft/Core/PatchParser.cs\n")]
    [InlineData("NEED_MORE_CONTEXT: src/a.cs\nNEED_MORE_CONTEXT: src/b.cs\n")]
    [InlineData("NEED_MORE_CONTEXT: `src/a.cs`")]
    [InlineData("`NEED_MORE_CONTEXT: src/a.cs`")]
    [InlineData("```text\nNEED_MORE_CONTEXT: src/a.cs\n```\n")]
    [InlineData("```text\nNEED_MORE_CONTEXT: src/a.cs\nNEED_MORE_CONTEXT: src/b.cs\n```\n")]
    [InlineData("````\nNEED_MORE_CONTEXT\n````")]
    [InlineData("NEED_MORE_CONTEXT : src/a.cs")]
    [InlineData("NEED_MORE_CONTEXT:src/a.cs")]
    [InlineData("  NEED_MORE_CONTEXT:   src/a.cs   ")]
    [InlineData("NEED_MORE_CONTEXT：src/a.cs")]
    [InlineData("NEED_MORE_CONTEXT: src/a.cs\r\nNEED_MORE_CONTEXT: src/b.cs\r\n")]
    public void 情報不足の合図はどの書き方でも検知する(string text)
        => StandardSearchReplaceAdapter.IsNeedMoreContext(text).Should().BeTrue();

    [Fact(DisplayName = "パス付きのNEED_MORE_CONTEXTはパーサーでE710になり、E001（ブロックが存在しない）にならない")]
    public void パス付きの合図はE710になる()
    {
        var result = new PatchParser().Parse("NEED_MORE_CONTEXT: src/a.cs\n");

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Code.Should().Be(ErrorCode.E710,
            "指示どおりパス付きで返ってきた回答を『ブロックが存在しない』と取り違えてはいけないため");
    }

    [Theory(DisplayName = "パス付きのNEED_MORE_CONTEXTはクリップボード監視でも検知する")]
    [InlineData("NEED_MORE_CONTEXT: src/a.cs\n")]
    [InlineData("```text\nNEED_MORE_CONTEXT: src/a.cs\nNEED_MORE_CONTEXT: src/b.cs\n```\n")]
    public void パス付きの合図はクリップボード監視で検知する(string text)
        => PatchTextDetector.LooksLikePatch(text).Should().BeTrue();

    // ------------------------------------------------------------------
    // 求められたパスの取り出し（コンテキスト収集への反映に使う）
    // ------------------------------------------------------------------

    [Fact(DisplayName = "パス付き1行から、求められたパスを取り出せる")]
    public void パス付き1行からパスを取り出せる()
    {
        StandardSearchReplaceAdapter.TryParseNeedMoreContext("NEED_MORE_CONTEXT: src/Graft/Core/PatchParser.cs\n", out var paths)
            .Should().BeTrue();

        paths.Should().Equal("src/Graft/Core/PatchParser.cs");
    }

    [Fact(DisplayName = "複数行（1行に1ファイル）から、求められたパスを出現順に取り出せる")]
    public void 複数行から出現順にパスを取り出せる()
    {
        var text = "NEED_MORE_CONTEXT: src/b.cs\nNEED_MORE_CONTEXT: src/a.cs\nNEED_MORE_CONTEXT: tests/c.cs\n";

        StandardSearchReplaceAdapter.TryParseNeedMoreContext(text, out var paths).Should().BeTrue();

        paths.Should().Equal("src/b.cs", "src/a.cs", "tests/c.cs");
    }

    [Fact(DisplayName = "語だけの合図は検知するがパスは空")]
    public void 語だけの合図はパスが空()
    {
        StandardSearchReplaceAdapter.TryParseNeedMoreContext("NEED_MORE_CONTEXT\n", out var paths).Should().BeTrue();

        paths.Should().BeEmpty("どのファイルが欲しいかは書かれていない");
    }

    [Theory(DisplayName = "バッククォート囲み・フェンス付き・区切りの空白の揺れがあっても同じパスを取り出せる")]
    [InlineData("NEED_MORE_CONTEXT: `src/a.cs`")]
    [InlineData("`NEED_MORE_CONTEXT: src/a.cs`")]
    [InlineData("```text\nNEED_MORE_CONTEXT: src/a.cs\n```\n")]
    [InlineData("````\nNEED_MORE_CONTEXT : src/a.cs\n````")]
    [InlineData("NEED_MORE_CONTEXT:src/a.cs")]
    [InlineData("NEED_MORE_CONTEXT：   src/a.cs   ")]
    [InlineData("NEED_MORE_CONTEXT: \"src/a.cs\"")]
    [InlineData("\tNEED_MORE_CONTEXT:\tsrc/a.cs\r\n")]
    public void 書き癖があっても同じパスを取り出せる(string text)
    {
        StandardSearchReplaceAdapter.TryParseNeedMoreContext(text, out var paths).Should().BeTrue();

        paths.Should().Equal("src/a.cs");
    }

    [Fact(DisplayName = "同じパスの重複は1つにまとめ、語だけの行とパス付きの行が混ざってもパス付きだけ取り出す")]
    public void 重複は1つにまとめ語だけの行は無視する()
    {
        var text = "NEED_MORE_CONTEXT: a.cs\nNEED_MORE_CONTEXT\nNEED_MORE_CONTEXT: a.cs\nNEED_MORE_CONTEXT: b.cs";

        StandardSearchReplaceAdapter.TryParseNeedMoreContext(text, out var paths).Should().BeTrue();

        paths.Should().Equal("a.cs", "b.cs");
    }

    [Fact(DisplayName = "指示文のプレースホルダ<ファイルパス>をそのまま返されても、実在しないパスを要求されたことにしない")]
    public void プレースホルダのままのパスは要求として扱わない()
    {
        StandardSearchReplaceAdapter.TryParseNeedMoreContext("NEED_MORE_CONTEXT: <ファイルパス>", out var paths).Should().BeTrue();

        paths.Should().BeEmpty();
    }

    [Fact(DisplayName = "検知しなかったときは、パスも空で返す")]
    public void 検知しないときパスは空()
    {
        StandardSearchReplaceAdapter.TryParseNeedMoreContext("説明\nNEED_MORE_CONTEXT: src/a.cs", out var paths).Should().BeFalse();

        paths.Should().BeEmpty();
    }

    [Fact(DisplayName = "E710には要求されたファイルの一覧が表示用の文面と構造化された一覧の両方で載る")]
    public void E710に要求されたファイルの一覧が載る()
    {
        var result = new PatchParser().Parse("```text\nNEED_MORE_CONTEXT: src/a.cs\nNEED_MORE_CONTEXT: src/b.cs\n```\n");

        var issue = result.Errors.Should().ContainSingle().Subject;
        issue.Code.Should().Be(ErrorCode.E710);
        issue.RequestedPaths.Should().Equal("src/a.cs", "src/b.cs");
        issue.Detail.Should().Contain("src/a.cs").And.Contain("src/b.cs");
        issue.ToDisplayText().Should().Contain("src/a.cs", "利用者が見るエラー表示にも載せる");
    }

    [Fact(DisplayName = "E710の表示文は多数のファイルを要求されても先頭だけ並べて件数にまとめ、構造化された一覧は全件を持つ")]
    public void E710の表示文は多数なら省略し一覧は全件を持つ()
    {
        var lines = Enumerable.Range(1, 12).Select(i => $"NEED_MORE_CONTEXT: src/f{i}.cs");

        var issue = new PatchParser().Parse(string.Join("\n", lines)).Errors.Single();

        issue.RequestedPaths.Should().HaveCount(12);
        issue.Detail.Should().Contain("src/f1.cs").And.Contain("ほか4件").And.NotContain("src/f12.cs");
    }

    [Fact(DisplayName = "語だけの合図のE710は、従来どおり一覧を持たない")]
    public void 語だけの合図のE710は一覧を持たない()
    {
        var issue = new PatchParser().Parse("NEED_MORE_CONTEXT").Errors.Single();

        issue.Code.Should().Be(ErrorCode.E710);
        issue.RequestedPaths.Should().BeNull();
        issue.Detail.Should().BeNull();
    }

    // ------------------------------------------------------------------
    // テンプレートの指示（Graft独自形式と標準SR形式で揃える）
    // ------------------------------------------------------------------

    [Theory(DisplayName = "既定の初回用・修正依頼は、Graft独自形式も標準SR形式も「NEED_MORE_CONTEXT: <ファイルパス>」の形で指示する")]
    [InlineData("builtin-full")]
    [InlineData("builtin-fix-request")]
    [InlineData("builtin-graft-full")]
    [InlineData("builtin-graft-fix-request")]
    public void テンプレートはパス付きの形で指示する(string id)
    {
        var body = PromptTemplateStore.BuiltIns.Single(t => t.Id == id).Body;

        body.Should().Contain("NEED_MORE_CONTEXT: <ファイルパス>");
        body.Should().Contain("ファイルごとに1行");
    }

    // ------------------------------------------------------------------
    // 誤検知の防止（ほかの内容が1行でも混じれば検知しない）
    // ------------------------------------------------------------------

    [Theory(DisplayName = "NEED_MORE_CONTEXT以外の内容が1行でも混じると検知しない")]
    [InlineData("説明です\nNEED_MORE_CONTEXT: src/a.cs")]
    [InlineData("NEED_MORE_CONTEXT: src/a.cs\n以上です")]
    [InlineData("NEED_MORE_CONTEXT: src/a.cs\nsrc/b.cs")]
    [InlineData("```text\nNEED_MORE_CONTEXT: src/a.cs\n```\n補足の文章")]
    [InlineData("- NEED_MORE_CONTEXT: src/a.cs")]
    [InlineData("「NEED_MORE_CONTEXT: <ファイルパス>」の1行のみを出力してください")]
    [InlineData("NEED_MORE_CONTEXT を返してください")]
    [InlineData("NEED_MORE_CONTEXT_EXTRA: src/a.cs")]
    [InlineData("NEED_MORE_CONTEXT src/a.cs")]
    [InlineData("")]
    [InlineData("```text\n```")]
    public void 別の内容が混じると検知しない(string text)
        => StandardSearchReplaceAdapter.IsNeedMoreContext(text).Should().BeFalse();

    [Fact(DisplayName = "標準SEARCH/REPLACEのブロックに語が現れるだけなら検知しない")]
    public void ブロックの中に語があるだけなら検知しない()
    {
        var text =
            "src/a.py\n" +
            "<<<<<<< SEARCH\n" +
            "NEED_MORE_CONTEXT: src/a.cs\n" +
            "=======\n" +
            "OK\n" +
            ">>>>>>> REPLACE\n";

        StandardSearchReplaceAdapter.IsNeedMoreContext(text).Should().BeFalse();
        new PatchParser().Parse(text).IsSuccess.Should().BeTrue();
    }

    [Fact(DisplayName = "組み込みテンプレートの本文全体を渡しても検知しない（テンプレートのコピーを情報不足と誤認しない）")]
    public void 組み込みテンプレートの本文は検知しない()
    {
        PromptTemplateStore.BuiltIns.Should().NotBeEmpty();
        foreach (var template in PromptTemplateStore.BuiltIns)
        {
            StandardSearchReplaceAdapter.IsNeedMoreContext(template.Body).Should().BeFalse(
                $"{template.Id} の本文は説明文の中にNEED_MORE_CONTEXTを含むだけで、AIの申告ではないため");
            PatchTextDetector.LooksLikePatch(template.Body).Should().BeFalse(
                $"{template.Id} の本文をコピーしただけでパッチ扱いにしてはいけないため");
        }
    }

    [Fact(DisplayName = "組み込みテンプレートの本文をフェンスで囲んで渡しても検知しない")]
    public void フェンスで囲んだ組み込みテンプレートの本文も検知しない()
    {
        foreach (var template in PromptTemplateStore.BuiltIns)
        {
            var fenced = "````text\n" + template.Body + "\n````\n";
            StandardSearchReplaceAdapter.IsNeedMoreContext(fenced).Should().BeFalse(template.Id);
        }
    }

    [Fact(DisplayName = "選択範囲の修正依頼プロンプトを渡しても検知しない")]
    public void 選択範囲の修正依頼プロンプトは検知しない()
    {
        var prompt = PromptTemplateStore.BuildSelectionFixRequestPrompt("a.cs", 1, 1, "code", ".cs");

        StandardSearchReplaceAdapter.IsNeedMoreContext(prompt).Should().BeFalse();
    }
}
