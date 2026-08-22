namespace Graft.Core.Update;

/// <summary>更新確認結果の種別。</summary>
public enum UpdateCheckStatus
{
    /// <summary>前回確認から24時間未満のため、今回は通信しなかった（起動時チェックのみ）。</summary>
    NotDue,

    /// <summary>確認でき、新しいバージョンが見つかった。</summary>
    UpdateAvailable,

    /// <summary>確認でき、現在のバージョンが最新だった。</summary>
    UpToDate,

    /// <summary>通信・解析いずれかに失敗し、確認できなかった。</summary>
    Failed,
}

/// <summary>更新確認結果。</summary>
public sealed record UpdateCheckResult
{
    public required UpdateCheckStatus Status { get; init; }
    public GitHubReleaseInfo? Release { get; init; }
    public string? ErrorMessage { get; init; }

    public static UpdateCheckResult NotDue() => new() { Status = UpdateCheckStatus.NotDue };

    public static UpdateCheckResult Available(GitHubReleaseInfo release)
        => new() { Status = UpdateCheckStatus.UpdateAvailable, Release = release };

    public static UpdateCheckResult UpToDate(GitHubReleaseInfo release)
        => new() { Status = UpdateCheckStatus.UpToDate, Release = release };

    public static UpdateCheckResult Failed(string message) => new() { Status = UpdateCheckStatus.Failed, ErrorMessage = message };
}

/// <summary>
/// 更新確認のオーケストレーション（1日1回の絞り込み・GitHub Releases APIとの通信・
/// バージョンの数値比較）を担う。通信の失敗はここで吸収し、例外を外へ投げない
/// （要件: 通信の失敗は握りつぶして「確認できなかった」で済ませる。起動を妨げない）。
/// </summary>
public sealed class UpdateChecker
{
    /// <summary>起動時チェックの絞り込み間隔（1日1回）。</summary>
    public static readonly TimeSpan MinimumCheckInterval = TimeSpan.FromHours(24);

    private readonly IReleaseFeed _feed;
    private readonly UpdateCheckStateStore _stateStore;
    private readonly Func<DateTimeOffset> _now;

    /// <param name="now">テスト用の時刻差し替え口。省略時は<see cref="DateTimeOffset.Now"/>。</param>
    public UpdateChecker(IReleaseFeed feed, UpdateCheckStateStore stateStore, Func<DateTimeOffset>? now = null)
    {
        _feed = feed ?? throw new ArgumentNullException(nameof(feed));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _now = now ?? (() => DateTimeOffset.Now);
    }

    /// <summary>
    /// 起動時チェック。前回確認から<see cref="MinimumCheckInterval"/>未満なら通信せず
    /// <see cref="UpdateCheckStatus.NotDue"/>を返す（要件: 起動時1日1回まで）。
    /// </summary>
    public async Task<UpdateCheckResult> CheckOnStartupAsync(
        string checkUrl, string currentVersion, string userAgent, CancellationToken ct = default)
    {
        var state = await _stateStore.LoadAsync(ct).ConfigureAwait(false);
        if (state.LastCheckedAt is { } last && _now() - last < MinimumCheckInterval)
        {
            return UpdateCheckResult.NotDue();
        }

        return await CheckNowAsync(checkUrl, currentVersion, userAgent, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 「今すぐ更新を確認」ボタン用。絞り込みを無視して必ず通信する。
    /// 呼び出しの成否に関わらず、前回確認日時を今回の時刻へ更新する
    /// （通信に失敗しても「確認しようとした」事実は記録し、失敗し続ける環境で毎起動ごとに
    /// GitHubへ再試行し続けることを避ける）。
    /// </summary>
    public async Task<UpdateCheckResult> CheckNowAsync(
        string checkUrl, string currentVersion, string userAgent, CancellationToken ct = default)
    {
        await _stateStore.SaveAsync(new UpdateCheckState { LastCheckedAt = _now() }, ct).ConfigureAwait(false);

        GitHubReleaseInfo? release;
        try
        {
            release = await _feed.GetLatestReleaseAsync(checkUrl, userAgent, ct).ConfigureAwait(false);
        }
        catch
        {
            // IReleaseFeedの実装は内部で例外を握りつぶす契約だが、フェイク実装の実装ミス等の
            // 保険として、ここでも念のため捕捉する（起動を妨げないことを最優先するため）。
            release = null;
        }

        if (release is null)
        {
            return UpdateCheckResult.Failed("更新の確認に失敗しました。ネットワーク接続や設定画面のチェック先URLを確認してください。");
        }

        if (!UpdateVersion.TryParse(release.TagName, out var latest))
        {
            return UpdateCheckResult.Failed($"リリースのバージョン表記を解釈できませんでした（{release.TagName}）。");
        }

        if (!UpdateVersion.TryParse(currentVersion, out var current))
        {
            return UpdateCheckResult.Failed("現在のバージョン情報を解釈できませんでした。");
        }

        // 【数値としての比較】文字列比較だと "1.0.10" < "1.0.9" と誤判定するため、
        // UpdateVersion.CompareToによる数値比較を必ず使う。
        return latest.CompareTo(current) > 0 ? UpdateCheckResult.Available(release) : UpdateCheckResult.UpToDate(release);
    }
}
