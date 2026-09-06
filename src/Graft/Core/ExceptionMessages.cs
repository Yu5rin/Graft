using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;

namespace Graft.Core;

/// <summary>
/// .NET例外の <see cref="Exception.Message"/> を利用者向けの日本語メッセージへ変換する。
/// 「UI文言はすべて日本語」という方針に対し、実機検証で見つかった不具合3
/// （存在しないフォルダをrootに持つプロジェクトで、ファイル監視の開始失敗ダイアログに
/// .NETの英語例外メッセージがそのまま出ていた）への対応。
/// <para>
/// よくある原因（フォルダ・ファイルが無い／アクセス拒否／他アプリが使用中／実行ファイルが
/// 見つからない）だけを判定して日本語の一言に置き換える。それ以外の想定外の例外までは
/// 無理に翻訳せず、「次に何をすればよいか」が分かる一般的な文言を返す。
/// </para>
/// <para>
/// いずれの場合も元の英語メッセージは「（詳細: ...）」として結果に残す。この層の呼び出し元の
/// 多くは静的なI/Oヘルパー（<c>Core</c>/<c>Features</c>配下）で、原因調査用のロガーへの参照を
/// 持たない。ロガーを引き回すにはコンストラクタ注入等の設計変更が必要になり本修正の範囲を
/// 超えるため、原文は握り潰さずメッセージ自体に残す方式を採った（ログへの転記が必要な場合は
/// 上位のUIハンドラ側で改めて記録できる）。
/// </para>
/// </summary>
public static class ExceptionMessages
{
    /// <summary>
    /// 例外を「日本語の理由＋（詳細: 原文）」の1文へ変換する。
    /// </summary>
    public static string Describe(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var reason = ex switch
        {
            DirectoryNotFoundException => "フォルダが見つかりません。移動または削除された可能性があります。",
            FileNotFoundException => "ファイルが見つかりません。移動または削除された可能性があります。",
            // FileSystemWatcher等、一部のAPIは存在しないフォルダに対してArgumentExceptionを
            // 投げる（DirectoryNotFoundExceptionではない）。メッセージ文言で判定する。
            ArgumentException when LooksLikeMissingPath(ex)
                => "フォルダが見つかりません。移動または削除された可能性があります。",
            UnauthorizedAccessException => "アクセスが拒否されました。権限を確認してください。",
            // フック実行（Process.Start）で実行ファイルが見つからない・起動できない場合。
            Win32Exception => "コマンドを実行できませんでした。実行ファイルが見つからないか、PATHが通っていない可能性があります。",
            IOException when IsSharingViolation(ex)
                => "他のアプリがファイルを使用中の可能性があります。閉じてから再試行してください。",
            // 異常系点検「低」5件目の対応: 更新ダウンロード（HttpUpdateDownloader）で
            // 「The proxy tunnel request to proxy '...' failed...」のような、プロキシ到達不能・
            // DNS名前解決不能を示す英語の生の例外メッセージがダイアログにそのまま出ていた。
            // SocketExceptionはHttpRequestException.InnerExceptionとして包まれて届くことが
            // 多い（名前解決不能はSocketError.HostNotFound、接続拒否はConnectionRefused等）ため、
            // まずInnerExceptionを見て「名前解決」特有の理由を判定し、それ以外は
            // HttpRequestException共通の一般的な理由（プロキシ・DNS・接続そのもの）を返す。
            HttpRequestException when IsNameResolutionFailure(ex)
                => "サーバーの名前を解決できませんでした。インターネット接続やDNS設定を確認してください。",
            HttpRequestException
                => "サーバーへの接続に失敗しました。プロキシ設定やインターネット接続を確認してください。",
            _ => "予期しないエラーが発生しました。解決しない場合は時間をおいて再試行するか、ログを確認してください。",
        };

        return $"{reason}（詳細: {ex.Message}）";
    }

    private static bool LooksLikeMissingPath(Exception ex)
        => ex.Message.Contains("does not exist", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 共有違反（他プロセスが使用中のファイル）の簡易判定。Windowsは HRESULT 0x80070020
    /// （ERROR_SHARING_VIOLATION）で判定できるが、.NETのIOExceptionはOS間で共通の型しか
    /// 持たないため、それ以外の環境向けにメッセージ文言でも補足判定する。
    /// </summary>
    private static bool IsSharingViolation(Exception ex)
        => ex.HResult == unchecked((int)0x80070020)
           || ex.Message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// DNS名前解決に失敗したことを示す<see cref="SocketException"/>（<see cref="SocketError.HostNotFound"/>）が
    /// 直接の内部例外として包まれているかどうかを判定する。<see cref="HttpRequestException"/>は
    /// プロキシ経由・直接接続いずれの失敗もこの型で表すため、InnerExceptionまで見ないと
    /// 「名前解決に失敗した」のか「（名前は解決できたが）接続やプロキシで失敗した」のかを
    /// 区別できない。判定できない場合（InnerExceptionが無い・別の型）は名前解決以外の
    /// 一般的な接続失敗として扱う（呼び出し元のswitch式のフォールスルー）。
    /// </summary>
    private static bool IsNameResolutionFailure(Exception ex)
        => ex.InnerException is SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.TryAgain };
}
