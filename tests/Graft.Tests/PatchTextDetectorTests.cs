using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using FluentAssertions;
using Graft.Core;
using Graft.Tests.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace Graft.Tests;

/// <summary>
/// クリップボード監視（仕様書9章・10章）の「パッチらしさ」判定。
/// WindowsとLinuxの監視実装が共用するため、判定基準をここで固定する。
///
/// 【誤検知の修正1】取扱説明書のようにパッチの書き方をコードブロックで例示している
/// だけの文書を、内容をコピーしただけで検知してしまう不具合の回帰テストを含む
/// （詳細は<see cref="PatchTextDetector"/>のクラスコメント参照）。基本の判定は
/// 「マーカーがコードブロックの外にあり、かつ対応関係（PatchParserで構造として
/// 成立しているか）が揃っている」ことを条件とする（段階1）。ただし見逃し（本物の
/// パッチを検知しない）の方が誤検知より害が大きいため、パスが不正・SEARCHが空と
/// いった「内容の誤り」で構造そのものは認識できているケースまでは非検知にしない
/// （E001＝何も認識できなかった場合のみ非検知とする）。
///
/// 【誤検知の修正2・案件3】プロンプトテンプレート（<c>PromptTemplateStore</c>）自身が
/// パスの仮の値として文字通り「相対パス」というプレースホルダを使っているため、
/// 利用者がテンプレートをコピーしただけで（AIに何も聞く前から）誤検知していた不具合の
/// 回帰テストを含む。Graft形式のパス付きヘッダが1つ以上あるときは、少なくとも1つの
/// パス欄が実在するファイルパスらしい見た目（拡張子または区切りを持つ）であることを
/// 追加で要求する（段階1・段階2のどちらにも適用）。
///
/// 【見逃しの救済・案件3】プロンプトテンプレートへ「パッチ全体を1つの```で囲んで
/// 出力する」指示を追加した結果、単一のコードフェンスで丸ごと囲まれたパッチが既定
/// シナリオになったため、段階1で見つからない場合に限り、閉じたフェンスがテキスト
/// 全体でちょうど1個だけのケースについてフェンスの中身も含めて再判定する（段階2）。
/// 解説文書のように複数の例示が独立して散らばっている文書はフェンスが2個以上になる
/// ため対象外のままである。
/// </summary>
public class PatchTextDetectorTests
{
    private readonly ITestOutputHelper _output;

    public PatchTextDetectorTests(ITestOutputHelper output) => _output = output;

    // ------------------------------------------------------------------
    // 既存の基本挙動（変更なし）
    // ------------------------------------------------------------------

    [Theory(DisplayName = "ブロックヘッダを含むテキストはパッチとみなす")]
    [InlineData("<<<< FILE: src/a.cs")]
    [InlineData("<<<< PATCH")]
    [InlineData("<<<< DELETE: src/a.cs")]
    [InlineData("<<<< RENAME: a.cs -> b.cs")]
    [InlineData("<<<< MKDIR: src/new")]
    [InlineData("<<<< APPEND: src/a.cs")]
    [InlineData("<<<< PREPEND: src/a.cs")]
    [InlineData("<<<<<<< SEARCH")]
    public void ブロックヘッダを含むテキストはパッチとみなす(string header)
        => PatchTextDetector.LooksLikePatch(header).Should().BeTrue();

    [Fact(DisplayName = "先頭以外の行にヘッダがあっても検知する")]
    public void 途中の行のヘッダも検知する()
        => PatchTextDetector.LooksLikePatch("説明文\n\n<<<< FILE: src/a.cs\n").Should().BeTrue();

    [Fact(DisplayName = "行頭以外に現れるヘッダらしき文字列は検知しない")]
    public void 行の途中のヘッダは検知しない()
        => PatchTextDetector.LooksLikePatch("この記法 <<<< FILE: について説明します").Should().BeFalse();

    [Theory(DisplayName = "通常のコピー内容はパッチとみなさない")]
    [InlineData("")]
    [InlineData("ふつうの文章")]
    [InlineData("var x = 1;")]
    [InlineData("---\n見出し\n---")]
    public void 通常のテキストは検知しない(string text)
        => PatchTextDetector.LooksLikePatch(text).Should().BeFalse();

