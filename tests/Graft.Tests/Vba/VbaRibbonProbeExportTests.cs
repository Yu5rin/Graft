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
/// (c) customUI.xml（2006/01 の古い形式）を新しく足したもの、
/// (d) 新旧の両方を足したもの（実機検証 4。どちらが効くかを見る）、
/// (e) 環境変数 <c>GRAFT_RIBBON_PROBE_SOURCE</c> に利用者のブック（Excel が作った本物）を指定したときだけ、その写しを書き換えたもの。
/// </para>
/// </summary>
public class VbaRibbonProbeExportTests
{
    /// <summary>書き出し先のフォルダを指す環境変数。</summary>
    public const string ProbeDirVariable = "GRAFT_RIBBON_PROBE_DIR";

    /// <summary>（任意）書き換えを試す、利用者のブック（Excel が作った本物）のパスを指す環境変数。写しだけを書き換える。</summary>
    public const string ProbeSourceVariable = "GRAFT_RIBBON_PROBE_SOURCE";

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

        // (d) 新旧の両方を足す（実機検証 4: 両方あるとき、Excel がどちらを使うか）。タブの名前で、どちらが効いたかが分かる。
        var d14 = ProbeRibbon(Ns14, "Graft検証D（新しい形式）", "新しい形式D", "D");
        var d12 = ProbeRibbon(Ns12, "Graft検証D（古い形式）", "古い形式D", "D");
        var dPath = Path.Combine(dir, "graft-ribbon-probe-d-add-both.xlsm");
        File.WriteAllBytes(dPath, RibbonPackageBuilder.ExcelWorkbook(sheetText: "検証(d): 新旧の両方を足したブック。どちらのタブが出たかを記録する"));
        var dResult = VbaRibbonPackageWriter.RewriteFile(dPath,
            new[] { new VbaRibbonWriteRequest(VbaRibbonFormat.Office2010, d14), new VbaRibbonWriteRequest(VbaRibbonFormat.Office2007, d12) });
        dResult.Succeeded.Should().BeTrue(dResult.Problem);
        VbaRibbonPackageReader.Read(dPath).ReadableParts.Should().HaveCount(2);
        files.Add(dPath);

        // (e) 利用者のブック（Excel が作った本物）の写しを書き換える。環境変数 GRAFT_RIBBON_PROBE_SOURCE にブックのパスを
        // 設定したときだけ（実機検証 1・3 の本番に近い形。画像つきのリボンなら、画像が残るかも確かめられる）。元のブックには触れない。
        var real = Environment.GetEnvironmentVariable(ProbeSourceVariable);
        if (!string.IsNullOrWhiteSpace(real)) files.Add(RewriteRealWorkbook(dir, real));

