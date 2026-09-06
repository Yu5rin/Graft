namespace Graft.Platform;

/// <summary>
/// セキュリティ点検の指摘事項（「release.HtmlUrlをスキーム検査なしでShellExecuteに渡している」）
/// への対応として切り出した共通処理。
///
/// 【何が問題だったか】 <see cref="IExternalLinkLauncher"/>の実装（<c>WindowsExternalLinkLauncher</c>）は
/// <c>UseShellExecute = true</c>でURL文字列をそのままシェルへ渡す。ShellExecuteは
/// <c>https://</c>のような通常のURLだけでなく、渡し方次第ではローカルファイルパスや
/// レジストリに登録された任意のプロトコルハンドラも解決してしまいうる。GitHub Releases API
/// の応答（<c>html_url</c>）は通信経路や配布元の設定（<see cref="Core.Update.UpdateHostPolicy"/>
/// 参照）次第で信頼できない値になりうるため、開く前に「絶対URIとして解釈できるか」
/// 「スキームがhttpsか」を検査し、かつ利用者が実際に開く前にURL全文を確認できるようにする。
///
/// 【既存の流儀との統一】 悪意あるMarkdownファイルからの外部リンク（<see
/// cref="Views.EditorPane.ConfirmAndOpenExternalLinkAsync"/>、分類は<see
/// cref="Views.ManualMarkdownRenderer.ClassifyLink"/>）で既に「確認ダイアログでURL全文を
/// 見せてから<see cref="IExternalLinkLauncher"/>で開く」という流儀が確立している。
/// ただしMarkdownプレビュー側は<c>mailto:</c>等も含めた「スキーム付き絶対URI全般」を対象に
/// しており、EditorPane固有のテスト用差し替え口（<c>MarkdownLinkDialogs</c>・
/// <c>OpenExternalLinkAction</c>）に強く結び付いているため、その実装自体を直接共有すると
/// Markdownプレビュー側の対応スキームまで狭めてしまう。ここでは「httpsのみ許可」という
/// より厳しい条件が必要な呼び出し元（自動更新のリリースページ表示）向けに、確認ダイアログ
/// ＋起動という同じ形だけを共通処理として切り出す。
/// </summary>
public static class ExternalLinkConfirmation
{
    /// <summary>
    /// <paramref name="url"/>が絶対URIとして解釈でき、かつスキームが<c>https</c>の場合に限り、
    /// 確認ダイアログ（URL全文を表示）を経てから<paramref name="launcher"/>で開く。
    /// それ以外（解析できない・https以外のスキーム）は何もせずfalseを返す
    /// （安全側に倒す。呼び出し元は「開けなかった」ことを利用者へ伝える）。
    /// </summary>
    /// <returns>実際に確認ダイアログを経て起動を試みたらtrue。利用者が「キャンセル」を選んだ
    /// 場合も、確認ダイアログ自体は表示できたという意味でtrueを返す（呼び出し元が
    /// 「URLが不正だったので開けなかった」と「利用者が開かないことを選んだ」を区別できるように、
    /// 前者のみfalseにしている）。</returns>
    public static async Task<bool> TryConfirmAndOpenHttpsAsync(
        IDialogService dialogs, IExternalLinkLauncher launcher, string? url, string dialogTitle)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        var confirmed = await dialogs.ConfirmAsync(
                dialogTitle,
                $"既定のブラウザで次のURLを開きます。{Environment.NewLine}{Environment.NewLine}{uri}{Environment.NewLine}{Environment.NewLine}信頼できるリンク先の場合のみ「OK」を押してください。")
            .ConfigureAwait(true);
        if (confirmed)
        {
            launcher.Open(uri.ToString());
        }
        return true;
    }
}
