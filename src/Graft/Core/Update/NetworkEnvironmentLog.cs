using System.Net;
using System.Net.Http;

namespace Graft.Core.Update;

/// <summary>
/// 通信がどの経路を通るのかを表す短い説明を組み立てる（ログ用）。実際にログへ書き込むのは
/// 呼び出し元（<see cref="Graft.ViewModels.SettingsViewModel"/>。<c>Graft.Core.Update</c>は
/// <see cref="Graft.Infra.Logger"/>を知らないため、ここでは文字列を組み立てるだけの純粋な
/// 処理にとどめ、ログへの書き込み自体はViewModel側に任せる）。
///
/// 【なぜ要るか（実機不具合対応。要件3）】
/// 実機ログ（2026-09-11、社内の共有回線）では、更新確認がなぜ失敗したのかが「通信に
/// 失敗しました」としか残っておらず、プロキシを経由しているかどうかすら分からなかった。
/// 会社のネットワークでは、Windowsの設定やPACファイルによってプロキシ経由になることが
/// 多く、経由の有無・経由先が分かるだけで切り分けの幅がかなり狭まる（別リポジトリpaneの
/// <c>UpdateService.LogNetworkEnvironmentOnce</c>と同じ考え方で、Graft向けに
/// 「文字列を組み立てるだけ」の純粋な形へ切り出した）。
///
/// 出す情報は経由先ホスト・ポート・資格情報の有無にとどめる。宛先URLの全体や、まして
/// 利用者名などは出さない（既存のログ方針＝ファイルの中身やAIの応答は残さない、と同じ
/// 配慮。<see cref="Uri.Host"/>だけを見るのは、社内の共有回線であってもホスト名程度は
/// 診断に必要な最小限の情報だと判断したため）。
/// </summary>
public static class NetworkEnvironmentLog
{
    /// <summary>
    /// <paramref name="url"/>への通信がプロキシを経由するかどうかを、
    /// <see cref="HttpClient.DefaultProxy"/>（.NETがOSのプロキシ設定・PACファイルから
    /// 解決した既定のプロキシ）から調べて説明文にする。<see cref="GitHubReleaseFeed"/>が
    /// 使うHttpClientも既定のハンドラを使う限りこのプロキシ設定に従うため、実際の通信経路と
    /// 一致する。
    /// </summary>
    public static string Describe(string url)
    {
        try
        {
            var target = new Uri(url);
            IWebProxy proxy = HttpClient.DefaultProxy;
            var via = proxy.GetProxy(target);
            if (via is null) return $"プロキシを経由しない（宛先 {target.Host}）";

            return $"プロキシを経由する（{via.Host}:{via.Port}, 宛先 {target.Host}, " +
                   $"資格情報={(proxy.Credentials is null ? "なし" : "あり")}）";
        }
        catch (Exception ex) when (ex is UriFormatException or InvalidOperationException or NotSupportedException)
        {
            return $"通信経路を調べられませんでした（{ex.GetType().Name}）";
        }
    }
}
