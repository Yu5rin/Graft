using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Core;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 実機不具合対応の回帰テスト: 「パッチ適用時にインデントが1文字削られる」という利用者報告の
/// 再発防止を固定する。真犯人は段階3（相対インデント一致）の補正処理
/// （<see cref="MatchEngine"/>・<see cref="TextNormalizer.ApplyIndentCorrection"/>）であり、
/// 補正の前提（AIがブロック全体を別の基準インデントで書いた）が崩れている状況で機械的に
/// 文字を書き換えていたことが原因だった。
///
/// このクラスは <see cref="ApplyHarness"/> を使い、一時ディレクトリ上の実ファイルへ
/// ドライラン→本適用まで実際に通し、書き込まれた結果を必ずバイト単位で比較する
/// （<see cref="MatchEngineTests"/> の段階3テストはインデント補正のロジック単体を検証する
/// ものであり、実際にディスクへ書き込まれるバイト列までは検証していないため、
/// このクラスで補う）。
///
/// 末尾（項目7・8）は利用者自身が疑った仮説（マーカー行の除去で直後の行の先頭文字が
/// 巻き込まれる／CRLFのパッチ本文で行分割が1文字ずれる）を検証したものだが、
/// 調査の結果どちらも現状のコードは正しく、成立していないことを確認済みである。
/// 「もう正しいから書かない」のではなく、将来のリファクタでこの2点が壊れたときに
/// 誰かが気づけるよう、否定された仮説の結果自体を回帰テストとして固定する。
/// </summary>
public class IndentPreservationTests
{
    // ------------------------------------------------------------------
    // パッチ本文組み立てヘルパー（ApplyEngineTests・RestoreThroughTests と同じ流儀）。
    // ------------------------------------------------------------------

    private static string BuildSrPatch(string path, string search, string replace)
        => $"<<<< FILE: {path}\n<<<<<<< SEARCH\n{search}\n=======\n{replace}\n>>>>>>> REPLACE\n";

    private static string BuildFullPatch(string path, string content)
        => $"<<<< FILE: {path} MODE=FULL\n{content}\n>>>> END\n";

    private static string BuildAppendPatch(string path, string content)
        => $"<<<< APPEND: {path}\n{content}\n>>>> END\n";

    private static string BuildPrependPatch(string path, string content)
        => $"<<<< PREPEND: {path}\n{content}\n>>>> END\n";

    /// <summary>harnessを介して1件のパッチをドライラン→適用まで実際に通し、成功を前提として返す。</summary>
    private static async Task ApplyRealAsync(ApplyHarness harness, int revision, string patchText)
    {
        var ctx = harness.MakeContext(revision);
        var dryRun = await harness.DryRunAsync(patchText, ctx);
        var applied = await harness.ApplyAsync(dryRun, ctx);
        applied.IsSuccess.Should().BeTrue(
            "テスト対象のシナリオは適用に成功するはず: "
            + string.Join(",", applied.Issues.Select(i => i.ToDisplayText())));
    }

    // ====================================================================
    // 項目1: 今回の報告の再現
    // ====================================================================

