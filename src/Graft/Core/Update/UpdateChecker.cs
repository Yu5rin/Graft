namespace Graft.Core.Update;

/// <summary>更新確認結果の種別。</summary>
public enum UpdateCheckStatus
{
    /// <summary>確認でき、新しいバージョンが見つかった（配布物の詳細を含む）。</summary>
    UpdateAvailable,

    /// <summary>
    /// Atomフィードで新しいバージョンがあることは分かったが、GitHub Releases APIが失敗し
    /// 配布物の詳細（ダウンロードURL・SHA256）を取得できなかった。自動更新はできないが、
    /// 「新しい版がある」こと自体と、リリースページのURL（<see cref="Release"/>の
    /// <see cref="GitHubReleaseInfo.HtmlUrl"/>）は伝えられる（実機不具合対応。詳しくは
    /// <see cref="CheckNowAsync"/>のコメント参照）。
    /// </summary>
    UpdateAvailableNoDetails,

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

    /// <summary>
    /// 利用者向けの<see cref="ErrorMessage"/>とは別に、ログにだけ残す診断情報
    /// （HTTP状態コード・例外の型名等）。<see cref="Core.ExceptionMessages"/>と同じ方針
    /// （利用者向けの文には型名や生の状態コードを出さない）を保ちつつ、実機不具合対応の
    /// 要件（「ログには状態コードと例外の型を必ず残す」）を満たすために持たせている。
    /// <see cref="UpdateCheckStatus.Failed"/>・<see cref="UpdateCheckStatus.UpdateAvailableNoDetails"/>
    /// でのみ設定されうる。
    /// </summary>
    public string? DiagnosticDetail { get; init; }

    public static UpdateCheckResult Available(GitHubReleaseInfo release)
        => new() { Status = UpdateCheckStatus.UpdateAvailable, Release = release };

    public static UpdateCheckResult AvailableNoDetails(GitHubReleaseInfo release, string message, string? diagnosticDetail)
        => new()
        {
            Status = UpdateCheckStatus.UpdateAvailableNoDetails,
            Release = release,
            ErrorMessage = message,
            DiagnosticDetail = diagnosticDetail,
        };

    public static UpdateCheckResult UpToDate(GitHubReleaseInfo release)
        => new() { Status = UpdateCheckStatus.UpToDate, Release = release };

    public static UpdateCheckResult Failed(string message, string? diagnosticDetail = null)
        => new() { Status = UpdateCheckStatus.Failed, ErrorMessage = message, DiagnosticDetail = diagnosticDetail };
}

