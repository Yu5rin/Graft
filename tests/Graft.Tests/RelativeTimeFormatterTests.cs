using System;
using FluentAssertions;
using Graft.Core;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 細かいユーザビリティ改善3: 履歴の相対時刻表示（<see cref="RelativeTimeFormatter"/>）。
/// <see cref="RelativeTimeFormatter.Format"/>は<c>target</c>・<c>now</c>のどちらも呼び出し側から
/// 明示的に渡す純粋関数のため、壁時計（<see cref="DateTimeOffset.Now"/>等）に一切触れずテストできる
/// （このリポジトリでは壁時計依存のテストがCIを繰り返し赤くしてきたため、この形にした判断を
/// RelativeTimeFormatterのクラスコメントにも残している）。
/// </summary>
public class RelativeTimeFormatterTests
{
    // RelativeTimeFormatter.Format内部はDateTimeOffset.LocalDateTime（実行機のローカルタイム
    // ゾーンへ変換）を使うため、テストの期待値もこの実行機のローカルオフセットに合わせて
    // 組み立てる（固定のTimeSpan.FromHours(9)等で決め打つと、CI実行機のタイムゾーンによって
    // 結果がずれ、壁時計・実行環境依存でテストが不安定になる）。
    private static readonly TimeSpan LocalOffset = TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 8, 10));
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 15, 0, 0, LocalOffset);

    [Fact(DisplayName = "1分未満は「たった今」")]
    public void 一分未満はたった今()
    {
        RelativeTimeFormatter.Format(Now.AddSeconds(-30), Now).Should().Be("たった今");
    }

    [Fact(DisplayName = "1分〜59分は「N分前」")]
    public void 分単位の表示()
    {
        RelativeTimeFormatter.Format(Now.AddMinutes(-1), Now).Should().Be("1分前");
        RelativeTimeFormatter.Format(Now.AddMinutes(-45), Now).Should().Be("45分前");
    }

    [Fact(DisplayName = "同じ日の1時間以上前は「N時間前」")]
    public void 時間単位の表示()
    {
        // Nowは15:00なので、3時間前の12:00は同じ暦日。
        RelativeTimeFormatter.Format(Now.AddHours(-3), Now).Should().Be("3時間前");
    }

    [Fact(DisplayName = "前日の日付なら「昨日 HH:mm」")]
    public void 前日は昨日表記()
    {
        // Nowは2026-08-10 15:00。2026-08-09 23:50は前日（1日前）。
        var target = new DateTimeOffset(2026, 8, 9, 23, 50, 0, LocalOffset);
        RelativeTimeFormatter.Format(target, Now).Should().Be("昨日 23:50");
    }

    [Fact(DisplayName = "日付をまたいでも1時間未満の差なら分単位のまま（「昨日」にはならない）")]
    public void 日付またぎでも直近なら分前表記()
    {
        // Now 2026-08-10 15:00 に対して、5分前でも日付境界の検証として
        // 深夜0時直後のケース（00:03 に対して23:58）を確かめる。
        var lateNight = new DateTimeOffset(2026, 8, 10, 0, 3, 0, LocalOffset);
        var justBefore = new DateTimeOffset(2026, 8, 9, 23, 58, 0, LocalOffset);

        RelativeTimeFormatter.Format(justBefore, lateNight).Should().Be("5分前");
    }

    [Fact(DisplayName = "2〜6日前は「N日前」")]
    public void 数日前の表示()
    {
        RelativeTimeFormatter.Format(Now.AddDays(-2), Now).Should().Be("2日前");
        RelativeTimeFormatter.Format(Now.AddDays(-6), Now).Should().Be("6日前");
    }

    [Fact(DisplayName = "7日以上前は日付表記（yyyy-MM-dd）")]
    public void 一週間以上前は日付表記()
    {
        var target = Now.AddDays(-10);
        RelativeTimeFormatter.Format(target, Now).Should().Be(target.LocalDateTime.ToString("yyyy-MM-dd"));
    }

    [Fact(DisplayName = "未来時刻（クロックのずれ）は安全側で「たった今」扱いにする")]
    public void 未来時刻はたった今扱い()
    {
        RelativeTimeFormatter.Format(Now.AddMinutes(5), Now).Should().Be("たった今");
    }
}
