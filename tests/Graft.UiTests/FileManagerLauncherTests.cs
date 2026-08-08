using System.Runtime.Versioning;
using FluentAssertions;
using Graft.Platform.Linux;
using Graft.Platform.Windows;
using Xunit;

namespace Graft.UiTests;

/// <summary>
/// 不具合2の回帰テスト: 「エクスプローラで表示」がフォルダを選んだときに、そのフォルダ自体
/// ではなく一段上の親フォルダを開いてしまう不具合。
///
/// 実際にファイルマネージャを起動して目視確認するのは実機（Windows）でしか完結しないため、
/// ここでは <see cref="WindowsFileManagerLauncher.BuildExplorerArguments"/>・
/// <see cref="LinuxFileManagerLauncher.BuildDbusCall"/>（いずれもプロセスを起動しない
/// 純粋な引数組み立て関数として切り出してある）を直接呼び、フォルダのときに
/// 「親フォルダの中で選択状態にする」系の指定（Windowsの /select、Linuxの ShowItems）が
/// 付かないことを検証する。
/// </summary>
public class FileManagerLauncherTests
{
    // BuildExplorerArgumentsは文字列組み立てのみでOS固有のAPIを一切呼ばないため、
    // 属性はコンパイラの互換性解析（CA1416）を通すための表明でしかなく、Linux上でも
    // そのまま実行できる（WindowsFileManagerLauncherクラス自体には
    // [SupportedOSPlatform("windows")] が付いており、その静的メンバーを呼ぶにはここにも
    // 同じ表明が要る。tests/Graft.Tests/AppPathsWritabilityTests.cs の[SupportedOSPlatform]
    // と同じ考え方）。
    [SupportedOSPlatform("windows")]
    [Fact(DisplayName = "Windows: ファイルは親フォルダを開いて選択状態にする(/select)")]
    public void Windowsはファイルなら親フォルダで選択状態にする()
    {
        var arguments = WindowsFileManagerLauncher.BuildExplorerArguments(@"C:\proj\file.txt", isDirectory: false);

        arguments.Should().Be("/select,\"C:\\proj\\file.txt\"");
    }

    [SupportedOSPlatform("windows")]
    [Fact(DisplayName = "Windows: フォルダは/selectを付けずそのフォルダ自体を開く")]
    public void Windowsはフォルダなら自分自身を開く()
    {
        var arguments = WindowsFileManagerLauncher.BuildExplorerArguments(@"C:\proj\subdir", isDirectory: true);

        arguments.Should().Be("\"C:\\proj\\subdir\"");
        arguments.Should().NotContain("/select", "フォルダのときは親フォルダで選択状態にする指定を付けてはならない");
    }

    [Fact(DisplayName = "Linux: ファイルはShowItemsで親フォルダの中を選択状態にする")]
    public void LinuxはファイルならShowItemsを使う()
    {
        var (method, uri) = LinuxFileManagerLauncher.BuildDbusCall("/home/user/proj/file.txt", isDirectory: false);

        method.Should().Be("org.freedesktop.FileManager1.ShowItems");
        uri.Should().Be(new Uri("/home/user/proj/file.txt").AbsoluteUri);
    }

    [Fact(DisplayName = "Linux: フォルダはShowFoldersでフォルダ自体を開く")]
    public void LinuxはフォルダならShowFoldersを使う()
    {
        var (method, uri) = LinuxFileManagerLauncher.BuildDbusCall("/home/user/proj/subdir", isDirectory: true);

        method.Should().Be("org.freedesktop.FileManager1.ShowFolders");
        method.Should().NotBe("org.freedesktop.FileManager1.ShowItems",
            "フォルダのときは親フォルダの中で選択状態にするShowItemsを使ってはならない");
        uri.Should().Be(new Uri("/home/user/proj/subdir").AbsoluteUri);
    }
}
