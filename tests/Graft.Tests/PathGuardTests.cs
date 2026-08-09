using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using Graft.Core;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 仕様書4.7・13章（PathGuard）の単体テスト。ルート外パスの拒否、拡張子・サイズ・
/// 読み取り専用の判定、大文字小文字を無視した比較、"\" 区切りの受理、
/// シンボリックリンク経由でのルート外脱出の防止を検証する。
/// </summary>
public class PathGuardTests
{
    [Theory(DisplayName = "絶対パス・上位ディレクトリ参照はE201になる")]
    [InlineData("/etc/passwd")]
    [InlineData("../outside.txt")]
    [InlineData("sub/../../outside.txt")]
    [InlineData("sub/../../../outside.txt")]
    public void ルート外パスはE201になる(string relativePath)
    {
        using var ws = new TempWorkspace();
        var guard = new PathGuard(ws.RootPath, PathGuardOptions.Default);

        var result = guard.Resolve(relativePath);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Single().Code.Should().Be(ErrorCode.E201);
    }

    [Fact(DisplayName = "空のパスはE201になる")]
    public void 空のパスはE201になる()
    {
        using var ws = new TempWorkspace();
        var guard = new PathGuard(ws.RootPath, PathGuardOptions.Default);

        var result = guard.Resolve("   ");

        result.IsSuccess.Should().BeFalse();
        result.Errors.Single().Code.Should().Be(ErrorCode.E201);
    }

    [Fact(DisplayName = "未許可拡張子はE202になる")]
    public void 未許可拡張子はE202になる()
    {
        using var ws = new TempWorkspace();
        var guard = new PathGuard(ws.RootPath, PathGuardOptions.Default);

        var result = guard.Resolve("scripts/malicious.exe");

        result.IsSuccess.Should().BeFalse();
        result.Errors.Single().Code.Should().Be(ErrorCode.E202);
    }

    [Fact(DisplayName = "不具合2: 拡張子の無いファイル名（Dockerfile等）はホワイトリストの対象外として許可される")]
    public void 拡張子の無いファイル名は許可される()
    {
        using var ws = new TempWorkspace();
        var guard = new PathGuard(ws.RootPath, PathGuardOptions.Default);

        var result = guard.Resolve("Dockerfile");

        result.IsSuccess.Should().BeTrue(
            "拡張子ホワイトリストは危険な拡張子（.exe等）の遮断が目的であり、拡張子そのものが無い名前は対象外のはず");
    }

    [Fact(DisplayName = "拡張子の比較は大文字小文字を無視する")]
    public void 拡張子の比較は大文字小文字を無視する()
    {
        using var ws = new TempWorkspace();
        var guard = new PathGuard(ws.RootPath, PathGuardOptions.Default);

        var result = guard.Resolve("README.TXT");

        result.IsSuccess.Should().BeTrue(".txt は許可拡張子であり大文字小文字を無視して比較されるべき");
    }

    [Fact(DisplayName = "\\区切りのパスも受理される")]
    public void バックスラッシュ区切りのパスも受理される()
    {
        using var ws = new TempWorkspace();
        var guard = new PathGuard(ws.RootPath, PathGuardOptions.Default);

        var result = guard.Resolve(@"src\features\module.py");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(Path.Combine(ws.RootPath, "src", "features", "module.py"));
    }

    [Fact(DisplayName = "サイズ上限を超えるファイルはE203になる")]
    public void サイズ上限超過はE203になる()
    {
        using var ws = new TempWorkspace();
        var options = PathGuardOptions.Default with { MaxFileSizeMB = 1 };
        var guard = new PathGuard(ws.RootPath, options);

        var bigContent = new byte[2 * 1024 * 1024];
        ws.WriteBytes("data/large.txt", bigContent);

        var result = guard.Inspect("data/large.txt");

        result.IsSuccess.Should().BeFalse();
        result.Errors.Single().Code.Should().Be(ErrorCode.E203);
    }

    [Fact(DisplayName = "サイズ上限以内のファイルは成功する")]
    public void サイズ上限以内のファイルは成功する()
    {
        using var ws = new TempWorkspace();
        var options = PathGuardOptions.Default with { MaxFileSizeMB = 1 };
        var guard = new PathGuard(ws.RootPath, options);
        ws.WriteText("data/small.txt", "こんにちは");

        var result = guard.Inspect("data/small.txt");

        result.IsSuccess.Should().BeTrue();
        result.Value.Exists.Should().BeTrue();
    }

