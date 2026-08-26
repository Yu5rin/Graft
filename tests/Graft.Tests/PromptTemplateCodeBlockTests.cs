using FluentAssertions;
using Graft.Core;
using Graft.Features;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 利用者からの指摘対応（案件3）: 「サンプルプロンプトでAIに依頼したら、回答がコードブロック
/// （```）で出力されずコピーが面倒だった」。既定テンプレートへ「パッチ全体を```で囲んで
/// 出力してください」という指示を追記したことの回帰テスト。3つの観点で検証する。
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
/// (3) クリップボード自動検知（<see cref="PatchTextDetector"/>）との両立: 「パッチ全体を
///     コードブロックで囲む」という今回の指示変更により、単一フェンスで丸ごと囲まれた
///     パッチが既定シナリオになる。これを自動検知できないままでは指摘の解決にならない
///     ため、PatchTextDetector側にも「閉じたフェンスがちょうど1個のときはフェンスの
///     中身も含めて再判定する」段階2と「パスが実在しそうな見た目であることを要求する」
///     絞り込みを追加した（詳細は<see cref="PatchTextDetector"/>のクラスコメント・
///     <see cref="PatchTextDetectorTests"/>参照）。ここでは案件3の観点、すなわち
///     「本物のパッチは検知する／解説文書の例示とテンプレート自体のコピーは検知しない」
///     という要求を、実際のプロンプトテンプレート本文・解説文書風のテキストを使って固定する。
/// </summary>
public class PromptTemplateCodeBlockTests
{
    private const string CodeBlockPhrase = "コードブロックとして囲んで出力してください";

    [Fact(DisplayName = "初回用（完全版）はコードブロックの指示を含む")]
    public void 初回用はコードブロックの指示を含む()
        => Body("builtin-full").Should().Contain(CodeBlockPhrase);

    [Fact(DisplayName = "継続用（短縮版）はコードブロックの指示を含む")]
    public void 継続用はコードブロックの指示を含む()
        => Body("builtin-continuation").Should().Contain("```text・最終行```で囲んでください");

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

    [Fact(DisplayName = "初回用（Graft独自形式）はエスケープ規則も含む（従来欠けていた不整合の是正）")]
    public void 初回用はエスケープ規則も含む()
        => Body("builtin-graft-full").Should().Contain("【エスケープ規則】");

    // ------------------------------------------------------------------
    // 案件1・2の回帰: 実際の事故（PATCHメタの二重出力・1つ目が未閉鎖）を踏まえ、
    // 「<<<< PATCH は1回だけ」「開いたブロックは必ず閉じる」「出力前に自分で確認する」の
    // 3点が既定テンプレートに含まれることを確認する。
    // ------------------------------------------------------------------

    // 【v1.0.14での付け替え】既定テンプレート（builtin-full 等）の本文は標準SR形式へ切り替えたため、
    // Graft独自形式に固有の指示（<<<< PATCH メタ・MODE=FULL・エスケープ規則）の検証対象は
    // Graft独自形式のテンプレート（builtin-graft-*）へ移した。指示文そのものは一字も変えて
    // いないので、案件1・2で固定した内容は引き続き同じ強さで守られている。
    [Theory(DisplayName = "パッチを出力するGraft独自形式テンプレートは「<<<< PATCHは1回だけ」の指示を含む")]
    [InlineData("builtin-graft-full")]
    [InlineData("builtin-graft-fix-request")]
    [InlineData("builtin-graft-new-file")]
    public void パッチを出力するテンプレートはPATCHメタ1回だけの指示を含む(string id)
        => Body(id).Should().Contain("<<<< PATCH は出力全体で1回だけ");

    [Fact(DisplayName = "継続用（Graft独自形式）も「<<<< PATCHは1回だけ」の指示を含む（事故が起きやすい場面のため）")]
    public void 継続用もPATCHメタ1回だけの指示を含む()
        => Body("builtin-graft-continuation").Should().Contain("<<<< PATCH は1回だけ");

    [Theory(DisplayName = "パッチを出力するGraft独自形式テンプレートは終了マーカーの対応と出力前の自己確認を促す")]
    [InlineData("builtin-graft-full")]
    [InlineData("builtin-graft-fix-request")]
    [InlineData("builtin-graft-new-file")]
    public void パッチを出力するテンプレートは終了マーカーの対応と自己確認を促す(string id)
    {
        var body = Body(id);
        body.Should().Contain("======= と >>>>>>> REPLACE で、MODE=FULLは >>>> END で、開いたブロックは必ず閉じてください", "SEARCH/REPLACEとMODE=FULLの終了マーカーが具体的に示されているはず");
        body.Should().Contain("開始マーカーと終了マーカーの数が対応しているか自分で確認", "出力前の自己点検が促されているはず");
    }

    [Fact(DisplayName = "調査依頼はパッチを出力しないため「1回だけ」の指示も含まない")]
    public void 調査依頼はPATCHメタ1回だけの指示も含まない()
        => Body("builtin-investigate").Should().NotContain("<<<< PATCH は");

