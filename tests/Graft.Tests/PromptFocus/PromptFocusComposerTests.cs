using System.Linq;
using FluentAssertions;
using Graft.Core.PromptFocus;
using Graft.Features;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// <see cref="PromptFocusComposer"/>（選ばれた観点 → AIに渡す1つの文）のテスト。
/// 仕様書_ブックの情報と依頼の観点 6.3・6.4・11.1。
/// </summary>
public class PromptFocusComposerTests
{
    private static IReadOnlyList<PromptFocus> Catalog => PromptFocusStore.BuiltIns;

    private static string Compose(bool isVba, bool hasWorkbookInfo, params string[] ids)
    {
        var selected = PromptFocusComposer.ResolveSelected(Catalog, ids, isVba);
        return PromptFocusComposer.Compose(selected, isVba, hasWorkbookInfo);
    }

    private static int Count(string text, string part)
    {
        var n = 0;
        var i = 0;
        while ((i = text.IndexOf(part, i, StringComparison.Ordinal)) >= 0) { n++; i += part.Length; }
        return n;
    }

    [Fact(DisplayName = "何も選ばなければ空の文字列（依頼文は従来と同じ）")]
    public void 何も選ばなければ空()
    {
        Compose(false, false).Should().BeEmpty();
        Compose(true, true).Should().BeEmpty("VBA プロジェクトでも、何も選ばなければ VBA の前提も付けない");
        PromptFocusComposer.Compose(Array.Empty<PromptFocus>(), true, false).Should().BeEmpty();
    }

    [Fact(DisplayName = "1つ選ぶと、見出し・そのチップの指示文・進め方の決まりが入る")]
    public void 一つ選ぶ()
    {
        var text = Compose(false, false, "focus-readable");

        text.Should().StartWith("# 依頼の観点\n\n## 読みやすく\n");
        text.Should().Contain("- 目的: 読みやすさを上げ、同じ動作をより少ない概念・行数で書く。動作は一切変えない。");
        text.Should().Contain("## 進め方の決まり\n- 観点は、書かれている順に優先する。動作を変えないことと正しさを、見た目より優先する。\n");
        text.Should().EndWith("- コードの前に、変更点と理由を 5 行以内で書く。\n");
        text.Should().NotContain("VBA の前提", "VBA でないプロジェクトには VBA の前提を付けない");
        text.Should().NotContain("字下げは 4 スペース", "VBA 追記は VBA プロジェクトのときだけ");
    }

    [Fact(DisplayName = "複数選んでも、進め方の決まりは1回だけ")]
    public void 複数選んでも決まりは一回()
    {
        var text = Compose(false, false, "focus-readable", "focus-bugfix", "focus-speed");

        Count(text, "## 進め方の決まり").Should().Be(1);
        Count(text, "# 依頼の観点").Should().Be(1, "大見出しも1回");
        text.Should().Contain("## 読みやすく").And.Contain("## 不具合を直す").And.Contain("## 速く");
    }

    [Fact(DisplayName = "画面の並び順で連結する（選んだ順・Id の渡し順には依らない）")]
    public void 画面の並び順で連結する()
    {
        var forward = Compose(false, false, "focus-readable", "focus-bugfix", "focus-speed");
        var reversed = Compose(false, false, "focus-speed", "focus-bugfix", "focus-readable");

        reversed.Should().Be(forward, "同じ選択なら、いつも同じ依頼文になる");
        var i1 = forward.IndexOf("## 読みやすく", StringComparison.Ordinal);
        var i2 = forward.IndexOf("## 不具合を直す", StringComparison.Ordinal);
        var i3 = forward.IndexOf("## 速く", StringComparison.Ordinal);
        (i1 < i2 && i2 < i3).Should().BeTrue("きれいに → 正しく → 品質 の順");
    }

