using System.IO;

namespace Graft.Core;

/// <summary>
/// 長いパス（MAX_PATH超過）とネットワーク／クラウド同期フォルダの扱いをまとめる。
/// 6.7節に対応する。Windows以外（テスト実行環境含む）ではプレフィックスを付けず、
/// 素直な絶対パスをそのまま返す。
/// </summary>
/// <remarks>
/// 実機不具合対応（ネットワークドライブ上ですべてのブロックがE101になる不具合）:
/// かつては Windows 上では長さに関わらず常に \\?\ プレフィックスを付けていた。
/// これがマップ済みネットワークドライブ（例: <c>net use Z: \\server\share</c> で
/// マウントした <c>Z:\...</c>）で <see cref="System.IO.File.Exists(string)"/> 等の
/// File/Directory 系APIを静かに失敗させていた。\\?\ プレフィックスはOSのパス解析を
/// 素通しさせる指定であり、その素通しの過程で「ドライブ文字とネットワーク共有の
/// 対応付け」を解決する処理まで一緒にスキップされてしまうため（UNCパスには
/// <c>\\?\UNC\server\share\...</c> という対応する拡張表記があるが、net use等で
/// マウントしたドライブ文字にはこれに相当する表記が存在しない）。
/// <para>
/// 対応方針として次の2案を検討した。
/// </para>
/// <list type="number">
/// <item>
/// <description>
/// 【不採用】拡張パスでAPIが失敗したら素のパスへ後退する経路を、各呼び出し箇所
/// （src/Graft全体で70箇所超。File.Exists・File.ReadAllBytes・Directory.CreateDirectory・
/// File.Move・File.Copy・File.Delete等）にそれぞれ追加する。
/// 不採用の理由: (a) 対象APIには戻り値で失敗を表すもの（File.Exists）と例外を
/// 投げるもの（File.ReadAllBytesAsync等）が混在し、後退条件の書き方が呼び出し箇所ごとに
/// 変わってしまい変更量・レビュー量とも大きい。(b) 「拡張パスが失敗した」ことの検知自体が
/// 曖昧（File.Existsは例外を投げず単にfalseを返すため、「本当に無い」のか「拡張パスが
/// 使えないだけ」なのか呼び出し側では区別できない）。(c) 何より、全呼び出し箇所が
/// <see cref="Extended(string)"/> 一箇所を経由する既存設計を活かせば、ここ一箇所を
/// 直すだけで70箇所超すべてに効く（下記2.採用案）。
/// </description>
/// </item>
/// <item>
/// <description>
/// 【採用】\\?\ プレフィックスは「付けないとWin32 APIが失敗しうる」場合
/// （パス長がMAX_PATHを超える場合）にのみ付ける。かつ、その中でもマップ済み
/// ネットワークドライブは、\\?\を付けてもどのみち解決できない（UNC相当の表記が
/// 無いため）ので素のパスのまま渡す。これにより実機で報告された「ネットワーク
/// ドライブ上の通常長パスがことごとく失敗する」不具合は根本から解消する。
/// なお「ネットワークドライブ上の260文字超の長いパス」という組み合わせは、
/// \\?\を付けても付けなくてもOS側の制約でどのみち厳しい（前者はドライブ文字を
/// 解決できず、後者はMAX_PATH制限に掛かりうる）。これはGraftの実装だけでは
/// 解消できないWindows自体の既知の制約であり、素のパスへ倒すことで「静かに
/// 存在しないと誤判定される」という今回の不具合と同じ壊れ方だけは避け、実際の
/// 制約に応じた分かりやすい失敗（例外）に委ねる。長いパス対応そのもの
/// （ローカルドライブ・UNC共有）は本対応で壊していない。<see cref="ExceedsExtendedLimit"/>
/// によるE206判定・単体テスト（LongPathTests）で担保する。
/// </description>
/// </item>
/// </list>
/// </remarks>
public static class LongPath
{
    /// <summary>\\?\ プレフィックス込みで許容する最大長。安全側に倒した値。</summary>
    public const int MaxExtendedPathLength = 32000;

    /// <summary>
    /// \\?\ プレフィックスが実際に必要になる境界値。Windows の MAX_PATH（260文字、
    /// null終端込み）。これ未満のパスは通常のWin32 APIでそのまま扱えるため拡張しない。
    /// </summary>
    public const int WindowsMaxPathLength = 260;

    /// <summary>
    /// MAX_PATH超過に備え、Windows上では \\?\ / \\?\UNC\ プレフィックスを付けた
    /// 絶対パスを返す。既にプレフィックス済み、非Windows、パス長がMAX_PATH未満、
    /// またはマップ済みネットワークドライブの場合はそのまま返す（詳細は型コメント参照）。
    /// </summary>
    public static string Extended(string absolutePath)
        => ExtendedCore(absolutePath, OperatingSystem.IsWindows(), IsNetworkDrive);

    /// <summary>
    /// <see cref="Extended(string)"/> の判定ロジック本体。OS判定・ネットワークドライブ判定を
    /// 引数として差し替え可能にしている。本体のテスト（tests/Graft.Tests）はLinux上でも動くため、
    /// <see cref="OperatingSystem.IsWindows"/> は常にfalseになり、そのままではWindows時の
    /// 分岐（プレフィックス付与そのもの）を一切検証できない。isWindowsとisNetworkDriveを
    /// 引数化することで、Linux上でも「Windowsだったらどう判定するか」という純粋ロジックを
    /// 単体テストで網羅できるようにする。
    /// </summary>
    internal static string ExtendedCore(string absolutePath, bool isWindows, Func<string, bool> isNetworkDrive)
    {
        if (string.IsNullOrEmpty(absolutePath)) return absolutePath;
        if (!isWindows) return absolutePath;
        if (absolutePath.StartsWith(@"\\?\", StringComparison.Ordinal)) return absolutePath;

        // MAX_PATH未満なら素のパスのままで安全に扱える。プレフィックスを付ける理由が無い。
        if (absolutePath.Length < WindowsMaxPathLength) return absolutePath;

        if (absolutePath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            // UNCパス: \\server\share\... -> \\?\UNC\server\share\...
            // （UNCには対応する拡張表記があるため、長い場合はここを使うのが正しい）
            return @"\\?\UNC\" + absolutePath[2..];
        }

        if (isNetworkDrive(absolutePath))
        {
            // マップ済みネットワークドライブ文字（例: Z:\...）に対応する \\?\ 拡張表記は
            // 存在しないため、付けても解決できずどのみち失敗する。素のパスのまま渡す。
            return absolutePath;
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