    // ------------------------------------------------------------------
    // 案件3の回帰: 「コピーボタンが出ない」対策として追加した、言語識別子の明示と
    // 本文に```を含む場合のバッククォート4個への切り替え指示。
    // ------------------------------------------------------------------

    [Theory(DisplayName = "パッチを出力するテンプレートは1行目・最終行の書き方を具体的に指示する")]
    [InlineData("builtin-full")]
    [InlineData("builtin-fix-request")]
    [InlineData("builtin-new-file")]
    [InlineData("builtin-graft-full")]
    [InlineData("builtin-graft-fix-request")]
    [InlineData("builtin-graft-new-file")]
    public void パッチを出力するテンプレートは1行目最終行を具体的に指示する(string id)
    {
        var body = Body(id);
        body.Should().Contain("```text", "言語識別子を明示する具体的な書き方が示されているはず");
        body.Should().Contain(" ``` が含まれる場合", "本文に```を含む場合の代替指示があるはず");
        body.Should().Contain("````text", "本文に```を含む場合はバッククォート4個にするよう具体的に示されているはず");
    }

    [Fact(DisplayName = "回帰_バッククォート4個で囲み、本文にMarkdownの```例示を含むAI出力も解析できる")]
    public void バッククォート4個で囲みMarkdown例示を含む出力も解析できる()
    {
        // CodeBlockWrapNoteが指示する「本文に```を含む場合は外側を````にする」形。
        // README.mdへの追記（内容自体に```で囲まれたコード例を含む）を模す。
        var text =
            "承知しました。\n\n" +
            "````text\n" +
            "<<<< PATCH\n" +
            "summary: READMEに使用例を追記する\n" +
            "type: docs\n" +
            ">>>>\n" +
            "\n" +
            "<<<< FILE: README.md MODE=FULL\n" +
            "# 使い方\n" +
            "```bash\n" +
            "echo hello\n" +
            "```\n" +
            ">>>> END\n" +
            "````\n";

        var result = new PatchParser().Parse(text);

        result.IsSuccess.Should().BeTrue("PatchScannerは```で始まる行（バッククォート3個以上すべて）を読み飛ばすため、" +
            "外側が4個・内側が3個でも解析できるはず");
        result.Value.Meta.Summary.Should().Be("READMEに使用例を追記する");
        result.Value.Blocks.Should().HaveCount(1);
        // 注意（既知の制約・本件の対応範囲外）: PatchScannerは"```"で始まる行を、外側の
        // ラップかどうかに関わらず一律で読み飛ばす実装のため、この本文中の"```bash"・"```"
        // （README.mdの中身として本来残したい行）も取り除かれ、Contentからは消える。
        // つまりバッククォート4個への切り替えは「チャットUIの表示・コピーボタン」問題
        // （案件3の対象）は解決するが、パッチ本文が文字どおり```を含む場合の内容保持は
        // 別問題として残る（PatchScanner.Createのfor文参照）。ここでは案件3の対象である
        // 解析成功（IsSuccess）とブロック数のみを確認し、内容の完全一致までは主張しない。
    }

    [Fact(DisplayName = "回帰_指示どおりパッチ全体を1つのコードブロックで囲んだAI出力を解析できる")]
    public void 指示どおり丸ごと囲んだ出力を解析できる()
    {
        var result = new PatchParser().Parse(SingleFencedRealPatch());

        result.IsSuccess.Should().BeTrue("PatchScannerが```行を読み飛ばすため、丸ごと囲まれていても解析できるはず");
        result.Value.Meta.Summary.Should().Be("挨拶関数を型安全にし、ログ出力を追加する");
        result.Value.Blocks.Should().HaveCount(2, "SEARCH/REPLACEブロックとFULLブロックの2件が認識されるはず");
    }

    // ------------------------------------------------------------------
    // クリップボード自動検知（PatchTextDetector）との両立。コーディネーターからの
    // 追加依頼（トレードオフの解消）に対する固定テスト。
    // ------------------------------------------------------------------

    [Fact(DisplayName = "回帰_単一フェンスで丸ごと囲まれた本物のパッチ（実在しそうなパス）は自動検知される")]
    public void 単一フェンスで丸ごと囲まれた本物のパッチは自動検知される()
    {
        // FullBody/FixRequestBody/NewFileBodyが指示する形（PATCHメタ＋複数のFILEブロックを
        // すべて含めて1つの外側```で囲む）を模した、AIの回答らしいテキスト。パスは
        // src/calc.py・src/logger.pyという実在しそうな見た目（拡張子・区切りを持つ）。
        PatchTextDetector.LooksLikePatch(SingleFencedRealPatch()).Should().BeTrue(
            "閉じたフェンスがちょうど1個で、中身が実在しそうなパスを持つ本物のパッチとして成立しているため");
    }