    [Fact(DisplayName = "VBA プロジェクトでは VBA の前提が1回だけ付き、各チップの末尾に VBA 追記が続く")]
    public void VBAの前提と追記()
    {
        var text = Compose(true, false, "focus-readable", "focus-maintainable");

        Count(text, "## VBA の前提").Should().Be(1);
        text.Should().Contain("- これは Excel VBA です。各ファイルは VBE のモジュール本文で、Attribute 行やエクスポート用のヘッダは含みません。");
        // VBA の前提は、チップの指示文より前。
        text.IndexOf("## VBA の前提", StringComparison.Ordinal)
            .Should().BeLessThan(text.IndexOf("## 読みやすく", StringComparison.Ordinal));

        // 追記は該当チップの末尾に、見出しなしの箇条書きとして続く（空行を挟まない）。
        text.Should().Contain("- 既存の書き方（命名規則・括弧の位置・引用符）に合わせ、ファイルの中で書き方を混在させない。\n- 字下げは 4 スペースにそろえてください。\n\n## 保守しやすく");
        text.Should().Contain("- 同じ処理を複数のモジュールが持っている場合は、標準モジュールの Public 関数に寄せてください。\n\n## 進め方の決まり");
    }

    [Fact(DisplayName = "追記を持たないチップ（変更は最小限）には、VBA プロジェクトでも追記が付かない")]
    public void 追記の無いチップ()
    {
        var generic = Compose(false, false, "focus-minimal");
        var vba = Compose(true, false, "focus-minimal");

        // VBA の前提を取り除けば、チップの部分は同じ。
        vba.Replace(vba[vba.IndexOf("## VBA の前提", StringComparison.Ordinal)..vba.IndexOf("## 変更は最小限", StringComparison.Ordinal)], string.Empty)
            .Should().Be(generic);
    }

    [Fact(DisplayName = "VBA 専用のチップは、VBA でないプロジェクトでは落とす")]
    public void VBA専用は非VBAで落ちる()
    {
        var nonVba = PromptFocusComposer.ResolveSelected(Catalog, new[] { "focus-vba-64bit", "focus-readable" }, isVba: false);
        nonVba.Select(f => f.Id).Should().Equal("focus-readable");

        var onlyVba = Compose(false, false, "focus-vba-guard");
        onlyVba.Should().BeEmpty("VBA 専用だけを選んでいても、VBA でなければ何も入らない");

        var vba = Compose(true, false, "focus-vba-64bit");
        vba.Should().Contain("## 64bit 版 Office に対応する");
    }

    [Fact(DisplayName = "カタログに無い Id（削除された自作の観点）は黙って捨てる")]
    public void 未知のIdは捨てる()
    {
        var resolved = PromptFocusComposer.ResolveSelected(Catalog, new[] { "custom-gone", "focus-docs" }, isVba: false);

        resolved.Select(f => f.Id).Should().Equal("focus-docs");
    }

    [Fact(DisplayName = "ブックの情報の有無で、VBA の前提の最終行が切り替わる")]
    public void ブックの情報の有無で最終行が変わる()
    {
        var with = Compose(true, true, "focus-readable");
        var without = Compose(true, false, "focus-readable");

        with.Should().Contain("- シート名・テーブル名・列は、「ブックの情報」に載っているものだけを使ってください。");
        with.Should().NotContain("確かめられないものを推測で使わず");
        without.Should().Contain("- シート名・テーブル名・列は、確かめられないものを推測で使わず、質問してください。");
        without.Should().NotContain("「ブックの情報」に載っている");
    }

    [Fact(DisplayName = "自作の観点は、見出しの順に並び、同じ見出しの中では組み込みの後ろに来る")]
    public void 自作の観点の並び()
    {
        var mine = new PromptFocus { Id = "custom-a", Group = PromptFocusGroups.Mine, Name = "自作A", Body = "## 自作A\n- a" };
        var inClean = new PromptFocus { Id = "custom-b", Group = PromptFocusGroups.Clean, Name = "自作B", Body = "## 自作B\n- b" };
        var catalog = PromptFocusStore.BuiltIns.Concat(new[] { mine, inClean }).ToArray();

        var order = PromptFocusComposer.OrderForDisplay(catalog).Select(f => f.Id).ToArray();

        order.Last().Should().Be("custom-a", "「自分用」は末尾");
        var minimal = Array.IndexOf(order, "focus-minimal");
        var bugfix = Array.IndexOf(order, "focus-bugfix");
        var inCleanAt = Array.IndexOf(order, "custom-b");
        (minimal < inCleanAt && inCleanAt < bugfix).Should().BeTrue("「きれいに」の見出しの中では組み込みの後ろ、「正しく」より前");
    }

    [Fact(DisplayName = "観点の文は LF だけで組み立てる（環境で依頼文のバイト列が変わらない）")]
    public void LFだけ()
    {
        Compose(true, false, "focus-readable", "focus-vba-guard").Should().NotContain("\r");
    }
}
