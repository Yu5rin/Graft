using Graft.Core;
using Graft.Infra;

namespace Graft.Platform;

/// <summary>
/// 起動時にログへ残すだけの「静かな縮退」の記録（E706・E709）をまとめる。
/// <see cref="Views.StartupCoordinator"/>（Avalonia依存）から切り出してある理由:
/// ここに置くロジックは <see cref="IPlatformService"/>・<see cref="GraftIssue"/>・
/// <see cref="Logger"/> のみに依存し、UIフレームワークに一切触れないため、
/// tests/Graft.Tests（UI非依存の単体テスト）から直接検証できるようにするため
/// （tests/Graft.Tests/Graft.Tests.csproj に本ファイルを個別追加している）。
/// </summary>
internal static class PlatformDiagnosticsLogging
{
    /// <summary>
    /// 依頼2（E706）: <paramref name="service"/>がこの環境で利用できない（<see cref="
    /// IPlatformService.IsSupported"/>がfalse）場合に、E706付きの1行をログへ残す。
    /// 例外を投げず利用者の操作も妨げない「静かに縮退する」という<see cref="IPlatformService"/>の
    /// 契約（IPlatformServices.csのXMLコメント参照）は変えない。あくまでログへ痕跡を残すだけの
    /// 追加であり、既存の無効表示（設定画面）の動作には一切影響しない。
    /// </summary>
    public static void LogUnsupportedFeature(Logger? logger, string featureName, IPlatformService service)
    {
        if (service.IsSupported)
        {
            return;
        }

        var issue = GraftIssue.Of(ErrorCode.E706, $"{featureName}: {service.UnsupportedReason}", severity: Severity.Info);
        logger?.Info("platform", issue.ToDisplayText());
    }

    /// <summary>
    /// 依頼3（E709）: OSのハイコントラストモードが有効（<paramref name="isHighContrastActive"/>
    /// がtrue）だったことをログへ残す。9.3のとおりGraftは配色トークンを切り替えないため、
    /// ダイアログでは通知せずログのみに記録する（E706と同じ方針。起動のたびに利用者を
    /// 煩わせないため）。判定不能（null）・無効（false）のいずれでも何もしない。
    /// </summary>
    public static void LogHighContrastIfDetected(Logger? logger, bool? isHighContrastActive)
    {
        if (isHighContrastActive != true)
        {
            return;
        }

        var issue = GraftIssue.Of(ErrorCode.E709,
            "OSのハイコントラストモードが有効です。Graftは9.3のとおりOSに関わらず同一の配色トークンを維持します。",
            severity: Severity.Info);
        logger?.Info("platform", issue.ToDisplayText());
    }
}
