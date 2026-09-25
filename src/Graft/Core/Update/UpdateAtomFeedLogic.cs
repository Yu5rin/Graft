using System.Xml;
using System.Xml.Linq;

namespace Graft.Core.Update;

/// <summary>
/// Atomフィード（<c>https://github.com/{owner}/{repo}/releases.atom</c>）関連の、通信を
/// 含まない純粋なロジックだけを集めたもの。<see cref="GitHubReleaseFeed"/>から呼ばれ、
/// 値を渡して戻り値を見るだけで試せる（<c>UpdateAtomFeedLogicTests</c>）。
///
/// 【経緯・出典】
/// 実機ログ（2026-09-11、社内の共有回線）で、GitHub APIの回数上限（未認証で1時間60回、
/// IPアドレス単位）に更新確認が繰り返し当たっていた（詳しい経緯は<see cref="IReleaseFeed"/>の
/// コメント参照）。Atomフィードはその上限とは別枠のため、普段の確認をこちらへ寄せる。
/// この考え方・実装は、同じ開発者の別リポジトリ「pane」の<c>UpdateCheckLogic.cs</c>・
/// <c>UpdateService.cs</c>が、同じ社内の共有回線で実機不具合として踏み、解決したものに
/// 倣っている（丸写しではなく、Graftの構造（<see cref="UpdateVersion"/>によるバージョン比較）に
/// 合わせて設計し直した）。
/// </summary>
public static class UpdateAtomFeedLogic
{
    /// <summary>
    /// GitHub Releases APIの確認先URL（<c>https://api.github.com/repos/{owner}/{repo}/releases/latest</c>）
    /// から、同じリポジトリのAtomフィードのURLを組み立てる。
    ///
    /// 【なぜ「組み立てられない場合」を用意するか】 設定画面の<c>update.checkUrl</c>は
    /// 利用者が変更できる（GitHub以外の配布元、別のAPIパス等）。そのすべてに対して
    /// Atomフィードの場所を推測することはできないし、すべきでもない。組み立てられない
    /// URL（<c>api.github.com</c>以外のホスト、<c>repos/{owner}/{repo}/releases/latest</c>の
    /// 形と一致しないパス等）ではnullを返し、呼び出し元（<see cref="GitHubReleaseFeed"/>）は
    /// 従来どおりAPIだけで確認する経路へ自然に後退する（要件）。
    /// </summary>
    public static string? TryBuildAtomUrl(string? checkUrl)
    {
        if (string.IsNullOrWhiteSpace(checkUrl)) return null;
        if (!Uri.TryCreate(checkUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }
        if (!string.Equals(uri.Host, "api.github.com", StringComparison.OrdinalIgnoreCase)) return null;

        // 期待する形: /repos/{owner}/{repo}/releases/latest
        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.None);
        if (segments.Length < 5) return null;
        if (!string.Equals(segments[0], "repos", StringComparison.OrdinalIgnoreCase)) return null;
        if (string.IsNullOrEmpty(segments[1]) || string.IsNullOrEmpty(segments[2])) return null;
        if (!string.Equals(segments[3], "releases", StringComparison.OrdinalIgnoreCase)) return null;
        if (!string.Equals(segments[4], "latest", StringComparison.OrdinalIgnoreCase)) return null;

        return $"https://github.com/{segments[1]}/{segments[2]}/releases.atom";
    }

    /// <summary>
    /// <see cref="TryBuildAtomUrl"/>が組み立てたAtomのURLから、あるタグのリリースページの
    /// URLを組み立てる（<c>.../releases.atom</c> → <c>.../releases/tag/{tag}</c>）。
    /// 形が合わない場合は空文字列を返す（呼び出し元は必ずTryBuildAtomUrlの戻り値を渡すため、
    /// 通常この分岐には来ない。防御的に用意しているだけ）。
    /// </summary>
    public static string BuildReleasePageUrl(string atomUrl, string tag)
        => atomUrl.EndsWith(".atom", StringComparison.OrdinalIgnoreCase)
            ? $"{atomUrl[..^".atom".Length]}/tag/{Uri.EscapeDataString(tag)}"
            : "";

