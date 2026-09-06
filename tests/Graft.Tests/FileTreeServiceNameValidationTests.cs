using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Core;
using Graft.Features;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 異常系点検「低」4件目（新規作成・名前の変更でパス区切りや不正文字が素通りする）の回帰テスト。
///
/// 実測で確認した不具合:
/// <list type="bullet">
/// <item>名前の変更で newName="dir/moved.txt" を渡すと、ドキュメント（「同じ親フォルダ内での
/// 名前変更のみ」）に反してdir/へ実際に移動できた。</item>
/// <item>新規ファイルで fileName="sub/child.txt"（subが無い）を渡すと、失敗はするが
/// 「一時ファイルの作成に失敗しました…（詳細: …/sub/.child.txt.graft-tmp-<guid>…）」と
/// 内部の一時ファイル名を晒すメッセージが出た。</item>
/// <item>300文字の名前で「予期しないエラーが発生しました…（詳細: The path '…' is too
/// long...）」という英語の生メッセージが出た。</item>
/// <item>"a.txt "（末尾空白）で「拡張子 '.txt ' は許可されていません」という、実際の問題
/// （末尾空白）とは無関係なメッセージが出た。</item>
/// <item>"a\nb.txt" / "a\tb.txt" はLinux上ではそのまま作成できた。</item>
/// </list>
/// </summary>
public class FileTreeServiceNameValidationTests
{
    private static Project MakeProject(TempWorkspace ws) => new() { Root = ws.CreateDirectory("project") };

    [Fact(DisplayName = "不具合回帰: 名前の変更でパス区切りを含むnewNameは拒否され、別フォルダへ移動しない")]
    public async Task 名前の変更でパス区切りを含むnewNameは拒否される()
    {
        using var ws = new TempWorkspace();
        var project = MakeProject(ws);
        ws.CreateDirectory("project/dir");
        var filePath = ws.WriteText("project/original.txt", "内容");
        var service = new FileTreeService();

        var result = await service.RenameAsync(project, "original.txt", "dir/moved.txt", isDirectory: false, PathGuardOptions.Default);

        result.IsSuccess.Should().BeFalse("パス区切りを含む名前は1階層の名前として拒否されるべき");
        result.Issues.Should().Contain(i => i.Code == ErrorCode.E213);
        File.Exists(filePath).Should().BeTrue("拒否された場合、元のファイルは移動されずそのまま残っているべき");
        File.Exists(Path.Combine(project.Root, "dir", "moved.txt")).Should().BeFalse("dir/へ移動してしまってはならない（ドキュメント通り同じ親フォルダ内でのみ変更可能）");
    }

    [Fact(DisplayName = "不具合回帰: 新規ファイルでパス区切りを含む名前は、内部の一時ファイル名を晒さず拒否される")]
    public async Task 新規ファイルでパス区切りを含む名前は拒否される()
    {
        using var ws = new TempWorkspace();
        var project = MakeProject(ws);
        var service = new FileTreeService();

        var result = await service.CreateFileAsync(project, string.Empty, "sub/child.txt", PathGuardOptions.Default);

        result.IsSuccess.Should().BeFalse();
        result.Issues.Should().ContainSingle().Which.Code.Should().Be(ErrorCode.E213);
        result.Issues[0].ToDisplayText().Should().NotContain("graft-tmp", "内部の一時ファイル名を利用者に晒してはならない");
    }

    [Fact(DisplayName = "不具合回帰: 300文字の名前は日本語の理由付きで拒否され、英語の生例外を出さない")]
    public async Task 長すぎる名前は日本語で拒否される()
    {
        using var ws = new TempWorkspace();
        var project = MakeProject(ws);
        var service = new FileTreeService();
        var longName = new string('a', 300) + ".txt";

        var result = await service.CreateFileAsync(project, string.Empty, longName, PathGuardOptions.Default);

        result.IsSuccess.Should().BeFalse();
        result.Issues.Should().ContainSingle().Which.Code.Should().Be(ErrorCode.E213);
        result.Issues[0].ToDisplayText().Should().Contain("255文字以内").And.NotContain("too long", "英語の生の例外メッセージを出してはならない");
    }

