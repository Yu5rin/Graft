using FluentAssertions;
using Graft.Features;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// クイックオープン（Ctrl+P）のあいまい一致ロジック <see cref="FuzzyMatcher"/> の単体テスト。
/// 一致判定（サブシーケンス一致・大文字小文字無視）・スコア順（ファイル名先頭一致 &gt;
/// ファイル名内一致 &gt; パスのみ一致、同点はパスが短い順）を検証する。
/// </summary>
public class FuzzyMatcherTests
{
    [Fact(DisplayName = "サブシーケンスとして順序どおり現れれば一致する")]
    public void サブシーケンス一致()
    {
        var result = FuzzyMatcher.TryMatch("svm", "src/ViewModels/ShellViewModel.cs");

        result.IsMatch.Should().BeTrue("s→v→mの順に文字が現れるため一致するはず");
    }

    [Fact(DisplayName = "順序が入れ替わっていると一致しない")]
    public void 順序が違うと不一致()
    {
        // "b" は一致するが、その後ろに続けて "a" は現れないため不一致。
        var result = FuzzyMatcher.TryMatch("ba", "abc");

        result.IsMatch.Should().BeFalse();
    }

    [Fact(DisplayName = "対象に含まれない文字があると一致しない")]
    public void 含まれない文字があると不一致()
    {
        var result = FuzzyMatcher.TryMatch("xyz", "src/ViewModels/ShellViewModel.cs");

        result.IsMatch.Should().BeFalse();
    }

    [Theory(DisplayName = "大文字小文字を区別しない")]
    [InlineData("SVM")]
    [InlineData("svm")]
    [InlineData("SvM")]
    public void 大文字小文字を区別しない(string query)
    {
        var result = FuzzyMatcher.TryMatch(query, "src/ViewModels/ShellViewModel.cs");

        result.IsMatch.Should().BeTrue();
    }

    [Fact(DisplayName = "ファイル名の先頭からの連続一致は最優先（FileNamePrefix）")]
    public void ファイル名先頭一致が最優先()
    {
        var result = FuzzyMatcher.TryMatch("shell", "src/ViewModels/ShellViewModel.cs");

        result.IsMatch.Should().BeTrue();
        result.Tier.Should().Be(FuzzyMatcher.MatchTier.FileNamePrefix);
    }

    [Fact(DisplayName = "ファイル名の先頭ではないがファイル名内で一致する場合はFileNameContains")]
    public void ファイル名内一致()
    {
        // "svm" は ShellViewModel.cs 内で S→V→M の順に現れるが、先頭からの連続一致ではない。
        var result = FuzzyMatcher.TryMatch("svm", "src/ViewModels/ShellViewModel.cs");

        result.Tier.Should().Be(FuzzyMatcher.MatchTier.FileNameContains);
    }

    [Fact(DisplayName = "ファイル名単独では一致せずディレクトリ部分を含めて一致する場合はPathOnly")]
    public void パスのみ一致()
    {
        // "vm" はディレクトリ名 "ViewModels" の中では現れるが、ファイル名 "AppState.cs" 単独では
        // 現れないため、パス全体を含めて初めて一致するPathOnly扱いになる。
        var result = FuzzyMatcher.TryMatch("vm", "src/ViewModels/AppState.cs");

        result.IsMatch.Should().BeTrue();
        result.Tier.Should().Be(FuzzyMatcher.MatchTier.PathOnly);
    }

    [Fact(DisplayName = "ファイル名内一致より先頭一致が優先される（スコア順の並びを数値で検証）")]
    public void スコア順は数値の小さい方が優先()
    {
        var prefix = FuzzyMatcher.TryMatch("shell", "src/ViewModels/ShellViewModel.cs");
        var contains = FuzzyMatcher.TryMatch("svm", "src/ViewModels/ShellViewModel.cs");
        var pathOnly = FuzzyMatcher.TryMatch("vm", "src/ViewModels/AppState.cs");

        ((int)prefix.Tier).Should().BeLessThan((int)contains.Tier);
        ((int)contains.Tier).Should().BeLessThan((int)pathOnly.Tier);
    }

    [Fact(DisplayName = "同点タイの並び順は呼び出し側がRelativePathLength（パスが短い順）で解決できる")]
    public void 同点はパス長で解決できる()
    {
        var shorter = FuzzyMatcher.TryMatch("app", "app.cs");
        var longer = FuzzyMatcher.TryMatch("app", "src/very/deep/nested/app.cs");

        shorter.Tier.Should().Be(longer.Tier, "どちらもファイル名先頭一致で同点のはず");
        shorter.RelativePathLength.Should().BeLessThan(longer.RelativePathLength);
    }

    [Fact(DisplayName = "空のクエリは常に一致する")]
    public void 空クエリは常に一致()
    {
        var result = FuzzyMatcher.TryMatch(string.Empty, "src/ViewModels/ShellViewModel.cs");

        result.IsMatch.Should().BeTrue();
    }
}
