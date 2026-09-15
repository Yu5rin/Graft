using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Core;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 実機不具合対応の回帰テスト（修正6）: 利用者から「MODE=FULLで新規作成したファイルが、
/// インデントのある行だけ行頭空白1個分減った状態でディスクに書かれていた」という実物の証拠が
/// 届いたが、同じパッチ本文をGraft自身に通した再現実験ではパーサ出力・書き込み結果ともに
/// パッチ本文とバイト単位で完全一致しており、原因はGraftの外（AIの出力やコピー経路）か、
/// まだ辿れていない経路にある可能性が高いと判明した。原因の所在によらずGraftが「パッチの指示」
/// と食い違う内容を黙って書き込んでしまう状態を避けるため、<see cref="ApplyEngine"/>に
/// MODE=FULL/APPEND/PREPEND向けの書き込み後検証を追加した
/// （<see cref="ApplyEngine.VerifyWrittenContentMatchesPatch"/>）。修正3（SR形式のE217、
/// <see cref="IndentPreservationTests"/>）と対になる、書き込み結果そのものに対する検証。
///
/// 通常のパッチ適用では、この検証が実際に不一致を検出する経路は存在しない（Graftの内部処理が
/// 正しい限り、書き込む内容は常にパッチの指示と一致するため）。「検証が実際に機能し、E217で
/// 中止したうえでロールバックが正しく動くこと」を確かめるには、書き込み直前の内容を意図的に
/// 壊す手段が要る。ここでは<see cref="SafeFileWriterTests"/>のIPrimaryReplaceOp/IMoveOpと同じ
/// 考え方の内部限定フック（<see cref="ApplyEngine.DebugCorruptFinalTextForTests"/>）を使い、
/// 判定ロジックそのものには一切手を加えず「書き込み直前の値」だけを差し替えて検証する。
/// </summary>
public class WrittenContentVerificationTests
{
    private static string BuildFullPatch(string path, string content)
        => $"<<<< FILE: {path} MODE=FULL\n{content}\n>>>> END\n";

    private static string BuildAppendPatch(string path, string content)
        => $"<<<< APPEND: {path}\n{content}\n>>>> END\n";

    private static string BuildPrependPatch(string path, string content)
        => $"<<<< PREPEND: {path}\n{content}\n>>>> END\n";

    /// <summary>実機報告どおり「行頭の半角スペース1個」を全行から取り除く破損を模す。</summary>
    private static string DropOneLeadingSpacePerLine(string text)
        => string.Join("\n", text.Split('\n').Select(line => line.StartsWith(' ') ? line[1..] : line));

    // ====================================================================
    // 検証成功系: MODE=FULLで書き込んだ内容がContentと一致すること（誤検知しないこと）
    // ====================================================================

    [Theory(DisplayName = "MODE=FULL: 2/4スペースインデント・タブ混在・空行を含む本文でも、書き込み後検証を素通りしてバイト単位で保全される")]
    [InlineData(2)]
    [InlineData(4)]
    public async Task FULL_書き込み後検証_インデントと混在本文が保全される(int spaces)
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        // 改行コードの検証（別テスト）と切り分けるため、LF・末尾改行ありの土台をあらかじめ
        // 用意しておく（新規ファイルの既定シェイプはCRLFのため、それと混同しないようにする）。
        harness.WriteProjectText("verify_full.py", "seed\n");

        var indent = new string(' ', spaces);
        // 2/4スペースインデント・タブ混在・空行のすべてを1本の内容に含める。
        var content = "def use():\n" + indent + "if flag:\n\n" + indent + "\tvalue = compute()\n" + indent + "return value";
        var patchText = BuildFullPatch("verify_full.py", content);

        var ctx = harness.MakeContext(1);
        var dryRun = await harness.DryRunAsync(patchText, ctx);
        var apply = await harness.ApplyAsync(dryRun, ctx);

