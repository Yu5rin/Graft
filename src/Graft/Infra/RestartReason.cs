namespace Graft.Infra;

/// <summary>
/// アプリの自己再起動を要求した理由。<c>App.RequestRestart</c>が、ログ（logs/）へ「なぜ再起動したか」を
/// 正しく記録するために使う。
///
/// 【なぜ理由を持たせたか】 以前は再起動要求のログが、常に「設定画面のデータ保存先移行完了ダイアログの
/// 「再起動」ボタン」という固定の文言だった。ところが再起動要求は<c>SettingsViewModel.RestartRequested</c>
/// という1つのイベントで届き、そのイベントは2か所から発火する（下の2値）。実機では自動更新の後の
/// 再起動でもこの文言が残り、ログを読んだ人が「データ保存先を移行したのか」と調査を誤る原因になった。
/// イベント自身が理由を運び（<see cref="RestartRequestedEventArgs"/>）、購読側（SettingsWindow・
/// StartupCoordinator）はそれをそのまま<c>RequestRestart</c>へ渡す。購読側が文言を決め打ちにしない
/// のは、同じ購読側が両方の経路のイベントを受け取るため（どちらの経路か購読側からは分からない）。
/// </summary>
public enum RestartReason
{
    /// <summary>
    /// 設定画面の「データ保存先の移行」が完了し、完了ダイアログの「再起動」ボタンが押された
    /// （<c>SettingsViewModel.DataDirectory.cs</c>の移行処理）。
    /// </summary>
    DataDirectoryMigration,

    /// <summary>
    /// 自動更新のファイル置き換えが済み、「更新の準備ができました」ダイアログの「今すぐ再起動」が
    /// 押された（<c>SettingsViewModel.Update.cs</c>のインストール後の処理）。起動時の自動確認から
    /// 始まった更新も、設定画面から手動で始めた更新も、ここへ来る経路は同じ。
    /// </summary>
    UpdateInstalled,
}

/// <summary><c>SettingsViewModel.RestartRequested</c>の引数。再起動の理由を運ぶ。</summary>
public sealed class RestartRequestedEventArgs : EventArgs
{
    public RestartRequestedEventArgs(RestartReason reason)
    {
        Reason = reason;
    }

    /// <summary>再起動を要求した理由。</summary>
    public RestartReason Reason { get; }
}

/// <summary><see cref="RestartReason"/>をログ用の日本語にする。</summary>
public static class RestartReasonText
{
    /// <summary>
    /// 再起動要求のログに添える、きっかけの説明（例: 「自動更新のインストール後、
    /// 「今すぐ再起動」ボタン」）。未知の値は黙って別の理由にせず、値そのものを出す。
    /// </summary>
    public static string Describe(RestartReason reason) => reason switch
    {
        RestartReason.DataDirectoryMigration => "設定画面のデータ保存先移行完了ダイアログの「再起動」ボタン",
        RestartReason.UpdateInstalled => "自動更新のインストール後の「今すぐ再起動」ボタン",
        _ => $"不明な理由（{reason}）",
    };

    /// <summary>再起動要求のログ1行の全文。</summary>
    public static string BuildLogMessage(RestartReason reason) => $"再起動が要求されました（{Describe(reason)}）。";
}
