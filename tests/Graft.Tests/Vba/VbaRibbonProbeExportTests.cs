using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Graft.Core.Vba;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// 実機検証 1〜4（仕様書_ブックの情報と依頼の観点 16.15。docs/Windows実機検証手順.md の 6-32）のための、検証用のブックを書き出す。
/// <b>環境変数 <c>GRAFT_RIBBON_PROBE_DIR</c> を設定したときだけ</b>、そのフォルダへ書き出す（設定していなければ何もせずに通る。
/// 通常のテストの実行でファイルを撒かないため）。
/// <para>
/// 【なぜ必要か】 書き換えは .NET の <c>ZipArchive</c> で zip を作り直す（16.7）。Excel が .NET の書いた zip を警告・修復の確認なしで
/// 開けるか、<c>Default Extension="xml"</c> だけで新しい customUI を受け入れるかは、Linux の CI では確かめられない。F1 の完了条件として、
/// 本番と同じ <see cref="VbaRibbonPackageWriter.RewriteFile"/>（一時ファイル → 照合 → 置き換え）で書いたブックを利用者が Excel で開き、
/// ここで通らなければ 16.7 の方針を見直してから F2 へ進む。
/// </para>
/// <para>
/// 書き出すもの（それぞれ、目で確かめられるタブ 1 つ・ボタン 1 つ。<c>onAction</c> の呼び先のマクロは無い）:
/// (0) 元のブック（customUI なし。Excel が元のブック自体を開けることの確認用）、
/// (a) customUI の無いブックに customUI14 を新しく足したもの、
/// (b) 既に customUI14 があるブックの XML を差し替えたもの（差し替え前の元のブックも並べる）、
/// (c) customUI.xml（2006/01 の古い形式）を新しく足したもの。
/// </para>
/// </summary>
public class VbaRibbonProbeExportTests
{
    /// <summary>書き出し先のフォルダを指す環境変数。</summary>
    public const string ProbeDirVariable = "GRAFT_RIBBON_PROBE_DIR";

    private const string Ns14 = "http://schemas.microsoft.com/office/2009/07/customui";
    private const string Ns12 = "http://schemas.microsoft.com/office/2006/01/customui";

    /// <summary>目で確かめるリボン（タブ 1・グループ 1・大きなボタン 1。絵は Office の組み込みの HappyFace）。</summary>
    private static string ProbeRibbon(string ns, string tabLabel, string buttonLabel, string suffix)
        => "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n"
           + $"<customUI xmlns=\"{ns}\">\r\n"
           + "  <ribbon>\r\n"
           + "    <tabs>\r\n"
           + $"      <tab id=\"tabGraftProbe{suffix}\" label=\"{tabLabel}\">\r\n"
           + $"        <group id=\"grpGraftProbe{suffix}\" label=\"Graft の検証\">\r\n"
           + $"          <button id=\"btnGraftProbe{suffix}\" label=\"{buttonLabel}\" size=\"large\" imageMso=\"HappyFace\" onAction=\"GraftProbe{suffix}_OnAction\"/>\r\n"
           + "        </group>\r\n"
           + "      </tab>\r\n"
           + "    </tabs>\r\n"
           + "  </ribbon>\r\n"
           + "</customUI>\r\n";

