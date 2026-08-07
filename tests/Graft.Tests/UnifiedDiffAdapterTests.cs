using System.Linq;
using FluentAssertions;
using Graft.Core;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// unified diff 入力対応（仕様書5章の拡張・<see cref="UnifiedDiffAdapter"/>）の単体テスト。
/// アダプタが変換した SEARCH/REPLACE ペアが、既存の <see cref="MatchEngine"/> で実際に
/// 位置決めできることまで検証する（パイプラインの再利用が壊れていないことの確認）。
/// </summary>
public class UnifiedDiffAdapterTests
{
    private const string OriginalUserPy =
        "import os\n\n\ndef get_user(id):\n    return db.query(id)\n\n\ndef other():\n    pass\n";

    // ---- 基本: 1ファイル1ハンク ----

    [Fact(DisplayName = "1ファイル1ハンクのunified diffがSRペアへ変換されMatchEngineで位置決めできる")]
    public void 基本_1ファイル1ハンクを変換しMatchEngineで位置決めできる()
    {
        var diff =
            "--- a/src/services/user.py\n" +
            "+++ b/src/services/user.py\n" +
            "@@ -4,2 +4,2 @@\n" +
            " def get_user(id):\n" +
            "-    return db.query(id)\n" +
            "+    return db.query(User).filter(User.id == id).first()\n";

        var result = new PatchParser().Parse(diff);

        result.IsSuccess.Should().BeTrue("unified diffとして解析できるはず");
        result.Value.Blocks.Should().HaveCount(1);
        result.Value.Meta.Type.Should().Be("chore", "summaryが無いため取り込み用の固定typeを補うはず");
        result.Value.Meta.Summary.Should().NotBeNullOrWhiteSpace("requireSummaryの必須チェックに引っかからないよう固定summaryを補うはず");

        var block = result.Value.Blocks[0].Should().BeOfType<SearchReplaceBlock>().Subject;
        block.Path.Should().Be("src/services/user.py", "a/ b/ 前置は除去され+++側のパスを正とするはず");
        block.Pairs.Should().HaveCount(1);
        block.Pairs[0].SearchText.Should().Be("def get_user(id):\n    return db.query(id)");
        block.Pairs[0].ReplaceText.Should().Be("def get_user(id):\n    return db.query(User).filter(User.id == id).first()");

        var match = new MatchEngine().Match(OriginalUserPy, block.Pairs[0], block.Occurrence);
        match.IsSuccess.Should().BeTrue("文脈行と削除行から組み立てたSEARCH部は実ファイルに存在するはず");
        match.Value.Single().Stage.Should().Be(MatchStage.Exact);
    }

    // ---- 複数ファイル・複数ハンク ----

    [Fact(DisplayName = "複数ファイル_複数ハンクのunified diffを変換できる")]
    public void 複数ファイル複数ハンクを変換できる()
    {
        var diff =
            "--- a/src/a.py\n" +
            "+++ b/src/a.py\n" +
            "@@ -1,1 +1,1 @@\n" +
            "-old_a\n" +
            "+new_a\n" +
            "@@ -5,1 +5,1 @@\n" +
            "-old_a2\n" +
            "+new_a2\n" +
            "--- a/src/b.py\n" +
            "+++ b/src/b.py\n" +
            "@@ -1,1 +1,1 @@\n" +
            "-old_b\n" +
            "+new_b\n";

        var result = new PatchParser().Parse(diff);

        result.IsSuccess.Should().BeTrue();
        result.Value.Blocks.Should().HaveCount(2, "2ファイル分のブロックが生成されるはず");

        var blockA = result.Value.Blocks[0].Should().BeOfType<SearchReplaceBlock>().Subject;
        blockA.Path.Should().Be("src/a.py");
        blockA.Pairs.Should().HaveCount(2, "1ファイル内の2ハンクがそれぞれ1ペアへ変換されるはず");
        blockA.Pairs[0].ReplaceText.Should().Be("new_a");
        blockA.Pairs[1].ReplaceText.Should().Be("new_a2");

        var blockB = result.Value.Blocks[1].Should().BeOfType<SearchReplaceBlock>().Subject;
        blockB.Path.Should().Be("src/b.py");
        blockB.Pairs.Should().HaveCount(1);
    }

    // ---- 新規ファイル ----

