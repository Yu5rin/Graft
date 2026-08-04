using System.IO;

namespace Graft.Core;

/// <summary>
/// 長いパス（MAX_PATH超過）とネットワーク／クラウド同期フォルダの扱いをまとめる。
/// 6.7節に対応する。Windows以外（テスト実行環境含む）ではプレフィックスを付けず、
/// 素直な絶対パスをそのまま返す。
/// </summary>
public static class LongPath
{
    /// <summary>\\?\ プレフィックス込みで許容する最大長。安全側に倒した値。</summary>
    public const int MaxExtendedPathLength = 32000;

    /// <summary>
    /// MAX_PATH超過に備え、Windows上では \\?\ / \\?\UNC\ プレフィックスを付けた
    /// 絶対パスを返す。既にプレフィックス済み、または非Windowsの場合はそのまま返す。
    /// </summary>
    public static string Extended(string absolutePath)
    {
        if (string.IsNullOrEmpty(absolutePath)) return absolutePath;
        if (!OperatingSystem.IsWindows()) return absolutePath;
        if (absolutePath.StartsWith(@"\\?\", StringComparison.Ordinal)) return absolutePath;

        if (absolutePath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            // UNCパス: \\server\share\... -> \\?\UNC\server\share\...
            return @"\\?\UNC\" + absolutePath[2..];
        }

        return @"\\?\" + absolutePath;
    }

    /// <summary>プレフィックス込みでも長さ上限を超えるかどうかを判定する（E206判定用）。</summary>
    public static bool ExceedsExtendedLimit(string absolutePath)
    {
        if (string.IsNullOrEmpty(absolutePath)) return false;
        return Extended(absolutePath).Length > MaxExtendedPathLength;
    }

    /// <summary>
    /// パスがネットワークドライブ・UNC共有・主要なクラウド同期フォルダ配下かどうかを判定する。
    /// File.Replace が失敗しやすい環境の事前検知に用いる。
    /// </summary>
    public static bool IsNetworkOrCloudSyncFolder(string absolutePath)
    {
        if (string.IsNullOrEmpty(absolutePath)) return false;

        if (absolutePath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return true;
        }

        if (OperatingSystem.IsWindows() && IsNetworkDrive(absolutePath))
        {
            return true;
        }

        return ContainsCloudSyncMarker(absolutePath);
    }

    private static bool IsNetworkDrive(string absolutePath)
    {
        try
        {
            var root = Path.GetPathRoot(absolutePath);
            if (string.IsNullOrEmpty(root)) return false;
            var drive = new DriveInfo(root);
            return drive.DriveType == DriveType.Network;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            // ドライブ情報が取得できない場合はネットワークドライブと断定しない
            return false;
        }
    }

    private static bool ContainsCloudSyncMarker(string absolutePath)
    {
        var markers = new[] { "OneDrive", "Dropbox", "Google Drive", "GoogleDrive", "iCloudDrive", "Box" };
        foreach (var marker in markers)
        {
            if (absolutePath.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
