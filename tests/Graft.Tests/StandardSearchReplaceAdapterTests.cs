using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Core;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 標準SEARCH/REPLACE形式の入力対応（仕様書5.2・<see cref="StandardSearchReplaceAdapter"/>）の
/// 単体テスト。
///
/// 【この形式を「標準」と呼ぶ根拠（2026-08 時点の裏取り）】Aider公式ドキュメント
/// （aider.chat/docs/more/edit-formats.html）と Aider本体のプロンプト実装
/// （aider/coders/editblock_prompts.py）を実際に参照して次を確認した。
///   - パスは「フェンス付きコードブロックの直前に、パスだけを単独の行として」置く
///     （"The *FULL* file path alone on a line, verbatim."）。
///   - 新規ファイルの作成は「SEARCH部を空にする」で表す
///     （"an empty `SEARCH` section, the new file's contents in the `REPLACE` section"）。
///   - 素の標準形式に NEW_FILE / WHOLE_FILE / DELETE_FILE というマーカーは**無い**。
///     これらは利用者の会社の「SRルール」による独自拡張である。
///     削除・改名は素の標準の守備範囲外（Aiderは応答末尾のシェルコマンドで表す）。
/// したがって Graft は「空SEARCH方式（素の標準）」と「NEW_FILE等のマーカー方式（会社ルール）」の
/// **両方**を受け付ける。どちらで来ても同じ内部表現になることを、このテストで固定する。
///
/// 検証は変換結果の形だけでなく、変換後のSEARCH/REPLACEペアが実際に
/// <see cref="MatchEngine"/> で位置決めできるところまで行う（既存パイプラインの
/// 再利用が壊れていないことの確認。<see cref="UnifiedDiffAdapterTests"/>と同じ方針）。
/// </summary>
public class StandardSearchReplaceAdapterTests
{
    private const string OriginalCalcPy =
        "import math\n\n\ndef add(a, b):\n    return a + b\n\n\ndef sub(a, b):\n    return a - b\n";

    // ==================================================================
    // 【1】既存ファイルの部分修正
    // ==================================================================

    [Fact(DisplayName = "標準SR形式の部分修正がSRペアへ変換されMatchEngineで位置決めできる")]
    public void 部分修正を変換しMatchEngineで位置決めできる()
    {
        var text =
            "src/calc.py\n" +
            "<<<<<<< SEARCH\n" +
            "def add(a, b):\n" +
            "    return a + b\n" +
            "=======\n" +
            "def add(a: int, b: int) -> int:\n" +
            "    return a + b\n" +
            ">>>>>>> REPLACE\n";

        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeTrue("標準SR形式として解析できるはず");
        result.Value.Meta.Type.Should().Be("chore", "summaryが無いため取り込み用の固定typeを補うはず");
        result.Value.Meta.Summary.Should().NotBeNullOrWhiteSpace(
            "requireSummaryの必須チェックに引っかからないよう固定summaryを補うはず（UnifiedDiffAdapterの前例）");

        var block = result.Value.Blocks.Single().Should().BeOfType<SearchReplaceBlock>().Subject;
        block.Path.Should().Be("src/calc.py", "ブロック直前の裸のパス行が対象ファイルになるはず");
        block.Pairs.Should().HaveCount(1);
        block.Pairs[0].SearchText.Should().Be("def add(a, b):\n    return a + b");
        block.Pairs[0].ReplaceText.Should().Be("def add(a: int, b: int) -> int:\n    return a + b");

        var match = new MatchEngine().Match(OriginalCalcPy, block.Pairs[0], block.Occurrence);
        match.IsSuccess.Should().BeTrue("SEARCH部は実ファイルに存在するはず");
        match.Value.Single().Stage.Should().Be(MatchStage.Exact);
    }

    [Fact(DisplayName = "Aider流のフェンス付き（パス行の次に```python）でも解析できる")]
    public void フェンス付きでも解析できる()
    {
        // Aiderのドキュメントが示す素の書き方。パス行 → ```言語 → ブロック → ``` の順。
        var text =
            "以下のとおり修正します。\n\n" +
            "src/calc.py\n" +
            "```python\n" +
            "<<<<<<< SEARCH\n" +
            "def add(a, b):\n" +
            "=======\n" +
            "def add(a: int, b: int) -> int:\n" +
            ">>>>>>> REPLACE\n" +
            "```\n";

        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeTrue();
        var block = result.Value.Blocks.Single().Should().BeOfType<SearchReplaceBlock>().Subject;
        block.Path.Should().Be("src/calc.py",
            "フェンス行はブロックの外側でのみ読み飛ばすため、その手前のパス行が使われるはず");
    }

