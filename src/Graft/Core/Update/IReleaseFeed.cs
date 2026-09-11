namespace Graft.Core.Update;

/// <summary>
/// Atomフィードから読み取れた「いちばん新しいリリースのタグ」。GitHub Releases APIの
/// 回数上限（未認証で1時間60回、IPアドレス単位）とは別枠のAtomフィードだけで済ませられる
/// 情報（タグ名とリリースページのURL）に限っている。配布物のURLやSHA256はAtomには
/// 載っていないため、新しい版だと分かったときだけ<see cref="IReleaseFeed.GetLatestReleaseAsync"/>
/// （API）へ問い合わせる（<see cref="UpdateChecker"/>参照）。
/// </summary>
/// <param name="TagName">Atomフィードから読み取れたタグ名（例: "v1.0.8"）。</param>
/// <param name="ReleasePageUrl">そのタグのリリースページのURL。API側の応答が得られない場合の案内に使う。</param>
public sealed record AtomFeedTag(string TagName, string ReleasePageUrl);

/// <summary>
/// GitHub Releases APIから最新リリース情報を取得する手段の抽象。テストでは実際に通信しない
/// フェイク実装に差し替える（実際にGitHubへ通信するテストは書かない方針）。
///
/// 【Atomフィードを別メソッドに分けた理由（実機不具合対応）】
/// 実機ログ（2026-09-11、社内の共有回線）で、起動時・手動いずれの更新確認もGitHub APIの
/// 403（回数上限。未認証で1時間60回、IPアドレス単位。共有回線では他の通信で先に
/// 使い切られる）で失敗し続けていた。別リポジトリpaneの<c>UpdateService.CheckAsync</c>が
/// 同じ環境で実際に踏んで解決した知見（コメント参照）に倣い、普段の確認は回数上限の
/// 外にあるAtomフィード（<see cref="TryGetLatestTagFromAtomAsync"/>）へ寄せ、新しい版が
/// 見つかったときだけ配布物の詳細（ダウンロードURL・SHA256）をAPIへ取りに行く形にする。
/// これにより、更新が無い（大多数の）確認ではAPIの回数上限を一切消費しなくなる
/// （<see cref="UpdateChecker.CheckNowAsync"/>のオーケストレーション参照）。
/// </summary>
public interface IReleaseFeed
{
    /// <summary>
    /// Atomフィードから、いちばん新しいリリースのタグを読む。次のいずれの場合もnullを返し、
    /// 例外は投げない（呼び出し元の<see cref="UpdateChecker"/>は、Atomが使えなくても
    /// <see cref="GetLatestReleaseAsync"/>だけで確認を続ける。Atomはあくまで節約のための
    /// 近道であり、使えないこと自体を致命的に扱わない）。
    /// <list type="bullet">
    /// <item><paramref name="checkUrl"/>からAtomのURLを組み立てられない
    /// （<c>api.github.com</c>以外・GitHub Releases APIの形と異なる等。設定画面で
    /// 確認先を変更している場合を含む）</item>
    /// <item>通信・解析いずれかに失敗した</item>
    /// <item>フィード中にバージョンとして解釈できるタグが1つも無かった</item>
    /// </list>
    /// </summary>
    /// <param name="checkUrl">確認先URL（GitHub Releases APIの<c>releases/latest</c>形式）。</param>
    Task<AtomFeedTag?> TryGetLatestTagFromAtomAsync(string checkUrl, CancellationToken ct);

    /// <summary>
    /// GitHub Releases APIから最新リリースの詳細（配布物のURL・SHA256を含む）を取得する。
    /// 通信・解析いずれの失敗も例外を投げず、<see cref="ReleaseFetchResult.Fail"/>で理由付きの
    /// 失敗として返す（要件: 通信の失敗は握りつぶし、起動を妨げない。ただし利用者へは
    /// 「何が起きたか」を伝えられるよう、失敗の理由（<see cref="ReleaseFetchFailureReason"/>）・
    /// HTTP状態コード・例外の型名は<see cref="ReleaseFetchResult"/>に残す）。
    /// </summary>
    /// <param name="checkUrl">確認先URL（<see cref="Infra.UpdateSettings.CheckUrl"/>）。HTTPS以外は拒否する。</param>
    /// <param name="userAgent">GitHub APIが必須とするUser-Agentヘッダ（例: "Graft/1.0.7"）。</param>
    Task<ReleaseFetchResult> GetLatestReleaseAsync(string checkUrl, string userAgent, CancellationToken ct);
}
