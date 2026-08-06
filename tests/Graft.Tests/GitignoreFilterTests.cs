using System.Threading.Tasks;
using FluentAssertions;
using Graft.Features;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 仕様書10.2（除外規則）の <see cref="GitignoreFilter"/> 単体テスト。
/// glob（<c>*</c> <c>**</c> <c>?</c>）・行頭 <c>!</c> 否定・末尾 <c>/</c> のディレクトリ限定・
/// 先頭 <c>/</c> アンカー、ネストした .gitignore の解釈を実ファイル構成で検証する。
/// </summary>
public class GitignoreFilterTests
{
    [Fact(DisplayName = "*は同一階層内の任意文字列にマッチしパス区切りは越えない")]
    public async Task アスタリスクはパス区切りを越えない()
    {
        using var ws = new TempWorkspace();
        ws.WriteText(".gitignore", "*.log\n");
        var filter = await GitignoreFilter.LoadAsync(ws.RootPath);

        filter.IsIgnored("app.log", isDirectory: false).Should().BeTrue();
        filter.IsIgnored("logs/app.log", isDirectory: false).Should().BeTrue(".gitignoreのパターンは末尾一致でどの階層にも適用されるはず");
        filter.IsIgnored("app.log.txt", isDirectory: false).Should().BeFalse();
    }

    [Fact(DisplayName = "**/はディレクトリ境界を含め0階層以上にマッチする")]
    public async Task 二重アスタリスクは複数階層にマッチする()
    {
        using var ws = new TempWorkspace();
        ws.WriteText(".gitignore", "**/generated/**\n");
        var filter = await GitignoreFilter.LoadAsync(ws.RootPath);

        filter.IsIgnored("generated/a.txt", isDirectory: false).Should().BeTrue();
        filter.IsIgnored("src/generated/a.txt", isDirectory: false).Should().BeTrue();
        filter.IsIgnored("src/sub/generated/deep/a.txt", isDirectory: false).Should().BeTrue();
    }

    [Fact(DisplayName = "?は1文字のみにマッチする")]
    public async Task はてなは1文字のみにマッチする()
    {
        using var ws = new TempWorkspace();
        ws.WriteText(".gitignore", "file?.txt\n");
        var filter = await GitignoreFilter.LoadAsync(ws.RootPath);

        filter.IsIgnored("file1.txt", isDirectory: false).Should().BeTrue();
        filter.IsIgnored("file12.txt", isDirectory: false).Should().BeFalse();
        filter.IsIgnored("file.txt", isDirectory: false).Should().BeFalse();
    }

    [Fact(DisplayName = "行頭!は直前までの除外を打ち消す（再包含）")]
    public async Task 否定は直前の除外を打ち消す()
    {
        using var ws = new TempWorkspace();
        ws.WriteText(".gitignore", "*.log\n!important.log\n");
        var filter = await GitignoreFilter.LoadAsync(ws.RootPath);

        filter.IsIgnored("debug.log", isDirectory: false).Should().BeTrue();
        filter.IsIgnored("important.log", isDirectory: false).Should().BeFalse("否定ルールにより再包含されるはず");
    }

    [Fact(DisplayName = "末尾/はディレクトリのみに限定する")]
    public async Task 末尾スラッシュはディレクトリのみ除外する()
    {
        using var ws = new TempWorkspace();
        ws.WriteText(".gitignore", "build/\n");
        var filter = await GitignoreFilter.LoadAsync(ws.RootPath);

        filter.IsIgnored("build", isDirectory: true).Should().BeTrue();
        filter.IsIgnored("build", isDirectory: false).Should().BeFalse("末尾/指定はファイルには適用されないはず");
    }

    [Fact(DisplayName = "先頭/はリポジトリルートからのアンカー指定になる")]
    public async Task 先頭スラッシュはルートへアンカーされる()
    {
        using var ws = new TempWorkspace();
        ws.WriteText(".gitignore", "/only_root.txt\n");
        var filter = await GitignoreFilter.LoadAsync(ws.RootPath);

        filter.IsIgnored("only_root.txt", isDirectory: false).Should().BeTrue();
        filter.IsIgnored("nested/only_root.txt", isDirectory: false).Should().BeFalse("先頭/はルート直下のみを指すはず");
    }

    [Fact(DisplayName = "スラッシュを含むが先頭では無いパターンもアンカー扱いになる")]
    public async Task スラッシュを含むパターンはアンカー扱いになる()
    {
        using var ws = new TempWorkspace();
        ws.WriteText(".gitignore", "src/temp.txt\n");
        var filter = await GitignoreFilter.LoadAsync(ws.RootPath);

        filter.IsIgnored("src/temp.txt", isDirectory: false).Should().BeTrue();
        filter.IsIgnored("other/src/temp.txt", isDirectory: false).Should().BeFalse();
    }

    [Fact(DisplayName = "ネストした.gitignoreはそのディレクトリ配下にのみ効力を持つ")]
    public async Task ネストしたgitignoreは配下のみに効力を持つ()
    {
        using var ws = new TempWorkspace();
        ws.WriteText(".gitignore", "*.tmp\n");
        ws.WriteText("sub/.gitignore", "local.txt\n");
        var filter = await GitignoreFilter.LoadAsync(ws.RootPath);

        filter.IsIgnored("sub/local.txt", isDirectory: false).Should().BeTrue("sub配下の.gitignoreが効くはず");
        filter.IsIgnored("local.txt", isDirectory: false).Should().BeFalse("ルート直下にはsub/.gitignoreの規則は及ばないはず");
        filter.IsIgnored("a.tmp", isDirectory: false).Should().BeTrue("ルートの.gitignoreは全階層に及ぶはず");
        filter.IsIgnored("sub/a.tmp", isDirectory: false).Should().BeTrue();
    }

    [Fact(DisplayName = "コメント行と空行は無視される")]
    public async Task コメントと空行は無視される()
    {
        using var ws = new TempWorkspace();
        ws.WriteText(".gitignore", "# コメント\n\n*.log\n");
        var filter = await GitignoreFilter.LoadAsync(ws.RootPath);

        filter.IsIgnored("a.log", isDirectory: false).Should().BeTrue();
    }

    [Fact(DisplayName = "FromPatternsは既定除外パターンやプロジェクト単位の除外をgitignoreと同じ文法で表現できる")]
    public void FromPatternsは同じ文法で判定できる()
    {
        var filter = GitignoreFilter.FromPatterns(new[] { "node_modules/", "*.min.js" }, "既定除外");

        filter.IsIgnored("node_modules", isDirectory: true).Should().BeTrue();
        filter.IsIgnored("lib/app.min.js", isDirectory: false).Should().BeTrue();
        filter.IsIgnored("app.js", isDirectory: false).Should().BeFalse();
    }

    [Fact(DisplayName = "Mergeは合成後のフィルタで両方のルールを適用し、後段の否定で前段の除外を打ち消せる")]
    public void Mergeは合成した順序でルールを適用する()
    {
        var baseFilter = GitignoreFilter.FromPatterns(new[] { "*.log" }, "既定除外");
        var overrideFilter = GitignoreFilter.FromPatterns(new[] { "!keep.log" }, "プロジェクト設定");

        var merged = baseFilter.Merge(overrideFilter);

        merged.IsIgnored("debug.log", isDirectory: false).Should().BeTrue();
        merged.IsIgnored("keep.log", isDirectory: false).Should().BeFalse("後段の否定ルールが前段の除外を打ち消すはず");
    }
}
