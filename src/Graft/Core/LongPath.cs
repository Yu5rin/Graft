using System.IO;

namespace Graft.Core;

/// <summary>
/// 長いパス（MAX_PATH超過）とネットワーク／クラウド同期フォルダの扱いをまとめる。
/// 6.7節に対応する。Windows以外（テスト実行環境含む）ではプレフィックスを付けず、
/// 素直な絶対パスをそのまま返す。
/// </summary>
/// <remarks>
/// 予防的な堅牢化（実機不具合対応の一環として着手したが、因果関係は未確認）:
/// 当初は、マップ済みネットワークドライブ（例: <c>net use Z: \\server\share</c> で
/// マウントした <c>Z:\...</c>）で「すべてのブロックがE101になる」実機不具合の原因を、
/// 「Windows上で長さに関わらず常に付けていた \\?\ プレフィックスが、マップ済み
/// ドライブ文字の解決を妨げ File.Exists 等を静かに失敗させている」と推定していた。
/// しかし <c>Editor/DocumentSession.cs</c> の <c>OpenAsync</c>（158行目付近）は本メソッド
/// 適用後の同じ <c>File.Exists(LongPath.Extended(fullPath))</c> という形を使っている
/// にもかかわらず、実機では同じファイルをエディタで問題なく開けたという報告があり、
/// この推定と矛盾する（矛盾する以上、\\?\ プレフィックスが単独の原因だったとは
/// 断定できない）。そのため、**本対応を「今回の不具合の修正」とは位置づけない。**
/// 実際の原因は、DryRunPlanner側の診断ログ（<see cref="DryRunFileProbe"/>。
/// MainViewModelがログへ書き出す）を実機で採取して初めて特定できる状態にある。
/// <para>
/// それでもこの変更自体は残す。理由は、\\?\ プレフィックスがマップ済みネットワーク
/// ドライブの解決を妨げうることはMicrosoftのドキュメントで知られた既知の不確実性であり
/// （UNCパスには <c>\\?\UNC\server\share\...</c> という対応する拡張表記があるが、
/// マップ済みドライブ文字にはこれに相当する表記が存在しない）、\\?\ プレフィックスは
/// 本来「付けないとMAX_PATHを超えるパスをAPIが受け付けない」場合にのみ必要なもの
/// だからである。「本当に必要な場合にのみ付ける」という変更自体は、今回の不具合の
/// 有無に関わらずネットワークドライブ上での既知の不確実性に対する妥当な予防策であり、
/// 既存の長いパス対応（ローカル・UNC共有）を壊さないことは単体テスト（LongPathTests）で
/// 担保している。ただし**これによって実機のE101不具合が解消する保証はない**（因果関係は
/// 未確認のまま）。
/// </para>
/// <para>
/// 検討した別案（拡張パスでAPIが失敗したら素のパスへ後退する経路を、各呼び出し箇所
/// （src/Graft全体で70箇所超。File.Exists・File.ReadAllBytes・Directory.CreateDirectory・
/// File.Move・File.Copy・File.Delete等）へ個別に追加する案）は採らなかった。理由:
/// (a) 対象APIには戻り値で失敗を表すもの（File.Exists）と例外を投げるもの
/// （File.ReadAllBytesAsync等）が混在し、後退条件の書き方が呼び出し箇所ごとに
/// 変わってしまい変更量・レビュー量とも大きい。(b) 「拡張パスが失敗した」ことの検知自体が
/// 曖昧（File.Existsは例外を投げず単にfalseを返すため、「本当に無い」のか「拡張パスが
/// 使えないだけ」なのか呼び出し側では区別できない）。(c) 何より、全呼び出し箇所が
/// <see cref="Extended(string)"/> 一箇所を経由する既存設計を活かせば、ここ一箇所の
/// 変更だけで70箇所超すべてに同じ予防策が及ぶ。
/// </para>
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
