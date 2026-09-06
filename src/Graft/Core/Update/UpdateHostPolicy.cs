namespace Graft.Core.Update;

/// <summary>
/// セキュリティ点検の指摘事項（「更新のダウンロード元ホストが未検証／SHA256が悪意ある
/// checkUrlに無力」）への対応。
///
/// 【何が問題だったか】 期待ハッシュ（<see cref="GitHubReleaseAsset.Digest"/>）と
/// ダウンロードURL（<see cref="GitHubReleaseAsset.BrowserDownloadUrl"/>）は、どちらも
/// <c>update.checkUrl</c>への1回のHTTP応答（同じJSON）から取り出している。つまり
/// <c>checkUrl</c>自体を書き換えられる攻撃者（例: projects.jsonの拡張子ホワイトリスト経由で
/// settings.jsonを差し替えられた場合）は、ダウンロードURルと期待ハッシュの両方を自分の
/// 都合の良い値に決められるため、SHA256の一致は「通信で壊れていないこと」しか保証せず、
/// 「配布元そのものが正規かどうか」は一切保証しない。
///
/// 【対策】 ダウンロードURLのホストを、<c>checkUrl</c>とは独立の基準で検証する。
/// ・<c>checkUrl</c>が既定値（<see cref="DefaultCheckUrl"/>）のままなら、実際のGitHub
///   Releases配布で使われることを確認済みのホスト集合（<see cref="DefaultAllowedDownloadHosts"/>）
///   でのみダウンロードを許可する。
/// ・<c>checkUrl</c>を設定画面で変更している場合は、ダウンロードURLのホストが
///   <c>checkUrl</c>自身のホストと完全に一致することを要求する（GitHub以外のミラー等を
///   利用者が意図して設定したケースを塞がないため）。
/// </summary>
public static class UpdateHostPolicy
{
    /// <summary>
    /// <see cref="Graft.Infra.Settings"/>の<c>Update.CheckUrl</c>と同じ既定値。
    /// 単一の情報源にするため、Settings.cs側の既定値もこの定数を参照する。
    /// </summary>
    public const string DefaultCheckUrl = "https://api.github.com/repos/Yu5rin/Graft/releases/latest";

    /// <summary>
    /// 既定のcheckUrl使用時に許可するダウンロードURLのホスト。
    ///
    /// 【実測の根拠（2026-09-06、本リポジトリの実際のv1.0.14リリースに対して確認）】
    /// ・GitHub Releases API（<c>GET /repos/Yu5rin/Graft/releases/latest</c>、
    ///   すなわち<see cref="DefaultCheckUrl"/>そのもの）が返す実際の
    ///   <c>browser_download_url</c>は
    ///   <c>https://github.com/Yu5rin/Graft/releases/download/v1.0.14/Graft-1.0.14-win-x64.zip</c>
    ///   であり、ホストは常に <c>github.com</c>（<c>api.github.com</c>ではない）だった。
    /// ・このURLへ実際に<c>curl -L</c>したところ、302で
    ///   <c>release-assets.githubusercontent.com</c>（署名付きURL）へ1回だけリダイレクトされ、
    ///   そこがZIP本体を200で返した。<see cref="HttpUpdateDownloader"/>が使う
    ///   <see cref="System.Net.Http.HttpClient"/>は既定でリダイレクトを自動的に辿るため、
    ///   このホストはコードが明示的にチェックする対象ではなく、
    ///   <c>github.com</c>自体が改ざんされない限り到達できない先として参考記録するに留める
    ///   （<see cref="IsAllowedDownloadUrl"/>が見るのは最初のダウンロードURLのホストのみ）。
    ///
    /// 【推測ではない】 上記はいずれもこの作業中に実際のGitHub API・実際のダウンロードURLへ
    /// 到達して確認した結果であり、ドキュメントからの推測ではない。将来GitHubが配布経路の
    /// ホストを変更した場合は、更新確認自体が「ダウンロード元ホストが信頼できない」として
    /// 拒否される形で気づけるようになっている（安全側に倒れる）。
    /// </summary>
    private static readonly string[] DefaultAllowedDownloadHosts = { "github.com" };

    /// <summary>
    /// <paramref name="checkUrl"/>が既定値かどうか。前後の空白・大文字小文字の違いは
    /// 設定画面からの入力揺れとして許容するが、それ以外は完全一致を要求する
    /// （既定かどうかで許可ホストの決め方そのものを変えるため、あいまいな判定にしない）。
    /// </summary>
    public static bool IsDefaultCheckUrl(string? checkUrl)
        => string.Equals(checkUrl?.Trim(), DefaultCheckUrl, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// ダウンロードURLのホストが、<paramref name="checkUrl"/>の設定内容から見て信頼できるかを判定する。
    /// どちらのURLも絶対URIとして解釈できない場合は安全側（false）に倒す。
    /// </summary>
    public static bool IsAllowedDownloadUrl(string checkUrl, string downloadUrl)
    {
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var download)
            || download.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        if (IsDefaultCheckUrl(checkUrl))
        {
            return DefaultAllowedDownloadHosts.Contains(download.Host, StringComparer.OrdinalIgnoreCase);
        }

        // 既定から変更されたcheckUrl: そのcheckUrl自身のホストとの完全一致のみ許可する。
        // checkUrl自体が不正なURLの場合は判定しようがないため拒否する。
        if (!Uri.TryCreate(checkUrl, UriKind.Absolute, out var check))
        {
            return false;
        }

        return string.Equals(check.Host, download.Host, StringComparison.OrdinalIgnoreCase);
    }
}