    [Fact(DisplayName = "新規ファイルのunified diffはFULL形式相当のブロックへ変換される")]
    public void 新規ファイルはFULL形式相当になる()
    {
        var diff =
            "--- /dev/null\n" +
            "+++ b/src/new_module.py\n" +
            "@@ -0,0 +1,2 @@\n" +
            "+def new_function():\n" +
            "+    return 42\n";

        var result = new PatchParser().Parse(diff);

        result.IsSuccess.Should().BeTrue();
        result.Value.Blocks.Should().HaveCount(1);
        var block = result.Value.Blocks[0].Should().BeOfType<FullContentBlock>().Subject;
        block.Path.Should().Be("src/new_module.py");
        block.Content.Should().Be("def new_function():\n    return 42");
    }

    // ---- 削除ファイル ----

    [Fact(DisplayName = "削除ファイルのunified diffはDELETEブロックへ変換される")]
    public void 削除ファイルはDELETEブロックになる()
    {
        var diff =
            "--- a/src/legacy/old.py\n" +
            "+++ /dev/null\n" +
            "@@ -1,2 +0,0 @@\n" +
            "-def old():\n" +
            "-    pass\n";

        var result = new PatchParser().Parse(diff);

        result.IsSuccess.Should().BeTrue();
        result.Value.Blocks.Should().HaveCount(1);
        var block = result.Value.Blocks[0].Should().BeOfType<DeleteBlock>().Subject;
        block.Path.Should().Be("src/legacy/old.py");
    }

    // ---- コードフェンス + 行番号ずれ ----

    [Fact(DisplayName = "diffフェンス付きかつ行番号がずれていても文脈行で段階マッチに成功する")]
    public void フェンス付きかつ行番号ずれでも段階マッチに成功する()
    {
        // ハンク見出しの行番号（@@ -99,2 +99,2 @@）は実際のファイル上の位置とは無関係な値。
        // アダプタはハンク見出しの数値を一切参照せず、文脈行の内容のみをSEARCH部として使うため、
        // MatchEngineの段階マッチ（完全一致〜類似度）で正しい位置を見つけられるはず。
        var diff =
            "AIの出力例です。\n" +
            "```diff\n" +
            "--- a/src/services/user.py\n" +
            "+++ b/src/services/user.py\n" +
            "@@ -99,2 +99,2 @@\n" +
            " def get_user(id):\n" +
            "-    return db.query(id)\n" +
            "+    return db.query(User).filter(User.id == id).first()\n" +
            "```\n";

        var result = new PatchParser().Parse(diff);

        result.IsSuccess.Should().BeTrue("コードフェンスは剥がされ、前後の説明文は無視されるはず");
        var block = result.Value.Blocks[0].Should().BeOfType<SearchReplaceBlock>().Subject;

        var match = new MatchEngine().Match(OriginalUserPy, block.Pairs[0], block.Occurrence);
        match.IsSuccess.Should().BeTrue("ハンク見出しの行番号がずれていても文脈行の内容で位置決めできるはず");
    }

    // ---- "\ No newline at end of file" ----

    [Fact(DisplayName = "No_newline注記があってもクラッシュせず解析できる")]
    public void No_newline注記があってもクラッシュしない()
    {
        var diff =
            "--- a/src/a.py\n" +
            "+++ b/src/a.py\n" +
            "@@ -1,1 +1,1 @@\n" +
            "-old\n" +
            "\\ No newline at end of file\n" +
            "+new\n" +
            "\\ No newline at end of file\n";

        var act = () => new PatchParser().Parse(diff);

        act.Should().NotThrow();
        var result = act();
        result.IsSuccess.Should().BeTrue();
        var block = result.Value.Blocks[0].Should().BeOfType<SearchReplaceBlock>().Subject;
        block.Pairs[0].SearchText.Should().Be("old", "No newline注記は無視され本文に混ざらないはず");
        block.Pairs[0].ReplaceText.Should().Be("new");
    }

    // ---- 回帰: Graft形式は従来どおり ----

    [Fact(DisplayName = "回帰_Graft形式のパッチはunified diffアダプタに奪われず従来どおり解析される")]
    public void 回帰_Graft形式は従来どおり解析される()
    {
        var text = FixtureLoader.LoadPatch("patch_meta_full");
        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeTrue();
        result.Value.Meta.Summary.Should().Be("ユーザー取得APIの型安全化", "Graft形式のsummaryがそのまま使われ、アダプタの固定文言に置き換わっていないはず");
        result.Value.Meta.Type.Should().Be("refactor");
    }

    // ---- どちらでもないテキスト ----

    [Fact(DisplayName = "unified diffともGraft形式とも判定できないテキストは従来どおりE001で失敗する")]
    public void どちらでもないテキストは従来どおりE001になる()
    {
        var text = "これはただの説明文です。パッチではありません。";
        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeFalse();
        result.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E001);
    }
}