    [Fact(DisplayName = "unified diffもパッチとみなす")]
    public void unified_diffもパッチとみなす()
    {
        var diff = "--- a/src/a.py\n+++ b/src/a.py\n@@ -1,1 +1,1 @@\n-old\n+new\n";
        PatchTextDetector.LooksLikePatch(diff).Should().BeTrue();
        PatchTextDetector.HasGraftMarker(diff).Should().BeFalse("Graft形式のマーカーは含まれないはず");
    }

    // ------------------------------------------------------------------
    // 回帰: 取扱説明書のようなパッチ例を含む解説文書での誤検知
    // ------------------------------------------------------------------

    [Fact(DisplayName = "回帰_取扱説明書.mdの実際の内容をコピーしても検知しない（利用者報告の再現）")]
    public void 取扱説明書の内容では検知しない()
    {
        var text = LoadManualMarkdown();
        text.Should().Contain("<<<< PATCH", "この回帰テストが本当に例示マーカーを含む本文を検証していることの確認");
        text.Should().Contain("<<<<<<< SEARCH");

        PatchTextDetector.LooksLikePatch(text).Should().BeFalse(
            "3章のパッチ例はすべてコードブロック内にあり、それ以外の行にマーカーは現れないはず");
    }

    [Fact(DisplayName = "取扱説明書の内容は手動の「解析」（PatchParser直接呼び出し）では従来どおり動作する")]
    public void 取扱説明書の内容は手動解析には影響しない()
    {
        // クリップボード自動検知だけを厳しくしており、手動の「解析」ボタンが呼ぶ
        // PatchParser.Parse自体には一切手を入れていないことの確認（影響範囲の境界確認）。
        var text = LoadManualMarkdown();
        var act = () => new PatchParser().Parse(text);
        act.Should().NotThrow();
    }

    [Fact(DisplayName = "回帰_パッチ例をコードブロックに含むREADMEでは検知しない")]
    public void パッチ例をコードブロックに含むREADMEでは検知しない()
    {
        var readme =
            "# サンプルプロジェクト\n\n" +
            "## 概要\n\n" +
            "このリポジトリはサンプルです。バグ報告や機能要望はIssueへお願いします。\n\n" +
            "## AIへの依頼の書き方\n\n" +
            "コードの修正はAIに次の形式で出力してもらってください。\n\n" +
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
            "## unified diff形式の例\n\n" +
            "`git diff`の出力もそのまま貼り付けられます。\n\n" +
            "```diff\n" +
            "--- a/src/app.py\n" +
            "+++ b/src/app.py\n" +
            "@@ -1,1 +1,1 @@\n" +
            "-old\n" +
            "+new\n" +
            "```\n\n" +
            "## ライセンス\n\nMITライセンスです。\n";

        PatchTextDetector.LooksLikePatch(readme).Should().BeFalse(
            "Graft形式・unified diff形式どちらの例もコードブロック内にしか無いはず");
    }

    [Fact(DisplayName = "コードブロックの外に書かれた本物のパッチは検知される")]
    public void コードブロック外の本物のパッチは検知される()
    {
        var text =
            "以下のとおり修正します。\n\n" +
            "<<<< FILE: src/app.py\n" +
            "<<<<<<< SEARCH\n" +
            "def greet(name):\n" +
            "    pass\n" +
            "=======\n" +
            "def greet(name):\n" +
            "    return f\"Hello, {name}!\"\n" +
            ">>>>>>> REPLACE\n";

        PatchTextDetector.LooksLikePatch(text).Should().BeTrue();
    }

    [Fact(DisplayName = "コードブロックの外に書かれた本物のunified diffは検知される")]
    public void コードブロック外の本物のunifieddiffは検知される()
    {
        var text =
            "以下のとおり修正します。\n\n" +
            "--- a/src/app.py\n" +
            "+++ b/src/app.py\n" +
            "@@ -1,2 +1,2 @@\n" +
            "-def greet(name):\n" +
            "-    pass\n" +
            "+def greet(name):\n" +
            "+    return f\"Hello, {name}!\"\n";

        PatchTextDetector.LooksLikePatch(text).Should().BeTrue();
    }

