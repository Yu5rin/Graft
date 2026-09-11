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
}
