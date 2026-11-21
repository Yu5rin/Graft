using System.IO;
using FluentAssertions;
using Graft.Core.Vba;
using Graft.Features;
using Graft.Infra;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// <see cref="VbaMirrorFolderName"/> のテスト（仕様書_VBA連携 4.2・4.4・10.1）。
/// </summary>
public class VbaMirrorFolderNameTests
{
    [Fact(DisplayName = "同じブックなら何度計算しても同じフォルダ名（再追加で同じフォルダ・同じ Id に戻る）")]
    public void 同じブックは同じ名前()
    {
        var a = VbaMirrorFolderName.Build(@"C:\Work\計画.xlsm");
        var b = VbaMirrorFolderName.Build(@"C:\Work\計画.xlsm");

        a.Should().Be(b);
        a.Should().MatchRegex("^計画-[0-9a-f]{8}$");
    }

    [Fact(DisplayName = "別の場所にある同名のブックは別のフォルダ名になる（衝突しない）")]
    public void 別の場所の同名ブックは衝突しない()
    {
        var a = VbaMirrorFolderName.Build(@"C:\Work\計画.xlsm");
        var b = VbaMirrorFolderName.Build(@"D:\Backup\計画.xlsm");

        a.Should().NotBe(b);
        a.Should().StartWith("計画-");
        b.Should().StartWith("計画-");
    }

    [Theory(DisplayName = "パスの大文字小文字・区切り・. と .. の違いは同じブックとして扱う")]
    [InlineData(@"c:\work\計画.XLSM")]
    [InlineData("C:/Work/計画.xlsm")]
    [InlineData(@"C:\Work\.\計画.xlsm")]
    [InlineData(@"C:\Work\Sub\..\計画.xlsm")]
    public void 表記ゆれは同じ名前(string variant)
    {
        VbaMirrorFolderName.Build(variant).Should().Be(VbaMirrorFolderName.Build(@"C:\Work\計画.xlsm"));
    }

    [Fact(DisplayName = "拡張子が違えば（同じ場所・同じ名前でも）別のフォルダ名になる")]
    public void 拡張子違いは別()
    {
        VbaMirrorFolderName.Build(@"C:\Work\a.xlsm").Should().NotBe(VbaMirrorFolderName.Build(@"C:\Work\a.xlsb"));
    }

    [Fact(DisplayName = "ファイル名に使えない文字は _ に置き換わる")]
    public void 不正文字は置き換える()
    {
        // ブック名として現実にあり得る例（Windows のファイル名には本来使えないが、他 OS 由来のパス表記や
        // 区切りの混在で紛れ込んでも、ミラーのフォルダ名としては安全であること）。
        var name = VbaMirrorFolderName.Build(@"C:\Work\a<b>c""d|e?f*g.xlsm");

        name.Should().StartWith("a_b_c_d_e_f_g-");
        name.IndexOfAny(new[] { '<', '>', '"', '|', '?', '*', ':', '\\', '/' }).Should().BeLessThan(0);
    }

    [Fact(DisplayName = "ブック名は 40 文字までに切る（拡張子は含めない）")]
    public void 長さは40文字まで()
    {
        var longName = new string('a', 100);

        var name = VbaMirrorFolderName.Build($@"C:\Work\{longName}.xlsm");

        name.Should().Be(new string('a', 40) + "-" + name[^8..]);
        name.Length.Should().Be(40 + 1 + 8);
    }

    [Fact(DisplayName = "40 文字の境界でサロゲートペアを割らない")]
    public void サロゲートペアを割らない()
    {
        var bookName = new string('a', 39) + "\U0001F600xyz";

        var name = VbaMirrorFolderName.Build($@"C:\Work\{bookName}.xlsm");

        // 39 文字の後に 😀 の前半だけが残らない（41 文字目を含めず 39 文字で切る）。
        name.Should().StartWith(new string('a', 39) + "-");
    }

