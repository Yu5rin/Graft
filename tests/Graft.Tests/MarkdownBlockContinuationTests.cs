using FluentAssertions;
using Graft.Editor;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// Markdown編集支援（検討書「Markdownの編集支援」）のリスト・引用のEnter継続/脱出判定
/// （<see cref="MarkdownBlockContinuation"/>）のテスト。
///
/// 【最重要】Pane実機で実際に利用者のデータが壊れた不具合の再発防止テストを含む
/// （<c>CommonMarkの遅延継続で本文の先頭を引用マーカーと誤認しない</c>・
/// <c>引用から完全に抜けるときは空行を挟む</c>）。これらは他のテストより優先して
/// 壊さないこと。
/// </summary>
public class MarkdownBlockContinuationTests
{
    // ========== リスト継続（Enter、中身のある項目） ==========

    [Fact(DisplayName = "箇条書きの項目でEnterすると同じマーカーで継続する")]
    public void 箇条書きの継続()
    {
        MarkdownBlockContinuation.ComputeListContinuationMarker("- 項目1").Should().Be("- ");
    }

    [Fact(DisplayName = "番号付きリストは番号が1つ繰り上がって継続する")]
    public void 番号付きリストの継続で番号が繰り上がる()
    {
        MarkdownBlockContinuation.ComputeListContinuationMarker("3. 三番目").Should().Be("4. ");
    }

    [Fact(DisplayName = "番号付きリストの区切り記号(.と))は元のまま継続する")]
    public void 番号付きリストの区切り記号を保つ()
    {
        MarkdownBlockContinuation.ComputeListContinuationMarker("3) 三番目").Should().Be("4) ");
    }

    [Fact(DisplayName = "チェックリストは継続すると未チェックの新項目になる")]
    public void チェックリストの継続は未チェックになる()
    {
        MarkdownBlockContinuation.ComputeListContinuationMarker("- [x] 完了済み").Should().Be("- [ ] ");
    }

    [Fact(DisplayName = "インデント付き(ネストした)リストはインデント幅を保って継続する")]
    public void ネストしたリストはインデントを保つ()
    {
        MarkdownBlockContinuation.ComputeListContinuationMarker("    - 内側の項目").Should().Be("    - ");
    }

    [Fact(DisplayName = "リストでない行・空項目は継続対象ではない(null)")]
    public void リストでない行や空項目はnull()
    {
        MarkdownBlockContinuation.ComputeListContinuationMarker("ただの文章").Should().BeNull();
        MarkdownBlockContinuation.ComputeListContinuationMarker("- ").Should().BeNull();
        MarkdownBlockContinuation.ComputeListContinuationMarker("-").Should().BeNull();
    }

    // ========== 引用継続（Enter、中身のある引用行） ==========
    // 実機のXvfb操作テストで発覚した不具合の再発防止: リストと違い、引用は
    // ContinuationMarkerPattern（リストマーカー専用の正規表現）に一致しないため、
    // 引用マーカーを読み飛ばす処理が無いとEnterで継続されず、ただの空行になってしまっていた。

    [Fact(DisplayName = "引用の本文でEnterすると同じ引用マーカーで継続する")]
    public void 引用の継続()
    {
        MarkdownBlockContinuation.ComputeListContinuationMarker("> 引用の本文").Should().Be("> ");
    }

    [Fact(DisplayName = "2段の引用は両方のマーカーを保って継続する")]
    public void 二段引用の継続()
    {
        MarkdownBlockContinuation.ComputeListContinuationMarker("> > 引用の本文").Should().Be("> > ");
    }

    [Fact(DisplayName = "引用の中の箇条書きは引用マーカーとリストマーカーの両方を保って継続する")]
    public void 引用の中のリストの継続()
    {
        MarkdownBlockContinuation.ComputeListContinuationMarker("> - 項目1").Should().Be("> - ");
    }