    [Fact(DisplayName = "回帰_解説文書の例示（コードブロック内で仮のパスを使う）は自動検知しない")]
    public void 解説文書の例示は自動検知しない()
    {
        // 取扱説明書・READMEのような解説文書を模したテキスト。無関係な地の文の中に
        // Graft形式の例示（コードブロック内）とunified diff形式の例示（別のコードブロック内）が
        // 独立して2つ存在する（実際のdocs/取扱説明書.mdやPatchTextDetectorTestsの
        // 「パッチ例をコードブロックに含むREADME」フィクスチャと同じ構造）。
        // フェンスが2個になるため、単一フェンス救済（段階2）の対象にならない。
        var manual =
            "# 使い方\n\n" +
            "AIには次の形式で出力してもらってください。\n\n" +
            "```\n" +
            "<<<< PATCH\n" +
            "summary: 例\n" +
            "type: fix\n" +
            ">>>>\n\n" +
            "<<<< FILE: src/app.py\n" +
            "<<<<<<< SEARCH\n" +
            "old\n" +
            "=======\n" +
            "new\n" +
            ">>>>>>> REPLACE\n" +
            "```\n\n" +
            "unified diff形式の例はこちらです。\n\n" +
            "```diff\n" +
            "--- a/src/app.py\n" +
            "+++ b/src/app.py\n" +
            "@@ -1,1 +1,1 @@\n" +
            "-old\n" +
            "+new\n" +
            "```\n\n" +
            "以上が使い方の説明です。\n";

        PatchTextDetector.LooksLikePatch(manual).Should().BeFalse(
            "解説文書の例示（コードブロックが2個以上に分かれている）は単一フェンス救済の対象外のまま");
    }

    [Fact(DisplayName = "回帰_既定プロンプトテンプレートの本文自体は自動検知しない（テンプレートのコピーが最頻出操作であるための確認）")]
    public void 既定テンプレートの本文自体は自動検知しない()
    {
        // 利用者がGraftの「プロンプト」ボタン等からテンプレート本文をそのままコピーする
        // ケース。テンプレート本文自体はコードブロックで囲まれていない（AIの将来の出力を
        // ```で囲むよう「指示」しているだけで、テンプレート自体がフェンスに入っている
        // わけではない）。<<<< PATCH・<<<< FILE: 相対パス という文字通りのマーカーを
        // フェンスの外に含むため、段階1がそのまま働く。パスの実在らしさ判定を追加する前は
        // ここが誤検知していた（既定テンプレートのパスは「相対パス」という仮の文字列で、
        // 拡張子も区切りも持たないため）。
        foreach (var id in new[]
                 {
                     "builtin-full", "builtin-fix-request", "builtin-new-file",
                     "builtin-graft-full", "builtin-graft-fix-request", "builtin-graft-new-file",
                 })
        {
            PatchTextDetector.LooksLikePatch(Body(id)).Should().BeFalse(
                $"{id} のテンプレート本文は「相対パス」という仮のパスしか持たないため誤検知してはいけない");
        }

        // 選択範囲の修正依頼プロンプト（修正依頼テンプレートと同じ形式指示を共用）も同様。
        var selectionPrompt = PromptTemplateStore.BuildSelectionFixRequestPrompt("a.cs", 1, 1, "code", ".cs");
        PatchTextDetector.LooksLikePatch(selectionPrompt).Should().BeFalse(
            "選択範囲の修正依頼プロンプトはヘッダ（<<<< PATCH等）自体を含まないため、元々検知対象にならない");
    }

    [Fact(DisplayName = "回帰_フェンスが閉じていない切断パッチは従来どおり自動検知される")]
    public void フェンスが閉じていない切断パッチは検知される()
    {
        // 実在しそうなパスを持つ本物のパッチが、コードフェンスを開いたまま
        // （閉じずに）AIの出力が途切れたケース。段階1（閉じていないフェンスの中身は
        // 除外しない既存の方針）でそのまま検知されるはずで、今回追加した段階2
        // （閉じたフェンスがちょうど1個のときの救済）には到達しない。
        var truncated =
            "以下のとおり修正します。\n\n" +
            "```\n" +
            "<<<< FILE: src/app.py\n" +
            "<<<<<<< SEARCH\n" +
            "def greet(name):\n" +
            "    pass\n" +
            "=======\n" +
            "def greet(name):\n";
            // ">>>>>>> REPLACE" も閉じの "```" も無いまま入力が尽きる

        PatchTextDetector.LooksLikePatch(truncated).Should().BeTrue(
            "閉じていないコードフェンスの中身は除外せず、切断パッチとして従来どおり検知するはず");
    }

    /// <summary>
    /// FullBody/FixRequestBody/NewFileBodyが指示する形（PATCHメタ＋複数のFILEブロックを
    /// すべて含めて1つの外側```で囲む）を模した、実在しそうなパスを持つAIの回答らしいテキスト。
    /// </summary>
    private static string SingleFencedRealPatch() =>
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

    private static string Body(string id)
        => PromptTemplateStore.BuiltIns.Single(t => t.Id == id).Body;
}
