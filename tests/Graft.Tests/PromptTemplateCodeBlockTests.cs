using FluentAssertions;
using Graft.Core;
using Graft.Features;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 利用者からの指摘対応（案件3）: 「サンプルプロンプトでAIに依頼したら、回答がコードブロック
/// （```）で出力されずコピーが面倒だった」。既定テンプレートへ「パッチ全体を```で囲んで
/// 出力してください」という指示を追記したことの回帰テスト。2つの観点で検証する。
///
/// (1) 構造: パッチを出力させるテンプレート（初回用・修正依頼・新規実装・継続用）には
///     コードブロックの指示が入っており、パッチを出力しない「調査依頼」には入っていないこと
///     （<see cref="PromptTemplateStore.BuiltIns"/>経由。定数は private のためBodyの内容で見る）。
/// (2) 実際の取り込み: 指示どおりAIがパッチ全体を1つの```で囲んで出力した場合、
///     <see cref="PatchParser"/>（貼り付け→解析の実処理）が問題なく解析できること。
///     Graft側の取り込み自体は既存のPatchScanner（```で始まる行を読み飛ばす。
///     PatchParserTests.Markdownコードフェンス混在でも解析できる 参照）で対応済みだが、
///     今回追加した指示の文面どおりの形（PATCHメタ＋複数のFILEブロックすべてを
///     1つの外側フェンスで囲む）で実際に成立することをここで確認する。
/// </summary>
public class PromptTemplateCodeBlockTests
{
    private const string CodeBlockPhrase = "コードブロックとして囲んで出力してください";

    [Fact(DisplayName = "初回用（完全版）はコードブロックの指示を含む")]
    public void 初回用はコードブロックの指示を含む()
        => Body("builtin-full").Should().Contain(CodeBlockPhrase);

    [Fact(DisplayName = "継続用（短縮版）はコードブロックの指示を含む")]
    public void 継続用はコードブロックの指示を含む()
        => Body("builtin-continuation").Should().Contain("```で囲んで出力してください");

    [Fact(DisplayName = "修正依頼はコードブロックの指示を含む")]
    public void 修正依頼はコードブロックの指示を含む()
        => Body("builtin-fix-request").Should().Contain(CodeBlockPhrase);

    [Fact(DisplayName = "新規実装はコードブロックの指示を含む")]
    public void 新規実装はコードブロックの指示を含む()
        => Body("builtin-new-file").Should().Contain(CodeBlockPhrase);

    [Fact(DisplayName = "選択範囲の修正依頼プロンプトもコードブロックの指示を含む（修正依頼テンプレートと共用のため）")]
    public void 選択範囲の修正依頼プロンプトもコードブロックの指示を含む()
    {
        var prompt = PromptTemplateStore.BuildSelectionFixRequestPrompt("a.cs", 1, 1, "code", ".cs");
        prompt.Should().Contain(CodeBlockPhrase);
    }

    [Fact(DisplayName = "調査依頼はパッチを出力しないためコードブロックの指示を含まない")]
    public void 調査依頼はコードブロックの指示を含まない()
        => Body("builtin-investigate").Should().NotContain(CodeBlockPhrase);

    [Fact(DisplayName = "初回用（完全版）はエスケープ規則も含む（従来欠けていた不整合の是正）")]
    public void 初回用はエスケープ規則も含む()
        => Body("builtin-full").Should().Contain("【エスケープ規則】");

    [Fact(DisplayName = "回帰_指示どおりパッチ全体を1つのコードブロックで囲んだAI出力を解析できる")]
    public void 指示どおり丸ごと囲んだ出力を解析できる()
    {
        // FullBody/FixRequestBody/NewFileBodyが指示する形（PATCHメタ＋複数のFILEブロックを
        // すべて含めて1つの外側```で囲む）を模した、AIの回答らしいテキスト。
        var aiResponse =
            "承知しました。以下のとおり修正します。\n\n" +
            "```\n" +
            "<<<< PATCH\n" +
            "summary: 挨拶関数を型安全にし、ログ出力を追加する\n" +
            "type: refactor\n" +
            ">>>>\n" +
            "\n" +
            "<<<< FILE: src/calc.py\n" +
            "<<<<<<< SEARCH  # 加算関数を修正\n" +
            "def add(a, b):\n" +
            "    return a + b\n" +
            "=======\n" +
            "def add(a: int, b: int) -> int:\n" +
            "    return a + b\n" +
            ">>>>>>> REPLACE\n" +
            "\n" +
            "<<<< FILE: src/logger.py MODE=FULL\n" +
            "def log(message):\n" +
            "    print(message)\n" +
            ">>>> END\n" +
            "```\n\n" +
            "以上です。何か問題があればお知らせください。";

        var result = new PatchParser().Parse(aiResponse);

        result.IsSuccess.Should().BeTrue("PatchScannerが```行を読み飛ばすため、丸ごと囲まれていても解析できるはず");
        result.Value.Meta.Summary.Should().Be("挨拶関数を型安全にし、ログ出力を追加する");
        result.Value.Blocks.Should().HaveCount(2, "SEARCH/REPLACEブロックとFULLブロックの2件が認識されるはず");
    }

    [Fact(DisplayName = "回帰_丸ごと囲んだ出力はクリップボード自動検知の対象外になる（既知のトレードオフの明示）")]
    public void 丸ごと囲んだ出力は自動検知の対象外になる()
    {
        // PatchTextDetector（クリップボード監視）は「単一のコードフェンスで丸ごと囲まれた
        // パッチは自動検知しない」既存の意図的な仕様を持つ（PatchTextDetectorTests.
        // 単一フェンスで丸ごと囲まれたパッチは自動検知しないが手動解析はできる 参照）。
        // 案件3でAIへ「パッチ全体を```で囲む」よう指示した結果、この既存のレアケース扱いが
        // 今後は既定シナリオになる。自動検知が働かなくなる一方、貼り付け→解析
        // （PatchScanner経由）は影響を受けないことを、今回追加した指示の文面で改めて確認する。
        var aiResponse =
            "```\n" +
            "<<<< FILE: src/calc.py\n" +
            "<<<<<<< SEARCH  # 加算関数を修正\n" +
            "def add(a, b):\n" +
            "    return a + b\n" +
            "=======\n" +
            "def add(a: int, b: int) -> int:\n" +
            "    return a + b\n" +
            ">>>>>>> REPLACE\n" +
            "```\n";

        PatchTextDetector.LooksLikePatch(aiResponse).Should().BeFalse(
            "マーカーが単一のコードフェンスの中に丸ごとあるため、クリップボード自動検知の対象にはならない（既知のトレードオフ）");

        var act = () => new PatchParser().Parse(aiResponse);
        act.Should().NotThrow();
        act().IsSuccess.Should().BeTrue("手動の貼り付け→解析は従来どおり成功するはず");
    }

    private static string Body(string id)
        => PromptTemplateStore.BuiltIns.Single(t => t.Id == id).Body;
}
