using FluentAssertions;
using Graft.Core;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// <see cref="PatchParser"/> の単体テスト（正常系・基本的な異常系）。
/// 附録A.7の方針に従い、テストデータは Fixtures/Patches 配下のファイルから読み込む。
/// </summary>
public class PatchParserTests
{
    [Fact(DisplayName = "SR形式_1ペアを解析できる")]
    public void SR形式_1ペアを解析できる()
    {
        var text = FixtureLoader.LoadPatch("sr_1pea");
        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeTrue("正しい形式のSRブロックは解析に成功するはず");
        result.Value.Blocks.Should().HaveCount(1);
        var block = result.Value.Blocks[0].Should().BeOfType<SearchReplaceBlock>().Subject;
        block.Path.Should().Be("src/services/user.py");
        block.Pairs.Should().HaveCount(1);
        block.Pairs[0].Description.Should().Be("戻り値をOptional型に変更し、N+1クエリを解消");
        block.Pairs[0].SearchText.Should().Be("def get_user(id):\n    return db.query(id)");
        block.Pairs[0].ReplaceText.Should()
            .Be("def get_user(id: int) -> User | None:\n    return db.query(User).filter(User.id == id).first()");
    }

    [Fact(DisplayName = "SR形式_複数ペアを1つのFILEヘッダに連結できる")]
    public void SR形式_複数ペアを連結できる()
    {
        var text = FixtureLoader.LoadPatch("sr_fukusuu_pair");
        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeTrue();
        result.Value.Blocks.Should().HaveCount(1, "1つのFILEヘッダに対するブロックは1つのみ生成されるはず");
        var block = result.Value.Blocks[0].Should().BeOfType<SearchReplaceBlock>().Subject;
        block.Pairs.Should().HaveCount(2, "連結された2つのSEARCH/REPLACEペアが保持されるはず");
        block.Pairs[0].Description.Should().Be("1つ目の修正");
        block.Pairs[1].Description.Should().Be("2つ目の修正");
        block.Pairs[0].ReplaceText.Should().Be("def a():\n    return 1");
        block.Pairs[1].ReplaceText.Should().Be("def b():\n    return 2");
    }

    [Fact(DisplayName = "SR形式_複数ファイルへのブロックを解析できる")]
    public void SR形式_複数ファイルを解析できる()
    {
        var text = FixtureLoader.LoadPatch("sr_fukusuu_file");
        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeTrue();
        result.Value.Blocks.Should().HaveCount(2, "2つのFILEヘッダに対応する2ブロックが生成されるはず");
        result.Value.Blocks[0].Path.Should().Be("src/a.py");
        result.Value.Blocks[1].Path.Should().Be("src/b.py");
    }

    [Fact(DisplayName = "FULL形式のブロックを解析できる")]
    public void FULL形式を解析できる()
    {
        var text = FixtureLoader.LoadPatch("full_keishiki");
        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeTrue();
        result.Value.Blocks.Should().HaveCount(1);
        var block = result.Value.Blocks[0].Should().BeOfType<FullContentBlock>().Subject;
        block.Path.Should().Be("src/new_module.py");
        block.Content.Should().Be("def new_function():\n    return 42");
    }

    [Fact(DisplayName = "DELETEブロックを解析できる")]
    public void DELETEブロックを解析できる()
    {
        var text = FixtureLoader.LoadPatch("delete_block");
        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeTrue();
        result.Value.Blocks.Should().HaveCount(1);
        var block = result.Value.Blocks[0].Should().BeOfType<DeleteBlock>().Subject;
        block.Path.Should().Be("src/legacy/deprecated_module.py");
    }

    [Fact(DisplayName = "RENAMEブロックを解析できる")]
    public void RENAMEブロックを解析できる()
    {
        var text = FixtureLoader.LoadPatch("rename_block");
        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeTrue();
        result.Value.Blocks.Should().HaveCount(1);
        var block = result.Value.Blocks[0].Should().BeOfType<RenameBlock>().Subject;
        block.FromPath.Should().Be("src/old_name.py");
        block.ToPath.Should().Be("src/new_name.py");
    }

    [Fact(DisplayName = "MKDIRブロックを解析できる")]
    public void MKDIRブロックを解析できる()
    {
        var text = FixtureLoader.LoadPatch("mkdir_block");
        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeTrue();
        result.Value.Blocks.Should().HaveCount(1);
        var block = result.Value.Blocks[0].Should().BeOfType<MkdirBlock>().Subject;
        block.Path.Should().Be("src/features/auth");
    }