    [Fact(DisplayName = "実機検証 1〜4 の検証用のブックを書き出す（環境変数 GRAFT_RIBBON_PROBE_DIR を設定したときだけ）")]
    public void 検証用のブックを書き出す()
    {
        var dir = Environment.GetEnvironmentVariable(ProbeDirVariable);
        if (string.IsNullOrWhiteSpace(dir)) return; // 通常のテストの実行では何もしない。
        Directory.CreateDirectory(dir);

        var a = ProbeRibbon(Ns14, "Graft検証A", "追加A", "A");
        var bBefore = ProbeRibbon(Ns14, "Graft検証B（差し替え前）", "差し替え前", "B");
        var bAfter = ProbeRibbon(Ns14, "Graft検証B", "差し替え後B", "B");
        var c = ProbeRibbon(Ns12, "Graft検証C", "追加C", "C");

        // 書く前の検査（本番と同じ）。検証用のリボン自体が定義に合っていることを確かめておく（Excel で出なかったとき、XML の誤りを疑わずに済む）。
        foreach (var (xml, format) in new[] { (a, VbaRibbonFormat.Office2010), (bAfter, VbaRibbonFormat.Office2010), (c, VbaRibbonFormat.Office2007) })
            VbaRibbonXmlValidator.Validate(xml, format).Findings.Should().BeEmpty();

        var files = new List<string>();

        // (0) 元のブック。
        var basePath = Path.Combine(dir, "graft-ribbon-probe-0-base.xlsm");
        File.WriteAllBytes(basePath, RibbonPackageBuilder.ExcelWorkbook(sheetText: "検証(0): 元のブック（customUI なし）。警告なしで開ければ成功"));
        files.Add(basePath);

        // (a) customUI の無いブックに customUI14 を新しく足す。
        files.Add(Rewrite(dir, "graft-ribbon-probe-a-add-customUI14.xlsm",
            RibbonPackageBuilder.ExcelWorkbook(sheetText: "検証(a): customUI14 を新しく足したブック。リボンに「Graft検証A」タブが出れば成功"),
            new VbaRibbonWriteRequest(VbaRibbonFormat.Office2010, a), expectAddedRelationship: true));

        // (b) 既に customUI14 があるブックの XML を差し替える。差し替え前のブックも並べる（前後でタブの名前が変わることを見比べられるように）。
        var bSource = RibbonPackageBuilder.ExcelWorkbook(customUi14: bBefore, sheetText: "検証(b): customUI14 の XML を差し替えたブック。「Graft検証B」タブに「差し替え後B」が出れば成功");
        var bBeforePath = Path.Combine(dir, "graft-ribbon-probe-b0-before-replace.xlsm");
        File.WriteAllBytes(bBeforePath, RibbonPackageBuilder.ExcelWorkbook(customUi14: bBefore, sheetText: "検証(b0): 差し替える前のブック。「Graft検証B（差し替え前）」タブが出る"));
        files.Add(bBeforePath);
        files.Add(Rewrite(dir, "graft-ribbon-probe-b-replace-customUI14.xlsm", bSource,
            new VbaRibbonWriteRequest(VbaRibbonFormat.Office2010, bAfter), expectAddedRelationship: false));

        // (c) customUI.xml（2006/01 の古い形式）を新しく足す。
        files.Add(Rewrite(dir, "graft-ribbon-probe-c-add-customUI.xlsm",
            RibbonPackageBuilder.ExcelWorkbook(sheetText: "検証(c): 古い形式の customUI.xml を新しく足したブック。リボンに「Graft検証C」タブが出れば成功"),
            new VbaRibbonWriteRequest(VbaRibbonFormat.Office2007, c), expectAddedRelationship: true));

        // 利用者の PC へ運ぶときに壊れていないことを確かめられるよう、sha256sum と同じ書式の照合値を並べる。
        var sums = new StringBuilder();
        foreach (var file in files)
            sums.Append(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant()).Append("  ").Append(Path.GetFileName(file)).Append('\n');
        File.WriteAllText(Path.Combine(dir, "SHA256SUMS.txt"), sums.ToString(), new UTF8Encoding(false));
    }

    /// <summary>元のブックを置き、本番と同じ原子的な書き換え（<see cref="VbaRibbonPackageWriter.RewriteFile"/>）で書き換えて、読み取りで往復を確かめる。</summary>
    private static string Rewrite(string dir, string fileName, byte[] source, VbaRibbonWriteRequest request, bool expectAddedRelationship)
    {
        var path = Path.Combine(dir, fileName);
        File.WriteAllBytes(path, source);

        var result = VbaRibbonPackageWriter.RewriteFile(path, new[] { request });

        result.Succeeded.Should().BeTrue(result.Problem);
        result.Parts.Should().ContainSingle().Which.AddedRelationship.Should().Be(expectAddedRelationship);
        var read = VbaRibbonPackageReader.Read(path);
        read.Status.Should().Be(VbaRibbonReadStatus.Ok);
        read.Parts.Should().ContainSingle().Which.Xml.Should().Be(request.Xml);
        Directory.GetFiles(dir, "~graft-*.tmp").Should().BeEmpty("一時ファイルは残らない");
        return path;
    }

    [Fact(DisplayName = "検証用のリボンは、両形式とも同梱の XSD の検査を通る（書き出しをしない通常の実行でも確かめる）")]
    public void 検証用のリボンは定義に合う()
    {
        VbaRibbonXmlValidator.Validate(ProbeRibbon(Ns14, "T", "B", "A"), VbaRibbonFormat.Office2010).Findings.Should().BeEmpty();
        VbaRibbonXmlValidator.Validate(ProbeRibbon(Ns12, "T", "B", "C"), VbaRibbonFormat.Office2007).Findings.Should().BeEmpty();
    }
}
