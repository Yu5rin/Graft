namespace Graft.Core.Update;

/// <summary>更新確認結果の種別。</summary>
public enum UpdateCheckStatus
{
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

    public static UpdateCheckResult Available(GitHubReleaseInfo release)
        => new() { Status = UpdateCheckStatus.UpdateAvailable, Release = release };

    public static UpdateCheckResult UpToDate(GitHubReleaseInfo release)
        => new() { Status = UpdateCheckStatus.UpToDate, Release = release };

    public static UpdateCheckResult Failed(string message) => new() { Status = UpdateCheckStatus.Failed, ErrorMessage = message };
}

/// <summary>
/// 更新確認のオーケストレーション（GitHub Releases APIとの通信・バージョンの数値比較）を担う。
/// 通信の失敗はここで吸収し、例外を外へ投げない
/// （要件: 通信の失敗は握りつぶして「確認できなかった」で済ませる。起動を妨げない）。
/// </summary>
public sealed class UpdateChecker
{
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
    /// 起動時チェック。
    ///
    /// 仕様変更（v1.0.12）: 以前はここで「前回確認から24時間未満なら通信しない」という
    /// 絞り込みをかけていたが、設定画面のチェックボックスの文言が最初から
    /// 「起動時に更新を確認する」であり、実態（1日1回まで）と食い違っていた（利用者からの
    /// 指摘）。文言どおり「起動するたびに必ず確認する」よう、この絞り込みを廃止した。
    /// 呼び出し元（<see cref="Graft.ViewModels.SettingsViewModel.CheckForUpdateOnStartupAsync"/>）
    /// 側で「起動時に更新を確認する」設定がオフなら、そもそもこのメソッドを呼ばない形で
    /// 「確認するかどうか」自体は引き続き利用者が制御できる。
    /// 中身は<see cref="CheckNowAsync"/>と同一だが、呼び出し側の意図（起動時経由か手動か）を
    /// 型で表すためにメソッドとして残す。
    /// </summary>
    public Task<UpdateCheckResult> CheckOnStartupAsync(
        string checkUrl, string currentVersion, string userAgent, CancellationToken ct = default)
        => CheckNowAsync(checkUrl, currentVersion, userAgent, ct);

    /// <summary>
    /// 実際に通信して確認する本体。「今すぐ更新を確認」ボタン、<see cref="CheckOnStartupAsync"/>
    /// いずれからも呼ばれ、必ず通信する（起動時・手動を問わず絞り込みは行わない）。
    /// 呼び出しの成否に関わらず、前回確認日時を今回の時刻へ更新する
    /// （通信に失敗しても「確認しようとした」事実は記録に残す。「最終確認」表示用）。
    ///
    /// 【成否も併せて記録する理由（実機不具合対応）】 以前は日時しか記録していなかったため、
    /// 確認が3回連続で失敗しても画面には「最終確認: 2026/09/06 06:26」とだけ出ており、
    /// 「確認した＝最新だった」と読めてしまっていた（失敗はログのwarnにしか残らない）。
    /// オフラインが続くと利用者は何日でも更新が止まっていることに気づけない。
    ///
    /// 【2回書く理由】 まず通信前に「試みた・まだ成功していない」（<c>LastCheckSucceeded=false</c>）
    /// として書き、最新かどうかの判定まで到達できたときだけ true で上書きする。こうしておくと、
    /// 通信中にプロセスが落ちた場合でも「確認できていない」という安全側の記録が残る
    /// （後から true を書かない限り成功にはならない）。書き込み先は数十バイトのJSON1個で、
    /// 更新確認は起動時か手動ボタンのときにしか走らないため、2回書く負荷は問題にならない。
    /// </summary>
    public async Task<UpdateCheckResult> CheckNowAsync(
        string checkUrl, string currentVersion, string userAgent, CancellationToken ct = default)
    {
        var startedAt = _now();
        await _stateStore
            .SaveAsync(new UpdateCheckState { LastCheckedAt = startedAt, LastCheckSucceeded = false }, ct)
            .ConfigureAwait(false);

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

        // ここまで来れば「最新かどうかを判定できた」＝確認は成功。日時は通信前に決めた値を
        // そのまま使い（成功のたびに時刻がずれると「いつ試みたか」がぶれるため）、成否だけを
        // true へ上書きする。
        await _stateStore
            .SaveAsync(new UpdateCheckState { LastCheckedAt = startedAt, LastCheckSucceeded = true }, ct)
            .ConfigureAwait(false);

        // 【数値としての比較】文字列比較だと "1.0.10" < "1.0.9" と誤判定するため、
        // UpdateVersion.CompareToによる数値比較を必ず使う。
        return latest.CompareTo(current) > 0 ? UpdateCheckResult.Available(release) : UpdateCheckResult.UpToDate(release);
    }
}