    [Fact(DisplayName = "APPENDブロックを解析できる")]
    public void APPENDブロックを解析できる()
    {
        var text = FixtureLoader.LoadPatch("append_block");
        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeTrue();
        result.Value.Blocks.Should().HaveCount(1);
        var block = result.Value.Blocks[0].Should().BeOfType<AppendBlock>().Subject;
        block.Path.Should().Be("src/routes.py");
        block.Description.Should().Be("ルート定義を追加");
        block.Content.Should().Be("router.add_route(\"/health\", health_check)");
    }

    [Fact(DisplayName = "PREPENDブロックを解析できる")]
    public void PREPENDブロックを解析できる()
    {
        var text = FixtureLoader.LoadPatch("prepend_block");
        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeTrue();
        result.Value.Blocks.Should().HaveCount(1);
        var block = result.Value.Blocks[0].Should().BeOfType<PrependBlock>().Subject;
        block.Path.Should().Be("src/main.py");
        block.Description.Should().Be("importを追加");
        block.Content.Should().Be("import logging");
    }

    [Fact(DisplayName = "PATCHメタのsummary_type_baseを解析できる")]
    public void PATCHメタを解析できる()
    {
        var text = FixtureLoader.LoadPatch("patch_meta_full");
        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeTrue();
        result.Value.Meta.Summary.Should().Be("ユーザー取得APIの型安全化");
        result.Value.Meta.Type.Should().Be("refactor");
        result.Value.Meta.BaseHashes.Should().HaveCount(2);
        result.Value.Meta.BaseHashes["src/services/user.py"].Should().Be("a3f9c1");
        result.Value.Meta.BaseHashes["src/models.py"].Should().Be("7b2e04");
        result.Issues.Should().NotContain(i => i.Code == ErrorCode.E004, "summaryが指定されているのでE004は出ないはず");
    }

    [Fact(DisplayName = "Markdownコードフェンスに囲まれたパッチも解析できる")]
    public void Markdownコードフェンス混在でも解析できる()
    {
        var text = FixtureLoader.LoadPatch("markdown_fence");
        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeTrue("```で囲まれていてもフェンス行は除去され解析できるはず");
        result.Value.Blocks.Should().HaveCount(1);
        var block = result.Value.Blocks[0].Should().BeOfType<SearchReplaceBlock>().Subject;
        block.Path.Should().Be("src/calc.py");
        block.HeaderLine.Should().Be(4, "フェンス行を除いても元テキストの行番号がそのまま使われるはず");
    }

    [Fact(DisplayName = "ブロック外の説明文が混ざっていても解析できる")]
    public void ブロック外の説明文が混ざっていても解析できる()
    {
        var text = FixtureLoader.LoadPatch("gaibu_setsumei");
        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeTrue("ブロック外の前置き・後書きは無視されるはず");
        result.Value.Blocks.Should().HaveCount(1);
        var block = result.Value.Blocks[0].Should().BeOfType<SearchReplaceBlock>().Subject;
        block.Path.Should().Be("src/greet.py");
        block.Pairs[0].ReplaceText.Should().Be("print(\"こんにちは\")");
    }