    [Fact(DisplayName = "不具合回帰: 末尾に空白がある名前は「拡張子」ではなく「末尾の空白」が理由だと伝える")]
    public async Task 末尾空白の名前は末尾空白が理由だと伝える()
    {
        using var ws = new TempWorkspace();
        var project = MakeProject(ws);
        var service = new FileTreeService();

        var result = await service.CreateFileAsync(project, string.Empty, "a.txt ", PathGuardOptions.Default);

        result.IsSuccess.Should().BeFalse();
        var detail = result.Issues.Should().ContainSingle().Which.Detail;
        detail.Should().Contain("末尾").And.Contain("空白");
        detail.Should().NotContain("拡張子", "実際の問題（末尾の空白）と無関係な「拡張子」の話にしてはならない");
    }

    [Theory(DisplayName = "不具合回帰: 改行・タブなど制御文字を含む名前はLinux上でも作成できず拒否される")]
    [InlineData("a\nb.txt")]
    [InlineData("a\tb.txt")]
    [InlineData("a\rb.txt")]
    public async Task 制御文字を含む名前は拒否される(string name)
    {
        using var ws = new TempWorkspace();
        var project = MakeProject(ws);
        var service = new FileTreeService();

        var result = await service.CreateFileAsync(project, string.Empty, name, PathGuardOptions.Default);

        result.IsSuccess.Should().BeFalse("Linux上でもそのまま作成できてはならない（ツリー表示が崩れる不具合の回帰）");
        result.Issues.Should().Contain(i => i.Code == ErrorCode.E213);
    }

    [Fact(DisplayName = "末尾がピリオドの名前は拒否される（Windowsのエクスプローラ制約とクロスプラットフォームで揃える）")]
    public async Task 末尾ピリオドの名前は拒否される()
    {
        using var ws = new TempWorkspace();
        var project = MakeProject(ws);
        var service = new FileTreeService();

        var result = await service.CreateFolderAsync(project, string.Empty, "folder.", PathGuardOptions.Default);

        result.IsSuccess.Should().BeFalse();
        result.Issues.Should().Contain(i => i.Code == ErrorCode.E213);
    }

    [Fact(DisplayName = "空・空白のみの名前は拒否される")]
    public async Task 空白のみの名前は拒否される()
    {
        using var ws = new TempWorkspace();
        var project = MakeProject(ws);
        var service = new FileTreeService();

        var result = await service.CreateFileAsync(project, string.Empty, "   ", PathGuardOptions.Default);

        result.IsSuccess.Should().BeFalse();
        result.Issues.Should().Contain(i => i.Code == ErrorCode.E213);
    }

    [Fact(DisplayName = "妥当な名前での新規ファイル作成・名前の変更は従来どおり成功する（回帰: 検証追加が正常系を壊していないこと）")]
    public async Task 妥当な名前は従来どおり成功する()
    {
        using var ws = new TempWorkspace();
        var project = MakeProject(ws);
        var service = new FileTreeService();

        var created = await service.CreateFileAsync(project, string.Empty, "note.txt", PathGuardOptions.Default);
        created.IsSuccess.Should().BeTrue();

        var renamed = await service.RenameAsync(project, "note.txt", "renamed.txt", isDirectory: false, PathGuardOptions.Default);
        renamed.IsSuccess.Should().BeTrue();
        File.Exists(Path.Combine(project.Root, "renamed.txt")).Should().BeTrue();

        var folder = await service.CreateFolderAsync(project, string.Empty, "同じ親フォルダ内のフォルダ", PathGuardOptions.Default);
        folder.IsSuccess.Should().BeTrue("日本語名は引き続き使えるべき（不許可文字の追加検証が誤爆していないこと）");
    }
}
