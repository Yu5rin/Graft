using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Core;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// v1.0.15 セキュリティ対応: <see cref="PathGuard"/> の書き込み先の検査に見つかった4つの穴
/// （(1) <c>.git</c> 配下への書き込み、(2) 拡張子なしの一律許可、(3) セグメント中の <c>:</c> による
/// 拡張子ホワイトリスト回避、(4) <c>.gitignore</c> が拒否される不整合）を固定する。
/// <para>
/// 【なぜ別ファイルにしたか】 <see cref="PathGuardTests"/> は「ルート外・リンク脱出・サイズ・
/// ロック」という従来からの検査の表であり、そこへ混ぜると「今回どこを塞いだのか」が
/// 読み取れなくなる。塞いだ穴とその対照（正常な用途が壊れていないこと）を1か所にまとめ、
/// 後から「なぜこの制限があるのか」を辿れるようにしている。
/// </para>
/// <para>
/// 【修正前の実測（対照）】 このファイルの「拒否されるべき」テストは、修正前のコードでは
/// すべて<b>成功（＝素通り）</b>していた。実際に次のパスが <c>PathGuard.Resolve</c> を通過し、
/// 適用エンジン経由でファイルが書き込まれることまで確認している:
/// <c>.git/hooks/pre-commit</c> / <c>.git/config</c> / <c>Makefile</c> / <c>script</c> /
/// <c>CON</c> / <c>NUL</c> / <c>COM1</c> / <c>evil.exe:payload.txt</c>。
/// 逆に <c>.gitignore</c> は <c>Path.GetExtension</c> が <c>".gitignore"</c> を返すため
/// E202 で拒否されていた（<c>Makefile</c> は通るのに <c>.gitignore</c> は通らない、という不整合）。
/// </para>
/// </summary>
public class PathGuardWriteGuardTests
{
    // ------------------------------------------------------------------
    // (1) .git / .hg / .svn 配下への書き込みは常に拒否する（最優先）
    // ------------------------------------------------------------------

    [Theory(DisplayName = "穴1: バージョン管理の内部フォルダ配下への書き込みはE211で拒否される")]
    [InlineData(".git/hooks/pre-commit")]
    [InlineData(".git/config")]
    [InlineData(".git/hooks/post-checkout")]
    [InlineData(".git/info/exclude")]
    [InlineData(".hg/hgrc")]
    [InlineData(".svn/entries")]
    public void バージョン管理の内部フォルダ配下はE211になる(string relativePath)
    {
        using var ws = new TempWorkspace();
        var guard = new PathGuard(ws.RootPath, PathGuardOptions.Default);

        var result = guard.Resolve(relativePath);

        result.IsSuccess.Should().BeFalse(
            "git は対象リポジトリの .git/config や .git/hooks/* をコマンド実行の設定として読むため、" +
            "ここへ書き込めることは任意コード実行と同義になる");
        result.Errors.Single().Code.Should().Be(ErrorCode.E211);
    }

    [Theory(DisplayName = "穴1: .git の比較は大文字小文字を区別しない（Windowsでは.GITも同じ場所を指す）")]
    [InlineData(".GIT/config")]
    [InlineData(".Git/hooks/pre-commit")]
    [InlineData(".HG/hgrc")]
    public void バージョン管理の内部フォルダの比較は大文字小文字を無視する(string relativePath)
    {
        using var ws = new TempWorkspace();
        var guard = new PathGuard(ws.RootPath, PathGuardOptions.Default);

        var result = guard.Resolve(relativePath);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Single().Code.Should().Be(ErrorCode.E211);
    }

    [Theory(DisplayName = "穴1: 途中の階層にある .git（入れ子のリポジトリ・サブモジュール）も拒否される")]
    [InlineData("vendor/lib/.git/hooks/pre-commit")]
    [InlineData("submodules/theme/.git/config")]
    public void 途中の階層のバージョン管理内部フォルダも拒否される(string relativePath)
    {
        using var ws = new TempWorkspace();
        var guard = new PathGuard(ws.RootPath, PathGuardOptions.Default);

        var result = guard.Resolve(relativePath);

        result.IsSuccess.Should().BeFalse("サブモジュールや入れ子のリポジトリのフックも同じ危険度のため");
        result.Errors.Single().Code.Should().Be(ErrorCode.E211);
    }

