using FluentAssertions;
using Graft.Features;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// <c>git status --porcelain=v1 -z</c> の出力をプロジェクト相対のパスへ直す純ロジック
/// （<see cref="GitChangedFilesParser"/>）の単体テスト。実際のgitは使わず、出力文字列そのものを渡す。
/// 実gitとの突き合わせは<see cref="GitIntegrationTests"/>のGetChangedFilesAsyncのテストで行う。
/// </summary>
public class GitChangedFilesParserTests
{
    /// <summary>項目をNUL区切りで連結し、末尾にもNULを付ける（gitの-z出力と同じ形）。</summary>
    private static string Z(params string[] fields) => string.Join('\0', fields) + "\0";

    [Fact(DisplayName = "変更・追加・未追跡のファイルが並び、ステージ有無（X・Yの位置）は問わない")]
    public void 変更と追加と未追跡を拾う()
    {
        var output = Z(" M src/a.cs", "M  src/b.cs", "MM src/c.cs", "A  src/d.cs", "?? src/e.cs", " T src/f.cs");

        var (paths, deleted) = GitChangedFilesParser.Parse(output, "");

        paths.Should().Equal("src/a.cs", "src/b.cs", "src/c.cs", "src/d.cs", "src/e.cs", "src/f.cs");
        deleted.Should().Be(0);
    }

    [Fact(DisplayName = "名前変更は新しい側だけを拾い、元の名前（直後の項目）は変更ファイルに数えない")]
    public void 名前変更は新しい側だけ()
    {
        var output = Z("R  new/name.cs", "old/name.cs", " M other.cs");

        var (paths, deleted) = GitChangedFilesParser.Parse(output, "");

        paths.Should().Equal("new/name.cs", "other.cs");
        deleted.Should().Be(0, "元の名前は削除として数えず、そもそも読み飛ばす");
    }

    [Fact(DisplayName = "コピー（C）も名前変更と同じく、新しい側だけを拾う")]
    public void コピーも新しい側だけ()
    {
        var (paths, _) = GitChangedFilesParser.Parse(Z("C  copy.cs", "origin.cs"), "");

        paths.Should().Equal("copy.cs");
    }

    [Fact(DisplayName = "削除されたファイルは変更ファイルに含めず、件数だけ数える（作業ツリー側の削除・git rm済みの両方）")]
    public void 削除は対象外で件数だけ数える()
    {
        var output = Z(" D gone1.cs", "D  gone2.cs", "AD added-then-deleted.cs", " M kept.cs", "DD both.cs");

        var (paths, deleted) = GitChangedFilesParser.Parse(output, "");

        paths.Should().Equal("kept.cs");
        deleted.Should().Be(4);
    }

    [Fact(DisplayName = "名前変更の後で作業ツリーから消したもの（RD）は、新しい側も存在しないので対象外")]
    public void 名前変更後に消したものは対象外()
    {
        var (paths, deleted) = GitChangedFilesParser.Parse(Z("RD moved.cs", "orig.cs", " M kept.cs"), "");

        paths.Should().Equal("kept.cs");
        deleted.Should().Be(1);
    }

    [Fact(DisplayName = "空白・日本語・矢印を含む名前も、引用符の処理なしでそのまま取れる（-zの出力）")]
    public void 特殊な名前をそのまま取る()
    {
        var output = Z("?? 設計 メモ/画面 仕様.md", " M weird -> name.txt", "R  新 名前.cs", "旧 名前.cs");

        var (paths, _) = GitChangedFilesParser.Parse(output, "");

        paths.Should().Equal("設計 メモ/画面 仕様.md", "weird -> name.txt", "新 名前.cs");
    }

    [Fact(DisplayName = "同じパスが複数回出ても1件にまとめる")]
    public void 重複は1件にまとめる()
    {
        var (paths, _) = GitChangedFilesParser.Parse(Z("D  a.cs", "?? a.cs", "?? a.cs"), "");

        paths.Should().Equal("a.cs");
    }

    [Fact(DisplayName = "プロジェクトのルートがリポジトリのサブフォルダなら、接頭辞を外してプロジェクト相対にする")]
    public void サブフォルダのルートでは接頭辞を外す()
    {
        var output = Z(" M app/src/a.cs", "?? app/readme.md", "R  app/new.cs", "app/old.cs");

        var (paths, _) = GitChangedFilesParser.Parse(output, "app/");

        paths.Should().Equal("src/a.cs", "readme.md", "new.cs");
    }

    [Fact(DisplayName = "サブフォルダのルートでは、プロジェクトの外（リポジトリの別の場所・名前の似たフォルダ）の変更は含めない")]
    public void プロジェクトの外の変更は含めない()
    {
        var output = Z(" M app/src/a.cs", " M other/b.cs", " M app2/c.cs", " M README.md", " D app/gone.cs", " D other/gone.cs");

        var (paths, deleted) = GitChangedFilesParser.Parse(output, "app/");

        paths.Should().Equal("src/a.cs");
        deleted.Should().Be(1, "削除の件数もプロジェクトの中のものだけ数える");
    }

    [Theory(DisplayName = "接頭辞の書き方（末尾の区切り無し・バックスラッシュ・先頭の ./ ・大文字小文字違い）に左右されない")]
    [InlineData("app")]
    [InlineData("app/")]
    [InlineData(@"app\")]
    [InlineData("./app/")]
    [InlineData("APP/")]
    public void 接頭辞の書き方に左右されない(string prefix)
    {
        var (paths, _) = GitChangedFilesParser.Parse(Z(" M app/x.cs"), prefix);

        paths.Should().Equal("x.cs");
    }

    [Fact(DisplayName = "深いサブフォルダ（a/b/c/）のルートでも正しく外せる")]
    public void 深いサブフォルダでも外せる()
    {
        var (paths, _) = GitChangedFilesParser.Parse(Z(" M a/b/c/d/e.cs", " M a/b/x.cs"), "a/b/c/");

        paths.Should().Equal("d/e.cs");
    }

    [Fact(DisplayName = "無視されているファイル（!!）と、記号として解釈できない行（標準エラーの警告文など）は数えない")]
    public void 無視ファイルと警告文は数えない()
    {
        var output = Z("!! build/out.dll", " M real.cs") + "warning: could not open directory 'x/': Permission denied\n";

        var (paths, deleted) = GitChangedFilesParser.Parse(output, "");

        paths.Should().Equal("real.cs");
        deleted.Should().Be(0);
    }

    [Fact(DisplayName = "出力が空なら変更ファイルは0件")]
    public void 空の出力は0件()
    {
        var (paths, deleted) = GitChangedFilesParser.Parse(string.Empty, "");

        paths.Should().BeEmpty();
        deleted.Should().Be(0);
    }

    [Fact(DisplayName = "フォルダとして返ったパス（サブモジュール・入れ子のリポジトリ）は末尾の区切りを外す")]
    public void フォルダは末尾の区切りを外す()
    {
        var (paths, _) = GitChangedFilesParser.Parse(Z("?? nested-repo/"), "");

        paths.Should().Equal("nested-repo");
    }
}