    [Fact(DisplayName = "実機報告の再現: SEARCH先頭行だけ1文字多いインデントでも、正しく書かれたREPLACEの4/8スペースは潰されず保全される")]
    public async Task 実機報告の再現_SEARCH先頭行のみ多いインデントでもREPLACEが保全される()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);

        // 実ファイルは4/8スペースのインデント（実機報告のcontent.jsを模す）。
        var original = "function outer() {\n    if (ready) {\n    }\n    return 1;\n}\n";
        harness.WriteProjectText("report.js", original);

        // AIのSEARCH部は「if (ready) {」の1行だけで、先頭に余分な1スペース（5スペース）が
        // 付いている（実機報告どおりの誤り）。REPLACEは正しく4/8スペースで書かれている。
        var search = "     if (ready) {";
        var replace = "    if (ready) {\n        doStuff();\n        doMore();\n    }";
        var patchText = BuildSrPatch("report.js", search, replace);

        await ApplyRealAsync(harness, 1, patchText);

        // 段階1・2はインデント差で外れ、段階3（相対インデント一致。SEARCHが1行のため
        // 相対オフセットは常に0同士で一致する）でマッチする。delta = 4-5 = -1 だが、
        // SEARCH基準（5）とREPLACE基準（4）が食い違うため修正1のゲート2が働き、
        // 補正は行われずREPLACEがそのまま書き込まれるはず（3/7スペースへは潰れない）。
        var expected = "function outer() {\n"
            + "    if (ready) {\n"
            + "        doStuff();\n"
            + "        doMore();\n"
            + "    }\n"
            + "    }\n"
            + "    return 1;\n"
            + "}\n";
        harness.ReadProjectBytes("report.js").Should().Equal(Encoding.UTF8.GetBytes(expected),
            "正しく書かれたREPLACEの4/8スペースが3/7スペースへ潰れてはならない");
    }

    // ====================================================================
    // 項目2: インデント1個・2個・4個それぞれでバイト単位一致
    // ====================================================================

    [Theory(DisplayName = "段階3補正: SEARCH/REPLACEの基準が一致していれば、delta 1・2・4文字いずれも書き込み結果がバイト単位で一致する")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task 段階3補正_deltaごとにバイト単位で一致する(int n)
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);

        var indent = new string(' ', n);
        var bodyIndent = new string(' ', n + 1);
        var original = "def use():\n" + indent + "if flag:\n" + bodyIndent + "value = compute()\n"
            + bodyIndent + "return value\n";
        harness.WriteProjectText($"delta{n}.py", original);

        // SEARCH/REPLACEともに基準0・本文相対+1というAI側の書き方（コピー元の実際の
        // ネストぶんのインデントを省略してしまうケース）を模す。SEARCH基準とREPLACE基準は
        // どちらも0で一致するため修正1のゲート2は働かず、従来どおり段階3の補正が実行される。
        var search = "if flag:\n value = compute()\n return value";
        var replace = "if flag:\n value = compute() * 2\n return value";
        var patchText = BuildSrPatch($"delta{n}.py", search, replace);

        await ApplyRealAsync(harness, 1, patchText);

        var expected = "def use():\n" + indent + "if flag:\n" + bodyIndent + "value = compute() * 2\n"
            + bodyIndent + "return value\n";
        harness.ReadProjectBytes($"delta{n}.py").Should().Equal(Encoding.UTF8.GetBytes(expected),
            $"delta={n}の補正結果はバイト単位で一致するはず");
    }

    // ====================================================================
    // 項目3: タブ・スペース混在、空行を含む本文の保全（delta==0でゲート1が働く場合）
    // ====================================================================

    [Fact(DisplayName = "ゲート1: delta==0のときは補正自体を行わないため、タブ・スペースが混在し空行を含むREPLACE本文もバイト単位で保全される")]
    public async Task ゲート1_混在本文と空行を含む本文が保全される()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);

        // ファイルはタブインデント。行間に空行を1行挟む。
        var original = "def use():\n\tif flag:\n\n\t\tvalue = compute()\n\t\treturn value\n";
        harness.WriteProjectText("mixed.py", original);

        // SEARCHはスペースインデントだが文字数（1・0・2・2）はファイルのタブと同じ長さのため
        // delta=0でマッチする（段階1・2は文字種の違いで外れ、段階3でマッチする）。
        var search = " if flag:\n\n  value = compute()\n  return value";
        // REPLACEはタブとスペースが混在し（3文字目のインデントが「タブ+スペース+タブ」）、
        // 空行も含む。delta==0のためゲート1が働き、これらは一切書き換えられず、
        // 元のバイト列のまま書き込まれるはず。
        var replace = "\tif flag:\n\n\t \tvalue = compute() * 2\n\t\treturn value";
        var patchText = BuildSrPatch("mixed.py", search, replace);

        await ApplyRealAsync(harness, 1, patchText);

        var expected = "def use():\n\tif flag:\n\n\t \tvalue = compute() * 2\n\t\treturn value\n";
        harness.ReadProjectBytes("mixed.py").Should().Equal(Encoding.UTF8.GetBytes(expected),
            "delta==0のときはタブ・スペース混在も空行も一切変更されず書き込まれるはず");
    }

    // ====================================================================
    // 項目4: delta==0で文字種が入れ替わらない
    // ====================================================================

    [Fact(DisplayName = "ゲート1: delta==0の段階3一致では、ファイルの優勢文字（タブ）にREPLACEのスペースが入れ替わらない")]
    public async Task ゲート1_文字種が入れ替わらない()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);

        // ファイルはタブ3個分（優勢文字はタブ）。
        var original = "def f():\n\tif x:\n\t\treturn x\n";
        harness.WriteProjectText("chartype.py", original);

        // SEARCHはスペースだが、ファイルのタブと同じ文字数（1・2）のためdelta=0でマッチする。
        var search = " if x:\n  return x";
        var replace = " if x:\n  return x * 2";
        var patchText = BuildSrPatch("chartype.py", search, replace);

        await ApplyRealAsync(harness, 1, patchText);

        // 修正前の実装はdelta==0でも new string(dominantChar, ws.Length) で作り直すため、
        // ファイルの優勢文字（タブ）へREPLACEのスペースが入れ替わっていた。修正後は
        // delta==0を検出した時点で一切書き換えないため、REPLACEのスペースがそのまま残る。
        var expected = "def f():\n if x:\n  return x * 2\n";
        harness.ReadProjectBytes("chartype.py").Should().Equal(Encoding.UTF8.GetBytes(expected),
            "delta==0ではREPLACEの文字種（スペース）がファイルの優勢文字（タブ）へ入れ替わってはならない");
    }

    // ====================================================================
    // 項目5: delta<0で潰れる行があるとき補正されない
    // ====================================================================

    [Fact(DisplayName = "ゲート3: delta<0でREPLACEの中に削り切れない行があると、補正せずREPLACEをそのまま書き込む")]
    public async Task ゲート3_削り切れない行があると補正されない()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);

        // ファイルは基準1スペース・本文相対+4（5スペース）。
        var original = "def use():\n if flag:\n     value = compute()\n     return value\n";
        harness.WriteProjectText("shrink.py", original);

        // SEARCHは基準4スペース・本文相対+4（8スペース）。相対オフセットが一致するため
        // 段階3でマッチする。delta = fileBase(1) - searchBase(4) = -3。
        var search = "    if flag:\n        value = compute()\n        return value";
        // REPLACE基準は4（searchBaseと一致、ゲート2は通る）だが、2行目だけ2スペースしか
        // 無く |delta|=3 文字を削り切れない。このため修正1のゲート3が働き、
        // ブロック全体の補正を見送ってREPLACEをそのまま書き込むはず。
        var replace = "    if flag:\n  value = compute() * 2\n        return value";
        var patchText = BuildSrPatch("shrink.py", search, replace);

        await ApplyRealAsync(harness, 1, patchText);

        var expected = "def use():\n    if flag:\n  value = compute() * 2\n        return value\n";
        harness.ReadProjectBytes("shrink.py").Should().Equal(Encoding.UTF8.GetBytes(expected),
            "削り切れない行がある場合はMath.Max(0, ...)で桁0へ潰さず、REPLACEをそのまま書き込むはず");
    }

    // ====================================================================
    // 項目6: MODE=FULL / APPEND / PREPEND のバイト保全（5シナリオ）
    // ====================================================================

    [Fact(DisplayName = "MODE=FULL: タブとスペースが混在する本文がバイト単位で保全される")]
    public async Task FULL_タブとスペースが混在する本文の保全()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        harness.WriteProjectText("full_mixed.txt", "seed\n");

        var content = "alpha\n\tbeta\n    gamma\n\t\tdelta";
        var patchText = BuildFullPatch("full_mixed.txt", content);

        await ApplyRealAsync(harness, 1, patchText);

        harness.ReadProjectBytes("full_mixed.txt").Should().Equal(Encoding.UTF8.GetBytes(content + "\n"),
            "タブ・スペース混在のFULL本文はバイト単位で保全されるはず");
    }

    [Fact(DisplayName = "MODE=FULL: パッチ本文がCRLFで書かれていても、ファイルへは1バイトも欠落・混入せず書き込まれる")]
    public async Task FULL_CRLFのパッチ本文の保全()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        harness.WriteProjectText("full_crlf.txt", "seed\n");

        // AI側の出力がCRLFで来たケースを模す（ヘッダ・本文・終了マーカーすべてCRLF）。
        var patchText = "<<<< FILE: full_crlf.txt MODE=FULL\r\nalpha\r\nbeta\r\ngamma\r\n>>>> END\r\n";

        await ApplyRealAsync(harness, 1, patchText);

        var written = harness.ReadProjectBytes("full_crlf.txt");
        written.Should().Equal(Encoding.UTF8.GetBytes("alpha\nbeta\ngamma\n"),
            "パッチ本文のCRLFがそのまま書き込まれてはならない（元ファイルの改行コードLFへ揃うはず）");
        written.Should().NotContain((byte)'\r', "CR（0x0D）が1バイトも紛れ込んではならない");
    }

    [Fact(DisplayName = "APPEND: 空行を含む本文がバイト単位で保全される")]
    public async Task APPEND_空行を含む本文の保全()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        harness.WriteProjectText("append_blank.txt", "first\n");

        var appendContent = "second\n\nthird";
        var patchText = BuildAppendPatch("append_blank.txt", appendContent);

        await ApplyRealAsync(harness, 1, patchText);

        harness.ReadProjectBytes("append_blank.txt").Should().Equal(
            Encoding.UTF8.GetBytes("first\nsecond\n\nthird\n"),
            "APPEND本文中の空行が失われたり詰められたりしてはならない");
    }

    [Fact(DisplayName = "PREPEND: 外側をMarkdownコードフェンスで囲んだパッチでも本文がバイト単位で保全される")]
    public async Task PREPEND_外側コードフェンスで囲んでも本文が保全される()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        harness.WriteProjectText("prepend_fence.txt", "keep\n");

        // フェンス除去（PatchScanner.Create）は「<<<< PATCH」行を含むフェンスだけを対象にする
        // ため、フィクスチャと同様にPATCHメタブロックを含めて丸ごと囲む。
        var patchText = "```text\n"
            + "<<<< PATCH\n"
            + "summary: 前置きテスト\n"
            + ">>>>\n"
            + BuildPrependPatch("prepend_fence.txt", "one\ntwo")
            + "```\n";

        await ApplyRealAsync(harness, 1, patchText);

        harness.ReadProjectBytes("prepend_fence.txt").Should().Equal(
            Encoding.UTF8.GetBytes("one\ntwo\nkeep\n"),
            "外側のコードフェンスは剥がされ、PREPEND本文はバイト単位で保全されるはず");
    }

    [Fact(DisplayName = "MODE=FULL: 既存ファイルを上書きすると、旧内容は残らず新しい内容がバイト単位で書き込まれる")]
    public async Task FULL_既存ファイル上書きでも新しい内容がバイト単位で保全される()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        harness.WriteProjectText("overwrite.txt", "old content line1\nold content line2\n");

        var patchText = BuildFullPatch("overwrite.txt", "new content only");

        await ApplyRealAsync(harness, 1, patchText);

        harness.ReadProjectBytes("overwrite.txt").Should().Equal(
            Encoding.UTF8.GetBytes("new content only\n"),
            "旧内容が一部でも残ってはならず、新しい内容がバイト単位で書き込まれるはず");
    }

    // ====================================================================
    // 項目7: マーカー行の除去が直後の行の先頭文字を巻き込まないこと
    // （利用者の仮説を否定した結果を固定する回帰テスト）
    // ====================================================================

    [Fact(DisplayName = "利用者仮説の否定を固定: SEARCH/REPLACEマーカー行の除去は直後の行の先頭文字を巻き込まない")]
    public void マーカー除去は直後の行の先頭文字を巻き込まない()
    {
        // 「<<<<<<< SEARCH」「=======」「>>>>>>> REPLACE」の直後の行がそれぞれ 'X'・'Y' で
        // 始まる。除去処理が1文字でも巻き込めばこの文字が失われる。
        var patchText = "<<<< FILE: marker.txt\n<<<<<<< SEARCH\nXvalue\n=======\nYvalue\n>>>>>>> REPLACE\n";

        var result = new PatchParser().Parse(patchText);

        result.IsSuccess.Should().BeTrue();
        var block = result.Value.Blocks.Should().ContainSingle().Subject.Should().BeOfType<SearchReplaceBlock>().Subject;
        block.Pairs.Should().ContainSingle();
        block.Pairs[0].SearchText.Should().Be("Xvalue", "SEARCHマーカー直後の行の先頭文字'X'が巻き込まれてはならない");
        block.Pairs[0].ReplaceText.Should().Be("Yvalue", "=======マーカー直後の行の先頭文字'Y'が巻き込まれてはならない");
    }

    [Fact(DisplayName = "利用者仮説の否定を固定: MODE=FULLのヘッダ行・終了マーカー行の除去も直後/直前の行の文字を巻き込まない")]
    public void FULL形式のマーカー除去も文字を巻き込まない()
    {
        var patchText = "<<<< FILE: marker2.txt MODE=FULL\nZfirstline\nsecondlineY\n>>>> END\n";

        var result = new PatchParser().Parse(patchText);

        result.IsSuccess.Should().BeTrue();
        var block = result.Value.Blocks.Should().ContainSingle().Subject.Should().BeOfType<FullContentBlock>().Subject;
        block.Content.Should().Be("Zfirstline\nsecondlineY",
            "ヘッダ行直後の先頭文字'Z'・終了マーカー直前の行末文字'Y'のいずれも巻き込まれてはならない");
    }

    // ====================================================================
    // 項目8: CRLFのパッチ本文でも行分割が1文字ずれないこと
    // （利用者の仮説を否定した結果を固定する回帰テスト）
    // ====================================================================

    [Fact(DisplayName = "利用者仮説の否定を固定: パッチ本文全体がCRLFで書かれていても行分割で\\rが混入せず1文字もずれない")]
    public void CRLFのパッチ本文でも行分割がずれない()
    {
        var patchText = "<<<< FILE: crlf.txt\r\n<<<<<<< SEARCH\r\nAAA\r\nBBB\r\n=======\r\nCCC\r\n>>>>>>> REPLACE\r\n";

        var result = new PatchParser().Parse(patchText);

        result.IsSuccess.Should().BeTrue();
        var block = result.Value.Blocks.Should().ContainSingle().Subject.Should().BeOfType<SearchReplaceBlock>().Subject;
        block.Pairs.Should().ContainSingle();
        block.Pairs[0].SearchText.Should().Be("AAA\nBBB", "CRLFは\\nへ揃い、行数も内容も1文字もずれてはならない");
        block.Pairs[0].SearchText.Should().NotContain("\r", "SEARCH本文に\\rが紛れ込んではならない");
        block.Pairs[0].ReplaceText.Should().Be("CCC");
    }
}