    [Fact(DisplayName = "会社ルールどおり出力全体を1つのtextフェンスで囲んでも解析できる")]
    public void 出力全体を1つのフェンスで囲んでも解析できる()
    {
        var text =
            "承知しました。\n\n" +
            "```text\n" +
            "src/calc.py\n" +
            "<<<<<<< SEARCH\n" +
            "def add(a, b):\n" +
            "=======\n" +
            "def add(a: int, b: int) -> int:\n" +
            ">>>>>>> REPLACE\n" +
            "```\n";

        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeTrue();
        result.Value.Blocks.Single().Path.Should().Be("src/calc.py");
    }

    [Fact(DisplayName = "同一ファイルへの複数ブロックは1つのブロックの複数ペアへまとめられる")]
    public void 同一ファイルの複数ブロックは1ブロックへまとまる()
    {
        var text =
            "src/calc.py\n" +
            "<<<<<<< SEARCH\n" +
            "def add(a, b):\n" +
            "=======\n" +
            "def add(a: int, b: int) -> int:\n" +
            ">>>>>>> REPLACE\n" +
            "\n" +
            "src/calc.py\n" +
            "<<<<<<< SEARCH\n" +
            "def sub(a, b):\n" +
            "=======\n" +
            "def sub(a: int, b: int) -> int:\n" +
            ">>>>>>> REPLACE\n";

        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeTrue();
        var block = result.Value.Blocks.Single().Should().BeOfType<SearchReplaceBlock>().Subject;
        block.Pairs.Should().HaveCount(2,
            "同一パスのブロックを別々のブロックとして並べるとキュー側で重複（E007）になるため、" +
            "UnifiedDiffAdapterと同じくペアとしてまとめるはず");
    }

    [Fact(DisplayName = "2つ目以降のブロックでパス行が省略された場合は直前のファイルを引き継ぐ")]
    public void パス行が省略されたら直前のファイルを引き継ぐ()
    {
        var text =
            "src/calc.py\n" +
            "<<<<<<< SEARCH\n" +
            "def add(a, b):\n" +
            "=======\n" +
            "def add(a: int, b: int) -> int:\n" +
            ">>>>>>> REPLACE\n" +
            "<<<<<<< SEARCH\n" +
            "def sub(a, b):\n" +
            "=======\n" +
            "def sub(a: int, b: int) -> int:\n" +
            ">>>>>>> REPLACE\n";

        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeTrue();
        var block = result.Value.Blocks.Single().Should().BeOfType<SearchReplaceBlock>().Subject;
        block.Path.Should().Be("src/calc.py");
        block.Pairs.Should().HaveCount(2, "パス行を省いた連続ブロックは直前のファイルの続きとみなすはず");
    }

    // ==================================================================
    // 【2】新規ファイル: NEW_FILE（会社ルール）と 空SEARCH（素の標準）の両方
    // ==================================================================

    [Fact(DisplayName = "NEW_FILEはFULL形式相当のブロックへ変換される")]
    public void NEW_FILEはFULL形式相当になる()
    {
        var text =
            "src/logger.py\n" +
            "<<<<<<< NEW_FILE\n" +
            "def log(message):\n" +
            "    print(message)\n" +
            ">>>>>>> END_FILE\n";

        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeTrue();
        var block = result.Value.Blocks.Single().Should().BeOfType<FullContentBlock>().Subject;
        block.Path.Should().Be("src/logger.py");
        block.Content.Should().Be("def log(message):\n    print(message)");
    }