    [Fact(DisplayName = "引用の中の番号付きリストは番号を繰り上げつつ引用マーカーも保って継続する")]
    public void 引用の中の番号付きリストの継続()
    {
        MarkdownBlockContinuation.ComputeListContinuationMarker("> 1. 項目1").Should().Be("> 2. ");
    }

    [Fact(DisplayName = "引用マーカーだけで中身が空の行は継続対象ではない(脱出判定に委ねる)")]
    public void 引用マーカーのみの空行は継続ではなくnull()
    {
        MarkdownBlockContinuation.ComputeListContinuationMarker("> ").Should().BeNull();
        MarkdownBlockContinuation.ComputeListContinuationMarker(">").Should().BeNull();
    }

    // ========== 脱出（Enter、空項目） ==========

    [Fact(DisplayName = "1段だけの箇条書きの空項目は脱出するとプレーンな行になる")]
    public void 単純な箇条書きの空項目は脱出する()
    {
        var source = new ArrayMarkdownLineSource(new[] { "- " });
        var ctx = MarkdownBlockContinuation.ComputeExitContext(source, 0);
        ctx.Should().NotBeNull();
        ctx!.Value.Levels.Should().HaveCount(1);
        MarkdownBlockContinuation.RenderShallowerPrefix(ctx.Value.Levels).Should().Be(string.Empty);
        MarkdownBlockContinuation.ExitsQuoteCompletely(ctx.Value.Levels).Should().BeFalse();
    }

    [Fact(DisplayName = "1段だけの引用の空項目は完全に脱出し、空行を挟む対象と判定される")]
    public void 単純な引用の空項目は完全に脱出する()
    {
        var source = new ArrayMarkdownLineSource(new[] { "> " });
        var ctx = MarkdownBlockContinuation.ComputeExitContext(source, 0);
        ctx.Should().NotBeNull();
        ctx!.Value.Levels.Should().HaveCount(1);
        ctx.Value.Levels[0].Kind.Should().Be(MarkupLevelKind.Quote);
        MarkdownBlockContinuation.ExitsQuoteCompletely(ctx.Value.Levels).Should().BeTrue();
        MarkdownBlockContinuation.RenderShallowerPrefix(ctx.Value.Levels).Should().Be(string.Empty);
    }

    [Fact(DisplayName = "2段ネストの引用は1回のEnterにつき1段だけ浅くなる")]
    public void 多段引用は1段ずつ脱出する()
    {
        var source = new ArrayMarkdownLineSource(new[] { "> > " });
        var ctx = MarkdownBlockContinuation.ComputeExitContext(source, 0);
        ctx.Should().NotBeNull();
        ctx!.Value.Levels.Should().HaveCount(2);

        var shallower = MarkdownBlockContinuation.RenderShallowerPrefix(ctx.Value.Levels);
        shallower.Should().Be("> "); // 1段浅くなり、まだ引用の中(1段目)が残る。
        MarkdownBlockContinuation.ExitsQuoteCompletely(ctx.Value.Levels).Should().BeFalse(); // まだ完全脱出ではない。

        // "> "だけが残った行でさらにEnterすると、今度こそ完全に脱出する。
        var source2 = new ArrayMarkdownLineSource(new[] { shallower });
        var ctx2 = MarkdownBlockContinuation.ComputeExitContext(source2, 0);
        ctx2.Should().NotBeNull();
        MarkdownBlockContinuation.ExitsQuoteCompletely(ctx2!.Value.Levels).Should().BeTrue();
    }

    [Fact(DisplayName = "3段ネストの引用も1回につき1段だけ浅くなる")]
    public void 三段引用も1段ずつ脱出する()
    {
        var source = new ArrayMarkdownLineSource(new[] { "> > > " });
        var ctx = MarkdownBlockContinuation.ComputeExitContext(source, 0)!.Value;
        ctx.Levels.Should().HaveCount(3);
        MarkdownBlockContinuation.RenderShallowerPrefix(ctx.Levels).Should().Be("> > ");
    }

