using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Core;
using Graft.Features;
using Graft.Platform;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 10件目の不具合修正: <see cref="FileTreeService.DeleteAsync"/> が <see cref="ITrashService"/>
/// 経由でごみ箱へ送るように配線されたことの検証。従来はWindows専用の<c>RecycleBin</c>を
/// <c>OperatingSystem.IsWindows()</c>判定で直呼びしており、Linux（<c>LinuxTrashService</c>）が
/// 一度も呼ばれず常に完全削除にフォールバックしていた。ここでは実OSのごみ箱APIには依存せず、
/// フェイクの<see cref="ITrashService"/>で「呼ばれたかどうか」「フォールバックの分岐」を検証する。
/// </summary>
public class FileTreeServiceTrashTests
{
    [Fact(DisplayName = "ITrashServiceが渡されていれば、ファイル削除はまずSendへ渡され、成功時は通常削除にフォールバックしない")]
    public async Task ファイル削除はITrashServiceへ渡される()
    {
        using var ws = new TempWorkspace();
        var root = ws.CreateDirectory("project");
        var filePath = ws.WriteText("project/sample.txt", "内容");
        var trash = new FakeTrashService(sendSucceeds: true);
        var service = new FileTreeService(trash);
        var project = new Project { Root = root };

        var result = await service.DeleteAsync(project, "sample.txt", isDirectory: false, PathGuardOptions.Default);

        result.IsSuccess.Should().BeTrue();
        trash.SentPaths.Should().ContainSingle().Which.Should().Be(filePath);
        // フェイクは実OSのごみ箱と違い実際にはファイルを動かさない。Send成功時に
        // フォールバック（File.Delete）を二重に呼んでいないことを、あえてファイルが
        // 「まだそこにある」ことで確認する（フォールバックが誤って走っていれば消えてしまう）。
        File.Exists(filePath).Should().BeTrue(
            "ITrashService.Sendが成功を返した場合、FileTreeService自身が二重に通常削除してはならない");
    }

    [Fact(DisplayName = "ITrashService.Sendが失敗を返した場合は通常削除にフォールバックする")]
    public async Task Send失敗時は通常削除にフォールバックする()
    {
        using var ws = new TempWorkspace();
        var root = ws.CreateDirectory("project");
        var filePath = ws.WriteText("project/sample.txt", "内容");
        var trash = new FakeTrashService(sendSucceeds: false);
        var service = new FileTreeService(trash);
        var project = new Project { Root = root };

        var result = await service.DeleteAsync(project, "sample.txt", isDirectory: false, PathGuardOptions.Default);

        result.IsSuccess.Should().BeTrue();
        trash.SentPaths.Should().ContainSingle();
        File.Exists(filePath).Should().BeFalse("Send失敗時はFile.Deleteへフォールバックして削除されている必要がある");
    }

    [Fact(DisplayName = "ITrashServiceを渡さない場合（テスト等）は従来どおり通常削除する")]
    public async Task Trash未指定なら通常削除する()
    {
        using var ws = new TempWorkspace();
        var root = ws.CreateDirectory("project");
        var filePath = ws.WriteText("project/sample.txt", "内容");
        var service = new FileTreeService(); // 引数省略＝null

        var result = await service.DeleteAsync(new Project { Root = root }, "sample.txt", isDirectory: false, PathGuardOptions.Default);

        result.IsSuccess.Should().BeTrue();
        File.Exists(filePath).Should().BeFalse();
    }

    [Fact(DisplayName = "フォルダの削除もITrashServiceへ渡される")]
    public async Task フォルダ削除もITrashServiceへ渡される()
    {
        using var ws = new TempWorkspace();
        var root = ws.CreateDirectory("project");
        var folderPath = ws.CreateDirectory("project/folder");
        ws.WriteText("project/folder/child.txt", "子");
        var trash = new FakeTrashService(sendSucceeds: true);
        var service = new FileTreeService(trash);

        var result = await service.DeleteAsync(new Project { Root = root }, "folder", isDirectory: true, PathGuardOptions.Default);

        result.IsSuccess.Should().BeTrue();
        trash.SentPaths.Should().ContainSingle().Which.Should().Be(folderPath);
        Directory.Exists(folderPath).Should().BeTrue("Send成功時は通常削除にフォールバックしてはならない（上と同じ理由）");
    }

    /// <summary>
    /// 実OSのごみ箱API・ファイルシステムには一切触れず、呼び出しの有無だけを記録するフェイク。
    /// <see cref="Send"/>が成功を返す場合、対象は実在するかどうかに関わらず「送った」ことにする
    /// （呼び出し側の分岐だけを検証したいため）。
    /// </summary>
    private sealed class FakeTrashService : ITrashService
    {
        private readonly bool _sendSucceeds;

        public FakeTrashService(bool sendSucceeds) => _sendSucceeds = sendSucceeds;

        public List<string> SentPaths { get; } = new();

        public bool IsSupported => true;

        public string? UnsupportedReason => null;

        public bool Send(string path)
        {
            SentPaths.Add(path);
            return _sendSucceeds;
        }
    }
}
