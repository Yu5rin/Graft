using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace Graft.Platform.Windows;

/// <summary>
/// <see cref="IExternalLinkLauncher"/> のWindows実装。<c>UseShellExecute = true</c>で
/// URLを直接起動すると、シェル（既定のブラウザ関連付け）が解決して開く。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsExternalLinkLauncher : IExternalLinkLauncher
{
    public bool IsSupported => true;

    public string? UnsupportedReason => null;

    public void Open(string url)
    {
        if (string.IsNullOrEmpty(url)) return;

        try
        {
            var info = new ProcessStartInfo(url) { UseShellExecute = true };
            using var process = Process.Start(info);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            // ブラウザの起動失敗は致命的ではないため無視する。
        }
    }
}
