using FluentAssertions;
using Graft.Core;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// クリップボード監視（仕様書9章・10章）の「パッチらしさ」判定。
/// WindowsとLinuxの監視実装が共用するため、判定基準をここで固定する。
/// </summary>
public class PatchTextDetectorTests
{
    [Theory(DisplayName = "ブロックヘッダを含むテキストはパッチとみなす")]
    [InlineData("<<<< FILE: src/a.cs")]
    [InlineData("<<<< PATCH")]
    [InlineData("<<<< DELETE: src/a.cs")]
    [InlineData("<<<< RENAME: a.cs -> b.cs")]
    [InlineData("<<<< MKDIR: src/new")]
    [InlineData("<<<< APPEND: src/a.cs")]
    [InlineData("<<<< PREPEND: src/a.cs")]
    [InlineData("<<<<<<< SEARCH")]
    public void ブロックヘッダを含むテキストはパッチとみなす(string header)
        => PatchTextDetector.LooksLikePatch(header).Should().BeTrue();

    [Fact(DisplayName = "先頭以外の行にヘッダがあっても検知する")]
    public void 途中の行のヘッダも検知する()
        => PatchTextDetector.LooksLikePatch("説明文\n\n<<<< FILE: src/a.cs\n").Should().BeTrue();

    [Fact(DisplayName = "行頭以外に現れるヘッダらしき文字列は検知しない")]
    public void 行の途中のヘッダは検知しない()
        => PatchTextDetector.LooksLikePatch("この記法 <<<< FILE: について説明します").Should().BeFalse();

    [Theory(DisplayName = "通常のコピー内容はパッチとみなさない")]
    [InlineData("")]
    [InlineData("ふつうの文章")]
    [InlineData("var x = 1;")]
    public void 通常のテキストは検知しない(string text)
        => PatchTextDetector.LooksLikePatch(text).Should().BeFalse();
}
