using System.ComponentModel;
using System.Diagnostics;

namespace Graft.Platform.Linux;

/// <summary>
/// <see cref="IFileManagerLauncher"/> のLinux実装（仕様書v2.1 19章 L4）。
/// ファイルを選択した状態で開く標準的な手段が無いため、DBusのFileManager1インターフェース
/// （<c>ShowItems</c>。Nautilus・Dolphin・Nemo等が実装している）を試し、
/// 使えない場合は <c>xdg-open</c> で親フォルダを開くところまでに縮退する。
/// </summary>
public sealed class LinuxFileManagerLauncher : IFileManagerLauncher
{
    public bool IsSupported => true;

    public string? UnsupportedReason => null;

    public void Reveal(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath)) return;

        if (TryShowItemViaDbus(fullPath)) return;

        var folder = Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(folder)) return;

        TryStart("xdg-open", folder);
    }

    /// <summary>
    /// org.freedesktop.FileManager1.ShowItems で「該当ファイルを選択した状態」で開く。
    /// gdbus が無い・対応するファイルマネージャが常駐していない場合は false を返す。
    /// </summary>
    private static bool TryShowItemViaDbus(string fullPath)
    {
        var uri = new Uri(fullPath).AbsoluteUri;
        return TryStart(
            "gdbus", "call", "--session",
            "--dest", "org.freedesktop.FileManager1",
            "--object-path", "/org/freedesktop/FileManager1",
            "--method", "org.freedesktop.FileManager1.ShowItems",
            $"['{uri}']", "");
    }

    /// <summary>プロセスを起動し、終了コード0で完了したかどうかを返す。</summary>
    private static bool TryStart(string fileName, params string[] arguments)
    {
        try
        {
            var info = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in arguments) info.ArgumentList.Add(argument);

            using var process = Process.Start(info);
            if (process is null) return false;

            // ファイルマネージャの起動は数秒で応答するのが普通。応答しない場合は
            // 呼び出し側を待たせないよう打ち切り、失敗として扱う。
            if (!process.WaitForExit(3000)) return false;
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }
}
