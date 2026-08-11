namespace Graft.Core;

/// <summary>
/// 細かいユーザビリティ改善3: 日時を「3分前」「昨日」のような相対表現へ変換する純粋関数。
/// <see cref="Format"/>は<paramref name="target"/>・<paramref name="now"/>のどちらも呼び出し側から
/// 明示的に受け取り、内部で壁時計（<see cref="DateTimeOffset.Now"/>等）を一切参照しない。
/// これにより、時刻をまたぐタイミングでテストが不安定になる問題（このリポジトリでは壁時計依存の
/// テストがCIを繰り返し赤くしてきた）を避けられる（tests/Graft.Tests/RelativeTimeFormatterTests.cs参照）。
///
/// UI非依存のCore層に置いているのは、<see cref="Graft.ViewModels.RevisionRowViewModel"/>
/// （履歴一覧の1行、Graft.UiTestsの担当）から使うだけでなく、Graft.Tests側で壁時計に依存しない
/// 純粋な単体テストとして直接検証できるようにするため（Graft.Tests.csprojはCore/Features/Infra
/// を直接取り込む方針、同csprojのコメント参照）。
/// </summary>
public static class RelativeTimeFormatter
{
    /// <summary><paramref name="target"/>を<paramref name="now"/>を基準にした相対表現へ変換する。</summary>
    public static string Format(DateTimeOffset target, DateTimeOffset now)
    {
        var span = now - target;
        if (span < TimeSpan.Zero) span = TimeSpan.Zero; // クロックのずれ等で未来時刻になった場合は「たった今」扱いにする安全側の丸め。

        if (span < TimeSpan.FromMinutes(1)) return "たった今";
        if (span < TimeSpan.FromMinutes(60)) return $"{(int)span.TotalMinutes}分前";

        var targetDate = target.LocalDateTime.Date;
        var nowDate = now.LocalDateTime.Date;

        if (span < TimeSpan.FromHours(24) && targetDate == nowDate) return $"{(int)span.TotalHours}時間前";

        var dayDiff = (nowDate - targetDate).Days;
        if (dayDiff <= 0) return target.LocalDateTime.ToString("HH:mm"); // 同日内だがDST等で24時間以上開いた稀なケース。
        if (dayDiff == 1) return $"昨日 {target.LocalDateTime:HH:mm}";
        if (dayDiff < 7) return $"{dayDiff}日前";

        return target.LocalDateTime.ToString("yyyy-MM-dd");
    }
}