    [Fact(DisplayName = "空SEARCH（素の標準の新規ファイル作法）もFULL形式相当のブロックへ変換される")]
    public void 空SEARCHもFULL形式相当になる()
    {
        // Aiderの本来の作法。SEARCH部を空にし、REPLACE部にファイル全内容を書く。
        var text =
            "src/logger.py\n" +
            "<<<<<<< SEARCH\n" +
            "=======\n" +
            "def log(message):\n" +
            "    print(message)\n" +
            ">>>>>>> REPLACE\n";

        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeTrue("空SEARCHは新規ファイル作成の正規の書き方のため、E003にしてはいけない");
        var block = result.Value.Blocks.Single().Should().BeOfType<FullContentBlock>().Subject;
        block.Path.Should().Be("src/logger.py");
        block.Content.Should().Be("def log(message):\n    print(message)");
    }

    // ==================================================================
    // 【3】全面書き換え / 【4】削除
    // ==================================================================

    [Fact(DisplayName = "WHOLE_FILEはFULL形式相当のブロックへ変換される")]
    public void WHOLE_FILEはFULL形式相当になる()
    {
        var text =
            "README.md\n" +
            "<<<<<<< WHOLE_FILE\n" +
            "# タイトル\n" +
            "本文\n" +
            ">>>>>>> END_FILE\n";

        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeTrue();
        var block = result.Value.Blocks.Single().Should().BeOfType<FullContentBlock>().Subject;
        block.Path.Should().Be("README.md");
        block.Content.Should().Be("# タイトル\n本文");
    }

    [Fact(DisplayName = "DELETE_FILEはDELETEブロックへ変換される")]
    public void DELETE_FILEはDELETEブロックになる()
    {
        var text =
            "src/old.py\n" +
            "<<<<<<< DELETE_FILE\n" +
            ">>>>>>> END_FILE\n";

        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeTrue();
        var block = result.Value.Blocks.Single().Should().BeOfType<DeleteBlock>().Subject;
        block.Path.Should().Be("src/old.py");
    }

    [Fact(DisplayName = "4種類が1つの出力に混ざっていてもすべて変換される")]
    public void 四種類が混ざっていてもすべて変換される()
    {
        var text =
            "src/calc.py\n" +
            "<<<<<<< SEARCH\n" +
            "def add(a, b):\n" +
            "=======\n" +
            "def add(a: int, b: int) -> int:\n" +
            ">>>>>>> REPLACE\n" +
            "\n" +
            "src/logger.py\n" +
            "<<<<<<< NEW_FILE\n" +
            "def log(m):\n" +
            "    print(m)\n" +
            ">>>>>>> END_FILE\n" +
            "\n" +
            "README.md\n" +
            "<<<<<<< WHOLE_FILE\n" +
            "# タイトル\n" +
            ">>>>>>> END_FILE\n" +
            "\n" +
            "src/old.py\n" +
            "<<<<<<< DELETE_FILE\n" +
            ">>>>>>> END_FILE\n";

        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeTrue();
        result.Value.Blocks.Select(b => b.Kind).Should().Equal(
            BlockKind.SearchReplace, BlockKind.FullContent, BlockKind.FullContent, BlockKind.Delete);
        result.Value.Blocks.Select(b => b.Path).Should().Equal(
            "src/calc.py", "src/logger.py", "README.md", "src/old.py");
    }

    [Fact(DisplayName = "本文中の```はブロックの中身として保持される（Markdownファイルの編集）")]
    public void 本文中のフェンスは保持される()
    {
        // UnifiedDiffAdapterは``` で始まる行を一律で剥がすが、標準SR形式の本文は
        // 「ファイルの中身そのもの」であり、剥がすと内容が欠落する。外側だけを剥がすこと。
        var text =
            "docs/使い方.md\n" +
            "<<<<<<< NEW_FILE\n" +
            "# 使い方\n" +
            "```bash\n" +
            "echo hello\n" +
            "```\n" +
            ">>>>>>> END_FILE\n";

        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeTrue();
        var block = result.Value.Blocks.Single().Should().BeOfType<FullContentBlock>().Subject;
        block.Path.Should().Be("docs/使い方.md", "日本語のファイル名も相対パスとして扱えるはず");
        block.Content.Should().Be("# 使い方\n```bash\necho hello\n```",
            "ブロック本文の``` は内容として残るはず");
    }

    // ==================================================================
    // Graft拡張との併用
    // ==================================================================