    [Fact(DisplayName = "穴1: 拡張子検査を通さない経路（ResolveDirectory・ResolveImportTarget）でも .git 配下は拒否される")]
    public void 拡張子検査を通さない経路でもバージョン管理内部フォルダは拒否される()
    {
        using var ws = new TempWorkspace();
        var guard = new PathGuard(ws.RootPath, PathGuardOptions.Default);

        guard.ResolveDirectory(".git/hooks").Errors.Single().Code.Should().Be(ErrorCode.E211);
        guard.ResolveImportTarget(".git/hooks/pre-commit").Errors.Single().Code.Should().Be(ErrorCode.E211);
        guard.Resolve(".git").Errors.Single().Code.Should().Be(ErrorCode.E211);
    }

    [Theory(DisplayName = "対照: .git で始まる別名のファイル・フォルダ（.github/.gitignore等）は従来どおり通る")]
    [InlineData(".github/workflows/ci.yml")]
    [InlineData(".gitignore")]
    [InlineData(".gitattributes")]
    [InlineData("git/README.md")]
    [InlineData("src/.gitkeep")]
    public void gitで始まる別名は拒否されない(string relativePath)
    {
        using var ws = new TempWorkspace();
        var guard = new PathGuard(ws.RootPath, PathGuardOptions.Default);

        var result = guard.Resolve(relativePath);

        result.IsSuccess.Should().BeTrue(
            "拒否するのはセグメントが .git そのものである場合だけで、前方一致で判定してはいけない");
    }

    // ------------------------------------------------------------------
    // (2) 拡張子なしの一律許可をやめ、名前の許可リストで判定する
    // ------------------------------------------------------------------

    [Theory(DisplayName = "穴2: 許可リストに無い拡張子なしのファイル名はE202で拒否される")]
    [InlineData("script")]
    [InlineData("run")]
    [InlineData("bin/entrypoint")]
    [InlineData("pre-commit")]
    public void 許可リストに無い拡張子なしの名前はE202になる(string relativePath)
    {
        using var ws = new TempWorkspace();
        var guard = new PathGuard(ws.RootPath, PathGuardOptions.Default);

        var result = guard.Resolve(relativePath);

        result.IsSuccess.Should().BeFalse(
            "拡張子が無いというだけで無条件に通すと、実行権限を付ければ動く任意の名前を書けてしまう");
        result.Errors.Single().Code.Should().Be(ErrorCode.E202);
    }

    [Theory(DisplayName = "対照: よく使う拡張子なしのファイル名は従来どおり通る")]
    [InlineData("Dockerfile")]
    [InlineData("Makefile")]
    [InlineData("LICENSE")]
    [InlineData("README")]
    [InlineData("CHANGELOG")]
    [InlineData("Rakefile")]
    [InlineData("Gemfile")]
    [InlineData("Procfile")]
    [InlineData("docker/Dockerfile")]
    [InlineData("dockerfile")]
    [InlineData("makefile")]
    public void 許可リストにある拡張子なしの名前は通る(string relativePath)
    {
        using var ws = new TempWorkspace();
        var guard = new PathGuard(ws.RootPath, PathGuardOptions.Default);

        var result = guard.Resolve(relativePath);

        result.IsSuccess.Should().BeTrue("拡張子なしで許可する名前の一覧に含まれているはず");
    }

    [Fact(DisplayName = "穴2: 許可リストはPathGuardOptionsで差し替えられる")]
    public void 拡張子なしの許可リストは差し替えられる()
    {
        using var ws = new TempWorkspace();
        var options = PathGuardOptions.Default with { AllowedExtensionlessNames = new[] { "script" } };
        var guard = new PathGuard(ws.RootPath, options);

        guard.Resolve("script").IsSuccess.Should().BeTrue("差し替えた一覧に含まれる名前は通るべき");
        guard.Resolve("Dockerfile").IsSuccess.Should().BeFalse("差し替えた一覧に含まれない名前は拒否されるべき");
    }

    [Theory(DisplayName = "穴5: Windowsの予約デバイス名は許可リスト方式の副作用として拒否される（追加対応は不要）")]
    [InlineData("CON")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("PRN")]
    [InlineData("LPT1")]
    [InlineData("AUX")]
    public void 予約デバイス名は拒否される(string relativePath)
    {
        using var ws = new TempWorkspace();
        var guard = new PathGuard(ws.RootPath, PathGuardOptions.Default);

        var result = guard.Resolve(relativePath);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Single().Code.Should().Be(ErrorCode.E202);
    }

    // ------------------------------------------------------------------
    // (3) セグメントに ':' を含むパス（Windowsの代替データストリーム）を拒否する
    // ------------------------------------------------------------------

