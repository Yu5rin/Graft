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

        ws.CreateDirectorySymlink("linked", outside.RootPath);
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
        ws.CreateDirectorySymlink("linked", realDir);
        var guard = new PathGuard(ws.RootPath, PathGuardOptions.Default);

        var result = guard.Resolve("linked/inside.txt");

        result.IsSuccess.Should().BeTrue();
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