    [Fact(DisplayName = "unified diffを解説しただけの文書（本文中に断片的に出てくるだけ）は検知しない")]
    public void unifieddiffを解説しただけの文書は検知しない()
    {
        var text =
            "# 差分形式について\n\n" +
            "unified diffは `--- a/ファイル` / `+++ b/ファイル` / `@@ ... @@` から始まる形式です。\n" +
            "詳しくは man diff を参照してください。実際の適用にはpatchコマンドを使います。\n" +
            "このほかにもcontext diff等の形式がありますが、ここでは扱いません。\n";

        PatchTextDetector.LooksLikePatch(text).Should().BeFalse(
            "地の文の中に触れられているだけで、行頭にヘッダ対＋ハンクが揃っていないはず");
    }

    // ------------------------------------------------------------------
    // 回帰: 見逃し防止（既存のサンプルパッチ・各種フィクスチャがすべて検知されること）
    // ------------------------------------------------------------------

    [Fact(DisplayName = "見逃し防止_OnboardingSample相当のパッチが検知される")]
    public void OnboardingSample相当のパッチが検知される()
    {
        // OnboardingSample.csが生成する文面をそのまま再現する
        // （tools/・OnboardingSample.cs自体は今回変更しないため、文面をここへ複製して検証する）。
        const string patchText =
            "<<<< PATCH\n" +
            "summary: サンプル: greet関数であいさつ文を組み立てて返すようにする\n" +
            "type: feat\n" +
            ">>>>\n" +
            "\n" +
            "<<<< FILE: hello.py\n" +
            "<<<<<<< SEARCH\n" +
            "def greet(name):\n" +
            "    pass\n" +
            "=======\n" +
            "def greet(name):\n" +
            "    return f\"Hello, {name}!\"\n" +
            ">>>>>>> REPLACE\n";

        PatchTextDetector.LooksLikePatch(patchText).Should().BeTrue();
    }

    [Theory(DisplayName = "見逃し防止_既存のパッチフィクスチャは引き続きすべて検知される")]
    [InlineData("append_block")]
    [InlineData("crlf_kaigyou")]
    [InlineData("delete_block")]
    [InlineData("escape_kaijo")]
    [InlineData("fence_shitei")]
    [InlineData("full_keishiki")]
    [InlineData("gaibu_setsumei")]
    [InlineData("headerline_kensho")]
    // "markdown_fence" は単一フェンスで丸ごと囲まれた実例のため、専用テスト
    // （見逃し防止_単一フェンスで丸ごと囲まれたパッチは...）で別途扱う（このループでは対象外）。
    [InlineData("mimikaeshi_marker_e006")]
    [InlineData("mkdir_block")]
    [InlineData("occurrence_2")]
    [InlineData("occurrence_all")]
    [InlineData("patch_meta_full")]
    [InlineData("path_dotdot")]
    [InlineData("path_zettai_drive")]
    [InlineData("path_zettai_slash")]
    [InlineData("prepend_block")]
    [InlineData("rename_block")]
    [InlineData("replace_karano_sakujo")]
    [InlineData("search_karano_e003")]
    [InlineData("search_range")]
    [InlineData("setsudan_e005")]
    [InlineData("sr_1pea")]
    [InlineData("sr_fukusuu_file")]
    [InlineData("sr_fukusuu_pair")]
    [InlineData("summary_kesson_e004")]
    public void 既存フィクスチャは検知される(string fixtureName)
    {
        var text = FixtureLoader.LoadPatch(fixtureName);
        PatchTextDetector.LooksLikePatch(text).Should().BeTrue(
            $"{fixtureName} はパッチとしての構造（対応関係）自体は成立しており、" +
            "内容の誤り（パス不正・SEARCH空など）があっても検知側では見逃さないはず");
    }

    [Fact(DisplayName = "block_zero_e001フィクスチャはそもそもパッチではないため検知しない")]
    public void block_zero_e001は検知しない()
    {
        // このフィクスチャだけは「マーカーもブロックも一切無い、ただの説明文」を
        // 意図的に表現したものなので、旧実装・新実装のいずれでも非検知が正しい。
        var text = FixtureLoader.LoadPatch("block_zero_e001");
        PatchTextDetector.LooksLikePatch(text).Should().BeFalse();
    }

