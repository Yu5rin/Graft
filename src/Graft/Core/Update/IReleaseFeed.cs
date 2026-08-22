namespace Graft.Core.Update;

/// <summary>
/// GitHub Releases APIから最新リリース情報を取得する手段の抽象。テストでは実際に通信しない
/// フェイク実装に差し替える（実際にGitHubへ通信するテストは書かない方針）。
/// </summary>
public interface IReleaseFeed
{
    /// <summary>
    /// 最新リリース情報を取得する。通信の失敗・想定外の応答・JSON解析の失敗など、
    /// いずれの場合も例外を投げずnullを返す（呼び出し側の<see cref="UpdateChecker"/>が
    /// 「確認できなかった」として扱う。要件: 通信の失敗は握りつぶし、起動を妨げない）。
    /// </summary>
    /// <param name="checkUrl">確認先URL（<see cref="Infra.UpdateSettings.CheckUrl"/>）。HTTPS以外は拒否する。</param>
    /// <param name="userAgent">GitHub APIが必須とするUser-Agentヘッダ（例: "Graft/1.0.7"）。</param>
    Task<GitHubReleaseInfo?> GetLatestReleaseAsync(string checkUrl, string userAgent, CancellationToken ct);
}
