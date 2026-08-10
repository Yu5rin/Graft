using FluentAssertions;
using Graft.Core;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// C: 差分表示・接ぎ木ブロックの右クリックメニュー「unified diff 形式でコピー」用の
/// <see cref="UnifiedDiffFormatter"/>の回帰テスト。
///
/// 最重要の確認事項は、出力が既存の取り込み側<see cref="UnifiedDiffAdapter"/>でそのまま
/// 解析できること（AIに貼り戻して再度読み込ませる往復が成立すること）。行番号の厳密な正しさは
/// <see cref="UnifiedDiffAdapter"/>が一切解釈しない（クラスコメント参照）ため対象外とし、
/// 内容（SEARCH/REPLACE相当のテキスト）が変更前後で正しく復元できることを確認する。
/// </summary>
public class UnifiedDiffFormatterTests
{
    [Fact(DisplayName = "変更ブロックの出力はUnifiedDiffAdapterで解析でき、変更前後のテキストが一致する")]
    public void 変更ブロックの出力はUnifiedDiffAdapterで解析できる()
    {
        var before = "1行目\n2行目\n3行目\n4行目\n5行目\n";
        var after = "1行目\n2行目（変更後）\n3行目\n4行目\n5行目\n";

        var text = UnifiedDiffFormatter.Format("sample.txt", before, after);

        text.Should().StartWith("--- a/sample.txt\n+++ b/sample.txt\n");
        text.Should().Contain("@@").And.Contain("-2行目\n").And.Contain("+2行目（変更後）\n");

        var parsed = UnifiedDiffAdapter.Parse(text);
        parsed.IsSuccess.Should().BeTrue("整形した内容が既存のunified diff取り込みで解析できる必要がある");

        var block = parsed.Value.Blocks.Should().ContainSingle().Which.Should().BeOfType<SearchReplaceBlock>().Subject;
        var pair = block.Pairs.Should().ContainSingle().Subject;
        pair.SearchText.Should().Contain("2行目");
        pair.ReplaceText.Should().Contain("2行目（変更後）");
    }

    [Fact(DisplayName = "新規作成（変更前が無い）はヘッダが/dev/nullになり、FULL形式として解析できる")]
    public void 新規作成の出力は解析できる()
    {
        var after = "はじめまして\n2行目\n";

        var text = UnifiedDiffFormatter.Format("new.txt", null, after);

        text.Should().StartWith("--- /dev/null\n+++ b/new.txt\n");

        var parsed = UnifiedDiffAdapter.Parse(text);
        parsed.IsSuccess.Should().BeTrue();
        var block = parsed.Value.Blocks.Should().ContainSingle().Which.Should().BeOfType<FullContentBlock>().Subject;
        block.Content.Should().Be("はじめまして\n2行目");
    }

    [Fact(DisplayName = "削除（変更後が無い）はヘッダが/dev/nullになり、DELETE操作として解析できる")]
    public void 削除の出力は解析できる()
    {
        var before = "消える内容\n2行目\n";

        var text = UnifiedDiffFormatter.Format("gone.txt", before, null);

        text.Should().Contain("+++ /dev/null\n");

        var parsed = UnifiedDiffAdapter.Parse(text);
        parsed.IsSuccess.Should().BeTrue();
        parsed.Value.Blocks.Should().ContainSingle().Which.Should().BeOfType<DeleteBlock>()
            .Which.Path.Should().Be("gone.txt");
    }

    [Fact(DisplayName = "遠く離れた2箇所の変更は別々のハンクに分かれ、両方とも解析できる")]
    public void 離れた変更は別ハンクになり両方解析できる()
    {
        var beforeLines = Enumerable.Range(1, 40).Select(i => $"L{i}").ToArray();
        var afterLines = (string[])beforeLines.Clone();
        afterLines[2] = "L3-変更";
        afterLines[35] = "L36-変更";

        var before = string.Join('\n', beforeLines) + "\n";
        var after = string.Join('\n', afterLines) + "\n";

        var text = UnifiedDiffFormatter.Format("far.txt", before, after);

        // 十分に離れているため、折りたたみ既定の文脈行数(3)を超えて2つの@@ハンクに分かれるはず。
        text.Split("@@ -").Length.Should().Be(3, "ヘッダ以外に2つのハンクが無いとおかしい（分割数=ハンク数+1）");

        var parsed = UnifiedDiffAdapter.Parse(text);
        parsed.IsSuccess.Should().BeTrue();
        var block = parsed.Value.Blocks.Should().ContainSingle().Which.Should().BeOfType<SearchReplaceBlock>().Subject;
        block.Pairs.Should().HaveCount(2);
        block.Pairs.Select(p => p.ReplaceText).Should().Contain(p => p.Contains("L3-変更"));
        block.Pairs.Select(p => p.ReplaceText).Should().Contain(p => p.Contains("L36-変更"));
    }

    [Fact(DisplayName = "変更が無い場合は空のdiff（ヘッダのみ）になる")]
    public void 変更が無い場合はヘッダのみになる()
    {
        var text = UnifiedDiffFormatter.Format("same.txt", "同じ\n", "同じ\n");

        text.Should().Be("--- a/same.txt\n+++ b/same.txt\n");
    }
}