        apply.IsSuccess.Should().BeTrue(
            "パッチの指示どおりに書き込めているため、修正6の検証で誤って中止されてはならない: "
            + string.Join(",", apply.Issues.Select(i => i.ToDisplayText())));
        harness.ReadProjectBytes("verify_full.py").Should().Equal(Encoding.UTF8.GetBytes(content + "\n"));
    }

    [Fact(DisplayName = "MODE=FULL: 改行コードがCRLFへ変換されても書き込み後検証は誤検知しない（正当な差異）")]
    public async Task FULL_書き込み後検証_CRLF変換は誤検知しない()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        // 既存ファイルはCRLF。以後の書き込みはこの改行コードを踏襲する（6.4節）。
        harness.WriteProjectBytes("crlf_shape.txt", Encoding.UTF8.GetBytes("seed\r\n"));

        var content = "alpha\nbeta\ngamma";
        var patchText = BuildFullPatch("crlf_shape.txt", content);
        var ctx = harness.MakeContext(1);
        var dryRun = await harness.DryRunAsync(patchText, ctx);
        var apply = await harness.ApplyAsync(dryRun, ctx);

        apply.IsSuccess.Should().BeTrue("改行コードの変換（LF→CRLF）は6.4節どおりの正当な差異であり、検証で中止されてはならない: "
            + string.Join(",", apply.Issues.Select(i => i.ToDisplayText())));
        harness.ReadProjectBytes("crlf_shape.txt").Should().Equal(Encoding.UTF8.GetBytes("alpha\r\nbeta\r\ngamma\r\n"),
            "書き込み自体は引き続きCRLFへ揃うはず");
    }

    [Fact(DisplayName = "APPEND: 末尾改行の無い既存ファイルへ追記しても、末尾改行の有無の違いを書き込み後検証は誤検知しない")]
    public async Task APPEND_書き込み後検証_末尾改行の有無は誤検知しない()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        // 末尾改行の無いファイル（EndsWithNewLine=false）。
        harness.WriteProjectBytes("no_trailing_nl.txt", Encoding.UTF8.GetBytes("first\nsecond"));

        var patchText = BuildAppendPatch("no_trailing_nl.txt", "third");
        var ctx = harness.MakeContext(1);
        var dryRun = await harness.DryRunAsync(patchText, ctx);
        var apply = await harness.ApplyAsync(dryRun, ctx);

        apply.IsSuccess.Should().BeTrue("末尾改行の有無の違いは正当な差異であり、検証で中止されてはならない: "
            + string.Join(",", apply.Issues.Select(i => i.ToDisplayText())));
        harness.ReadProjectBytes("no_trailing_nl.txt").Should().Equal(Encoding.UTF8.GetBytes("first\nsecond\nthird"));
    }

    [Fact(DisplayName = "MODE=FULL: Shift_JISで表現できない文字があっても、既存の置換フォールバック挙動を書き込み後検証は誤検知しない")]
    public async Task FULL_書き込み後検証_ShiftJISの表現限界は誤検知しない()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        var shiftJis = System.Text.Encoding.GetEncoding(932);
        harness.WriteProjectBytes("sjis.txt", shiftJis.GetBytes("旧内容\r\n"));

        // 🎉(U+1F389)はcp932の範囲外のため、既存のFileTextIO.WriteAsync（shape.Encoding.GetBytes）
        // は例外を投げず既定の置換フォールバックで書き込む（EncodingRoundTripTestsが検証している
        // 既存挙動）。修正6の検証はこの往復を期待値側にも同じエンコーディングで通すため、
        // 表現限界による正当な差異までは検出しないはず（パッチ本文はどの改行で書いても
        // パーサ側で\nへ正規化されるため、ここでは素直に\nで書く）。
        var content = "先頭行\n祝う🎉気持ち\n末尾行";
        var patchText = BuildFullPatch("sjis.txt", content);
        var ctx = harness.MakeContext(1);
        var dryRun = await harness.DryRunAsync(patchText, ctx);
        var apply = await harness.ApplyAsync(dryRun, ctx);

        apply.IsSuccess.Should().BeTrue("エンコーディングの表現限界による置換は既存の許容挙動であり、検証で中止されてはならない: "
            + string.Join(",", apply.Issues.Select(i => i.ToDisplayText())));
    }

    // ====================================================================
    // 検証破れ系: 意図的に書き込み直前の内容を壊し、E217で中止・ロールバックされることを確認
    // ====================================================================

    [Fact(DisplayName = "MODE=FULL: 新規作成で書き込み直前の内容が壊れていると、検証がE217で中止しファイルは作られない")]
    public async Task FULL_新規作成_検証破れでE217になりファイルが作られない()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        // 実機報告どおり「書き込み直前に行頭スペースが1個ずつ失われる」状態を模す。
        harness.Engine.DebugCorruptFinalTextForTests = DropOneLeadingSpacePerLine;

        var content = "function outer() {\n  if (ready) {\n    doStuff();\n  }\n}";
        var patchText = BuildFullPatch("broken_new.js", content);
        var ctx = harness.MakeContext(1);
        var dryRun = await harness.DryRunAsync(patchText, ctx);

        var apply = await harness.ApplyAsync(dryRun, ctx);

        apply.IsSuccess.Should().BeFalse("書き込み直前の内容がパッチの指示と食い違うため、検証で中止されるはず");
        apply.Errors.Should().Contain(i => i.Code == ErrorCode.E217);
        harness.ProjectFileExists("broken_new.js").Should().BeFalse(
            "新規作成が検証破れで中止された場合、ロールバックにより新規作成ファイルは削除されているはず");
    }

    [Fact(DisplayName = "MODE=FULL: 既存ファイル上書きで書き込み直前の内容が壊れていると、検証がE217で中止しファイルは元の内容へ戻る")]
    public async Task FULL_既存上書き_検証破れでE217になり元の内容へ戻る()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        var original = "old first line\nold second line\n";
        harness.WriteProjectText("broken_overwrite.txt", original);
        var originalBytes = harness.ReadProjectBytes("broken_overwrite.txt");

        harness.Engine.DebugCorruptFinalTextForTests = DropOneLeadingSpacePerLine;

        var content = "  new first line\n  new second line";
        var patchText = BuildFullPatch("broken_overwrite.txt", content);
        var ctx = harness.MakeContext(1);
        var dryRun = await harness.DryRunAsync(patchText, ctx);

        var apply = await harness.ApplyAsync(dryRun, ctx);

        apply.IsSuccess.Should().BeFalse();
        apply.Errors.Should().Contain(i => i.Code == ErrorCode.E217);
        harness.ReadProjectBytes("broken_overwrite.txt").Should().Equal(originalBytes,
            "検証破れによる中止後は、既存ファイルはロールバックで元の内容へ完全に戻っているはず");
    }

    [Fact(DisplayName = "APPEND: 書き込み直前の内容が壊れていると、検証がE217で中止し元のファイル内容へ戻る（追記されない）")]
    public async Task APPEND_検証破れでE217になり元の内容へ戻る()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        var original = "existing line\n";
        harness.WriteProjectText("broken_append.txt", original);
        var originalBytes = harness.ReadProjectBytes("broken_append.txt");

        harness.Engine.DebugCorruptFinalTextForTests = DropOneLeadingSpacePerLine;

        var patchText = BuildAppendPatch("broken_append.txt", "  appended line");
        var ctx = harness.MakeContext(1);
        var dryRun = await harness.DryRunAsync(patchText, ctx);

        var apply = await harness.ApplyAsync(dryRun, ctx);

        apply.IsSuccess.Should().BeFalse();
        apply.Errors.Should().Contain(i => i.Code == ErrorCode.E217);
        harness.ReadProjectBytes("broken_append.txt").Should().Equal(originalBytes,
            "検証破れによる中止後は、既存ファイルはロールバックで元の内容へ完全に戻っているはず（追記された痕跡が残ってはならない）");
    }

    [Fact(DisplayName = "PREPEND: 書き込み直前の内容が壊れていると、検証がE217で中止し元のファイル内容へ戻る（先頭挿入されない）")]
    public async Task PREPEND_検証破れでE217になり元の内容へ戻る()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        var original = "existing line\n";
        harness.WriteProjectText("broken_prepend.txt", original);
        var originalBytes = harness.ReadProjectBytes("broken_prepend.txt");

        harness.Engine.DebugCorruptFinalTextForTests = DropOneLeadingSpacePerLine;

        var patchText = BuildPrependPatch("broken_prepend.txt", "  prepended line");
        var ctx = harness.MakeContext(1);
        var dryRun = await harness.DryRunAsync(patchText, ctx);

        var apply = await harness.ApplyAsync(dryRun, ctx);

        apply.IsSuccess.Should().BeFalse();
        apply.Errors.Should().Contain(i => i.Code == ErrorCode.E217);
        harness.ReadProjectBytes("broken_prepend.txt").Should().Equal(originalBytes,
            "検証破れによる中止後は、既存ファイルはロールバックで元の内容へ完全に戻っているはず（先頭挿入された痕跡が残ってはならない）");
    }
}
