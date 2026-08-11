using System.IO;
using FluentAssertions;
using Graft.UiTests.TestSupport;
using Graft.Views;
using Xunit;

namespace Graft.UiTests;

/// <summary>
/// プロジェクトペイン改善 要望7（フォルダのドラッグ＆ドロップ登録）の判定ロジックの単体テスト。
/// <see cref="ProjectPane.ResolveDropTarget"/>は、Avaloniaの実ドラッグイベント
/// （<c>DragEventArgs</c>・<c>IDataObject</c>）をテストから安定して合成するのが難しいため、
/// 「1件のローカルパスから登録すべきフォルダを決める」判定だけを取り出したpublic staticメソッド。
/// Avalonia.Headlessのウィンドウ表示を伴わない、通常の<c>[Fact]</c>として書ける。
/// </summary>
public class ProjectPaneDropTargetTests
{
    [Fact(DisplayName = "フォルダが落とされた場合はそのフォルダをそのまま登録先にする")]
    public void フォルダはそのまま登録先になる()
    {
        var dir = Path.Combine(Path.GetTempPath(), "graft-drop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            ProjectPane.ResolveDropTarget(dir).Should().Be(dir);
        }
        finally
        {
            TempDirectoryCleanup.TryDeleteRecursive(dir);
        }
    }

    [Fact(DisplayName = "ファイルが落とされた場合はその親フォルダを登録先にする")]
    public void ファイルは親フォルダが登録先になる()
    {
        var dir = Path.Combine(Path.GetTempPath(), "graft-drop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, "some-file.txt");
        File.WriteAllText(filePath, "内容");
        try
        {
            ProjectPane.ResolveDropTarget(filePath).Should().Be(dir);
        }
        finally
        {
            TempDirectoryCleanup.TryDeleteRecursive(dir);
        }
    }

    [Fact(DisplayName = "空またはnullのパスはnullを返す")]
    public void 空やnullはnullを返す()
    {
        ProjectPane.ResolveDropTarget(null).Should().BeNull();
        ProjectPane.ResolveDropTarget(string.Empty).Should().BeNull();
    }
}
