using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using Graft.Core;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 仕様書5章（マッチングエンジン）の単体テスト。段階1〜6それぞれの成功・失敗例、
/// 5.1の複数候補（OCCURRENCE）、5.2のインデント補正規則（タブ・スペース混在を含む）を検証する。
/// </summary>
public class MatchEngineTests
{
    private static string LoadFixture(string fileName)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Sources", fileName));

    private static SearchReplacePair Pair(string search, string replace)
        => new() { SearchText = search, ReplaceText = replace, SourceLine = 1 };

    // ---- 段階1: 完全一致 ----

    [Fact(DisplayName = "段階1: SEARCH部が完全一致する場合はExactでマッチする")]
    public void 段階1_完全一致でマッチする()
    {
        var original = LoadFixture("greet_exact.py");
        var pair = Pair(
            "    message = \"Hello, \" + name\n    return message",
            "    message = f\"Hello, {name}!\"\n    return message");

        var engine = new MatchEngine();
        var result = engine.Match(original, pair, OccurrenceSpec.Single);

        result.IsSuccess.Should().BeTrue();
        var match = result.Value.Single();
        match.Stage.Should().Be(MatchStage.Exact);
        match.StartLine.Should().Be(1);
        match.LineCount.Should().Be(2);
        match.AppliedReplacement.Should().Be(pair.ReplaceText);
        match.NeedsConfirmation.Should().BeFalse();
    }

    [Fact(DisplayName = "段階1: 存在しないSEARCH部かつ類似度も届かない場合は完全一致しない")]
    public void 段階1_内容が異なる場合は完全一致しない()
    {
        var original = LoadFixture("greet_exact.py");
        // 内容そのものが原文に存在しないため、段階1〜4はもちろん段階5の閾値にも届かず失敗する。
        var pair = Pair(
            "    completely_unrelated_statement_that_does_not_exist_anywhere()",
            "    replaced()");

        var engine = new MatchEngine();
        var result = engine.Match(original, pair, OccurrenceSpec.Single);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Single().Code.Should().Be(ErrorCode.E101);
    }

    // ---- 段階2: 行末空白のみ差異 ----

    [Fact(DisplayName = "段階2: 行末の空白・タブのみが異なる場合はTrailingWhitespaceでマッチする")]
    public void 段階2_行末空白差でマッチする()
    {
        // 原文の行末に意図的な空白・タブを残すため、フィクスチャファイルではなく
        // コード中の1行の文字列リテラルとして組み立てる（\n・\tはエスケープ表記なので
        // 物理行末には現れず、保存時に空白除去される心配がない）。
        var original = "def total(a, b):\n    result = a + b   \n    return result\t\n";
        var pair = Pair(
            "    result = a + b\n    return result",
            "    result = a + b + 1\n    return result");

        var engine = new MatchEngine();
        var result = engine.Match(original, pair, OccurrenceSpec.Single);

        result.IsSuccess.Should().BeTrue();
        var match = result.Value.Single();
        match.Stage.Should().Be(MatchStage.TrailingWhitespace);
        match.StartLine.Should().Be(1);
        match.LineCount.Should().Be(2);
        match.AppliedReplacement.Should().Be(pair.ReplaceText);
    }

    // ---- 段階3: 先頭インデント量のみ差異（タブ・スペース混在を含む） ----

    [Fact(DisplayName = "段階3: 元ファイルがタブ・SEARCH部がスペースでも相対インデントが一致すればマッチし、適用時はタブに揃う")]
    public void 段階3_タブファイルにスペースSEARCHがマッチしタブへ補正される()
    {
        var original = LoadFixture("indent_tabs.py");
        var pair = Pair(
            "if items:\n total = sum(items)\n return total",
            "if items:\n total = sum(items) * 2\n return total");

        var engine = new MatchEngine();
        var result = engine.Match(original, pair, OccurrenceSpec.Single);

        result.IsSuccess.Should().BeTrue();
        var match = result.Value.Single();
        match.Stage.Should().Be(MatchStage.RelativeIndent);
        match.StartLine.Should().Be(1);
        match.LineCount.Should().Be(3);
        match.AppliedReplacement.Should().Be("\tif items:\n\t\ttotal = sum(items) * 2\n\t\treturn total");
    }

