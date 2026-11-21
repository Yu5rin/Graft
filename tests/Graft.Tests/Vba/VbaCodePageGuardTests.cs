using FluentAssertions;
using Graft.Core;
using Graft.Core.Vba;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// <see cref="VbaCodePageGuard"/> のテスト（仕様書_VBA連携 6.4・10.1）。
/// <para>
/// 【文字の可否は .NET 8 の CP932 エンコーダの実測で固定している】 仕様書には Python の <c>cp932</c> での
/// 結果が書かれているが、.NET とは異なる場合がある。実測の結果、仕様書の記載と異なったのは
/// 波ダッシュ「〜」（U+301C）で、Python の cp932 に倣うと表せそうに見えるが、.NET の CP932 では
/// 例外（表せない）になった。ほかに AI が出しやすい「−」（U+2212、マイナス記号）も表せない
/// （全角ハイフンマイナス U+FF0D は表せる）。
/// </para>
/// </summary>
public class VbaCodePageGuardTests
{
    [Theory(DisplayName = "表せる文字: 全角チルダ・囲み数字・全角ハイフンマイナス・水平線・三点リーダ・日本語・ASCII")]
    [InlineData("～")]   // ～ 全角チルダ
    [InlineData("①")]   // ①
    [InlineData("⑳")]   // ⑳
    [InlineData("－")]   // － 全角ハイフンマイナス
    [InlineData("―")]   // ― 水平線
    [InlineData("…")]   // … 三点リーダ
    [InlineData("×")]   // ×
    [InlineData("日本語のコメント")]
    [InlineData("abc ~ \\ 123")]
    public void 表せる文字(string text)
    {
        VbaCodePageGuard.CanEncode(text).Should().BeTrue();
        VbaCodePageGuard.Find(text).Should().BeEmpty();
    }

    [Theory(DisplayName = "表せない文字: emダッシュ・enダッシュ・箇条書きの点・円記号・NBSP・絵文字・波ダッシュ（.NET の実測）・マイナス記号")]
    [InlineData("—", 0x2014)]    // — emダッシュ
    [InlineData("–", 0x2013)]    // – enダッシュ
    [InlineData("•", 0x2022)]    // •
    [InlineData("¥", 0x00A5)]    // ¥
    [InlineData(" ", 0x00A0)]    // ノーブレークスペース
    [InlineData("\U0001F600", 0x1F600)] // 絵文字（サロゲートペア）
    [InlineData("〜", 0x301C)]    // 〜 波ダッシュ（.NET の CP932 では表せない）
    [InlineData("−", 0x2212)]    // − マイナス記号
    public void 表せない文字(string text, int codePoint)
    {
        VbaCodePageGuard.CanEncode(text).Should().BeFalse();
        var hits = VbaCodePageGuard.Find(text);
        hits.Should().ContainSingle();
        hits[0].CodePoint.Should().Be(codePoint);
        hits[0].Character.Should().Be(text);
    }

    [Fact(DisplayName = "位置は複数行で正確（行・列は 1 始まり。CRLF・LF・CR のどれでも 1 行として数える）")]
    public void 複数行での位置()
    {
        var text = "Sub A()\r\n  ' コメント — ここ\r\nEnd Sub\n'x•\r'–";

        var hits = VbaCodePageGuard.Find(text);

        hits.Select(h => (h.Line, h.Column, h.CodePoint)).Should().Equal(
            (2, 10, 0x2014),   // "  ' コメント " は 2+1+1+4+1 = 9 文字なので、— は 10 列目
            (4, 3, 0x2022),
            (5, 2, 0x2013));
    }

    [Fact(DisplayName = "サロゲートペアは 1 文字として数え、後続の列がずれない")]
    public void サロゲートペアの位置()
    {
        // 「a😀b—」: 😀 は 2 列目、b は 3 列目、— は 4 列目。UTF-16 の位置で数えると 5 列目になってしまう。
        var hits = VbaCodePageGuard.Find("a\U0001F600b—");

        hits.Select(h => (h.Column, h.CodePoint)).Should().Equal((2, 0x1F600), (4, 0x2014));
    }

    [Fact(DisplayName = "同じ文字が何度出てもすべて位置つきで列挙する")]
    public void 同じ文字の繰り返し()
    {
        VbaCodePageGuard.Find("——\r\n—").Should().HaveCount(3);
    }

    [Fact(DisplayName = "報告の最大件数で打ち切る")]
    public void 最大件数で打ち切る()
    {
        var text = string.Concat(Enumerable.Repeat("—\r\n", 200));

        VbaCodePageGuard.Find(text, maxHits: 5).Should().HaveCount(5);
        VbaCodePageGuard.Find(text).Should().HaveCount(VbaCodePageGuard.DefaultMaxHits);
    }

    [Fact(DisplayName = "空文字・null 相当は問題なし")]
    public void 空は問題なし()
    {
        VbaCodePageGuard.CanEncode(string.Empty).Should().BeTrue();
        VbaCodePageGuard.Find(string.Empty).Should().BeEmpty();
        VbaCodePageGuard.ToIssue("Modules/A.bas", string.Empty).Should().BeNull();
    }

    [Fact(DisplayName = "ToIssue は E811（エラー）で、ファイル名・行・列・文字を詳細に含む")]
    public void 問題の詳細()
    {
        var issue = VbaCodePageGuard.ToIssue("Modules/Foo.bas", "Sub A()\r\n  x = 1 — 2\r\nEnd Sub");

        issue.Should().NotBeNull();
        issue!.Code.Should().Be(ErrorCode.E811);
        issue.Severity.Should().Be(Severity.Error);
        issue.Path.Should().Be("Modules/Foo.bas");
        issue.LineNumber.Should().Be(2);
        issue.Detail.Should().Contain("Modules/Foo.bas").And.Contain("2行9列").And.Contain("U+2014");
    }

    [Fact(DisplayName = "多数の違反は先頭 10 件だけ詳細に出し、残りは件数で示す")]
    public void 多数の違反は件数で示す()
    {
        var text = string.Concat(Enumerable.Repeat("—", 15));

        var issue = VbaCodePageGuard.ToIssue("Modules/A.bas", text);

        issue!.Detail.Should().Contain("ほか5件");
    }

    [Fact(DisplayName = "表示用の文字列: 通常の文字は U+XXXX（文字）、制御文字は文字を出さない")]
    public void 表示用の文字列()
    {
        new VbaUnencodableChar(1, 1, 0x2014, "—").Display.Should().Be("U+2014（—）");
        new VbaUnencodableChar(1, 1, 0x1F600, "\U0001F600").Display.Should().Be("U+1F600（\U0001F600）");
        new VbaUnencodableChar(1, 1, 0x85, "\u0085").Display.Should().Be("U+0085");
    }
}
