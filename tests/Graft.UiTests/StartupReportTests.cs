using FluentAssertions;
using Graft.Core;
using Graft.ViewModels;
using Xunit;

namespace Graft.UiTests;

/// <summary>
/// 課題2-3の回帰テスト: 起動時に複数の問題を1枚のダイアログへ集約したとき、
/// 全体の件数（「N件の問題を検出しました」）が本文の先頭に出ることを確認する。
/// StartupReportはViewModel層（Graft.UiTests側で参照するアセンブリ）にあり、画面は使わない
/// ため、HistoryDateFilterTests.csと同じ理由でこちらに置く。
/// </summary>
public class StartupReportTests
{
    [Fact(DisplayName = "課題2-3: 通知対象の問題が複数あると、先頭に総数の行が出る")]
    public void 複数件の問題があると先頭に総数が出る()
    {
        var report = new StartupReport
        {
            Issues = new[]
            {
                GraftIssue.Of(ErrorCode.E404, "設定ファイル", severity: Severity.Warning),
                GraftIssue.Of(ErrorCode.E601, "ホットキー", severity: Severity.Warning),
                GraftIssue.Of(ErrorCode.E404, "履歴インデックス", severity: Severity.Warning),
            },
        };

        var summary = report.BuildIssuesSummaryText();

        summary.Should().StartWith("3件の問題を検出しました。", "件数が多い日にスクロールしなくても全体像が分かるようにするため");
        summary.Should().Contain("・E404");
        summary.Should().Contain("・E601");
    }

    [Fact(DisplayName = "課題2-3: 問題が1件も無い場合は従来どおり空文字列のまま（ダイアログを出さない）")]
    public void 問題が無い場合は空文字列()
    {
        var report = new StartupReport();

        report.BuildIssuesSummaryText().Should().BeEmpty();
    }

    [Fact(DisplayName = "課題2-3: Info重大度は件数に含めない（NotifiableIssuesと同じ扱い）")]
    public void Info重大度は件数に含めない()
    {
        var report = new StartupReport
        {
            Issues = new[]
            {
                GraftIssue.Of(ErrorCode.E404, severity: Severity.Info),
                GraftIssue.Of(ErrorCode.E601, severity: Severity.Warning),
            },
        };

        report.BuildIssuesSummaryText().Should().StartWith("1件の問題を検出しました。");
    }
}
