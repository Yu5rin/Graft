using FluentAssertions;
using Graft.Core;
using Graft.ViewModels;
using Xunit;

namespace Graft.UiTests;

/// <summary>
/// 細かいユーザビリティ改善3: 履歴一覧の1行（<see cref="RevisionRowViewModel"/>）が、
/// 相対時刻表示（<see cref="RevisionRowViewModel.RelativeAppliedAtText"/>）を注入された時計
/// （壁時計ではない）から組み立てることを検証する。表示ロジック自体（「3分前」等の文言）は
/// <c>RelativeTimeFormatterTests</c>（Graft.Tests）で網羅済みのため、ここでは配線
/// （<c>_now()</c>を毎回呼び直して<see cref="RelativeTimeFormatter.Format"/>へ渡すこと）だけを見る。
/// </summary>
public class RevisionRowRelativeTimeTests
{
    private static RevisionSummary MakeSummary(DateTimeOffset appliedAt) => new()
    {
        Manifest = new RevisionManifest
        {
            Revision = 3,
            ProjectId = "p_test",
            Summary = "テスト用の変更",
            Type = "feat",
            AppliedAt = appliedAt,
            Status = RevisionStatus.Success,
        },
        FolderPath = "/tmp/dummy",
        IsRestorable = true,
    };

    [Fact(DisplayName = "RelativeAppliedAtTextは注入したnow()を使ってRelativeTimeFormatter.Formatと同じ結果を返す")]
    public void 注入した時計で相対表示を組み立てる()
    {
        var appliedAt = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 8, 10, 12, 5, 0, TimeSpan.Zero); // 5分後

        var row = new RevisionRowViewModel(MakeSummary(appliedAt), () => now);

        row.RelativeAppliedAtText.Should().Be(RelativeTimeFormatter.Format(appliedAt, now));
        row.RelativeAppliedAtText.Should().Be("5分前");
    }

    [Fact(DisplayName = "呼び出すたびにnow()を呼び直すため、注入した時計を進めれば表示も追従する（壁時計は一切参照しない）")]
    public void 時計を進めると表示も追従する()
    {
        var appliedAt = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
        var current = appliedAt.AddMinutes(1);
        var row = new RevisionRowViewModel(MakeSummary(appliedAt), () => current);

        row.RelativeAppliedAtText.Should().Be("1分前");

        current = appliedAt.AddDays(1); // 時計を注入経由で進める（Thread.Sleep等で実時間を待たない）。
        row.RelativeAppliedAtText.Should().Be("昨日 12:00");
    }

    [Fact(DisplayName = "AppliedAtText（ホバー用の正確な日時）は相対表示とは独立して常に固定書式")]
    public void 正確な日時表示は相対表示と独立している()
    {
        var appliedAt = new DateTimeOffset(2026, 8, 10, 12, 34, 0, TimeSpan.Zero);
        var row = new RevisionRowViewModel(MakeSummary(appliedAt), () => appliedAt.AddDays(3));

        row.AppliedAtText.Should().Be(appliedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm"));
        row.RelativeAppliedAtText.Should().Be("3日前");
    }

    [Fact(DisplayName = "nowを省略した既定コンストラクタでも例外にならない（既定はDateTimeOffset.Now）")]
    public void nowを省略しても動作する()
    {
        var row = new RevisionRowViewModel(MakeSummary(DateTimeOffset.Now));

        var act = () => row.RelativeAppliedAtText;

        act.Should().NotThrow();
    }
}
