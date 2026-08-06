using FluentAssertions;
using Graft.Platform.Linux;
using Xunit;

namespace Graft.UiTests;

/// <summary>
/// Linux向けOS機能実装のうち、画面もデスクトップ環境も使わずに検証できるものを押さえる
/// （仕様書v2.1 19章 L4）。ごみ箱はfreedesktop.orgのTrash specificationに従うため、
/// 出力されるファイル配置と .trashinfo の内容を実際に確認する。
/// </summary>
public class LinuxPlatformTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-linux-tests", Guid.NewGuid().ToString("N"));
    private readonly string? _originalDataHome;

    public LinuxPlatformTests()
    {
        Directory.CreateDirectory(_root);

        // ごみ箱の位置は XDG_DATA_HOME に従うため、テスト中だけ一時ディレクトリへ向ける。
        _originalDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(_root, "data"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", _originalDataHome);
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 後始末の失敗は検証結果に影響しない。
        }

        GC.SuppressFinalize(this);
    }

    [Fact(DisplayName = "ごみ箱へ送るとfilesへ実体が移り、infoへ.trashinfoが作られる")]
    public void ごみ箱へ送ると実体と情報ファイルができる()
    {
        var target = Path.Combine(_root, "sample.txt");
        File.WriteAllText(target, "内容");

        new LinuxTrashService().Send(target).Should().BeTrue();

        File.Exists(target).Should().BeFalse("実体はごみ箱へ移動している必要がある");

        var trash = Path.Combine(_root, "data", "Trash");
        File.Exists(Path.Combine(trash, "files", "sample.txt")).Should().BeTrue();

        var info = File.ReadAllText(Path.Combine(trash, "info", "sample.txt.trashinfo"));
        info.Should().StartWith("[Trash Info]");
        info.Should().Contain("Path=");
        info.Should().Contain("DeletionDate=");
    }

    [Fact(DisplayName = "同名のファイルを続けて送っても互いに上書きしない")]
    public void 同名でも上書きしない()
    {
        var service = new LinuxTrashService();

        for (var i = 0; i < 2; i++)
        {
            var target = Path.Combine(_root, "dup.txt");
            File.WriteAllText(target, $"内容{i}");
            service.Send(target).Should().BeTrue();
        }

        var files = Path.Combine(_root, "data", "Trash", "files");
        Directory.GetFiles(files).Should().HaveCount(2, "衝突時は別名で保存される必要がある");
    }

    [Fact(DisplayName = "フォルダもごみ箱へ送れる")]
    public void フォルダも送れる()
    {
        var folder = Path.Combine(_root, "folder");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "child.txt"), "子");

        new LinuxTrashService().Send(folder).Should().BeTrue();

        Directory.Exists(folder).Should().BeFalse();
        var moved = Path.Combine(_root, "data", "Trash", "files", "folder");
        File.Exists(Path.Combine(moved, "child.txt")).Should().BeTrue("中身ごと移動している必要がある");
    }

    [Fact(DisplayName = "存在しないパスを送っても例外にせず失敗を返す")]
    public void 存在しないパスは失敗を返す()
        => new LinuxTrashService().Send(Path.Combine(_root, "missing.txt")).Should().BeFalse();

    [Fact(DisplayName = "パスに空白や日本語があってもパーセントエンコードして記録する")]
    public void パスをパーセントエンコードする()
    {
        var target = Path.Combine(_root, "テスト ファイル.txt");
        File.WriteAllText(target, "内容");

        new LinuxTrashService().Send(target).Should().BeTrue();

        var infoDirectory = Path.Combine(_root, "data", "Trash", "info");
        var info = File.ReadAllText(Directory.GetFiles(infoDirectory).Single());
        info.Should().Contain("%20", "空白はパーセントエンコードされる必要がある");
        info.Should().NotContain("テスト", "非ASCII文字はそのまま書き出してはならない");
    }
}
