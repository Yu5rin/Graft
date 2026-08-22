using FluentAssertions;
using Graft.Core.Update;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 自動更新の要（指示書より）: バージョン比較は必ず数値として行うこと。文字列比較だと
/// "1.0.10" が "1.0.9" より小さいと誤判定される（'1' &lt; '9'）ため、これを固定で押さえる。
/// </summary>
public class UpdateVersionTests
{
    [Theory(DisplayName = "1.0.10は1.0.9より新しいと判定される（文字列比較なら逆になる桁）")]
    [InlineData("1.0.10", "1.0.9")]
    [InlineData("v1.0.10", "v1.0.9")]
    [InlineData("2.0.0", "1.99.99")]
    [InlineData("1.10.0", "1.9.0")]
    public void 桁の多い方が数値として大きければ新しいと判定される(string newer, string older)
    {
        UpdateVersion.TryParse(newer, out var a).Should().BeTrue();
        UpdateVersion.TryParse(older, out var b).Should().BeTrue();

        (a > b).Should().BeTrue($"{newer} は {older} より数値として新しいはず");
        (b < a).Should().BeTrue();
        a.CompareTo(b).Should().BePositive();
    }

    [Fact(DisplayName = "同値（桁数が違っても末尾0なら等しい）")]
    public void 桁数が違っても値が同じなら等しい()
    {
        UpdateVersion.TryParse("1.0.7", out var a).Should().BeTrue();
        UpdateVersion.TryParse("1.0.7.0", out var b).Should().BeTrue();

        a.Equals(b).Should().BeTrue();
        (a == b).Should().BeTrue();
        a.CompareTo(b).Should().Be(0);
    }

    [Fact(DisplayName = "完全に同じ表記も等しい")]
    public void 完全一致は等しい()
    {
        UpdateVersion.TryParse("1.0.7", out var a).Should().BeTrue();
        UpdateVersion.TryParse("1.0.7", out var b).Should().BeTrue();
        (a == b).Should().BeTrue();
    }

    [Fact(DisplayName = "より古いバージョンはCompareToが負になる")]
    public void より古いバージョンは負になる()
    {
        UpdateVersion.TryParse("1.0.7", out var current).Should().BeTrue();
        UpdateVersion.TryParse("1.0.6", out var older).Should().BeTrue();

        older.CompareTo(current).Should().BeNegative();
        (older < current).Should().BeTrue();
    }

    [Theory(DisplayName = "v接頭辞の有無に関わらず解釈できる")]
    [InlineData("v1.0.7")]
    [InlineData("V1.0.7")]
    [InlineData("1.0.7")]
    public void v接頭辞の有無を許容する(string text)
    {
        UpdateVersion.TryParse(text, out var version).Should().BeTrue();
        version.ToString().Should().Be("1.0.7");
    }

    [Theory(DisplayName = "不正な文字列はTryParseがfalseを返す")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("1.0.x")]
    [InlineData("1..0")]
    [InlineData("v")]
    [InlineData("1.0.7-beta")]
    [InlineData("release-notes")]
    public void 不正な文字列は解釈できない(string? text)
    {
        UpdateVersion.TryParse(text, out _).Should().BeFalse();
    }

    [Fact(DisplayName = "現在のGraft.csprojのVersionと同じ表記でも比較できる（回帰の固定）")]
    public void csprojのバージョン表記と比較できる()
    {
        // Graft.csproj の <Version> はこのテスト作成時点で 1.0.7（3点区切り）。
        // アセンブリのFileVersionは4点区切り（1.0.7.0）へ自動的に補われるため、
        // 両方の表記が正しく比較できることを固定する。
        UpdateVersion.TryParse("1.0.7", out var csprojVersion).Should().BeTrue();
        UpdateVersion.TryParse("1.0.7.0", out var assemblyVersion).Should().BeTrue();
        (csprojVersion == assemblyVersion).Should().BeTrue();

        UpdateVersion.TryParse("v1.0.8", out var nextRelease).Should().BeTrue();
        (nextRelease > assemblyVersion).Should().BeTrue();
    }
}