    [Fact(DisplayName = "読み取り専用属性のファイルは警告付きで検出される")]
    public void 読み取り専用属性は警告付きで検出される()
    {
        using var ws = new TempWorkspace();
        var guard = new PathGuard(ws.RootPath, PathGuardOptions.Default);
        ws.WriteText("data/locked.txt", "内容");
        ws.SetReadOnly("data/locked.txt", true);

        try
        {
            var result = guard.Inspect("data/locked.txt");

            result.IsSuccess.Should().BeTrue("読み取り専用は警告であり致命的失敗ではないため");
            result.Value.IsReadOnly.Should().BeTrue();
            var warning = result.Issues.Single(i => i.Code == ErrorCode.E205);
            warning.Severity.Should().Be(Severity.Warning);

            // 課題2-1回帰テスト: 「確認のうえ属性を解除できます」は誰が何をするか曖昧だった。
            // ApplyContext.AllowReadOnlyOverrideは常にfalse（Graftが自動で解除することはない）
            // ため、利用者自身が解除する必要があることを明示した文言になっているか確認する。
            warning.Remedy.Should().Contain("解除してから", "Graftが自動で解除するわけではなく、利用者が解除する必要があることを明示するため");
            warning.Remedy.Should().NotBe("確認のうえ属性を解除できます", "誰が何をするか分からない旧文言に戻っていないこと");
        }
        finally
        {
            ws.SetReadOnly("data/locked.txt", false);
        }
    }

    [Fact(DisplayName = "存在しないファイルはロックも読み取り専用も無しとして扱われる")]
    public void 存在しないファイルは追加検証で問題なしとなる()
    {
        using var ws = new TempWorkspace();
        var guard = new PathGuard(ws.RootPath, PathGuardOptions.Default);

        var result = guard.Inspect("data/not-created-yet.txt");

        result.IsSuccess.Should().BeTrue();
        result.Value.Exists.Should().BeFalse();
        result.Value.IsReadOnly.Should().BeFalse();
        result.Value.IsLocked.Should().BeFalse();
    }

    [Fact(DisplayName = "シンボリックリンク経由でルート外へ出るパスはE201になる")]
    public void シンボリックリンク経由でルート外はE201になる()
    {
        using var ws = new TempWorkspace();
        using var outside = new TempWorkspace();
        outside.WriteText("secret.txt", "ルート外の内容");

        if (TryCreateSymlinkOrSkip(ws, "linked", outside.RootPath) is null) return;
        var guard = new PathGuard(ws.RootPath, PathGuardOptions.Default);

        var result = guard.Resolve("linked/secret.txt");

        result.IsSuccess.Should().BeFalse();
        result.Errors.Single().Code.Should().Be(ErrorCode.E201);
    }

    [Fact(DisplayName = "シンボリックリンクがルート内を指す場合は許可される")]
    public void シンボリックリンクがルート内を指す場合は許可される()
    {
        using var ws = new TempWorkspace();
        var realDir = ws.CreateDirectory("real");
        if (TryCreateSymlinkOrSkip(ws, "linked", realDir) is null) return;
        var guard = new PathGuard(ws.RootPath, PathGuardOptions.Default);

        var result = guard.Resolve("linked/inside.txt");

        result.IsSuccess.Should().BeTrue();
    }

    /// <summary>
    /// シンボリックリンクを実際に作成してみて、権限不足で作成できない環境ではテストをスキップする
    /// （不具合3）。Windowsでのシンボリックリンク作成には管理者権限または開発者モードが必要で、
    /// 一般ユーザーの通常環境では<see cref="IOException"/>（ERROR_PRIVILEGE_NOT_HELD）になる。
    /// これはこの環境固有の制約であり、テスト対象（PathGuard）の不具合ではないため、
    /// 「常にWindowsならスキップ」ではなく実際に権限エラーになった場合のみスキップする
    /// （開発者モードが有効なWindows環境では通常どおり実行される）。Linux上では通常権限で
    /// 常に成功するため、このテストはLinux上では必ず実行される。
    /// </summary>
    private static string? TryCreateSymlinkOrSkip(TempWorkspace ws, string linkRelativePath, string targetAbsolutePath)
    {
        try
        {
            return ws.CreateDirectorySymlink(linkRelativePath, targetAbsolutePath);
        }
        catch (IOException ex)
        {
            Console.WriteLine(
                "シンボリックリンクを作成する権限が無いためこのテストをスキップします"
                + $"（{ex.Message}）。実行するには、Windowsで「開発者モード」を有効にするか、"
                + "テストを管理者として実行してください。");
            return null;
        }
    }

    [Fact(DisplayName = "1リビジョンあたりのファイル数上限を超えるとE203になる")]
    public void ファイル数上限超過はE203になる()
    {
        using var ws = new TempWorkspace();
        var options = PathGuardOptions.Default with { MaxFilesPerRevision = 2 };
        var guard = new PathGuard(ws.RootPath, options);

        var result = guard.CheckFileCount(3);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Single().Code.Should().Be(ErrorCode.E203);
    }

    [Fact(DisplayName = "1リビジョンあたりのファイル数が上限以内なら成功する")]
    public void ファイル数上限以内は成功する()
    {
        using var ws = new TempWorkspace();
        var options = PathGuardOptions.Default with { MaxFilesPerRevision = 2 };
        var guard = new PathGuard(ws.RootPath, options);

        var result = guard.CheckFileCount(2);

        result.IsSuccess.Should().BeTrue();
    }
}