    [Fact(DisplayName = "段階3: 元ファイルがスペース・SEARCH部がタブでも相対インデントが一致すればマッチし、適用時はスペースに揃う")]
    public void 段階3_スペースファイルにタブSEARCHがマッチしスペースへ補正される()
    {
        var original = LoadFixture("indent_spaces.py");
        var pair = Pair(
            "if items:\n\ttotal = sum(items)\n\treturn total",
            "if items:\n\ttotal = sum(items) * 2\n\treturn total");

        var engine = new MatchEngine();
        var result = engine.Match(original, pair, OccurrenceSpec.Single);

        result.IsSuccess.Should().BeTrue();
        var match = result.Value.Single();
        match.Stage.Should().Be(MatchStage.RelativeIndent);
        match.StartLine.Should().Be(1);
        match.LineCount.Should().Be(3);
        match.AppliedReplacement.Should().Be(" if items:\n  total = sum(items) * 2\n  return total");
    }

    [Fact(DisplayName = "段階3: インデント量以外にも差異がある場合はマッチしない")]
    public void 段階3_インデント以外の差異があるとマッチしない()
    {
        var original = LoadFixture("indent_tabs.py");
        // 内容自体（識別子）が違うため相対インデントが一致してもマッチしない。
        var pair = Pair(
            "if items:\n total = sum(different_items)\n return total",
            "pass");

        var engine = new MatchEngine();
        var result = engine.Match(original, pair, OccurrenceSpec.Single);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Single().Code.Should().Be(ErrorCode.E101);
    }

    // ---- 段階4: 空行のみ差異 ----

    [Fact(DisplayName = "段階4: SEARCH部が空行を省略していてもIgnoreBlankLinesでマッチする")]
    public void 段階4_空行無視でマッチする()
    {
        var original = LoadFixture("blank_lines.py");
        var pair = Pair(
            "    total = x + y\n    average = total / 2",
            "    total = x + y\n    average = total // 2");

        var engine = new MatchEngine();
        var result = engine.Match(original, pair, OccurrenceSpec.Single);

        result.IsSuccess.Should().BeTrue();
        var match = result.Value.Single();
        match.Stage.Should().Be(MatchStage.IgnoreBlankLines);
        match.StartLine.Should().Be(1);
        // 間に挟まる空行1行を含むため3行分になる。
        match.LineCount.Should().Be(3);
        match.AppliedReplacement.Should().Be(pair.ReplaceText);
    }

    // ---- 段階5: わずかな文字差（類似度） ----

    [Fact(DisplayName = "段階5: 類似度が閾値以上ならSimilarityでマッチし要確認扱いになる")]
    public void 段階5_閾値以上の類似度でマッチする()
    {
        var original = LoadFixture("similarity_target.py");
        var pair = Pair(
            "    if order is None:\n" +
            "        return False\n" +
            "    if not order.items:\n" +
            "        return False\n" +
            "    if order.total < 0:\n" +
            "        return False\n" +
            "    return True",
            "    if order is None:\n        raise ValueError(\"invalid order\")");

        var engine = new MatchEngine(new MatchOptions { SimilarityThreshold = 0.85, AllowSimilarityMatch = true });
        var result = engine.Match(original, pair, OccurrenceSpec.Single);

        result.IsSuccess.Should().BeTrue();
        var match = result.Value.Single();
        match.Stage.Should().Be(MatchStage.Similarity);
        match.StartLine.Should().Be(1);
        match.LineCount.Should().Be(7);
        match.Similarity.Should().BeApproximately(6.0 / 7.0, 0.0001);
        match.NeedsConfirmation.Should().BeTrue();
        match.AppliedReplacement.Should().Be(pair.ReplaceText);
    }