    [Theory(DisplayName = "穴3: セグメントに':'を含むパスはE212で拒否される（代替データストリームによるホワイトリスト回避）")]
    [InlineData("evil.exe:payload.txt")]
    [InlineData("payload.txt:hidden")]
    [InlineData("a.txt::$DATA")]
    [InlineData("src/evil.exe:note.md")]
    [InlineData("dir:stream/inner.txt")]
    public void セグメントにコロンを含むパスはE212になる(string relativePath)
    {
        using var ws = new TempWorkspace();
        var guard = new PathGuard(ws.RootPath, PathGuardOptions.Default);

        var result = guard.Resolve(relativePath);

        result.IsSuccess.Should().BeFalse(
            "Path.GetExtension(\"evil.exe:payload.txt\") は \".txt\" を返すため、" +
            "拡張子ホワイトリストだけでは .exe の書き込みを止められない");
        result.Errors.Single().Code.Should().Be(ErrorCode.E212);
    }

    [Fact(DisplayName = "穴3: ':'の拒否は拡張子検査を通さない経路（ResolveDirectory・ResolveImportTarget）にも効く")]
    public void コロンの拒否は拡張子検査を通さない経路にも効く()
    {
        using var ws = new TempWorkspace();
        var guard = new PathGuard(ws.RootPath, PathGuardOptions.Default);

        guard.ResolveDirectory("dir:stream").Errors.Single().Code.Should().Be(ErrorCode.E212);
        guard.ResolveImportTarget("evil.exe:payload.png").Errors.Single().Code.Should().Be(ErrorCode.E212);
    }

    [Fact(DisplayName = "対照: 絶対パスの拒否（E201）は':'の判定より先に効く（ドライブ文字を巻き添えにしない）")]
    public void 絶対パスは従来どおりE201のまま()
    {
        using var ws = new TempWorkspace();
        var guard = new PathGuard(ws.RootPath, PathGuardOptions.Default);

        guard.Resolve(@"C:\Windows\system32\evil.txt").Errors.Single().Code.Should().Be(ErrorCode.E201);
    }

    // ------------------------------------------------------------------
    // (4) .gitignore 等の「ドットで始まる設定ファイル」を許可する
    // ------------------------------------------------------------------

    [Theory(DisplayName = "穴4: ドットで始まる設定ファイルは通る（Makefileは通るのに.gitignoreはE202、という不整合の解消）")]
    [InlineData(".gitignore")]
    [InlineData(".gitattributes")]
    [InlineData(".editorconfig")]
    [InlineData(".dockerignore")]
    [InlineData("src/.gitignore")]
    public void ドットで始まる設定ファイルは通る(string relativePath)
    {
        using var ws = new TempWorkspace();
        var guard = new PathGuard(ws.RootPath, PathGuardOptions.Default);

        var result = guard.Resolve(relativePath);

        result.IsSuccess.Should().BeTrue(
            ".gitignore はプロジェクト直下の普通のファイルであり、.git フォルダとは別物");
    }

    [Theory(DisplayName = "穴4: 許可リストに無いドットファイル（シェルが自動で読み込むもの等）は拒否される")]
    [InlineData(".bashrc")]
    [InlineData(".profile")]
    [InlineData(".envrc")]
    [InlineData(".zshrc")]
    public void 許可リストに無いドットファイルは拒否される(string relativePath)
    {
        using var ws = new TempWorkspace();
        var guard = new PathGuard(ws.RootPath, PathGuardOptions.Default);

        var result = guard.Resolve(relativePath);

        result.IsSuccess.Should().BeFalse(
            "シェルの起動時に自動で実行される設定ファイルは、フックと同じ「勝手に走る」性質を持つ");
        result.Errors.Single().Code.Should().Be(ErrorCode.E202);
    }

    [Theory(DisplayName = "対照: ドットで始まっていても拡張子があるものは従来どおり拡張子で判定される")]
    [InlineData(".graft/config.json", true)]
    [InlineData(".vscode/settings.json", true)]
    [InlineData(".hidden.exe", false)]
    [InlineData(".env.sh", false)]
    public void ドットで始まる名前でも拡張子があれば拡張子で判定される(string relativePath, bool expected)
    {
        using var ws = new TempWorkspace();
        var guard = new PathGuard(ws.RootPath, PathGuardOptions.Default);

        guard.Resolve(relativePath).IsSuccess.Should().Be(expected);
    }

    // ------------------------------------------------------------------
    // 正常な用途が壊れていないことの確認（回帰）
    // ------------------------------------------------------------------

    [Theory(DisplayName = "回帰: 通常のテキストファイルは従来どおり通る")]
    [InlineData("src/Program.cs")]
    [InlineData("docs/README.md")]
    [InlineData("package.json")]
    [InlineData("src/app/main.py")]
    [InlineData("styles/theme.css")]
    [InlineData("data/table.sql")]
    public void 通常のファイルは従来どおり通る(string relativePath)
    {
        using var ws = new TempWorkspace();
        var guard = new PathGuard(ws.RootPath, PathGuardOptions.Default);

        guard.Resolve(relativePath).IsSuccess.Should().BeTrue();
    }

