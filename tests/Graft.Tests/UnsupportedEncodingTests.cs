using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Core;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// B1の回帰テスト: UTF-8でもShift_JISでもないファイル（EUC-JP・Windows-1252など）に適用・保存すると、
/// 置換つきのUTF-8として読んだ内容が書き戻され、変更していない行の日本語まで
/// すべてU+FFFDになって「成功」していた。読み込みで元のバイト列を復元できないファイルは
/// 「文字コードを判定できない」ものとして書き込みの対象から外し、適用はドライランの時点で
/// E218のエラーにする。Shift_JISで表せない文字を書く場合は、書く前にE219の警告（要確認）で知らせる。
/// </summary>
public class UnsupportedEncodingTests
{
    static UnsupportedEncodingTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    // EUC-JP: 1行目「// 日本語」、2行目「int x = 1;」。日=C6 FC、本=CB DC、語=B8 EC。
    // FC CB はShift_JISとしては不正（FCの後ろに続けられるのは40〜4Bだけ）なので判定から外れる。
    private static readonly byte[] EucJp = Concat(
        Encoding.ASCII.GetBytes("// "), new byte[] { 0xC6, 0xFC, 0xCB, 0xDC, 0xB8, 0xEC },
        Encoding.ASCII.GetBytes("\nint x = 1;\n"));

    // Windows-1252: 1行目「// café résumé」（é=E9）、2行目「int x = 1;」。E9 20 はShift_JISとして不正。
    private static readonly byte[] Cp1252 = Concat(
        Encoding.ASCII.GetBytes("// caf"), new byte[] { 0xE9 }, Encoding.ASCII.GetBytes(" r"),
        new byte[] { 0xE9 }, Encoding.ASCII.GetBytes("sum"), new byte[] { 0xE9 },
        Encoding.ASCII.GetBytes("\nint x = 1;\n"));

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private const string ChangeSecondLine = "<<<< FILE: code.txt\n<<<<<<< SEARCH\nint x = 1;\n=======\nint x = 2;\n>>>>>>> REPLACE\n>>>> END\n";

    // ------------------------------------------------------------------
    // 判定
    // ------------------------------------------------------------------

    [Fact(DisplayName = "B1: EUC-JPとWindows-1252は「文字コードを判定できない」になる")]
    public void 未対応の文字コードは判定できないとされる()
    {
        EncodingDetector.Detect(EucJp).UnsupportedEncoding.Should().BeTrue();
        EncodingDetector.Detect(Cp1252).UnsupportedEncoding.Should().BeTrue();
    }

