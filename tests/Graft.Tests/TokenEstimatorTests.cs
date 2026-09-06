using FluentAssertions;
using Graft.Features;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 異常系点検「中」3件目（トークン概算がintオーバーフローで負数になる）の回帰テスト。
///
/// 修正前は<see cref="TokenEstimator.EstimateLength"/>が<c>(int)Math.Ceiling(length /
/// effectiveRatio)</c>で戻り値を作っており、<c>length</c>が非常に大きい・
/// <c>effectiveRatio</c>が極端に小さい場合にint.MaxValueを超えて負数へラップしていた
/// （実測: <c>EstimateLength(long.MaxValue)</c>・<c>EstimateLength(6_000_000_000)</c>・
/// <c>EstimateLength(10_000_000_000, 0.0001)</c>のいずれも-2147483648を返すことを確認済み）。
/// </summary>
public class TokenEstimatorTests
{
    [Fact(DisplayName = "不具合3回帰: EstimateLength(long.MaxValue)は負数にならず、int.MaxValueへ丸められる")]
    public void long最大値は負数にならない()
    {
        var result = TokenEstimator.EstimateLength(long.MaxValue);

        result.Should().BePositive("修正前はint桁あふれで-2147483648を返していた");
        result.Should().Be(int.MaxValue);
    }

    [Fact(DisplayName = "不具合3回帰: EstimateLength(6_000_000_000)は負数にならず、int.MaxValueへ丸められる")]
    public void 巨大なバイト数は負数にならない()
    {
        // 6_000_000_000 / 2.5(既定ratio) = 2_400_000_000 > int.MaxValue(約21億)
        var result = TokenEstimator.EstimateLength(6_000_000_000L);

        result.Should().BePositive("修正前はint桁あふれで-2147483648を返していた");
        result.Should().Be(int.MaxValue);
    }

    [Fact(DisplayName = "不具合3回帰: EstimateLength(10_000_000_000, 0.0001)は負数にならず、int.MaxValueへ丸められる")]
    public void 極端に小さいratioでも負数にならない()
    {
        // 不具合2（設定値に上限が無い）と組み合わさって context.tokenRatio に極端に小さい値
        // （0.0001等）が入ると、通常サイズのコンテキストでも桁あふれしうることの再現。
        var result = TokenEstimator.EstimateLength(10_000_000_000L, 0.0001);

        result.Should().BePositive("修正前はint桁あふれで-2147483648を返していた");
        result.Should().Be(int.MaxValue);
    }

    [Fact(DisplayName = "通常サイズの入力では既定の比率どおりの値をそのまま返す（回帰: 丸め処理の追加が通常ケースを壊していないこと）")]
    public void 通常サイズは丸め処理の影響を受けない()
    {
        TokenEstimator.EstimateLength(1000, 2.5).Should().Be(400);
        TokenEstimator.EstimateLength(0).Should().Be(0);
        TokenEstimator.EstimateLength(-100).Should().Be(0, "0以下の長さは0を返す既存の挙動を維持する");
    }

    [Fact(DisplayName = "Estimate(string, ratio)経由でも桁あふれしない（文字列長はint範囲内のためEstimateLengthへそのまま委譲できる）")]
    public void 文字列版でも同じ丸めが働く()
    {
        var result = TokenEstimator.Estimate("a", ratio: 1e-10);

        result.Should().BePositive();
        result.Should().Be(int.MaxValue);
    }
}
