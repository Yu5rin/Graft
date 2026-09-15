using FluentAssertions;
using Graft.Core.Update;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// <see cref="UpdateAtomFeedLogic"/>の要件を固定する（通信を含まない純粋なロジックのみ）。
/// 実機不具合対応（GitHub APIの回数上限。詳しい経緯は<see cref="IReleaseFeed"/>の
/// コメント参照）で、普段の更新確認をAtomフィードへ寄せるために追加した。
/// </summary>
public class UpdateAtomFeedLogicTests
{
    [Fact(DisplayName = "既定のcheckUrlからAtomのURLを組み立てられる")]
    public void 既定のURLから組み立てられる()
    {
        var atomUrl = UpdateAtomFeedLogic.TryBuildAtomUrl("https://api.github.com/repos/Yu5rin/Graft/releases/latest");

        atomUrl.Should().Be("https://github.com/Yu5rin/Graft/releases.atom");
    }

    [Theory(DisplayName = "api.github.com以外や形が違うURLではnullを返し、APIだけの確認へ後退できるようにする")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://example.com/repos/Yu5rin/Graft/releases/latest")] // ホストが違う
    [InlineData("http://api.github.com/repos/Yu5rin/Graft/releases/latest")] // httpsではない
    [InlineData("https://api.github.com/repos/Yu5rin/Graft")] // パスが短い
    [InlineData("https://api.github.com/repos/Yu5rin/Graft/releases")] // "latest"が無い
    [InlineData("https://api.github.com/orgs/Yu5rin/repos")] // "repos/{owner}/{repo}/releases/latest"の形ではない
    [InlineData("not a url")]
    public void 組み立てられないURLではnullになる(string? checkUrl)
    {
        UpdateAtomFeedLogic.TryBuildAtomUrl(checkUrl).Should().BeNull();
    }

    [Fact(DisplayName = "リリースページのURLを組み立てられる")]
    public void リリースページURLを組み立てられる()
    {
        var url = UpdateAtomFeedLogic.BuildReleasePageUrl("https://github.com/Yu5rin/Graft/releases.atom", "v1.0.10");

        url.Should().Be("https://github.com/Yu5rin/Graft/releases/tag/v1.0.10");
    }

    [Fact(DisplayName = "並び順どおりでなくても、バージョンとして最大のタグを選ぶ")]
    public void 並び順が崩れていても最大バージョンを選ぶ()
    {
        // 実機知見（pane UpdateCheckLogic.ExtractLatestTagFromAtomのコメント参照）:
        // フィードは通常新しい順に並ぶが、それに依存すると並びが変わったときに誤判定する。
        // ここでは意図的に古いものを先頭に置く。
        var xml = BuildAtomXml("v1.0.9", "v1.0.10", "v1.0.2");

        UpdateAtomFeedLogic.ExtractLatestTag(xml).Should().Be("v1.0.10");
    }

    [Fact(DisplayName = "バージョンとして読めないタグ（下書き名等）は候補から除外する")]
    public void 読めないタグは無視する()
    {
        var xml = BuildAtomXml("nightly-build", "v1.0.10-beta", "v1.0.3");

        // "v1.0.10-beta" は接尾辞つきでUpdateVersion.TryParseが弾くため、
        // "v1.0.3" だけが候補として残るはず。
        UpdateAtomFeedLogic.ExtractLatestTag(xml).Should().Be("v1.0.3");
    }

    [Fact(DisplayName = "壊れたXMLではnullを返し、呼び出し元はAPIで確認できる")]
    public void 壊れたXMLはnullになる()
    {
        UpdateAtomFeedLogic.ExtractLatestTag("これはxmlではない").Should().BeNull();
    }

    [Fact(DisplayName = "エントリが1つも無いフィードではnullを返す")]
    public void 空のフィードはnullになる()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <feed xmlns="http://www.w3.org/2005/Atom"></feed>
            """;

        UpdateAtomFeedLogic.ExtractLatestTag(xml).Should().BeNull();
    }

    [Fact(DisplayName = "組み立てたダウンロードURLは、実物（v1.0.17）と完全一致する")]
    public void ダウンロードURLは実物と完全一致する()
    {
        // 実機で確認済みの値（CLAUDE.md記載）。推測ではなく、この文字列リテラルとの
        // 完全一致で固定する。
        var url = UpdateAtomFeedLogic.TryBuildDownloadUrl("https://github.com/Yu5rin/Graft/releases.atom", "v1.0.17");

        url.Should().Be("https://github.com/Yu5rin/Graft/releases/download/v1.0.17/Graft-1.0.17-win-x64.zip");
    }

    [Fact(DisplayName = "タグの先頭の\"v\"は、ファイル名側からだけ取り除かれる（URLのパス部分はタグそのまま）")]
    public void タグのvはファイル名側だけ取り除かれる()
    {
        var url = UpdateAtomFeedLogic.TryBuildDownloadUrl("https://github.com/Yu5rin/Graft/releases.atom", "v1.0.17");

        // パス部分（.../download/の直後）はタグそのまま("v"付き)。
        url.Should().Contain("/download/v1.0.17/");
        // ファイル名側は"v"を落とした表記。
        url.Should().EndWith("/Graft-1.0.17-win-x64.zip");
    }

    [Fact(DisplayName = "\"v\"の付かないタグでも壊れない（先頭が\"v\"でなければ何も取り除かない）")]
    public void v無しのタグでも壊れない()
    {
        var url = UpdateAtomFeedLogic.TryBuildDownloadUrl("https://github.com/Yu5rin/Graft/releases.atom", "1.0.17");

        url.Should().Be("https://github.com/Yu5rin/Graft/releases/download/1.0.17/Graft-1.0.17-win-x64.zip");
    }

    [Fact(DisplayName = "BuildWindowsAssetFileNameは、tools/New-Release.ps1の命名（\"Graft-<vを除いたバージョン>-win-x64.zip\"）と一致する")]
    public void ファイル名の組み立てはNewReleaseスクリプトと一致する()
    {
        UpdateAtomFeedLogic.BuildWindowsAssetFileName("v1.0.17").Should().Be("Graft-1.0.17-win-x64.zip");
        UpdateAtomFeedLogic.BuildWindowsAssetFileName("V1.0.17").Should().Be("Graft-1.0.17-win-x64.zip");
        UpdateAtomFeedLogic.BuildWindowsAssetFileName("1.0.17").Should().Be("Graft-1.0.17-win-x64.zip");
    }

    [Fact(DisplayName = "checkUrlがGitHub Releases APIの形でない場合、ダウンロードURLは組み立てない（TryBuildAtomUrlがnullを返すため）")]
    public void 独自の確認先ではダウンロードURLを組み立てない()
    {
        // 利用者が確認先URLをGitHub以外（独自のミラー等）へ変更している場合、TryBuildAtomUrl
        // 自体がnullを返す。この性質をそのまま利用して、こちらの都合でgithub.comへ推測
        // アクセスしに行くことがないようにしている（UpdateAtomFeedLogic.TryBuildAtomUrl・
        // TryBuildDownloadUrlのクラス/メソッドコメント参照）。
        var atomUrl = UpdateAtomFeedLogic.TryBuildAtomUrl("https://git.example.co.jp/api/v4/projects/1/releases/latest");

        atomUrl.Should().BeNull();
    }

    [Fact(DisplayName = "タグが空文字列ならダウンロードURLを組み立てない")]
    public void タグが空ならnullになる()
    {
        UpdateAtomFeedLogic.TryBuildDownloadUrl("https://github.com/Yu5rin/Graft/releases.atom", "").Should().BeNull();
    }

    /// <summary>
    /// GitHubの実際のreleases.atomに近い最小限の形のフィードを組み立てる。タグ名は
    /// entryのlinkのhref末尾（.../releases/tag/{tag}）に置く。
    /// </summary>
    private static string BuildAtomXml(params string[] tagsInFeedOrder)
    {
        var entries = string.Join("\n", tagsInFeedOrder.Select(tag =>
            $"""<entry><link href="https://github.com/Yu5rin/Graft/releases/tag/{tag}"/></entry>"""));
        return $"""<?xml version="1.0" encoding="UTF-8"?><feed xmlns="http://www.w3.org/2005/Atom">{entries}</feed>""";
    }
}
