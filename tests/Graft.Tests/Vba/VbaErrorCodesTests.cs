using FluentAssertions;
using Graft.Core;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// E801〜E814（Excel VBA 連携。docs/仕様書_VBA連携.md 9章）の登録の確認。
/// 全コードの登録漏れ検出そのものは <see cref="ErrorCatalogTests"/> が担う。ここでは「仕様の14件が
/// 揃っていること」と、見出し・対処の中身が仕様の意図どおりであることを固定する。
/// </summary>
public class VbaErrorCodesTests
{
    private static readonly ErrorCode[] VbaCodes =
    {
        ErrorCode.E801, ErrorCode.E802, ErrorCode.E803, ErrorCode.E804, ErrorCode.E805, ErrorCode.E806, ErrorCode.E807,
        ErrorCode.E808, ErrorCode.E809, ErrorCode.E810, ErrorCode.E811, ErrorCode.E812, ErrorCode.E813, ErrorCode.E814,
    };

    [Fact(DisplayName = "E8xx は E801〜E814 の 14 件ちょうどで、すべて見出しと対処が空でない")]
    public void 十四件が揃っている()
    {
        var all = Enum.GetValues<ErrorCode>().Where(c => c.ToString().StartsWith("E8", StringComparison.Ordinal)).ToArray();

        all.Should().BeEquivalentTo(VbaCodes);
        foreach (var code in VbaCodes)
        {
            ErrorCatalog.SummaryOf(code).Should().NotBeNullOrWhiteSpace($"{code} の見出し");
            ErrorCatalog.RemedyOf(code).Should().NotBeNullOrWhiteSpace($"{code} の対処");
        }
    }

    [Theory(DisplayName = "仕様 9 章の見出し")]
    [InlineData(ErrorCode.E801, "Excel と通信できませんでした")]
    [InlineData(ErrorCode.E802, "VBA プロジェクトへのアクセスが許可されていません")]
    [InlineData(ErrorCode.E803, "VBA プロジェクトがパスワードで保護されています")]
    [InlineData(ErrorCode.E804, "紐付けたブックが開いていません")]
    [InlineData(ErrorCode.E805, "Excel が応答しません")]
    [InlineData(ErrorCode.E806, "このブックはマクロを保存できない形式です")]
    [InlineData(ErrorCode.E808, "モジュールを作成できませんでした")]
    [InlineData(ErrorCode.E810, "同期の対象外のファイルがあります")]
    [InlineData(ErrorCode.E811, "Shift_JIS で表せない文字があります")]
    [InlineData(ErrorCode.E812, "Excel 側とファイル側の両方が変更されています")]
    [InlineData(ErrorCode.E813, "マクロの実行中または中断中です")]
    [InlineData(ErrorCode.E814, "VBA プロジェクトでは、ファイルの削除・名前変更を適用できません")]
    public void 見出し(ErrorCode code, string summary)
    {
        ErrorCatalog.SummaryOf(code).Should().Be(summary);
    }

    [Fact(DisplayName = "E813・E814 が含まれる（v0.2 で新設）")]
    public void E813とE814()
    {
        ErrorCatalog.SummaryOf(ErrorCode.E813).Should().Contain("マクロ");
        ErrorCatalog.SummaryOf(ErrorCode.E814).Should().Contain("削除").And.Contain("名前変更");
        ErrorCatalog.RemedyOf(ErrorCode.E813).Should().Contain("リセット");
        ErrorCatalog.RemedyOf(ErrorCode.E814).Should().Contain("チェックを外");
    }

    [Fact(DisplayName = "見出しに重大度（警告・衝突）は入れない（重大度は GraftIssue.Of の severity で発生のたびに指定する）")]
    public void 見出しに重大度を入れない()
    {
        foreach (var code in VbaCodes)
        {
            ErrorCatalog.SummaryOf(code).Should().NotContain("（警告）").And.NotContain("（衝突）");
        }
    }

    [Fact(DisplayName = "E802 の対処はトラストセンターの設定手順を案内し、Graft が設定を書き換えないことを伝える")]
    public void E802の案内()
    {
        var remedy = ErrorCatalog.RemedyOf(ErrorCode.E802);

        remedy.Should().Contain("トラストセンター").And.Contain("VBA プロジェクト オブジェクト モデルへのアクセスを信頼する");
        remedy.Should().Contain("書き換えること");
    }

    [Fact(DisplayName = "E706 の実際の文言（「この環境では使えない機能です」）が NullVbaBridge の失敗で使われる前提と一致する")]
    public void E706の文言()
    {
        ErrorCatalog.SummaryOf(ErrorCode.E706).Should().Be("この環境では使えない機能です");
    }

    [Fact(DisplayName = "GraftIssue から見出しと対処を引ける（E811 を例に、詳細つきの表示文字列）")]
    public void Issueの表示()
    {
        var issue = GraftIssue.Of(ErrorCode.E811, "Modules/A.bas: 2行9列 U+2014", path: "Modules/A.bas", line: 2);

        issue.Summary.Should().Be("Shift_JIS で表せない文字があります");
        issue.Remedy.Should().Contain("Shift_JIS");
        issue.ToDisplayText().Should().StartWith("E811 Shift_JIS で表せない文字があります（Modules/A.bas）");
    }
}