        // 利用者の PC へ運ぶときに壊れていないことを確かめられるよう、sha256sum と同じ書式の照合値を並べる。
        var sums = new StringBuilder();
        foreach (var file in files)
            sums.Append(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant()).Append("  ").Append(Path.GetFileName(file)).Append('\n');
        File.WriteAllText(Path.Combine(dir, "SHA256SUMS.txt"), sums.ToString(), new UTF8Encoding(false));
    }

    /// <summary>
    /// 利用者のブックの写しを作り、本番と同じ書き換えを掛ける。customUI があれば、<b>同じ本文のまま</b>書き直す（.NET が zip を作り直した
    /// だけのブックを Excel が受け入れるか、画像の部品と関係が残るかを、リボンの中身の違いと混ぜずに確かめるため）。customUI が無ければ、
    /// 検証用のタブ（Graft検証E）を足す。本文の検査の結果は、止めずに <c>graft-ribbon-probe-e-findings.txt</c> へ書き出す
    /// （本物の XML で、検査の重さ〔16.8 の #7・#11〕が厳しすぎないかを見直す材料にする）。
    /// </summary>
    private static string RewriteRealWorkbook(string dir, string sourcePath)
    {
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        var path = Path.Combine(dir, $"graft-ribbon-probe-e-real-{name}{Path.GetExtension(sourcePath)}");
        File.Copy(sourcePath, path, overwrite: true);

        var read = VbaRibbonPackageReader.Read(path);
        var requests = read.ReadableParts
            .Select(p => new VbaRibbonWriteRequest(VbaRibbonMirrorLayout.FormatOf(p), p.Xml!))
            .GroupBy(r => r.Format).Select(g => g.First())
            .ToList();
        if (requests.Count == 0)
            requests.Add(new VbaRibbonWriteRequest(VbaRibbonFormat.Office2010, ProbeRibbon(Ns14, "Graft検証E", "追加E", "E")));

        var findings = new StringBuilder();
        findings.Append("元のブック: ").Append(Path.GetFileName(sourcePath)).Append('\n');
        findings.Append("読み取り: ").Append(read.Status).Append(read.Reason is null ? string.Empty : "（" + read.Reason + "）").Append('\n');
        foreach (var request in requests)
        {
            var validation = VbaRibbonXmlValidator.Validate(request.Xml, request.Format);
            findings.Append('\n').Append(VbaRibbonMirrorLayout.FileName(request.Format)).Append(": エラー ")
                .Append(validation.Errors.Count()).Append(" 件・警告 ").Append(validation.Warnings.Count()).Append(" 件\n");
            foreach (var f in validation.Findings)
                findings.Append("  [").Append(f.Severity == Graft.Core.Severity.Error ? "エラー" : "警告").Append(" #").Append(f.Check).Append("] ")
                    .Append(f.ToDisplayText()).Append('\n');
        }

        var result = VbaRibbonPackageWriter.RewriteFile(path, requests);
        findings.Append("\n書き換え: ").Append(result.Status).Append(result.Problem is null ? string.Empty : "（" + result.Problem + "）").Append('\n');
        File.WriteAllText(Path.Combine(dir, "graft-ribbon-probe-e-findings.txt"), findings.ToString(), new UTF8Encoding(false));
        result.Succeeded.Should().BeTrue(result.Problem);
        return path;
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

    /// <summary>（任意）customUI の無い利用者のブック（Excel が作った本物）のパス。足す → 差し替えるの 2 段を、その写しで試す。</summary>
    public const string ProbeUserBookVariable = "GRAFT_RIBBON_PROBE_USER_BOOK";

    [Fact(DisplayName = "利用者のブックの写しに customUI14 を足し、さらに差し替える（環境変数 GRAFT_RIBBON_PROBE_USER_BOOK と GRAFT_RIBBON_PROBE_DIR を設定したときだけ）")]
    public void 利用者のブックで足して差し替える()
    {
        var dir = Environment.GetEnvironmentVariable(ProbeDirVariable);
        var source = Environment.GetEnvironmentVariable(ProbeUserBookVariable);
        if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(source)) return;
        Directory.CreateDirectory(dir);

        // 元のブックは読むだけ（利用者のファイル）。写しを作ってから書き換え、最後に元が 1 バイトも変わっていないことも確かめる。
        var original = File.ReadAllBytes(source);
        VbaRibbonPackageReader.Read(source).Status.Should().Be(VbaRibbonReadStatus.NoRibbon, "customUI の無いブックを想定している");

        var added = ProbeRibbon(Ns14, "Graft検証U", "追加U", "U");
        var replaced = ProbeRibbon(Ns14, "Graft検証U（差し替え後）", "差し替え後U", "U");
        VbaRibbonXmlValidator.Validate(added, VbaRibbonFormat.Office2010).Findings.Should().BeEmpty();
        VbaRibbonXmlValidator.Validate(replaced, VbaRibbonFormat.Office2010).Findings.Should().BeEmpty();

        // (d) 写しに customUI14 を新しく足す。
        var dPath = Path.Combine(dir, "user-test-ribbon14.xlsm");
        File.WriteAllBytes(dPath, original);
        var d = VbaRibbonPackageWriter.RewriteFile(dPath, new[] { new VbaRibbonWriteRequest(VbaRibbonFormat.Office2010, added) });
        d.Succeeded.Should().BeTrue(d.Problem);
        d.Parts.Should().ContainSingle().Which.AddedRelationship.Should().BeTrue();
        VbaRibbonPackageReader.Read(dPath).Parts.Should().ContainSingle().Which.Xml.Should().Be(added);

        // (e) (d) の XML を差し替える（タブの名前が変わる）。
        var ePath = Path.Combine(dir, "user-test-ribbon14-replaced.xlsm");
        File.Copy(dPath, ePath, overwrite: true);
        var e = VbaRibbonPackageWriter.RewriteFile(ePath, new[] { new VbaRibbonWriteRequest(VbaRibbonFormat.Office2010, replaced) });
        e.Succeeded.Should().BeTrue(e.Problem);
        e.Parts.Should().ContainSingle().Which.AddedRelationship.Should().BeFalse();
        VbaRibbonPackageReader.Read(ePath).Parts.Should().ContainSingle().Which.Xml.Should().Be(replaced);

        // 部品ごとの比較（元 → (d)、(d) → (e)）を書き出し、変わってよい部品以外がバイト単位で同じことを確かめる。
        var report = new StringBuilder();
        var before = RibbonPackageBuilder.ReadEntries(original);
        var afterD = RibbonPackageBuilder.ReadEntries(File.ReadAllBytes(dPath));
        var afterE = RibbonPackageBuilder.ReadEntries(File.ReadAllBytes(ePath));
        Compare("元 → user-test-ribbon14.xlsm", before, afterD, new[] { "_rels/.rels", "customUI/customUI14.xml" }, report);
        Compare("user-test-ribbon14.xlsm → user-test-ribbon14-replaced.xlsm", afterD, afterE, new[] { "customUI/customUI14.xml" }, report);
        File.WriteAllText(Path.Combine(dir, "user-test-parts.txt"), report.ToString(), new UTF8Encoding(false));

        File.ReadAllBytes(source).Should().Equal(original, "元のブックには書かない");
    }

    /// <summary>2 つのブックの部品を名前ごとに比べ、<paramref name="mayChange"/> 以外が同じことを確かめて、結果を <paramref name="report"/> に書く。</summary>
    private static void Compare(string title, List<(string Name, byte[] Data)> a, List<(string Name, byte[] Data)> b, string[] mayChange, StringBuilder report)
    {
        report.Append("## ").Append(title).Append('\n');
        foreach (var (name, data) in a)
        {
            var other = b.FirstOrDefault(x => x.Name == name);
            var state = other.Data is null ? "無くなった" : other.Data.AsSpan().SequenceEqual(data) ? "同じ" : "変わった";
            report.Append(state).Append(": ").Append(name).Append(" (").Append(data.Length).Append(" → ").Append(other.Data?.Length ?? 0).Append(" バイト)\n");
            if (!mayChange.Contains(name)) state.Should().Be("同じ", name);
        }

        foreach (var (name, data) in b.Where(x => a.All(y => y.Name != x.Name)))
        {
            report.Append("足された: ").Append(name).Append(" (").Append(data.Length).Append(" バイト)\n");
            mayChange.Should().Contain(name);
        }

        b.Select(x => x.Name).Take(a.Count).Should().Equal(a.Select(x => x.Name), "元の部品の順序を保つ");
        report.Append('\n');
    }

    [Fact(DisplayName = "検証用のリボンは、両形式とも同梱の XSD の検査を通る（書き出しをしない通常の実行でも確かめる）")]
    public void 検証用のリボンは定義に合う()
    {
        VbaRibbonXmlValidator.Validate(ProbeRibbon(Ns14, "T", "B", "A"), VbaRibbonFormat.Office2010).Findings.Should().BeEmpty();
        VbaRibbonXmlValidator.Validate(ProbeRibbon(Ns12, "T", "B", "C"), VbaRibbonFormat.Office2007).Findings.Should().BeEmpty();
    }
}