    [Fact(DisplayName = "SEARCHマーカーの#以降の説明とOCCURRENCE指定（Graft拡張）も使える")]
    public void Graft拡張の説明とOCCURRENCEも使える()
    {
        var text =
            "src/calc.py\n" +
            "<<<<<<< SEARCH OCCURRENCE=2  # 2番目の加算を直す\n" +
            "return a + b\n" +
            "=======\n" +
            "return int(a) + int(b)\n" +
            ">>>>>>> REPLACE\n";

        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeTrue();
        var block = result.Value.Blocks.Single().Should().BeOfType<SearchReplaceBlock>().Subject;
        block.Pairs[0].Description.Should().Be("2番目の加算を直す");
        block.Occurrence.Index.Should().Be(2, "OCCURRENCEはペアではなくブロック側に載る（4章のSEARCHマーカーと同じ扱い）");
    }

    // ==================================================================
    // パスの安全性
    // ==================================================================

    [Theory(DisplayName = "親階層へ出るパス・絶対パスは拒否される（E201）")]
    [InlineData("../etc/passwd")]
    [InlineData("../../secret.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/system.ini")]
    public void 危険なパスは拒否される(string path)
    {
        var text =
            path + "\n" +
            "<<<<<<< SEARCH\n" +
            "old\n" +
            "=======\n" +
            "new\n" +
            ">>>>>>> REPLACE\n";

        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeFalse("プロジェクト外への書き込みは絶対に許してはいけない");
        result.Errors.Should().Contain(e => e.Code == ErrorCode.E201);
    }

