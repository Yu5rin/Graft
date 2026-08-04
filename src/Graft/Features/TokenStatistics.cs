using System.Globalization;
using Graft.Core;

namespace Graft.Features;

/// <summary>
/// 仕様書12章のトークン統計。リビジョンごとに記録済みの推定トークン数・削減トークン数
/// （<see cref="RevisionStats.EstimatedTokens"/>・<see cref="RevisionStats.EstimatedSavedTokens"/>）を
/// 期間別に集計する。プロジェクト別の集計は、呼び出し側が対象プロジェクトの
/// <see cref="RevisionSummary"/> 一覧に絞り込んだ上でこのメソッドへ渡すことで実現する。
/// </summary>
public static class TokenStatistics
{
    /// <summary>期間granularityの指定値。</summary>
    public static class Granularity
    {
        public const string Day = "day";
        public const string Week = "week";
        public const string Month = "month";
    }

    /// <summary>期間ごとの集計1件。</summary>
    public sealed record Bucket
    {
        /// <summary>期間ラベル（例: "2026-08-04" / "2026-W31" / "2026-08"）。</summary>
        public required string Label { get; init; }
        /// <summary>推定トークン数の合計。</summary>
        public int Tokens { get; init; }
        /// <summary>削減できた推定トークン数の合計。</summary>
        public int SavedTokens { get; init; }
        /// <summary>リビジョン件数。</summary>
        public int Revisions { get; init; }
    }

    /// <summary>
    /// リビジョン一覧を期間別（"day" / "week" / "month"）に集計する。未知のgranularityは
    /// "day" として扱う。ラベルの昇順（時系列順）に並べて返す。
    /// </summary>
    public static IReadOnlyList<Bucket> ByPeriod(IEnumerable<RevisionSummary> revisions, string granularity)
    {
        return revisions
            .GroupBy(r => BucketLabel(r.Manifest.AppliedAt, granularity))
            .Select(g => new Bucket
            {
                Label = g.Key,
                Tokens = g.Sum(r => r.Manifest.Stats.EstimatedTokens),
                SavedTokens = g.Sum(r => r.Manifest.Stats.EstimatedSavedTokens),
                Revisions = g.Count(),
            })
            .OrderBy(b => b.Label, StringComparer.Ordinal)
            .ToList();
    }

    private static string BucketLabel(DateTimeOffset appliedAt, string granularity) => granularity switch
    {
        Granularity.Week => WeekLabel(appliedAt),
        Granularity.Month => appliedAt.ToString("yyyy-MM", CultureInfo.InvariantCulture),
        _ => appliedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
    };

    private static string WeekLabel(DateTimeOffset appliedAt)
    {
        var date = appliedAt.UtcDateTime;
        var week = ISOWeek.GetWeekOfYear(date);
        var year = ISOWeek.GetYear(date);
        return $"{year}-W{week:00}";
    }
}