    [Fact(DisplayName = "名前が空になる場合は book に、末尾のドットや空白は取り除かれる（Windows はそのようなフォルダ名を作れない）")]
    public void 極端な名前()
    {
        // 「.xlsm」だけのファイル名は、拡張子ではなく名前として扱う（先頭のドットの前には何も無い）。
        VbaMirrorFolderName.Build(@"C:\Work\.xlsm").Should().StartWith(".xlsm-");

        // 拡張子を除くと「 .」だけが残り、末尾の空白とドットを取り除くと空になるので、代わりの名前を使う。
        var name = VbaMirrorFolderName.Build(@"C:\Work\ ..xlsm");
        name.Should().StartWith("book-");
        name.Should().NotContain(@"\");
    }

    [Fact(DisplayName = "UNC パスも区別でき、ローカルパスとは別のフォルダ名になる")]
    public void UNCパス()
    {
        var unc = VbaMirrorFolderName.Build(@"\\server\share\計画.xlsm");
        var local = VbaMirrorFolderName.Build(@"C:\server\share\計画.xlsm");

        unc.Should().NotBe(local);
    }

    [Fact(DisplayName = "NormalizePath: 区切りの統一・. と .. の解決・小文字化")]
    public void パスの正規化()
    {
        VbaMirrorFolderName.NormalizePath("C:/Work/./Sub/../Book.XLSM").Should().Be(@"c:\work\book.xlsm");
        VbaMirrorFolderName.NormalizePath(@"\\Server\Share\A.xlsm").Should().Be(@"\\server\share\a.xlsm");
    }

    // ------------------------------------------------------------------
    // 保存先の移行での Root の付け替え（4.4）
    // ------------------------------------------------------------------

    [Fact(DisplayName = "旧 vba-mirrors の配下の Root は、新 vba-mirrors の同じ相対位置へ付け替わる")]
    public void 配下のRootは付け替わる()
    {
        var oldDir = Path.Combine(Path.GetTempPath(), "old-data", "vba-mirrors");
        var newDir = Path.Combine(Path.GetTempPath(), "new-data", "vba-mirrors");
        var root = Path.Combine(oldDir, "計画-1a2b3c4d");

        VbaMirrorFolderName.RelocateRoot(root, oldDir, newDir).Should().Be(Path.Combine(newDir, "計画-1a2b3c4d"));
    }

    [Fact(DisplayName = "付け替えるとプロジェクトの Id が変わる（Id は Root から導くため。移行処理が back/ と vba-sync/ も付け替える前提）")]
    public void 付け替えるとIdが変わる()
    {
        var oldDir = Path.Combine(Path.GetTempPath(), "old-data", "vba-mirrors");
        var newDir = Path.Combine(Path.GetTempPath(), "new-data", "vba-mirrors");
        var oldRoot = Path.Combine(oldDir, "計画-1a2b3c4d");

        var newRoot = VbaMirrorFolderName.RelocateRoot(oldRoot, oldDir, newDir)!;

        ProjectStore.CreateId(newRoot).Should().NotBe(ProjectStore.CreateId(oldRoot));
    }

    [Fact(DisplayName = "利用者が自分で選んだ場所のミラー（旧 vba-mirrors の外）は付け替えない")]
    public void 外のRootは付け替えない()
    {
        var oldDir = Path.Combine(Path.GetTempPath(), "old-data", "vba-mirrors");
        var newDir = Path.Combine(Path.GetTempPath(), "new-data", "vba-mirrors");

        VbaMirrorFolderName.RelocateRoot(Path.Combine(Path.GetTempPath(), "mine", "mirror"), oldDir, newDir).Should().BeNull();
    }

    [Fact(DisplayName = "vba-mirrors 自身や、名前が前方一致するだけの別フォルダ（vba-mirrors-extra）は付け替えない")]
    public void 前方一致だけの別フォルダは付け替えない()
    {
        var oldDir = Path.Combine(Path.GetTempPath(), "old-data", "vba-mirrors");
        var newDir = Path.Combine(Path.GetTempPath(), "new-data", "vba-mirrors");

        VbaMirrorFolderName.RelocateRoot(oldDir, oldDir, newDir).Should().BeNull();
        VbaMirrorFolderName.RelocateRoot(oldDir + "-extra" + Path.DirectorySeparatorChar + "x", oldDir, newDir).Should().BeNull();
    }

    [Fact(DisplayName = "入れ子の相対位置も保たれる")]
    public void 入れ子の相対位置()
    {
        var oldDir = Path.Combine(Path.GetTempPath(), "old-data", "vba-mirrors");
        var newDir = Path.Combine(Path.GetTempPath(), "new-data", "vba-mirrors");
        var root = Path.Combine(oldDir, "a", "b");

        VbaMirrorFolderName.RelocateRoot(root, oldDir, newDir).Should().Be(Path.Combine(newDir, "a", "b"));
    }

    [Fact(DisplayName = "AppPaths の VbaMirrorsDirectory を基準にした付け替え（実際の使い方）")]
    public void AppPaths基準の付け替え()
    {
        var oldPaths = new AppPaths(Path.Combine(Path.GetTempPath(), "old-base"));
        var newPaths = new AppPaths(Path.Combine(Path.GetTempPath(), "new-base"));
        var root = Path.Combine(oldPaths.VbaMirrorsDirectory, VbaMirrorFolderName.Build(@"C:\Work\計画.xlsm"));

        var moved = VbaMirrorFolderName.RelocateRoot(root, oldPaths.VbaMirrorsDirectory, newPaths.VbaMirrorsDirectory);

        moved.Should().Be(Path.Combine(newPaths.VbaMirrorsDirectory, Path.GetFileName(root)));
    }
}