    [Fact(DisplayName = "引用の中の箇条書きが空なら、まずリストだけ抜けて引用は残る")]
    public void 引用の中のリストはリストだけ先に抜ける()
    {
        var source = new ArrayMarkdownLineSource(new[] { "> - " });
        var ctx = MarkdownBlockContinuation.ComputeExitContext(source, 0)!.Value;
        ctx.Levels.Should().HaveCount(2);
        ctx.Levels[0].Kind.Should().Be(MarkupLevelKind.Quote);
        ctx.Levels[^1].Kind.Should().Be(MarkupLevelKind.List);

        var shallower = MarkdownBlockContinuation.RenderShallowerPrefix(ctx.Levels);
        shallower.Should().Be("> "); // リストだけ抜け、引用はまだ残る。
        MarkdownBlockContinuation.ExitsQuoteCompletely(ctx.Levels).Should().BeFalse();
    }

    [Fact(DisplayName = "ネストした箇条書き(親リストの内側)の空項目は親の階層まで1段だけ浅くなる")]
    public void ネストしたリストは1段だけ浅くなる()
    {
        var lines = new[]
        {
            "- 親項目",
            "  - ", // 親("- ")の中の子リストの空項目(カーソル行)
        };
        var source = new ArrayMarkdownLineSource(lines);
        var ctx = MarkdownBlockContinuation.ComputeExitContext(source, 1);
        ctx.Should().NotBeNull("親のリストマーカーを後方探索で見つけられるはず");
        ctx!.Value.Levels.Should().HaveCount(2, "親リスト1段+自分のリスト1段");

        var shallower = MarkdownBlockContinuation.RenderShallowerPrefix(ctx.Value.Levels);
        // 親("- ")の階層だけが残り、その階層自身のマーカー"- "がそのまま使われる。
        shallower.Should().Be("- ");
    }

    [Fact(DisplayName = "本文が残っている行(通常の継続行)は脱出対象ではない")]
    public void 本文がある行は脱出対象ではない()
    {
        var source = new ArrayMarkdownLineSource(new[] { "- まだ中身がある" });
        MarkdownBlockContinuation.ComputeExitContext(source, 0).Should().BeNull();
    }

    [Fact(DisplayName = "リストでも引用でもない空行は脱出対象ではない")]
    public void 無関係な空行は脱出対象ではない()
    {
        var source = new ArrayMarkdownLineSource(new[] { "   " });
        MarkdownBlockContinuation.ComputeExitContext(source, 0).Should().BeNull();
    }

    // ========== 最重要: CommonMarkの遅延継続の不具合再発防止 ==========

    [Fact(DisplayName = "★遅延継続: 引用直後の行が\">\"無しでも脱出対象と誤認しない(本文を消さない)")]
    public void CommonMarkの遅延継続で本文の先頭を引用マーカーと誤認しない()
    {
        // ">"1つぶんの幅は2文字("> ")。遅延継続の本文の先頭2文字がたまたま同じ2文字だと、
        // 「幅の合計だけ」で判定する実装は誤って引用マーカーだと誤認して消してしまう
        // (Pane実機で実際に発生した不具合)。ここでは先頭が"ab"(2文字)の遅延継続行を用意し、
        // 消されないこと(=脱出対象として判定されないこと)を確認する。
        var lines = new[]
        {
            "> quote",
            "ab", // 遅延継続の本文行。行頭に">"が無い。
        };
        var source = new ArrayMarkdownLineSource(lines);

        var ctx = MarkdownBlockContinuation.ComputeExitContext(source, 1);
        ctx.Should().BeNull("行頭に\">\"が無い以上、この行は脱出対象(空のマーカー行)ではなく通常の本文行");
    }

