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
/// 追加テスト: 適用（<see cref="ApplyEngine"/>）→復元（<see cref="RevisionRestorer"/>）の通しで、
/// エンコーディング・改行コード・BOM・末尾改行がファイルのバイト列レベルで完全に維持されることを
/// 検証する（仕様書6.4）。<see cref="BackupRevisionTests"/>・<see cref="PartialPairApplyTests"/>と
/// 同様、<see cref="ApplyHarness"/>経由で一時ディレクトリ上の実ファイルに対して検証する。
/// </summary>
public class EncodingRoundTripTests
{
    static EncodingRoundTripTests()
    {
        // Shift_JIS（コードページ932）を使うための登録。EncodingDetectorの静的コンストラクタでも
        // 登録されるが、このテストは直接 Encoding.GetEncoding(932) を呼ぶため明示しておく
        // （二重登録しても例外にはならない）。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    [Fact(DisplayName = "Shift_JIS + CRLFの往復: 適用後も見た目を保持し、復元でバイト列が完全一致する")]
    public async Task ShiftJIS_CRLFの往復()
    {
        var shiftJis = Encoding.GetEncoding(932);
        var originalBytes = shiftJis.GetBytes("先頭行\r\n変更対象の行\r\n末尾行\r\n");

        await VerifyRoundTripAsync(
            originalBytes,
            searchLine: "変更対象の行",
            replaceLine: "変更後の行",
            expectedCodePage: 932,
            expectedHasBom: false,
            expectedNewLine: "\r\n",
            expectedEndsWithNewLine: true);
    }

    [Fact(DisplayName = "UTF-8 BOM付き + CRLFの往復: 適用後もBOM・改行を保持し、復元でバイト列が完全一致する")]
    public async Task UTF8BOM付きCRLFの往復()
    {
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        var body = new UTF8Encoding(false).GetBytes("先頭行\r\n変更対象の行\r\n末尾行\r\n");
        var originalBytes = bom.Concat(body).ToArray();

        await VerifyRoundTripAsync(
            originalBytes,
            searchLine: "変更対象の行",
            replaceLine: "変更後の行",
            expectedCodePage: 65001,
            expectedHasBom: true,
            expectedNewLine: "\r\n",
            expectedEndsWithNewLine: true);
    }

    [Fact(DisplayName = "UTF-8 BOMなし + LF + 末尾改行なしの往復: 適用後も見た目を保持し、復元でバイト列が完全一致する")]
    public async Task UTF8BOMなしLF末尾改行なしの往復()
    {
        var originalBytes = new UTF8Encoding(false).GetBytes("line1\nline2\nline3");

        await VerifyRoundTripAsync(
            originalBytes,
            searchLine: "line2",
            replaceLine: "line2changed",
            expectedCodePage: 65001,
            expectedHasBom: false,
            expectedNewLine: "\n",
            expectedEndsWithNewLine: false);
    }

    /// <summary>
    /// 指定したバイト列をプロジェクトファイルとして書き込み、SEARCH/REPLaceパッチを適用したうえで
    /// 見た目（エンコーディング・BOM・改行・末尾改行）が保持されることを検証し、続けてリビジョンを
    /// 復元してバイト列が元と完全一致することまで確認する。
    /// </summary>
    private static async Task VerifyRoundTripAsync(
        byte[] originalBytes, string searchLine, string replaceLine,
        int expectedCodePage, bool expectedHasBom, string expectedNewLine, bool expectedEndsWithNewLine)
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        harness.WriteProjectBytes("target.txt", originalBytes);

        var patchText = $"""
            <<<< FILE: target.txt
            <<<<<<< SEARCH
            {searchLine}
            =======
            {replaceLine}
            >>>>>>> REPLACE
            """;
        var ctx = harness.MakeContext(1);
        var dryRun = await harness.DryRunAsync(patchText, ctx);
        var filePlan = dryRun.Plans.Single(p => p.Path == "target.txt");
        filePlan.CanApply.Should().BeTrue("対象行が一意に見つかり適用できるはず");

        var apply = await harness.ApplyAsync(dryRun, ctx);
        apply.IsSuccess.Should().BeTrue(string.Join(",", apply.Issues.Select(i => i.ToDisplayText())));

        // 適用後: エンコーディング・BOM・改行コード・末尾改行が元のまま保持されていること。
        var applied = await FileTextIO.ReadAsync(Path.Combine(harness.ProjectRoot, "target.txt"));
        applied.IsSuccess.Should().BeTrue();
        applied.Value.Shape.Encoding.CodePage.Should().Be(expectedCodePage, "エンコーディングが維持されているはず");
        applied.Value.Shape.HasBom.Should().Be(expectedHasBom, "BOMの有無が維持されているはず");
        applied.Value.Shape.NewLine.Should().Be(expectedNewLine, "改行コードが維持されているはず");
        applied.Value.Shape.EndsWithNewLine.Should().Be(expectedEndsWithNewLine, "末尾改行の有無が維持されているはず");
        applied.Value.Text.Should().Contain(replaceLine, "置換後の内容が反映されているはず");

        // 復元: 退避しておいたバイト列をそのまま書き戻すため、元のバイト列と完全一致するはず。
        var summary = await harness.Revisions.ReadAsync(harness.ProjectId, 1);
        summary.IsSuccess.Should().BeTrue();
        var restorer = new RevisionRestorer(harness.Paths);
        var restored = await restorer.RestoreAsync(harness.ProjectId, harness.ProjectRoot, summary.Value, force: false);
        restored.IsSuccess.Should().BeTrue(string.Join(",", restored.Issues.Select(i => i.ToDisplayText())));

        var restoredBytes = harness.ReadProjectBytes("target.txt");
        restoredBytes.Should().Equal(originalBytes, "復元後は適用前のバイト列と完全に一致するはず");
    }
}
