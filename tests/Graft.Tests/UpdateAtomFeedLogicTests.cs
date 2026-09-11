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
