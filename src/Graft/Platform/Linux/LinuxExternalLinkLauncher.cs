using System.ComponentModel;
using System.Diagnostics;

namespace Graft.Platform.Linux;

/// <summary>
/// <see cref="IExternalLinkLauncher"/> のLinux実装。<c>xdg-open</c>はURLを渡すと、
/// フォルダを渡したとき（<see cref="LinuxFileManagerLauncher"/>）と同じ仕組みで、
/// 既定のブラウザ（<c>~/.config/mimeapps.list</c>で決まる）を起動する。
/// </summary>
public sealed class LinuxExternalLinkLauncher : IExternalLinkLauncher
{
    public bool IsSupported => true;

    public string? UnsupportedReason => null;

    public void Open(string url)
    {
        if (string.IsNullOrEmpty(url)) return;

        try
        {
            var info = new ProcessStartInfo("xdg-open")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            info.ArgumentList.Add(url);
            using var process = Process.Start(info);
            // ブラウザの起動自体は数秒かかることがあり、xdg-openはブラウザプロセスの終了
            // まで待たない実装が一般的なため、ここでは起動できたかどうかだけを確認し
            // WaitForExitはしない（LinuxFileManagerLauncher.TryStartとは異なり、ここでの
            // 失敗はブラウザが開かないだけで致命的ではないため、待ち時間を掛けない）。
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            // ブラウザの起動失敗は致命的ではないため無視する。
        }
    }
}