    [Fact(DisplayName = "回帰: 取り込み（ResolveImportTarget）とフォルダ（ResolveDirectory）は拡張子・名前の検査の対象外のまま")]
    public void 取り込みとフォルダは名前の検査の対象外のまま()
    {
        using var ws = new TempWorkspace();
        var guard = new PathGuard(ws.RootPath, PathGuardOptions.Default);

        // 取り込みは利用者が明示的に選んだ既存ファイルのコピーであり、画像等の非テキスト資産の
        // 持ち込みが動機。拡張子なしの一律許可をやめても、この経路の判定は変わってはいけない。
        guard.ResolveImportTarget("assets/photo.png").IsSuccess.Should().BeTrue();
        guard.ResolveImportTarget("bin/tool").IsSuccess.Should().BeTrue("取り込みは名前の許可リストの対象外");
        guard.ResolveImportTarget("archive/backup.zip").IsSuccess.Should().BeTrue();

        // フォルダ名は拡張子ホワイトリストの対象外（13章はファイルに対する規則のため）。
        guard.ResolveDirectory("scripts").IsSuccess.Should().BeTrue();
        guard.ResolveDirectory("assets/img").IsSuccess.Should().BeTrue();
        guard.ResolveDirectory(".vscode").IsSuccess.Should().BeTrue();
    }

    // ------------------------------------------------------------------
    // 実際に報告された攻撃経路（適用エンジンを通した端から端までの確認）
    // ------------------------------------------------------------------

    [Fact(DisplayName = "穴1（実際の攻撃経路）: .git/hooks/pre-commit を書くパッチは解析で適用不可になり、適用してもファイルが作られない")]
    public async Task gitフックを書き込むパッチは適用エンジンを通っても書き込まれない()
    {
        // セキュリティ点検で実測された経路そのもの。修正前はこのパッチが
        // plans=.git/hooks/pre-commit canApply=True → applySuccess=True exists=True まで通り、
        // フックの中身（echo pwned）がディスクへ書き込まれていた。
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        var patchText = """
            <<<< FILE: .git/hooks/pre-commit MODE=FULL
            #!/bin/sh
            echo pwned
            >>>> END
            """;
        var ctx = harness.MakeContext(revision: 1);

        var dryRun = await harness.DryRunAsync(patchText, ctx);

        dryRun.Plans.Should().NotBeEmpty();
        dryRun.Plans.Should().OnlyContain(p => !p.CanApply, "解析の時点で適用不可になっているべき");
        dryRun.Plans.SelectMany(p => p.Issues).Should().Contain(i => i.Code == ErrorCode.E211);

        await harness.ApplyAsync(dryRun, ctx);

        harness.ProjectFileExists(".git/hooks/pre-commit").Should().BeFalse(
            "適用まで進んでもフックが書き込まれてはならない（gitのコミット時に自動実行されるため）");
    }

    [Fact(DisplayName = "穴3（実際の攻撃経路）: 代替データストリーム記法で.exeを書くパッチも適用エンジンを通らない")]
    public async Task 代替データストリーム記法のパッチも適用エンジンを通らない()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        var patchText = """
            <<<< FILE: evil.exe:payload.txt MODE=FULL
            MZ...
            >>>> END
            """;
        var ctx = harness.MakeContext(revision: 1);

        var dryRun = await harness.DryRunAsync(patchText, ctx);

        dryRun.Plans.Should().OnlyContain(p => !p.CanApply);
        dryRun.Plans.SelectMany(p => p.Issues).Should().Contain(i => i.Code == ErrorCode.E212);
    }

    [Fact(DisplayName = "回帰: 拒否したパスは実際に書き込みへ進まない（Resolveの戻り値が失敗であること）")]
    public void 拒否したパスの絶対パスは返らない()
    {
        using var ws = new TempWorkspace();
        var guard = new PathGuard(ws.RootPath, PathGuardOptions.Default);

        var result = guard.Resolve(".git/hooks/pre-commit");

        result.IsSuccess.Should().BeFalse();
        // 呼び出し元（FileTreeService・ApplyEngine）はIsSuccessがfalseならValueを見ずに中断する。
        // 実ファイルが作られていないことも併せて確かめる。
        File.Exists(Path.Combine(ws.RootPath, ".git", "hooks", "pre-commit")).Should().BeFalse();
    }
}
