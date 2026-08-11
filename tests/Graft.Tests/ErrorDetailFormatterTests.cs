using FluentAssertions;
using Graft.Core;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 機能1（エラーダイアログの「詳細をコピー」）の回帰テスト。
/// UI（<c>Graft.Platform.AvaloniaDialogService</c>）を経由せず、文面組み立てロジック
/// （<see cref="ErrorDetailFormatter"/>）だけを検証する。
/// </summary>
public class ErrorDetailFormatterTests
{
    [Fact(DisplayName = "エラーコードを含むメッセージはContainsErrorCodeがtrueを返す")]
    public void エラーコードを含むメッセージを検出する()
    {
        var issue = GraftIssue.Of(ErrorCode.E101, "テスト詳細", line: 3, path: "foo.txt");
        ErrorDetailFormatter.ContainsErrorCode(issue.ToDisplayText()).Should().BeTrue();
    }

    [Theory(DisplayName = "エラーコードを含まない通常のメッセージはContainsErrorCodeがfalseを返す")]
    [InlineData("本当に削除しますか？")]
    [InlineData("")]
    [InlineData("設定を既定値に戻しました。")]
    public void エラーコードを含まないメッセージは検出しない(string message)
    {
        ErrorDetailFormatter.ContainsErrorCode(message).Should().BeFalse();
    }

    [Fact(DisplayName = "コピー文面にエラーコード・要約・詳細・対処・バージョン・OSがすべて含まれる")]
    public void コピー文面に必要な情報がすべて含まれる()
    {
        var issue = GraftIssue.Of(ErrorCode.E101, "3行目で発生", line: 3, path: "foo.txt");
        var message = issue.ToDisplayText();

        var text = ErrorDetailFormatter.BuildCopyText("解析に失敗しました", message, "1.2.3", "Linux 6.1 (test)");

        text.Should().Contain("解析に失敗しました", "タイトルを含めること");
        text.Should().Contain("E101", "エラーコードを含めること");
        text.Should().Contain(ErrorCatalog.SummaryOf(ErrorCode.E101), "要約を含めること");
        text.Should().Contain("3行目で発生", "詳細（Detail）を含めること");
        text.Should().Contain(ErrorCatalog.RemedyOf(ErrorCode.E101), "対処（Remedy）を含めること");
        text.Should().Contain("1.2.3", "アプリのバージョンを含めること");
        text.Should().Contain("Linux 6.1 (test)", "OSの情報を含めること");
    }

    [Fact(DisplayName = "複数のエラーコードを含むメッセージは、それぞれの対処が重複せず1回ずつ載る")]
    public void 複数コードの対処が重複せず載る()
    {
        var message = string.Join(Environment.NewLine,
            GraftIssue.Of(ErrorCode.E101).ToDisplayText(),
            GraftIssue.Of(ErrorCode.E402).ToDisplayText(),
            GraftIssue.Of(ErrorCode.E101).ToDisplayText()); // 同一コードの重複

        var text = ErrorDetailFormatter.BuildCopyText("適用に失敗しました", message, "1.0.0", "Windows 11");

        text.Should().Contain(ErrorCatalog.RemedyOf(ErrorCode.E101));
        text.Should().Contain(ErrorCatalog.RemedyOf(ErrorCode.E402));

        // "E101 " というエラーコード自体の出現は元のmessage中に2回あるが、対処セクションの
        // 行頭マーカー「・E101」は重複除去され1回だけのはず。
        CountOccurrences(text, "・E101").Should().Be(1, "同一コードの対処を重複させてはいけない");
    }

    [Fact(DisplayName = "エラーコードを含まないメッセージには対処セクションが付かない")]
    public void エラーコードが無ければ対処セクションが無い()
    {
        var text = ErrorDetailFormatter.BuildCopyText("確認", "本当に削除しますか？", "1.0.0", "Windows 11");

        text.Should().NotContain("対処:");
    }

    private static int CountOccurrences(string text, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }
}