    [Fact(DisplayName = "見逃し防止_単一フェンスで丸ごと囲まれた本物のパッチはクリップボード自動検知の対象になる（案件3で方針転換）")]
    public void 単一フェンスで丸ごと囲まれた本物のパッチは自動検知される()
    {
        // markdown_fence.txt: 「以下がパッチです。」+ ```で丸ごと囲まれたパッチ（実在する
        // 見た目のパス src/calc.py を使う本物のSEARCH/REPLACEブロック）+ 「以上です。」
        // という、AIがチャットの表示上パッチ全体をコードブロックで囲んだ場合の実例。
        // これは解説文書の「例示」ではなく、内容そのものは正当な本物のパッチである。
        //
        // 【方針転換の理由】 当初は「コードブロック外のマーカーだけを見る」方針を採り、
        // このケースは意図的に自動検知の対象外としていた。しかしプロンプトテンプレート
        // （PromptTemplateStore）へ「パッチ全体を1つの```で囲んで出力する」指示を追加した
        // 結果、このケースが「稀な例外」から「AIが指示に従った場合の既定シナリオ」へ
        // 変わったため、自動検知できないままでは既定の使い方で通知が出なくなってしまう。
        // そこで、閉じたフェンスがテキスト全体でちょうど1個だけという条件に絞って
        // フェンスの中身も含めて再判定する第二段階を追加した（PatchTextDetectorの
        // クラスコメント参照）。取扱説明書・READMEのような、無関係な地の文の中に
        // 複数のコードブロック例示が独立して散らばっている解説文書（下の別テストが
        // 検証する）は、フェンスが2個以上になるためこの第二段階の対象にならず、
        // 引き続き非検知のままである。
        var text = FixtureLoader.LoadPatch("markdown_fence");

        PatchTextDetector.LooksLikePatch(text).Should().BeTrue(
            "閉じたフェンスがちょうど1個だけで、その中身が実在するパスらしい見た目の本物のパッチとして成立しているため、案件3以降は自動検知の対象にする");

        var act = () => new PatchParser().Parse(text);
        act.Should().NotThrow();
        act().IsSuccess.Should().BeTrue("手動の「解析」は従来どおり成功するはず");
    }

    // ------------------------------------------------------------------
    // 回帰: 途中で切れたパッチ（4.10・切断検出）の扱いが壊れないこと
    // ------------------------------------------------------------------

    [Fact(DisplayName = "回帰_途中で切れたパッチも引き続き検知される")]
    public void 途中で切れたパッチも検知される()
    {
        var text = FixtureLoader.LoadPatch("setsudan_e005");
        PatchTextDetector.LooksLikePatch(text).Should().BeTrue();

        var result = new PatchParser().Parse(text);
        result.IsSuccess.Should().BeTrue();
        result.Value.IsTruncated.Should().BeTrue("切断パッチの扱い自体は今回変更していないはず");
    }

    [Fact(DisplayName = "回帰_コードフェンスの途中でAIの出力が切れた場合も検知される")]
    public void コードフェンス途中で切れたパッチも検知される()
    {
        // ```で開いたまま閉じずに入力が尽きるケース（AIの出力がコードフェンスの
        // 内側で途切れた場合）。閉じていないフェンスの中身は「除外しない」方針の確認。
        var text =
            "以下のとおり修正します。\n\n" +
            "```\n" +
            "<<<< FILE: src/app.py\n" +
            "<<<<<<< SEARCH\n" +
            "def greet(name):\n" +
            "    pass\n" +
            "=======\n" +
            "def greet(name):\n";
            // ">>>>>>> REPLACE" も閉じの "```" も無いまま入力が尽きる

        PatchTextDetector.LooksLikePatch(text).Should().BeTrue(
            "閉じていないコードフェンスの中身は除外せず、切断パッチとして検知するはず");
    }

    // ------------------------------------------------------------------
    // 性能: クリップボードが変わるたびに走るため、大きなテキストでも高速であること
    // ------------------------------------------------------------------

