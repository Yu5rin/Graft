using System.ComponentModel;
using System.IO;

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
}
