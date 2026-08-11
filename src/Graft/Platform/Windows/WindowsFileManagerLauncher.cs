using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;

namespace Graft.Platform.Windows;

/// <summary>
/// <see cref="IFileManagerLauncher"/> のWindows実装。移設元は
/// <c>Features/FileTreeService.cs</c> の <c>RevealInFileExplorer</c>。
///
/// 不具合2対応: 対象がファイルのときは「親フォルダを開き、そのファイルを選択状態にする」
/// （<c>explorer.exe /select,&lt;path&gt;</c>）で正しいが、対象がフォルダのときに同じ
/// <c>/select</c> を使うと、Windowsのエクスプローラはフォルダ自身ではなく「その親フォルダを
/// 開いてフォルダを選択状態にする」動作になる。利用者が選んだフォルダの中身を見たいのに、
/// 一段上のフォルダが開いてしまう不具合の原因はここだった。フォルダのときは <c>/select</c> を
/// 使わず、そのフォルダ自体をパスだけ渡して開く。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsFileManagerLauncher : IFileManagerLauncher
{
    public bool IsSupported => true;

    public string? UnsupportedReason => null;

    public void Reveal(string fullPath)
    {
        try
        {
            var arguments = BuildExplorerArguments(fullPath, Directory.Exists(fullPath));
            Process.Start("explorer.exe", arguments);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            // エクスプローラの起動失敗は致命的ではないため無視する。
        }
    }

    /// <summary>
    /// explorer.exe へ渡す引数を組み立てる。実際のプロセス起動を伴わない純粋な関数として
    /// 分離してあり、不具合2の回帰テスト（フォルダのときに <c>/select</c> が付かないこと）を
    /// プロセスを起動せずに検証できる。
    /// </summary>
    public static string BuildExplorerArguments(string fullPath, bool isDirectory)
        => isDirectory ? $"\"{fullPath}\"" : $"/select,\"{fullPath}\"";
}
