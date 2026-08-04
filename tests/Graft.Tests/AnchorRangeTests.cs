using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using Graft.Core;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 仕様書4.4（アンカー省略記法 SEARCH-RANGE）の単体テスト。
/// 正常な範囲解決、終了アンカー欠落（E103）、"..." 行の欠落・重複（E002）、
/// 範囲行数の閾値超過警告、アンカー自体への段階2〜4フォールバックを検証する。
/// </summary>
public class AnchorRangeTests
{
    private const int DefaultWarningLines = 300;

    private static string LoadFixture(string fileName)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Sources", fileName));

    private static string[] Lines(string text) => TextNormalizer.SplitLines(text).ToArray();

    [Fact(DisplayName = "開始・終了アンカーが完全一致する場合は正しい範囲を解決する")]
    public void 正常な範囲解決()
    {
        var fileLines = Lines(LoadFixture("anchor_target.py"));
        var searchText = "    def render(self):\n...\n        return lines";

        var result = AnchorRangeResolver.Resolve(fileLines, searchText, DefaultWarningLines);

        result.IsSuccess.Should().BeTrue();
        result.Value.StartLine.Should().Be(8);
        result.Value.LineCount.Should().Be(5);
        result.Value.Stage.Should().Be(MatchStage.Exact);
        result.Issues.Should().BeEmpty();
    }

    [Fact(DisplayName = "終了アンカーが見つからない場合はE103になる")]
    public void 終了アンカーが見つからない場合はE103()
    {
        var fileLines = Lines(LoadFixture("anchor_target.py"));
        var searchText = "    def render(self):\n...\n        return nonexistent_variable";

        var result = AnchorRangeResolver.Resolve(fileLines, searchText, DefaultWarningLines);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Single().Code.Should().Be(ErrorCode.E103);
    }

    [Fact(DisplayName = "開始アンカーが見つからない場合はE101になる")]
    public void 開始アンカーが見つからない場合はE101()
    {
        var fileLines = Lines(LoadFixture("anchor_target.py"));
        var searchText = "    def nonexistent_method(self):\n...\n        return lines";

        var result = AnchorRangeResolver.Resolve(fileLines, searchText, DefaultWarningLines);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Single().Code.Should().Be(ErrorCode.E101);
    }

    [Fact(DisplayName = "\"...\" 行が無い場合はE002になる")]
    public void アンカー区切り行が無い場合はE002()
    {
        var fileLines = Lines(LoadFixture("anchor_target.py"));
        var searchText = "    def render(self):\n        return lines";

        var result = AnchorRangeResolver.Resolve(fileLines, searchText, DefaultWarningLines);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Single().Code.Should().Be(ErrorCode.E002);
    }

    [Fact(DisplayName = "\"...\" 行が複数ある場合はE002になる")]
    public void アンカー区切り行が複数ある場合はE002()
    {
        var fileLines = Lines(LoadFixture("anchor_target.py"));
        var searchText = "...\n    def render(self):\n...\n        return lines";

        var result = AnchorRangeResolver.Resolve(fileLines, searchText, DefaultWarningLines);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Single().Code.Should().Be(ErrorCode.E002);
    }

    [Fact(DisplayName = "開始・終了どちらかのアンカーが空になる場合もE002になる")]
    public void アンカーの片側が空の場合もE002()
    {
        var fileLines = Lines(LoadFixture("anchor_target.py"));
        // "..." が先頭行のため開始アンカーが空になる。
        var searchText = "...\n        return lines";

        var result = AnchorRangeResolver.Resolve(fileLines, searchText, DefaultWarningLines);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Single().Code.Should().Be(ErrorCode.E002);
    }

    [Fact(DisplayName = "範囲行数が閾値を超える場合は警告付きで成功する（E104）")]
    public void 範囲行数が閾値超過で警告付き成功()
    {
        var fileLines = Lines(LoadFixture("anchor_target.py"));
        var searchText = "    def render(self):\n...\n        return lines";

        // レンダーメソッドの範囲は5行なので、閾値を3に下げて超過させる。
        var result = AnchorRangeResolver.Resolve(fileLines, searchText, rangeWarningLines: 3);

        result.IsSuccess.Should().BeTrue("行数超過は警告であり致命的失敗ではないため");
        result.Value.LineCount.Should().Be(5);
        result.Issues.Should().ContainSingle();
        var warning = result.Issues.Single();
        warning.Severity.Should().Be(Severity.Warning);
        warning.Code.Should().Be(ErrorCode.E104,
            "仕様書4.4の範囲超過警告はE104であるべき（仕様書16章の表には無いためErrorCodes.cs側でE104として追加されている）");
    }

    [Fact(DisplayName = "アンカー自体に空行無視（段階4）のフォールバックが効く")]
    public void アンカーに段階4のフォールバックが効く()
    {
        var fileLines = Lines(LoadFixture("anchor_target.py"));
        // 開始アンカーは実際のファイルでは間に空行を挟む2行。SEARCH側はその空行を省略している。
        var searchText = "        self.rows = []\n    def add_row(self, row):\n...\n" + "return lines";

        var result = AnchorRangeResolver.Resolve(fileLines, searchText, DefaultWarningLines);

        result.IsSuccess.Should().BeTrue();
        result.Value.StartLine.Should().Be(3);
        result.Value.LineCount.Should().Be(10);
        result.Value.Stage.Should().Be(MatchStage.IgnoreBlankLines,
            "開始アンカー側が段階4（空行無視）でのみマッチするため、より緩い方の段階が採用される");
    }

    [Fact(DisplayName = "アンカー自体に行末空白無視（段階2）・相対インデント無視（段階3）のフォールバックが効く")]
    public void アンカーに段階2と段階3のフォールバックが効く()
    {
        var fileLines = Lines(LoadFixture("anchor_target.py"));
        // 開始アンカーは行末に余分な空白（段階2）、終了アンカーはインデントを持たない（段階3で
        // 単一行の場合は相対インデントが常に0同士となるため実質インデント無視でマッチする）。
        var searchText = "    def render(self):  \n...\nreturn lines";

        var result = AnchorRangeResolver.Resolve(fileLines, searchText, DefaultWarningLines);

        result.IsSuccess.Should().BeTrue();
        result.Value.StartLine.Should().Be(8);
        result.Value.LineCount.Should().Be(5);
        result.Value.Stage.Should().Be(MatchStage.RelativeIndent,
            "開始アンカーは段階2、終了アンカーは段階3でマッチするため、より緩い段階3が採用される");
    }

    [Fact(DisplayName = "MatchEngine経由でもSEARCH-RANGEの範囲解決が結果に反映される")]
    public void MatchEngine経由でのアンカー範囲解決()
    {
        var original = LoadFixture("anchor_target.py");
        var pair = new SearchReplacePair
        {
            SearchText = "    def render(self):\n...\n        return lines",
            ReplaceText = "    def render(self):\n        return list(self.rows)",
            IsRange = true,
            SourceLine = 1,
        };

        var engine = new MatchEngine();
        var result = engine.Match(original, pair, OccurrenceSpec.Single);

        result.IsSuccess.Should().BeTrue();
        var match = result.Value.Single();
        match.StartLine.Should().Be(8);
        match.LineCount.Should().Be(5);
        match.AppliedReplacement.Should().Be(pair.ReplaceText);
    }
}