    [Fact(DisplayName = "段階5: 類似度が閾値未満ならマッチせずE101になる")]
    public void 段階5_閾値未満の類似度ではマッチしない()
    {
        var original = LoadFixture("similarity_target.py");
        var pair = Pair(
            "    if order is None:\n" +
            "        return None\n" +
            "    if not order.items:\n" +
            "        return False",
            "    pass");

        var engine = new MatchEngine(new MatchOptions { SimilarityThreshold = 0.85, AllowSimilarityMatch = true });
        var result = engine.Match(original, pair, OccurrenceSpec.Single);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Single().Code.Should().Be(ErrorCode.E101);
    }

    [Fact(DisplayName = "段階5: allowSimilarityMatch=falseの場合は類似度を試さずE101になる")]
    public void allowSimilarityMatchがfalseなら段階5を試さない()
    {
        var original = LoadFixture("similarity_target.py");
        // 単体では段階5（既定閾値0.85）でマッチするはずの入力。
        var pair = Pair(
            "    if order is None:\n" +
            "        return False\n" +
            "    if not order.items:\n" +
            "        return False\n" +
            "    if order.total < 0:\n" +
            "        return False\n" +
            "    return True",
            "    pass");

        var engine = new MatchEngine(new MatchOptions { SimilarityThreshold = 0.85, AllowSimilarityMatch = false });
        var result = engine.Match(original, pair, OccurrenceSpec.Single);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Single().Code.Should().Be(ErrorCode.E101);
    }

    // ---- 段階6: 該当なし ----

    [Fact(DisplayName = "段階6: どの段階にも該当しない場合はE101になる")]
    public void 段階6_該当なしはE101になる()
    {
        var original = LoadFixture("no_match.py");
        var pair = Pair("class Something:\n    pass", "class Something:\n    pass  # 変更後");

        var engine = new MatchEngine();
        var result = engine.Match(original, pair, OccurrenceSpec.Single);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Single().Code.Should().Be(ErrorCode.E101);
    }

    // ---- 5.1 複数候補 ----

    [Fact(DisplayName = "5.1: OCCURRENCE未指定で複数マッチした場合は既定でE102になる")]
    public void 複数候補_既定はE102になる()
    {
        var original = LoadFixture("repeated_block.py");
        var pair = Pair(
            "    log_event(\"start\")\n    process()\n    log_event(\"end\")",
            "    log_event(\"start\")\n    process()\n    log_event(\"done\")");

        var engine = new MatchEngine();
        var result = engine.Match(original, pair, OccurrenceSpec.Single);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Single().Code.Should().Be(ErrorCode.E102);
    }

    [Fact(DisplayName = "5.1: OCCURRENCE=2を指定すると2番目のマッチが選ばれる")]
    public void 複数候補_OCCURRENCE指定で2番目が選ばれる()
    {
        var original = LoadFixture("repeated_block.py");
        var pair = Pair(
            "    log_event(\"start\")\n    process()\n    log_event(\"end\")",
            "    log_event(\"start\")\n    process()\n    log_event(\"done\")");

        var engine = new MatchEngine();
        var occurrence = new OccurrenceSpec { Index = 2, All = false };
        var result = engine.Match(original, pair, occurrence);

        result.IsSuccess.Should().BeTrue();
        var match = result.Value.Single();
        match.StartLine.Should().Be(7);
    }

    [Fact(DisplayName = "5.1: OCCURRENCE=ALLを指定すると全件がStartLine降順で返る")]
    public void 複数候補_OCCURRENCE_ALLで全件が降順で返る()
    {
        var original = LoadFixture("repeated_block.py");
        var pair = Pair(
            "    log_event(\"start\")\n    process()\n    log_event(\"end\")",
            "    log_event(\"start\")\n    process()\n    log_event(\"done\")");

        var engine = new MatchEngine();
        var occurrence = new OccurrenceSpec { All = true };
        var result = engine.Match(original, pair, occurrence);

        result.IsSuccess.Should().BeTrue();
        result.Value.Select(m => m.StartLine).Should().Equal(13, 7, 1);
    }
}
