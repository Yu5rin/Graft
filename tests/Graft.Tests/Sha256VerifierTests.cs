using FluentAssertions;
using Graft.Core.Update;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// <see cref="Sha256Verifier"/>: GitHubの<c>digest</c>フィールド（"sha256:xxxx" 形式）の解釈と、
/// ファイルの実際のSHA256計算・突き合わせを検証する。
/// </summary>
public class Sha256VerifierTests
{
    [Fact(DisplayName = "sha256:接頭辞付きの正しい64桁16進はそのまま取り出せる")]
    public void 正しいdigestを取り出せる()
    {
        var hex = new string('a', 64);
        Sha256Verifier.ExtractSha256($"sha256:{hex}").Should().Be(hex);
    }

    [Fact(DisplayName = "大文字混じりでも小文字化して取り出せる")]
    public void 大文字は小文字化される()
    {
        Sha256Verifier.ExtractSha256("sha256:" + new string('A', 64)).Should().Be(new string('a', 64));
    }

    [Theory(DisplayName = "digestが無い・空・sha256以外・桁数不正・非16進のときはnull（検証できない扱い）")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("md5:abcdef")]
    [InlineData("sha256:")]
    [InlineData("sha256:tooShort")]
    public void digestが無いか不正なときはnullを返す(string? digest)
    {
        Sha256Verifier.ExtractSha256(digest).Should().BeNull();
    }

    [Fact(DisplayName = "非16進文字を含む64桁はnullを返す")]
    public void 非16進文字を含む場合はnull()
    {
        var invalid = "sha256:" + new string('g', 64);
        Sha256Verifier.ExtractSha256(invalid).Should().BeNull();
    }

    [Fact(DisplayName = "ComputeHexAsyncは既知の内容のSHA256を正しく計算する")]
    public async Task ファイルのSHA256を計算できる()
    {
        using var ws = new TempWorkspace();
        var path = ws.WriteText("sample.txt", "hello");

        // "hello" のSHA256は既知の値（sha256sum等で確認できる固定値）。
        var hex = await Sha256Verifier.ComputeHexAsync(path);

        hex.Should().Be("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824");
    }

    [Fact(DisplayName = "Matchesは大文字小文字を無視して一致判定する")]
    public void 大文字小文字を無視して一致判定する()
    {
        Sha256Verifier.Matches("ABCDEF", "abcdef").Should().BeTrue();
        Sha256Verifier.Matches("abcdef", "abcdee").Should().BeFalse();
    }
}