    [Fact(DisplayName = "★遅延継続: 引用直後の行の本文がちょうどマーカー幅と同じ長さでも誤認しない")]
    public void 遅延継続で本文の長さがマーカー幅と一致していても誤認しない()
    {
        // ネストした引用("> > ")の幅は4文字。遅延継続本文が偶然4文字("abcd")でも
        // 消してはならない。
        var lines = new[]
        {
            "> > quote",
            "abcd",
        };
        var source = new ArrayMarkdownLineSource(lines);
        MarkdownBlockContinuation.ComputeExitContext(source, 1).Should().BeNull();
    }

    [Fact(DisplayName = "遅延継続の行でも、実際に\">\"だけがあり中身が空なら通常どおり脱出対象になる")]
    public void 実際に引用マーカーがある空行は正しく脱出対象になる()
    {
        var lines = new[] { "> quote", "> " };
        var source = new ArrayMarkdownLineSource(lines);
        var ctx = MarkdownBlockContinuation.ComputeExitContext(source, 1);
        ctx.Should().NotBeNull();
        MarkdownBlockContinuation.ExitsQuoteCompletely(ctx!.Value.Levels).Should().BeTrue();
    }

    // ========== 最重要: 引用から完全に抜けるときは空行を挟む ==========

    [Fact(DisplayName = "★引用から完全に抜けるときはExitsQuoteCompletelyがtrueになる(呼び出し側が空行を挟む契機)")]
    public void 引用から完全に抜けるときは空行を挟む()
    {
        // 呼び出し側(MarkdownEditingSupport)はExitsQuoteCompletely=trueのとき、書き込む文字列の
        // 先頭に"\n"を1つ足す(空行を挟む)契約になっている。CommonMarkでは空行が無いと遅延継続が
        // 働き、マーカーを消しただけでは文書の意味(保存後に他のビューアで開いたときの見え方)が
        // 変わってしまう(検討書の指摘)。ここではその契機となるフラグが正しい条件でのみ立つことを
        // 確認する。
        var singleQuote = new ArrayMarkdownLineSource(new[] { "> " });
        var ctx1 = MarkdownBlockContinuation.ComputeExitContext(singleQuote, 0)!.Value;
        MarkdownBlockContinuation.ExitsQuoteCompletely(ctx1.Levels).Should().BeTrue("引用1段だけが残っており、それを抜けるとプレーンな行になるため");

        // リストの脱出では(引用が絡まない限り)空行は不要。
        var singleList = new ArrayMarkdownLineSource(new[] { "- " });
        var ctx2 = MarkdownBlockContinuation.ComputeExitContext(singleList, 0)!.Value;
        MarkdownBlockContinuation.ExitsQuoteCompletely(ctx2.Levels).Should().BeFalse("リストからの脱出はCommonMark上、空行が無くても意味が変わらないため");

        // 引用の中のリストを1段抜けただけ(まだ引用が残る)では、まだ完全脱出ではない。
        var quoteWithList = new ArrayMarkdownLineSource(new[] { "> - " });
        var ctx3 = MarkdownBlockContinuation.ComputeExitContext(quoteWithList, 0)!.Value;
        MarkdownBlockContinuation.ExitsQuoteCompletely(ctx3.Levels).Should().BeFalse("この時点ではリストの階層のみが取り除かれ、引用はまだ残るため");
    }

    // ========== コードフェンス内は対象外 ==========

    [Fact(DisplayName = "コードフェンスの中では脱出判定を行わない")]
    public void コードフェンス内は対象外()
    {
        var lines = new[]
        {
            "```",
            "> ",
            "```",
        };
        var source = new ArrayMarkdownLineSource(lines);
        MarkdownBlockContinuation.ComputeExitContext(source, 1).Should().BeNull();
    }

    [Fact(DisplayName = "コードフェンスを閉じた後の行では通常どおり判定される")]
    public void コードフェンスの外では通常どおり()
    {
        var lines = new[]
        {
            "```",
            "code",
            "```",
            "> ",
        };
        var source = new ArrayMarkdownLineSource(lines);
        MarkdownBlockContinuation.ComputeExitContext(source, 3).Should().NotBeNull();
    }
}