    /// <summary>
    /// Atomフィードのxmlから、いちばん新しいリリースのタグ名を取り出す。読めなければnull。
    ///
    /// 【並び順に頼らない理由（pane実機知見）】 フィードは通常新しい順に並ぶが、それに
    /// 依存すると、配布元の並びが変わった場合（実際に起こりうる。手動でのリリース削除・
    /// 作り直し等）に古い版を「最新」と誤判定してしまう。読み取れた全エントリのタグの
    /// うち、<see cref="UpdateVersion"/>として解釈できてバージョンとして最大のものを選ぶ。
    /// バージョンとして解釈できないタグ（下書き用の名前、リリースではなくタグだけの
    /// エントリ等）は無視する。
    ///
    /// バージョンの解釈に<see cref="UpdateVersion"/>（GitHub APIから読んだタグの比較にも
    /// 使っている、Graft本体の基準）をそのまま使うのは重要: Atom用に別の緩い解析を
    /// 混ぜると、「Atomでは新しいと判定したのに、APIの値と突き合わせると実は新しくない」
    /// といったズレが起きうるため。
    ///
    /// タグ名はリリースページのURL（entryのlinkのhref）の末尾から取り出す。GitHubの
    /// Atomフィードにはタグ名を直接示す専用の要素が無いため。
    /// </summary>
    public static string? ExtractLatestTag(string xml)
    {
        XDocument feed;
        try
        {
            feed = XDocument.Parse(xml);
        }
        catch (XmlException)
        {
            // 壊れたxmlが返ってきても、呼び出し元はAPIで確認できる。ここでは黙って諦める。
            return null;
        }

        XNamespace atom = "http://www.w3.org/2005/Atom";
        string? bestTag = null;
        UpdateVersion? bestVersion = null;

        foreach (var entry in feed.Root?.Elements(atom + "entry") ?? Enumerable.Empty<XElement>())
        {
            var href = entry.Elements(atom + "link")
                .Select(e => (string?)e.Attribute("href"))
                .FirstOrDefault(h => !string.IsNullOrEmpty(h));
            if (string.IsNullOrEmpty(href)) continue;

            var tag = Uri.UnescapeDataString(href.TrimEnd('/').Split('/').Last());
            if (string.IsNullOrEmpty(tag)) continue;
            if (!UpdateVersion.TryParse(tag, out var version)) continue;
            if (bestVersion is { } existing && version.CompareTo(existing) <= 0) continue;

            bestVersion = version;
            bestTag = tag;
        }

        return bestTag;
    }

    /// <summary>
    /// Windows版配布物（ZIP）のファイル名を、タグから組み立てる。
    ///
    /// 【実物で確認済みの規則（推測ではない）】 タグ<c>v1.0.17</c>に対する実際の配布物は
    /// <c>Graft-1.0.17-win-x64.zip</c>である（先頭の<c>"v"</c>が落ちている）。つまり
    /// <b>タグの先頭の<c>"v"/"V"</c>を1つだけ取り除いた文字列</b>をファイル名に使う。
    /// （URLのパス部分（タグそのもの）とファイル名（"v"を落とした版）とで表記が異なる点を
    /// 混同しないこと。<see cref="TryBuildDownloadUrl"/>参照。）
    ///
    /// 規則そのものは<see cref="UpdatePlatformPolicy.BuildAssetFileName"/>（Windows・Linux共通の
    /// 単一の情報源）に置いてあり、ここはそのWindows版を呼ぶだけ。
    ///
    /// 【配布物を作る2つの経路との整合】
    /// <list type="bullet">
    /// <item><c>tools/New-Release.ps1</c>: ZIPのファイル名を<c>"Graft-$resolvedVersion-win-x64.zip"</c>
    /// として作り、<c>$resolvedVersion</c>には<c>Graft.csproj</c>の<c>&lt;Version&gt;</c>
    /// （"v"を付けない、例: "1.0.17"）をそのまま使う。v1.0.2以降の実際のリリースはすべてこちら。</item>
    /// <item><c>.github/workflows/release.yml</c>: 以前は<c>github.ref_name</c>（タグそのもの。
    /// "v"付き）をファイル名に使っており、<c>Graft-v1.0.1-win-x64.zip</c>のような名前を作って
    /// いた（v1.0.0・v1.0.1の実物）。この名前ではここで組み立てたURLが404になり、APIに
    /// 届かないときの自動更新が必ず失敗する。ワークフロー側をタグから"v"を除いた
    /// <c>VERSION</c>で名前を付けるよう直し、この規則に揃えた（2026-09-25）。</item>
    /// </list>
    /// <b>どちらかの経路でファイル名の付け方を変えるなら、ここ（<see cref="UpdatePlatformPolicy.BuildAssetFileName"/>）も
    /// 合わせて直す必要がある。</b>（ずれた場合の実害は「組み立てたURLが404になり自動更新に
    /// 失敗する」だけで、誤ったファイルが入ることはない。）<c>UpdatePlatformPolicyTests</c>が
    /// 両経路のファイルを読んで、この規則との一致を確かめている。
    /// </summary>
    public static string BuildWindowsAssetFileName(string tag)
        => UpdatePlatformPolicy.BuildAssetFileName(tag, UpdatePlatform.Windows)!;