    [Fact(DisplayName = "SEARCH部が空の場合はE003で失敗する")]
    public void SEARCH部が空の場合はE003になる()
    {
        var text = FixtureLoader.LoadPatch("search_karano_e003");
        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeFalse("SEARCH部が空のパッチは失敗として扱われるはず");
        result.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E003);
        result.Issues.Single(i => i.Code == ErrorCode.E003).LineNumber.Should().Be(2, "SEARCHマーカー行の行番号が記録されるはず");
    }

    [Fact(DisplayName = "パス不正_先頭がスラッシュの絶対パスはE201になる")]
    public void パス不正_先頭スラッシュはE201になる()
    {
        var text = FixtureLoader.LoadPatch("path_zettai_slash");
        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeFalse();
        result.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E201);
        result.Issues.Single(i => i.Code == ErrorCode.E201).LineNumber.Should().Be(1);
    }

    [Fact(DisplayName = "パス不正_ドライブレターの絶対パスはE201になる")]
    public void パス不正_ドライブレターはE201になる()
    {
        var text = FixtureLoader.LoadPatch("path_zettai_drive");
        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeFalse();
        result.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E201);
    }

    [Fact(DisplayName = "パス不正_ドットドットを含むパスはE201になる")]
    public void パス不正_ドットドットを含むとE201になる()
    {
        var text = FixtureLoader.LoadPatch("path_dotdot");
        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeFalse();
        result.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E201);
    }

    [Fact(DisplayName = "OCCURRENCE=2を解析できる")]
    public void OCCURRENCEが2の場合を解析できる()
    {
        var text = FixtureLoader.LoadPatch("occurrence_2");
        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeTrue();
        var block = result.Value.Blocks[0].Should().BeOfType<SearchReplaceBlock>().Subject;
        block.Occurrence.All.Should().BeFalse();
        block.Occurrence.Index.Should().Be(2);
    }

    [Fact(DisplayName = "OCCURRENCE=ALLを解析できる")]
    public void OCCURRENCEがALLの場合を解析できる()
    {
        var text = FixtureLoader.LoadPatch("occurrence_all");
        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeTrue();
        var block = result.Value.Blocks[0].Should().BeOfType<SearchReplaceBlock>().Subject;
        block.Occurrence.All.Should().BeTrue();
    }

    [Fact(DisplayName = "SEARCH-RANGEはIsRangeを保持したままアンカーを解析する")]
    public void SEARCH_RANGEを解析できる()
    {
        var text = FixtureLoader.LoadPatch("search_range");
        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeTrue();
        var block = result.Value.Blocks[0].Should().BeOfType<SearchReplaceBlock>().Subject;
        block.Pairs[0].IsRange.Should().BeTrue("SEARCH-RANGE形式であることが保持されるはず");
        block.Pairs[0].SearchText.Should().Contain("def process_batch(items):");
        block.Pairs[0].SearchText.Should().Contain("...", "開始・終了アンカーの間の省略記法がそのまま保持されるはず");
        block.Pairs[0].SearchText.Should().Contain("return results");
    }

    [Fact(DisplayName = "REPLACE部が空の場合は削除操作として解析される")]
    public void REPLACE部が空の場合は削除操作になる()
    {
        var text = FixtureLoader.LoadPatch("replace_karano_sakujo");
        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeTrue();
        var block = result.Value.Blocks[0].Should().BeOfType<SearchReplaceBlock>().Subject;
        block.Pairs[0].ReplaceText.Should().Be(string.Empty, "REPLACE部が空文字列であることが削除操作を意味するはず");
    }

    [Fact(DisplayName = "ブロックが0件の場合はE001で失敗する")]
    public void ブロック0件はE001になる()
    {
        var text = FixtureLoader.LoadPatch("block_zero_e001");
        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeFalse("ブロックが1つも無いパッチは失敗として扱われるはず");
        result.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E001);
    }

    [Fact(DisplayName = "summaryが欠落していてもE004警告のうえ解析は成功する")]
    public void summary欠落はE004警告で解析成功する()
    {
        var text = FixtureLoader.LoadPatch("summary_kesson_e004");
        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeTrue("summary欠落は警告に留まり解析自体は成功するはず");
        result.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E004);
        result.Issues.Single(i => i.Code == ErrorCode.E004).Severity.Should().Be(Severity.Warning);
    }

    [Fact(DisplayName = "HeaderLineとIssueのLineNumberが正しい")]
    public void HeaderLineとLineNumberが正しい()
    {
        var text = FixtureLoader.LoadPatch("headerline_kensho");
        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeTrue();
        result.Value.Blocks.Should().HaveCount(2);
        var block1 = result.Value.Blocks[0].Should().BeOfType<SearchReplaceBlock>().Subject;
        var block2 = result.Value.Blocks[1].Should().BeOfType<SearchReplaceBlock>().Subject;

        block1.HeaderLine.Should().Be(3, "前置き文章の分だけ行番号がずれているはず");
        block1.Pairs[0].SourceLine.Should().Be(4);
        block2.HeaderLine.Should().Be(10);
        block2.Pairs[0].SourceLine.Should().Be(11);
    }

    [Fact(DisplayName = "CRLF改行のパッチでも正しく解析できる")]
    public void CRLF改行でも正しく解析できる()
    {
        var text = FixtureLoader.LoadPatch("crlf_kaigyou");
        text.Should().Contain("\r\n", "フィクスチャがCRLFであることの前提確認");

        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeTrue("CRLF改行でもLFと同様に解析できるはず");
        result.Value.Blocks.Should().HaveCount(1);
        var block = result.Value.Blocks[0].Should().BeOfType<SearchReplaceBlock>().Subject;
        block.Path.Should().Be("src/crlf_test.py");
        block.Pairs[0].SearchText.Should().Be("old_line = 1", "本文にCRやその他の余分な文字が残っていないはず");
        block.Pairs[0].ReplaceText.Should().Be("new_line = 2");
    }
}
