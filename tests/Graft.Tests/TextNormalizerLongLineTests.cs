using FluentAssertions;
using Graft.Core;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 課題3（1行が極端に長いファイルを開くと遅い）の判定ロジック
/// <see cref="TextNormalizer.HasLineLongerThan"/> の単体テスト。
/// </summary>
public class TextNormalizerLongLineTests
{
    [Fact(DisplayName = "しきい値以下の行だけならfalse")]
    public void しきい値以下ならfalse()
    {
        var text = string.Join('\n', Enumerable.Repeat(new string('x', 100), 50));

        TextNormalizer.HasLineLongerThan(text, 200).Should().BeFalse();
    }

    [Fact(DisplayName = "しきい値を超える行が1つでもあればtrue")]
    public void しきい値超過でtrue()
    {
        var text = "短い行\n" + new string('x', 300) + "\n短い行";

        TextNormalizer.HasLineLongerThan(text, 200).Should().BeTrue();
    }

    [Fact(DisplayName = "ちょうどしきい値の長さの行はfalse（超過のみtrue）")]
    public void ちょうどしきい値なら含まない()
    {
        var text = new string('x', 200);

        TextNormalizer.HasLineLongerThan(text, 200).Should().BeFalse();
        TextNormalizer.HasLineLongerThan(text, 199).Should().BeTrue();
    }

    [Fact(DisplayName = "CRLF・LF・CRのいずれも行区切りとして扱う")]
    public void 改行コードの種類によらず判定する()
    {
        var longLine = new string('x', 300);

        TextNormalizer.HasLineLongerThan($"a\r\n{longLine}\r\nb", 200).Should().BeTrue();
        TextNormalizer.HasLineLongerThan($"a\r{longLine}\rb", 200).Should().BeTrue();
        TextNormalizer.HasLineLongerThan($"a\n{longLine}\nb", 200).Should().BeTrue();
    }

    [Fact(DisplayName = "空文字列はfalse")]
    public void 空文字列はfalse()
    {
        TextNormalizer.HasLineLongerThan(string.Empty, 200).Should().BeFalse();
    }

    [Fact(DisplayName = "課題3の再現ファイル相当（10万文字の1行）を実運用のしきい値20,000で検知する")]
    public void 実運用のしきい値で検知する()
    {
        // 20,000はDocumentSession.LongLineThreshold（EditorはこのGraft.Testsから参照できないため
        // 値を直接記述する。Graft.UiTests側でDocumentSession経由の検証を行う）。
        var text = "class L { /* " + new string('x', 100_000) + " */ }\n";

        TextNormalizer.HasLineLongerThan(text, 20_000).Should().BeTrue();
    }
}
