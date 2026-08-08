using FluentAssertions;
using Graft.Features;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 実機検証で発見した不具合2（プロジェクト名の異常値がそのままUIに出る）に対する
/// <see cref="ProjectNameFormatter"/>／<see cref="Project.DisplayName"/> の単体テスト。
/// projects.json を直接書き換えて起動した際の再現データ
/// （空文字列の名前、改行・タグ・引用符混じりの名前、500文字の同一文字の繰り返し）を
/// そのまま入力として使う。
/// </summary>
public class ProjectNameFormatterTests
{
    [Fact(DisplayName = "空の名前はフォルダ名（rootの末尾要素）へ差し替わる")]
    public void 空の名前はフォルダ名になる()
    {
        var project = new Project { Id = "p_x", Name = "", Root = "/tmp/my-project" };

        project.DisplayName.Should().Be("my-project");
    }

    [Fact(DisplayName = "名前・rootの両方が空なら既定のプレースホルダーになる")]
    public void 名前とrootが両方空ならプレースホルダーになる()
    {
        // 実機検証データ: {"id":"p_aaa003","name":"","root":""}
        var project = new Project { Id = "p_aaa003", Name = "", Root = "" };

        project.DisplayName.Should().Be(ProjectNameFormatter.Placeholder);
    }

    [Fact(DisplayName = "空白のみの名前は空扱いになりフォルダ名へ差し替わる")]
    public void 空白のみの名前はフォルダ名になる()
    {
        var project = new Project { Id = "p_x", Name = "   ", Root = "/tmp/proj" };

        project.DisplayName.Should().Be("proj");
    }

    [Fact(DisplayName = "改行・タブを含む名前は空白へ置き換えられ1行に収まる")]
    public void 改行タブを含む名前は1行に正規化される()
    {
        // 実機検証データ: {"id":"p_aaa001","name":"名前に\"引用符\"と<タグ>と&記号\nと改行", ...}
        var project = new Project { Id = "p_aaa001", Name = "名前に\"引用符\"と<タグ>と&記号\nと改行", Root = "/tmp/x" };

        project.DisplayName.Should().NotContain("\n");
        project.DisplayName.Should().NotContain("\r");
        project.DisplayName.Should().Be("名前に\"引用符\"と<タグ>と&記号 と改行");
    }

    [Fact(DisplayName = "タブ・CRLFも空白へ置き換えられる")]
    public void タブとCRLFも空白になる()
    {
        var project = new Project { Id = "p_x", Name = "A\tB\r\nC\rD\nE", Root = "/tmp/x" };

        project.DisplayName.Should().Be("A B C D E");
    }

    [Fact(DisplayName = "前後の空白は除去される")]
    public void 前後の空白は除去される()
    {
        var project = new Project { Id = "p_x", Name = "  名前あり  ", Root = "/tmp/x" };

        project.DisplayName.Should().Be("名前あり");
    }

    [Fact(DisplayName = "500文字の極端に長い名前でも正規化結果は切り詰めず全文を保持する（切り詰めはUI側の責務）")]
    public void 極端に長い名前は切り詰めずそのまま保持される()
    {
        // 実機検証データ: {"id":"p_aaa002","name":"あ（500文字繰り返し）", ...}
        var longName = new string('あ', 500);
        var project = new Project { Id = "p_aaa002", Name = longName, Root = "/tmp" };

        project.DisplayName.Should().HaveLength(500);
        project.DisplayName.Should().Be(longName);
    }

    [Fact(DisplayName = "通常の名前はそのまま表示される")]
    public void 通常の名前はそのまま表示される()
    {
        var project = new Project { Id = "p_x", Name = "MyProject", Root = "/tmp/other" };

        project.DisplayName.Should().Be("MyProject");
    }

    [Fact(DisplayName = "rootの末尾に区切り文字があってもフォルダ名を正しく取り出せる")]
    public void root末尾の区切り文字を無視してフォルダ名を取り出す()
    {
        var project = new Project { Id = "p_x", Name = "", Root = "/tmp/my-project/" };

        project.DisplayName.Should().Be("my-project");
    }

    [Fact(DisplayName = "Windows形式の区切り文字でもフォルダ名を取り出せる")]
    public void Windows形式の区切り文字でもフォルダ名を取り出せる()
    {
        var project = new Project { Id = "p_x", Name = "", Root = @"C:\Users\me\my-project" };

        project.DisplayName.Should().Be("my-project");
    }

    [Fact(DisplayName = "Nameがnullでも例外にならずフォルダ名またはプレースホルダーになる")]
    public void Nameがnullでも例外にならない()
    {
        var project = new Project { Id = "p_x", Name = null!, Root = "/tmp/proj" };

        var act = () => project.DisplayName;

        act.Should().NotThrow();
        project.DisplayName.Should().Be("proj");
    }
}