    [Fact(DisplayName = "性能_大きなテキスト(非パッチ)でも高速に判定できる")]
    public void 大きなテキスト非パッチでも高速()
    {
        // 数百KB規模の「パッチではない」テキスト（マーカーを含まない、取扱説明書の
        // ような長文の解説文書を模した内容）。安価な前判定だけで打ち切れるはず。
        var sb = new StringBuilder();
        var paragraph = "これはGraftの取扱説明書のような、通常のコピー内容を模した長文です。" +
            "AI（ChatGPT・Claudeなど）が提案したコードの変更を、手元のプロジェクトへ安全に取り込むためのデスクトップアプリです。\n";
        while (sb.Length < 500_000) sb.Append(paragraph);
        var text = sb.ToString();

        var sw = Stopwatch.StartNew();
        var detected = PatchTextDetector.LooksLikePatch(text);
        sw.Stop();

        detected.Should().BeFalse();
        _output.WriteLine($"非パッチ約{text.Length / 1024}KB: {sw.Elapsed.TotalMilliseconds:F2}ms");
        sw.Elapsed.TotalMilliseconds.Should().BeLessThan(200,
            "クリップボードが変わるたびに走る判定なので、体感で引っかからない速さである必要がある");
    }

    [Fact(DisplayName = "性能_大きな文書内にコードブロック例示があっても高速に判定できる")]
    public void 大きな文書内のコードブロック例示でも高速()
    {
        // 取扱説明書のように、長い地の文の中に数個のコードブロック例示（フェンス）が
        // 混在するケース。フェンス除去＋前判定＋（該当時のみ）実解析までを含めた実測。
        var manual = LoadManualMarkdown();
        var sb = new StringBuilder();
        while (sb.Length < 500_000) sb.Append(manual).Append('\n');
        var text = sb.ToString();

        var sw = Stopwatch.StartNew();
        var detected = PatchTextDetector.LooksLikePatch(text);
        sw.Stop();

        detected.Should().BeFalse();
        _output.WriteLine($"取扱説明書を繰り返した約{text.Length / 1024}KB: {sw.Elapsed.TotalMilliseconds:F2}ms");
        sw.Elapsed.TotalMilliseconds.Should().BeLessThan(500,
            "コードブロックを含む長文でも体感で引っかからない速さである必要がある");
    }

    [Fact(DisplayName = "性能_大きな本物のパッチでも高速に判定できる")]
    public void 大きな本物のパッチでも高速()
    {
        // コードブロックの外に大量のSEARCH/REPLACEブロックが並ぶ、大規模な本物のパッチ。
        // 実解析（PatchParser.Parse）まで通る側のケースでも速いことを確認する。
        var sb = new StringBuilder();
        sb.Append("<<<< PATCH\nsummary: 大規模パッチの性能測定\ntype: refactor\n>>>>\n\n");
        for (var i = 0; i < 2000; i++)
        {
            sb.Append($"<<<< FILE: src/module_{i}.py\n");
            sb.Append("<<<<<<< SEARCH\n");
            sb.Append($"old_value_{i} = {i}\n");
            sb.Append("=======\n");
            sb.Append($"new_value_{i} = {i}\n");
            sb.Append(">>>>>>> REPLACE\n\n");
        }
        var text = sb.ToString();

        var sw = Stopwatch.StartNew();
        var detected = PatchTextDetector.LooksLikePatch(text);
        sw.Stop();

        detected.Should().BeTrue();
        _output.WriteLine($"本物の大規模パッチ約{text.Length / 1024}KB（2000ブロック）: {sw.Elapsed.TotalMilliseconds:F2}ms");
        sw.Elapsed.TotalMilliseconds.Should().BeLessThan(1000,
            "実解析まで通るケースでも体感で引っかからない速さである必要がある");
    }

    // ------------------------------------------------------------------
    // ヘルパ
    // ------------------------------------------------------------------

    /// <summary>
    /// docs/取扱説明書.md を、テストソースファイルの位置からの相対パスで読み込む
    /// （Graft.Testsはビルド高速化のためGraft本体を参照せず埋め込みリソースを
    /// 経由できないので、ソースツリーから直接読む。Graft.csproj側の埋め込み設定
    /// <c>&lt;EmbeddedResource Include="..\..\docs\取扱説明書.md" .../&gt;</c> と
    /// 同じファイルを指している）。
    /// </summary>
    private static string LoadManualMarkdown([CallerFilePath] string sourceFilePath = "")
    {
        var testsDir = Path.GetDirectoryName(sourceFilePath)!; // .../tests/Graft.Tests
        var repoRoot = Path.GetFullPath(Path.Combine(testsDir, "..", ".."));
        var path = Path.Combine(repoRoot, "docs", "取扱説明書.md");
        if (!File.Exists(path))
            throw new FileNotFoundException($"取扱説明書.mdが見つかりません: {path}", path);
        return File.ReadAllText(path);
    }
}
