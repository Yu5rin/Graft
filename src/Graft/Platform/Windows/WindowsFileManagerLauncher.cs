using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;

namespace Graft.Platform.Windows;

/// <summary>
/// <see cref="IFileManagerLauncher"/> のWindows実装。移設元は
/// <c>Features/FileTreeService.cs</c> の <c>RevealInFileExplorer</c>。ロジックは
/// 移設元から変更していない。
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
            Process.Start("explorer.exe", $"/select,\"{fullPath}\"");
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            // エクスプローラの起動失敗は致命的ではないため無視する。
        }
    }
}