    /// <summary>
    /// GitHub Releases APIを使わずに、Windows版配布物のダウンロードURLを規則から組み立てる。
    /// 組み立てられない場合はnull。
    ///
    /// <code>
    /// https://github.com/{owner}/{repo}/releases/download/{tag}/Graft-{タグから"v"を除いた値}-win-x64.zip
    /// </code>
    ///
    /// 【なぜ要るか（実機不具合対応。CLAUDE.mdの実測ログ参照）】 GitHub Releases APIには
    /// 未認証で1時間60回・IPアドレス単位の上限がある。社内の共有回線（プロキシ経由・
    /// 他者と共有するIP）では他の通信で先に使い切られ、Atomフィードで「新しい版がある」ことは
    /// 分かっているのに、配布物の詳細（ダウンロードURL・SHA256）を取りに行くAPIだけが
    /// 毎回403で失敗し、自動更新が一度も成立しなかった（実機ログ、2026-09-XX、v1.0.17）。
    /// ダウンロードURLは上記のとおり規則的なので、APIに頼らず組み立てられる。
    ///
    /// 【引き換えに失うもの】 SHA256はAPIの応答（アセットのdigestフィールド）からしか
    /// 取れないため、この経路で得られる配布物情報には付けられない。呼び出し元
    /// （<see cref="UpdateChecker.CheckNowAsync"/>）は、ここでURLを組み立てられた場合に限り
    /// <see cref="GitHubReleaseInfo.AllowMissingChecksum"/>をtrueにして続行する。「APIに
    /// 到達できず理由が分かっている場合だけ省く」のであって「常に省く」わけではないことが
    /// 重要（<see cref="UpdateInstallPipeline"/>のクラスコメント・RunAsyncのコメント参照）。
    ///
    /// 【前提】 <paramref name="atomUrl"/>は必ず<see cref="TryBuildAtomUrl"/>の戻り値
    /// （非null）をそのまま渡すこと。この前提が崩れていない限り、下のEndsWith判定に
    /// 落ちることは無い（呼び出し元がGitHub以外の確認先を設定している場合はそもそも
    /// <see cref="TryBuildAtomUrl"/>がnullを返し、このメソッド自体を呼ぶ機会が無い。
    /// 独自の配布元を設定した利用者に対して、こちらの都合でgithub.comへ推測アクセスしに
    /// 行くことがないようにするための設計）。
    /// </summary>
    public static string? TryBuildDownloadUrl(string atomUrl, string tag)
    {
        if (string.IsNullOrEmpty(tag)) return null;
        if (!atomUrl.EndsWith(".atom", StringComparison.OrdinalIgnoreCase)) return null;

        var baseUrl = atomUrl[..^".atom".Length]; // https://github.com/{owner}/{repo}/releases
        return $"{baseUrl}/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(BuildWindowsAssetFileName(tag))}";
    }
}