    [Fact(DisplayName = "親階層へ出るパスはNEW_FILEでも拒否される（E201）")]
    public void 危険なパスはNEW_FILEでも拒否される()
    {
        var text =
            "../evil.sh\n" +
            "<<<<<<< NEW_FILE\n" +
            "rm -rf /\n" +
            ">>>>>>> END_FILE\n";

        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == ErrorCode.E201);
    }

    [Fact(DisplayName = "パス指定行が無いブロックはE002になり、何を直せばよいか文言で伝える")]
    public void パス指定行が無い場合はE002()
    {
        var text =
            "<<<<<<< SEARCH\n" +
            "old\n" +
            "=======\n" +
            "new\n" +
            ">>>>>>> REPLACE\n";

        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeFalse();
        var error = result.Errors.Should().ContainSingle().Subject;
        error.Code.Should().Be(ErrorCode.E002);
        error.Detail.Should().Contain("パスだけを書いた行");
    }

    // ==================================================================
    // NEED_MORE_CONTEXT
    // ==================================================================

    [Theory(DisplayName = "NEED_MORE_CONTEXTの1行だけならE710（AIが情報不足を訴えている）として区別する")]
    [InlineData("NEED_MORE_CONTEXT")]
    [InlineData("NEED_MORE_CONTEXT\n")]
    [InlineData("```text\nNEED_MORE_CONTEXT\n```\n")]
    public void NEED_MORE_CONTEXTはE710になる(string text)
    {
        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Code.Should().Be(ErrorCode.E710,
            "E001（ブロックが存在しない）では「AIが情報不足を訴えている」ことが伝わらないため");
    }

    [Fact(DisplayName = "NEED_MORE_CONTEXTを本文の一部に含むだけのテキストはE710にしない")]
    public void 本文にNEED_MORE_CONTEXTを含むだけならE710にしない()
    {
        var text =
            "src/a.py\n" +
            "<<<<<<< SEARCH\n" +
            "NEED_MORE_CONTEXT\n" +
            "=======\n" +
            "OK\n" +
            ">>>>>>> REPLACE\n";

        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeTrue("1行だけの合図と、本文にたまたま同じ語が現れる場合は区別する");
    }

    [Fact(DisplayName = "NEED_MORE_CONTEXTはクリップボード監視でも検知する（何も起きない状態にしない）")]
    public void NEED_MORE_CONTEXTは自動検知される()
        => PatchTextDetector.LooksLikePatch("NEED_MORE_CONTEXT\n").Should().BeTrue();

    // ==================================================================
    // 切断（4.10相当）
    // ==================================================================

    [Fact(DisplayName = "REPLACEが閉じられないまま途切れた出力は切断として扱う（E005警告）")]
    public void 途中で切れた出力は切断として扱う()
    {
        var text =
            "src/calc.py\n" +
            "<<<<<<< SEARCH\n" +
            "def add(a, b):\n" +
            "=======\n" +
            "def add(a: int, b: int) -> int:\n";
            // ">>>>>>> REPLACE" が来ないまま入力が尽きる

        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeTrue("切断は警告付き成功として扱い、継続依頼の導線へ載せる（4.10と同じ）");
        result.Value.IsTruncated.Should().BeTrue();
        result.Issues.Should().Contain(i => i.Code == ErrorCode.E005);
    }

    // ==================================================================
    // 回帰: 既存形式を絶対に壊さない
    // ==================================================================

    [Fact(DisplayName = "回帰_Graft独自形式のパッチは標準SRアダプタに奪われず従来どおり解析される")]
    public void 回帰_Graft独自形式は従来どおり解析される()
    {
        // <<<<<<< SEARCH は両形式で共通のマーカーであり、ここを取り違えると
        // 既存のGraft形式のパッチがすべて壊れる。この形式の要になる回帰テスト。
        var text =
            "<<<< PATCH\n" +
            "summary: 加算を型安全にする\n" +
            "type: refactor\n" +
            ">>>>\n" +
            "\n" +
            "<<<< FILE: src/calc.py\n" +
            "<<<<<<< SEARCH\n" +
            "def add(a, b):\n" +
            "=======\n" +
            "def add(a: int, b: int) -> int:\n" +
            ">>>>>>> REPLACE\n";

        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeTrue();
        result.Value.Meta.Summary.Should().Be("加算を型安全にする", "Graft形式のPATCHメタが従来どおり読めるはず");
        result.Value.Meta.Type.Should().Be("refactor");
        var block = result.Value.Blocks.Single().Should().BeOfType<SearchReplaceBlock>().Subject;
        block.Path.Should().Be("src/calc.py", "<<<< FILE: 行のパスが使われるはず（直前の裸の行ではない）");
    }

    [Fact(DisplayName = "回帰_unified diffは標準SRアダプタに奪われず従来どおり解析される")]
    public void 回帰_unified_diffは従来どおり解析される()
    {
        var diff =
            "--- a/src/calc.py\n" +
            "+++ b/src/calc.py\n" +
            "@@ -4,2 +4,2 @@\n" +
            " def add(a, b):\n" +
            "-    return a + b\n" +
            "+    return int(a) + int(b)\n";

        var result = new PatchParser().Parse(diff);

        result.IsSuccess.Should().BeTrue();
        result.Value.Meta.Summary.Should().Be("unified diff からの取り込み");
        result.Value.Blocks.Single().Path.Should().Be("src/calc.py");
    }

    [Fact(DisplayName = "回帰_MODE=FULL・RENAME等のGraft拡張は従来どおり解析される")]
    public void 回帰_Graft拡張は従来どおり解析される()
    {
        var text =
            "<<<< MKDIR: src/new\n" +
            "<<<< RENAME: src/old.py -> src/new/moved.py\n" +
            "<<<< FILE: src/logger.py MODE=FULL\n" +
            "def log(m):\n" +
            "    print(m)\n" +
            ">>>> END\n" +
            "<<<< APPEND: CHANGELOG.md\n" +
            "- 追記\n" +
            ">>>> END\n" +
            "<<<< DELETE: src/dead.py\n";

        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeTrue();
        result.Value.Blocks.Select(b => b.Kind).Should().Equal(
            BlockKind.Mkdir, BlockKind.Rename, BlockKind.FullContent, BlockKind.Append, BlockKind.Delete);
    }

    // ==================================================================
    // 両形式の混在
    // ==================================================================

    [Fact(DisplayName = "両形式が混在した入力はGraft独自形式として解析される（Graft優先）")]
    public void 混在時はGraft独自形式が優先される()
    {
        // 【設計判断の根拠】5.1（unified diff）の接続点が「Graft形式のマーカーが1つでもあれば
        // 4章の解析を優先する」と定めているため、新形式もこれに揃える。加えて、混在時に
        // 標準SR形式を優先すると既存の <<<< FILE: ブロックが解釈されなくなり、履歴・
        // パッチキューに残る既存データが読めなくなる恐れがある（後方互換の破壊は論外）。
        // 逆にGraft形式を優先しても、標準SR形式は新機能のため「これまで動いていたものが
        // 動かなくなる」ことは起こらない。
        var text =
            "<<<< FILE: src/calc.py\n" +
            "<<<<<<< SEARCH\n" +
            "def add(a, b):\n" +
            "=======\n" +
            "def add(a: int, b: int) -> int:\n" +
            ">>>>>>> REPLACE\n" +
            "\n" +
            "src/logger.py\n" +
            "<<<<<<< NEW_FILE\n" +
            "def log(m):\n" +
            "    print(m)\n" +
            ">>>>>>> END_FILE\n";

        var result = new PatchParser().Parse(text);

        // Graft形式として解析されるため、標準SR形式側のマーカー（<<<<<<< NEW_FILE）は
        // 「知らないブロックヘッダ」としてE002になる。混在は仕様として非対応であり、
        // 黙って一方を取りこぼすより、失敗させて利用者に気づかせる方が安全と判断した。
        // 既定テンプレート（PromptTemplateStore.StandardExtensionNote）でも、1回の出力で
        // 2つの形式を混ぜないようAIへ明示的に指示している。
        result.IsSuccess.Should().BeFalse("混在は非対応。Graft形式として解析され、標準SR形式のマーカーで失敗するはず");
        result.Errors.Should().Contain(e => e.Code == ErrorCode.E002);
    }

    // ==================================================================
    // クリップボード監視（10章）の「パッチらしさ」判定
    // ==================================================================

    [Fact(DisplayName = "標準SR形式の本物のパッチは自動検知される")]
    public void 標準SR形式の本物のパッチは自動検知される()
    {
        var text =
            "承知しました。以下のとおり修正します。\n\n" +
            "```text\n" +
            "src/calc.py\n" +
            "<<<<<<< SEARCH\n" +
            "def add(a, b):\n" +
            "=======\n" +
            "def add(a: int, b: int) -> int:\n" +
            ">>>>>>> REPLACE\n" +
            "```\n";

        PatchTextDetector.LooksLikePatch(text).Should().BeTrue();
    }

    [Fact(DisplayName = "NEW_FILEだけで構成された標準SR形式のパッチも自動検知される")]
    public void NEW_FILEだけのパッチも自動検知される()
    {
        var text =
            "src/logger.py\n" +
            "<<<<<<< NEW_FILE\n" +
            "def log(m):\n" +
            "    print(m)\n" +
            ">>>>>>> END_FILE\n";

        PatchTextDetector.LooksLikePatch(text).Should().BeTrue();
    }

    [Fact(DisplayName = "誤検知防止_パスが仮の文字列のままの例示は自動検知しない")]
    public void パスが仮の文字列の例示は自動検知しない()
    {
        // 既定プロンプトテンプレート（標準SR形式）と同じ形。利用者がテンプレートを
        // コピーしただけで検知されると邪魔になるため、実在しそうなパスを要求する。
        var text =
            "相対パス\n" +
            "<<<<<<< SEARCH\n" +
            "（現在のファイルに存在する完全一致テキスト）\n" +
            "=======\n" +
            "（置換後テキスト）\n" +
            ">>>>>>> REPLACE\n";

        PatchTextDetector.LooksLikePatch(text).Should().BeFalse();
    }

    [Fact(DisplayName = "誤検知防止_日本語の地の文はパス指定行として拾わない")]
    public void 日本語の地の文はパス指定行として拾わない()
    {
        // 日本語の文は空白を含まないため「空白が無い」だけでは弾けない。"/" を含む文
        // （SEARCH/REPLACE形式を…等）が誤ってパスと認識される事故を防ぐ。
        var text =
            "【原則】既存ファイルの修正はSEARCH/REPLACE形式を使い、\n" +
            "<<<<<<< SEARCH\n" +
            "old\n" +
            "=======\n" +
            "new\n" +
            ">>>>>>> REPLACE\n";

        PatchTextDetector.LooksLikePatch(text).Should().BeFalse(
            "全角の約物を含む行はパス指定行として認めないため、実在しそうなパスが1つも無い扱いになるはず");
    }

    [Fact(DisplayName = "回帰_Graft形式のパッチは自動検知され続ける（標準SR形式の絞り込みに巻き込まれない）")]
    public void 回帰_Graft形式のパッチは自動検知され続ける()
    {
        // Graft形式も <<<<<<< SEARCH を含むが、そのパスは <<<< FILE: 行の中にあり
        // 裸の行としては現れない。標準SR形式向けの「裸のパス行が要る」という絞り込みを
        // Graft形式にまで課すと、正しいパッチが一律で非検知になってしまう。
        var text =
            "<<<< FILE: src/calc.py\n" +
            "<<<<<<< SEARCH\n" +
            "def add(a, b):\n" +
            "=======\n" +
            "def add(a: int, b: int) -> int:\n" +
            ">>>>>>> REPLACE\n";

        PatchTextDetector.LooksLikePatch(text).Should().BeTrue();
    }

    // ==================================================================
    // パス指定行の書き方の揺れ
    // ==================================================================

    [Theory(DisplayName = "太字・引用符・末尾コロンなどの装飾が付いたパス行も受け付ける")]
    [InlineData("**src/calc.py**")]
    [InlineData("`src/calc.py`")]
    [InlineData("\"src/calc.py\"")]
    [InlineData("src/calc.py:")]
    [InlineData("  src/calc.py  ")]
    public void 装飾付きのパス行も受け付ける(string pathLine)
    {
        // Aiderは「太字にするな・引用符で囲むな」と明示的に指示しているが、実際のAIは
        // これらを付けてくる。安全性検査（TryNormalizePath）は装飾を外した後に必ず通すため、
        // ここで寛容にしても危険は増えない。
        var text =
            pathLine + "\n" +
            "<<<<<<< SEARCH\n" +
            "old\n" +
            "=======\n" +
            "new\n" +
            ">>>>>>> REPLACE\n";

        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeTrue();
        result.Value.Blocks.Single().Path.Should().Be("src/calc.py");
    }

    // ==================================================================
    // 通し確認（解析 → プレビュー(ドライラン) → 適用）
    // ==================================================================

    [Fact(DisplayName = "通し_標準SR形式のパッチが解析からプレビュー・適用まで既存パイプラインで通る")]
    public async Task 通し_解析からプレビュー適用まで通る()
    {
        // アダプタ方式（既存の内部表現へ変換するだけ）が本当に機能していることを、
        // 実ファイルを使った通し実行で確認する。部分修正・新規作成・削除の3種を同時に含める。
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        harness.WriteProjectText("src/calc.py", OriginalCalcPy);
        harness.WriteProjectText("src/old.py", "# 消される予定\n");

        var patchText =
            "src/calc.py\n" +
            "<<<<<<< SEARCH\n" +
            "def add(a, b):\n" +
            "    return a + b\n" +
            "=======\n" +
            "def add(a: int, b: int) -> int:\n" +
            "    return a + b\n" +
            ">>>>>>> REPLACE\n" +
            "\n" +
            "src/logger.py\n" +
            "<<<<<<< NEW_FILE\n" +
            "def log(message):\n" +
            "    print(message)\n" +
            ">>>>>>> END_FILE\n" +
            "\n" +
            "src/old.py\n" +
            "<<<<<<< DELETE_FILE\n" +
            ">>>>>>> END_FILE\n";

        var ctx = harness.MakeContext(1);
        var dryRun = await harness.DryRunAsync(patchText, ctx);
        dryRun.FailedCount.Should().Be(0, "3件とも適用可能と判定されるはず");

        var apply = await harness.ApplyAsync(dryRun, ctx);

        apply.IsSuccess.Should().BeTrue();
        Encoding.UTF8.GetString(harness.ReadProjectBytes("src/calc.py"))
            .Should().Contain("def add(a: int, b: int) -> int:", "部分修正が書き込まれるはず");
        harness.ProjectFileExists("src/logger.py").Should().BeTrue("NEW_FILEで新規作成されるはず");
        Encoding.UTF8.GetString(harness.ReadProjectBytes("src/logger.py"))
            .Should().Contain("def log(message):");
        harness.ProjectFileExists("src/old.py").Should().BeFalse("DELETE_FILEで削除されるはず");
    }
}