    [Fact(DisplayName = "B1: UTF-8・Shift_JIS・UTF-16（BOM付き）・空ファイルは判定できる扱いのまま")]
    public void 対応する文字コードは判定できる()
    {
        EncodingDetector.Detect(Encoding.UTF8.GetBytes("日本語\n")).UnsupportedEncoding.Should().BeFalse();
        EncodingDetector.Detect(Encoding.GetEncoding(932).GetBytes("日本語\n")).UnsupportedEncoding.Should().BeFalse();
        EncodingDetector.Detect(new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("日本語\n")).ToArray())
            .UnsupportedEncoding.Should().BeFalse();
        EncodingDetector.Detect(System.Array.Empty<byte>()).UnsupportedEncoding.Should().BeFalse();
    }

    [Fact(DisplayName = "B1: BOM付きUTF-8でも、BOMの後ろに不正なバイトがあれば判定できない扱いになる")]
    public void BOM付きUTF8の不正なバイトも見逃さない()
    {
        // 以前はBOMを見た時点で通していたため、中身が壊れていても置換つきで読んで書き戻していた。
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.ASCII.GetBytes("a")).Concat(new byte[] { 0xE9, 0x20 }).ToArray();

        EncodingDetector.Detect(bytes).UnsupportedEncoding.Should().BeTrue();
    }

    // ------------------------------------------------------------------
    // 書き込み
    // ------------------------------------------------------------------

    [Fact(DisplayName = "B1: 判定できないファイルも読み込みは成功し（読むだけの機能は動く）、書き込みはE218で止まる")]
    public async Task 読めるが書けない()
    {
        using var ws = new TempWorkspace();
        var path = System.IO.Path.Combine(ws.CreateDirectory("p"), "code.txt");
        await System.IO.File.WriteAllBytesAsync(path, EucJp);

        var read = await FileTextIO.ReadAsync(path);
        read.IsSuccess.Should().BeTrue();
        read.Value.Text.Should().Contain("int x = 1;", "ASCII部分はこれまでどおり読める");

        var write = await FileTextIO.WriteAsync(path, read.Value.Text, read.Value.Shape);
        write.IsSuccess.Should().BeFalse();
        write.Issues.Should().Contain(i => i.Code == ErrorCode.E218);
        (await System.IO.File.ReadAllBytesAsync(path)).Should().Equal(EucJp, "止めた書き込みでファイルは1バイトも変わらない");
    }

    // ------------------------------------------------------------------
    // 適用（ドライラン）
    // ------------------------------------------------------------------

    [Theory(DisplayName = "B1: 判定できないファイルへのSEARCH/REPLACEは、ドライランでE218になり、適用しても内容は変わらない")]
    [InlineData("euc-jp")]
    [InlineData("windows-1252")]
    public async Task 適用はドライランで止まる(string kind)
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        var original = kind == "euc-jp" ? EucJp : Cp1252;
        harness.WriteProjectBytes("code.txt", original);
        var ctx = harness.MakeContext(1);

        var dryRun = await harness.DryRunAsync(ChangeSecondLine, ctx);

        var plan = dryRun.Plans.Should().ContainSingle().Subject;
        plan.CanApply.Should().BeFalse("修正前は警告なしで成功し、1行目の日本語がU+FFFDになっていた");
        plan.Issues.Should().Contain(i => i.Code == ErrorCode.E218);
        plan.Issues.First(i => i.Code == ErrorCode.E218).Detail.Should().Contain("UTF-8・Shift_JIS 以外");

        var apply = await harness.ApplyAsync(dryRun, ctx);
        apply.IsSuccess.Should().BeFalse();
        harness.ReadProjectBytes("code.txt").Should().Equal(original, "適用が止まったので元のバイト列のまま");
    }

    [Fact(DisplayName = "B1: 判定できないファイルへのAPPENDも止まる")]
    public async Task APPENDも止まる()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        harness.WriteProjectBytes("code.txt", EucJp);
        var ctx = harness.MakeContext(1);

        var dryRun = await harness.DryRunAsync("<<<< APPEND: code.txt\nint y = 2;\n>>>> END\n", ctx);

        dryRun.Plans.Should().ContainSingle().Which.Issues.Should().Contain(i => i.Code == ErrorCode.E218);
    }

    [Fact(DisplayName = "B1: UTF-8のファイルは従来どおり適用できる（回帰なし）")]
    public async Task UTF8は従来どおり()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        harness.WriteProjectBytes("code.txt", new UTF8Encoding(false).GetBytes("// 日本語\nint x = 1;\n"));
        var ctx = harness.MakeContext(1);

        var dryRun = await harness.DryRunAsync(ChangeSecondLine, ctx);
        var apply = await harness.ApplyAsync(dryRun, ctx);

        apply.IsSuccess.Should().BeTrue();
        new UTF8Encoding(false).GetString(harness.ReadProjectBytes("code.txt")).Should().Be("// 日本語\nint x = 2;\n");
    }

    // ------------------------------------------------------------------
    // Shift_JISで表せない文字
    // ------------------------------------------------------------------

    [Theory(DisplayName = "B1: Shift_JISで表せない文字を書く適用は、ドライランでE219の警告（要確認）になる")]
    [InlineData("🎉")]
    [InlineData("€")]
    [InlineData("✓")]
    [InlineData("é")]
    public async Task 表せない文字は警告になる(string ch)
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        harness.WriteProjectBytes("sjis.txt", Encoding.GetEncoding(932).GetBytes("日本語のコメント\r\nint x = 1;\r\n"));
        var ctx = harness.MakeContext(1);
        var patch = $"<<<< FILE: sjis.txt\n<<<<<<< SEARCH\nint x = 1;\n=======\nint x = 2; // {ch}\n>>>>>>> REPLACE\n>>>> END\n";

        var dryRun = await harness.DryRunAsync(patch, ctx);

        var plan = dryRun.Plans.Should().ContainSingle().Subject;
        plan.CanApply.Should().BeTrue("警告であり、了承すれば書ける");
        plan.NeedsConfirmation.Should().BeTrue();
        var issue = plan.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E219).Subject;
        issue.Severity.Should().Be(Severity.Warning);
        issue.Detail.Should().Contain(ch);
    }

    [Fact(DisplayName = "B1: Shift_JISで表せる文字だけなら警告は出ない")]
    public async Task 表せる文字なら警告なし()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        harness.WriteProjectBytes("sjis.txt", Encoding.GetEncoding(932).GetBytes("日本語のコメント\r\nint x = 1;\r\n"));
        var ctx = harness.MakeContext(1);
        var patch = "<<<< FILE: sjis.txt\n<<<<<<< SEARCH\nint x = 1;\n=======\nint x = 2; // 日本語①\n>>>>>>> REPLACE\n>>>> END\n";

        var dryRun = await harness.DryRunAsync(patch, ctx);

        var plan = dryRun.Plans.Should().ContainSingle().Subject;
        plan.NeedsConfirmation.Should().BeFalse();
        plan.Issues.Should().NotContain(i => i.Code == ErrorCode.E219);
    }

    [Fact(DisplayName = "B1: UTF-8のファイルには、どんな文字でも警告を出さない")]
    public async Task UTF8には警告なし()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        harness.WriteProjectBytes("u.txt", new UTF8Encoding(false).GetBytes("a\nint x = 1;\n"));
        var ctx = harness.MakeContext(1);
        var patch = "<<<< FILE: u.txt\n<<<<<<< SEARCH\nint x = 1;\n=======\nint x = 2; // 🎉€✓é\n>>>>>>> REPLACE\n>>>> END\n";

        var dryRun = await harness.DryRunAsync(patch, ctx);

        dryRun.Plans.Should().ContainSingle().Which.Issues.Should().NotContain(i => i.Code == ErrorCode.E219);
    }

    // ------------------------------------------------------------------
    // EncodingSafety
    // ------------------------------------------------------------------

    [Fact(DisplayName = "B1: EncodingSafety は表せない文字だけを（サロゲートペアは1文字として）重複なしで返す")]
    public void 表せない文字の列挙()
    {
        var sjis = Encoding.GetEncoding(932);

        EncodingSafety.FindUnrepresentable("日本語 abc ①", sjis).Should().BeEmpty();
        EncodingSafety.FindUnrepresentable("a🎉b€c🎉✓é", sjis).Should().Equal("🎉", "€", "✓", "é");
        EncodingSafety.FindUnrepresentable("🎉€", new UTF8Encoding(false)).Should().BeEmpty();
    }
}