/// <summary>
/// 更新確認のオーケストレーション（Atomフィード・GitHub Releases APIとの通信・
/// バージョンの数値比較）を担う。通信の失敗はここで吸収し、例外を外へ投げない
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
    ///
    /// 【オーケストレーション（実機不具合対応。詳しい経緯は<see cref="IReleaseFeed"/>・
    /// <see cref="GitHubReleaseFeed"/>のコメント参照）】
    /// 1. まずAtomフィード（<see cref="IReleaseFeed.TryGetLatestTagFromAtomAsync"/>。
    ///    GitHub APIの回数上限とは別枠）でタグだけを見る。読み取れたタグが現在と同じか
    ///    古ければ、そこでAPIを一切呼ばずに「最新版です」を返す（節約の本体。更新が無い
    ///    大多数の確認では、これでAPIの回数上限を1回も消費しなくなる）。
    /// 2. Atomが使えなかった（<c>checkUrl</c>がGitHub API形式でない、通信・解析に失敗、
    ///    バージョンとして読めるタグが無い等）、またはAtomで「新しい」と分かった場合は、
    ///    配布物の詳細（ダウンロードURL・SHA256）を取りにAPIへ問い合わせる。
    /// 3. APIが失敗した場合、Atomで新しいタグが分かっていれば
    ///    <see cref="UpdateCheckResult.AvailableNoDetails"/>（「新しい版があることは
    ///    分かったが、自動更新はできない。リリースページから手動で」）を返す。Atomの
    ///    情報も無ければ、理由別の文言（<see cref="BuildFailureMessage"/>）で
    ///    <see cref="UpdateCheckResult.Failed"/>を返す。
    /// 4. APIが成功した場合は、その応答のタグを<see cref="UpdateVersion"/>で現在と数値比較する
    ///    （Atomのタグをそのまま信用しない）。これにより、AtomとAPIの既知の非対称——
    ///    Atomフィードはプレリリースも載せるが、APIの<c>releases/latest</c>は安定版だけを
    ///    返す——があっても、最終的な「新しいかどうか」の判定は必ずAPIの値（＝実際に
    ///    ダウンロードする対象）を基準にする。
    ///    【Graftでの扱い（既知の非対称への対応）】 Atomのタグ抽出（<see
    ///    cref="UpdateAtomFeedLogic.ExtractLatestTag"/>）は<see cref="UpdateVersion.TryParse"/>で
    ///    解釈できるタグしか候補にしない。Graftのタグ運用は"vX.Y.Z"のみで
    ///    "-beta"等の接尾辞を使わず、<see cref="UpdateVersion.TryParse"/>はそのような接尾辞
    ///    付きの文字列を素直に解釈失敗として弾く（同メソッドのコメント参照）ため、
    ///    「接尾辞つきのプレリリースタグ」がAtom側の最新候補として誤って選ばれることは
    ///    無い。ただし、GitHub上で"通常の"タグ名（接尾辞なし）のままprereleaseとして
    ///    公開した場合はAtom側に区別する手がかりが無く、上記3のAPI失敗時の案内
    ///    （AvailableNoDetails）がそのプレリリースを「新しい版」として案内してしまう
    ///    可能性は残る。Graftは現状（<c>tools/New-Release.ps1</c>）prereleaseを作らない
    ///    運用のため許容する（別リポジトリpaneの<c>UpdateService.cs</c>が同じ理由で
    ///    同じ限界を許容していることも確認済み）。将来prereleaseを使い始めるなら、
    ///    Atomのentryからprerelease相当を除外する条件を追加する必要がある。
    /// </summary>
    public async Task<UpdateCheckResult> CheckNowAsync(
        string checkUrl, string currentVersion, string userAgent, CancellationToken ct = default)
    {
        var startedAt = _now();
        await _stateStore
            .SaveAsync(new UpdateCheckState { LastCheckedAt = startedAt, LastCheckSucceeded = false }, ct)
            .ConfigureAwait(false);

        if (!UpdateVersion.TryParse(currentVersion, out var current))
        {
            return UpdateCheckResult.Failed("現在のバージョン情報を解釈できませんでした。");
        }

        var atomTag = await TryPeekAtomAsync(checkUrl, ct).ConfigureAwait(false);
        if (atomTag is not null
            && UpdateVersion.TryParse(atomTag.TagName, out var fromAtom)
            && fromAtom.CompareTo(current) <= 0)
        {
            // 節約の本体: Atomで「新しくない」と分かった時点でAPIを呼ばずに終える。
            await MarkSucceededAsync(startedAt, ct).ConfigureAwait(false);
            return UpdateCheckResult.UpToDate(
                new GitHubReleaseInfo { TagName = atomTag.TagName, HtmlUrl = atomTag.ReleasePageUrl });
        }

        var fetch = await SafeFetchReleaseAsync(checkUrl, userAgent, ct).ConfigureAwait(false);
        if (!fetch.Success)
        {
            var (userMessage, diagnostic) = BuildFailureMessage(fetch);
            if (atomTag is not null)
            {
                // Atomで新しい版があることは分かっているので、「確認できなかった」だけで
                // 終わらせない（実機不具合対応。要件2）。「確認できた」の一種として扱い、
                // LastCheckSucceededもtrueにする。
                await MarkSucceededAsync(startedAt, ct).ConfigureAwait(false);
                return UpdateCheckResult.AvailableNoDetails(
                    new GitHubReleaseInfo { TagName = atomTag.TagName, HtmlUrl = atomTag.ReleasePageUrl },
                    $"新しい版 {atomTag.TagName} があります。ただし配布物の詳細を取得できなかったため" +
                    "自動更新はできません。リリースページから手動で更新してください。",
                    diagnostic);
            }
            return UpdateCheckResult.Failed(userMessage, diagnostic);
        }

        var release = fetch.Release!;
        if (!UpdateVersion.TryParse(release.TagName, out var latest))
        {
            return UpdateCheckResult.Failed($"リリースのバージョン表記を解釈できませんでした（{release.TagName}）。");
        }

        // ここまで来れば「最新かどうかを判定できた」＝確認は成功。日時は通信前に決めた値を
        // そのまま使い（成功のたびに時刻がずれると「いつ試みたか」がぶれるため）、成否だけを
        // true へ上書きする。
        await MarkSucceededAsync(startedAt, ct).ConfigureAwait(false);

        // 【数値としての比較】文字列比較だと "1.0.10" < "1.0.9" と誤判定するため、
        // UpdateVersion.CompareToによる数値比較を必ず使う。
        return latest.CompareTo(current) > 0 ? UpdateCheckResult.Available(release) : UpdateCheckResult.UpToDate(release);
    }

    /// <summary>
    /// Atomフィードを覗き見る。<see cref="IReleaseFeed"/>の実装は内部で例外を握りつぶす契約
    /// だが、フェイク実装の実装ミス等の保険として、ここでも念のため捕捉する（Atomが使えない
    /// ことを致命的に扱わないという方針を、想定外の例外に対しても一貫させるため）。
    /// </summary>
    private async Task<AtomFeedTag?> TryPeekAtomAsync(string checkUrl, CancellationToken ct)
    {
        try
        {
            return await _feed.TryGetLatestTagFromAtomAsync(checkUrl, ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// <see cref="IReleaseFeed.GetLatestReleaseAsync"/>を呼ぶ。実装は内部で例外を握りつぶす
    /// 契約だが、フェイク実装の実装ミス等の保険として、ここでも念のため捕捉する（起動を
    /// 妨げないことを最優先するため）。
    /// </summary>
    private async Task<ReleaseFetchResult> SafeFetchReleaseAsync(string checkUrl, string userAgent, CancellationToken ct)
    {
        try
        {
            return await _feed.GetLatestReleaseAsync(checkUrl, userAgent, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ReleaseFetchResult.Fail(ReleaseFetchFailureReason.Unknown, exceptionTypeName: ex.GetType().Name);
        }
    }

    private async Task MarkSucceededAsync(DateTimeOffset startedAt, CancellationToken ct)
        => await _stateStore
            .SaveAsync(new UpdateCheckState { LastCheckedAt = startedAt, LastCheckSucceeded = true }, ct)
            .ConfigureAwait(false);

    /// <summary>
    /// <see cref="ReleaseFetchResult"/>の失敗理由を、(利用者向けの日本語1文, ログ専用の
    /// 診断情報) の組へ変換する。
    ///
    /// 【文言の方針（実機不具合対応。要件3）】 少なくとも次を区別する。いずれも
    /// 「次に何をすればよいか」が利用者に伝わる文にし、状態コードや例外の型名などの
    /// 技術的な語は診断情報側にのみ残す（<see cref="ExceptionMessages.Describe"/>と
    /// 同じ方針）。403の文言は別リポジトリpaneの<c>UpdateService.NewerButNoDetails</c>・
    /// <c>CheckAsync</c>の文言を、Graftの口調に合わせて書き直したもの。
    /// </summary>
    private static (string UserMessage, string Diagnostic) BuildFailureMessage(ReleaseFetchResult fetch)
    {
        var reason = fetch.FailureReason ?? ReleaseFetchFailureReason.Unknown;
        var userMessage = reason switch
        {
            ReleaseFetchFailureReason.RateLimited =>
                "配布元への問い合わせが、回数の上限に達していました。この上限は同じネットワークを" +
                "使う人たちで共有されるため、自分が何度も確認していなくても起こります。しばらく" +
                "時間をおくか、リリースページから直接ご確認ください。",
            ReleaseFetchFailureReason.ProxyAuthenticationRequired =>
                "プロキシの認証が必要なため、配布元へ問い合わせられませんでした。ネットワーク管理者に" +
                "プロキシの設定をご確認ください。",
            ReleaseFetchFailureReason.TimedOut =>
                "配布元から時間内に応答がありませんでした。ネットワーク接続の状態を確認してください。",
            ReleaseFetchFailureReason.NameResolutionFailed =>
                "配布元のサーバー名を解決できませんでした。ネットワーク接続やDNSの設定、通信が" +
                "ブロックされていないかを確認してください。",
            ReleaseFetchFailureReason.ConnectionFailed =>
                "配布元に接続できませんでした。ネットワーク接続や、通信がブロックされていないかを" +
                "確認してください。",
            ReleaseFetchFailureReason.HttpError =>
                $"配布元が想定外の応答（HTTP {fetch.HttpStatusCode}）を返しました。時間をおいて再試行するか、" +
                "設定画面のチェック先URLを確認してください。",
            _ => "更新の確認に失敗しました。ネットワーク接続や設定画面のチェック先URLを確認してください。",
        };

        var diagnosticParts = new List<string> { $"理由={reason}" };
        if (fetch.HttpStatusCode is { } code) diagnosticParts.Add($"HTTP {code}");
        if (fetch.ExceptionTypeName is { } type) diagnosticParts.Add($"例外={type}");
        return (userMessage, string.Join(", ", diagnosticParts));
    }
}
