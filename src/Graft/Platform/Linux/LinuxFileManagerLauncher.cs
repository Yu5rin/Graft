using System.ComponentModel;
using System.Diagnostics;

namespace Graft.Platform.Linux;

/// <summary>
/// <see cref="IFileManagerLauncher"/> のLinux実装（仕様書v2.1 19章 L4）。
/// ファイルを選択した状態で開く標準的な手段が無いため、DBusのFileManager1インターフェース
/// （Nautilus・Dolphin・Nemo等が実装している）を試し、使えない場合は <c>xdg-open</c> で
/// フォルダを開くところまでに縮退する。
///
/// 不具合2対応: FileManager1には用途の異なる2つのメソッドがある。<c>ShowItems</c>は
/// 「指定した項目を、その親フォルダの中で選択状態にする」動作のため、対象がフォルダの
/// ときに使うと（Windowsの <c>explorer.exe /select</c> と同じく）フォルダ自身ではなく
/// 一段上の親フォルダが開いてしまう。フォルダ自体を開きたいときは代わりに
/// <c>ShowFolders</c>（フォルダそのものを開く。親フォルダの中で選択状態にはしない）を使う。
/// xdg-openへの縮退経路は元々 <c>Directory.Exists</c> で分岐済みだったため対応不要だった。
/// </summary>
public sealed class LinuxFileManagerLauncher : IFileManagerLauncher
{
    public bool IsSupported => true;

    public string? UnsupportedReason => null;

    public void Reveal(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath)) return;

        var isDirectory = Directory.Exists(fullPath);
        if (TryShowViaDbus(fullPath, isDirectory)) return;

        var folder = isDirectory ? fullPath : Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(folder)) return;

        TryStart("xdg-open", folder);
    }

    /// <summary>
    /// org.freedesktop.FileManager1 でファイルマネージャに表示させる。ファイルは
    /// <c>ShowItems</c>（親フォルダの中で選択状態にする）、フォルダは <c>ShowFolders</c>
    /// （フォルダ自体を開く）を使い分ける。gdbus が無い・対応するファイルマネージャが
    /// 常駐していない場合は false を返す。
    /// </summary>
    private static bool TryShowViaDbus(string fullPath, bool isDirectory)
    {
        var (method, uri) = BuildDbusCall(fullPath, isDirectory);
        return TryStart(
            "gdbus", "call", "--session",
            "--dest", "org.freedesktop.FileManager1",
            "--object-path", "/org/freedesktop/FileManager1",
            "--method", method,
            $"['{uri}']", "");
    }

    /// <summary>
    /// gdbusへ渡すメソッド名とURIを組み立てる。プロセスを起動しない純粋な関数として分離し、
    /// 不具合2の回帰テスト（フォルダのときに ShowItems ではなく ShowFolders が選ばれること）を
    /// プロセスを起動せずに検証できるようにしてある。
    /// </summary>
    public static (string Method, string Uri) BuildDbusCall(string fullPath, bool isDirectory)
    {
        var uri = new Uri(fullPath).AbsoluteUri;
        var method = isDirectory
            ? "org.freedesktop.FileManager1.ShowFolders"
            : "org.freedesktop.FileManager1.ShowItems";
        return (method, uri);
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
