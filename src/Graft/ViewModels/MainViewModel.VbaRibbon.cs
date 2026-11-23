using Graft.Core.Vba;
using Graft.Features;

namespace Graft.ViewModels;

/// <summary>
/// <see cref="MainViewModel"/> の分割ファイル。リボン（customUI）の書き換えを、適用・取り消し・復元の流れへ組み込む部分の補助
/// （仕様書_ブックの情報と依頼の観点 16.10。段階 F5）。
/// <para>
/// 流れそのもの（準備 → 衝突ダイアログ → 確認 → 書き換え → 通知）は <c>ProjectPaneViewModel.PushRibbonAsync</c> に 1 か所だけ置き、
/// 適用（<c>MainViewModel.Apply.cs</c>）・取り消しと復元（<c>MainViewModel.VbaPush.cs</c>）・手動の「Excel へ反映」（<c>ProjectPaneViewModel.Vba.cs</c>）の
/// どれも、VBA の反映が済んだあとに <c>ProjectPane.FollowWithRibbonAsync</c> を呼ぶだけにする。ここにあるのは、その結果を適用の通知の文に
/// 写す補助と、衝突ダイアログで選ばれた「Graft 側で上書き」を実行する入口。
/// </para>
/// <para>
/// 【VBA でないプロジェクト・customUI の無いプロジェクトでは何も起きない】 <c>FollowWithRibbonAsync</c> は、リボンを同期していない・
/// 未反映が無いときは、通知も確認も出さずに戻る（ミラーと同期基準だけで調べるので、Excel にもブックのファイルにも触れない）。
/// 適用の通知の文も、<see cref="RibbonFollow.NotNeeded"/> のときは従来のまま。
/// </para>
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// 適用の通知（「rN として適用しました（…）」）に添える一言へ、リボンの結果を足す（16.10「成功したとき」）。
    /// <list type="bullet">
    /// <item>反映した: 「リボンも反映済み」（VBA の一言が無いときは「リボンを反映済み」）。</item>
    /// <item>中止・失敗: 「リボンは未反映」（バッジの「リボン未反映」と対応する）。</item>
    /// <item>要らなかった・VBA の反映が済んでいなかった（その通知は別に出している）: 変えない。</item>
    /// </list>
    /// </summary>
    private static string? RibbonNoticeDetail(string? vbaDetail, RibbonFollow follow) => follow switch
    {
        RibbonFollow.Rewritten => vbaDetail is null ? "リボンを反映済み" : vbaDetail + "、リボンも反映済み",
        RibbonFollow.Cancelled or RibbonFollow.Failed => vbaDetail is null ? "リボンは未反映" : vbaDetail + "、リボンは未反映",
        _ => vbaDetail,
    };

    /// <summary>
    /// 衝突ダイアログで「Graft 側で上書き」を選んだリボン（ミラーのパス）を、ブックへ書き換える（確認ダイアログ・控え・書き換えの手順は省かない）。
    /// </summary>
    /// <returns>書き換えた（または、もう反映するものが無い）なら true。確認で「中止」した・書き換えに失敗したなら false（衝突は残る）。</returns>
    private async Task<bool> OverwriteRibbonConflictsAsync(Project project, IReadOnlyCollection<string> paths, VbaConflictScene scene)
    {
        var end = await ProjectPane.PushRibbonAsync(project, new RibbonPushOptions
        {
            OverwriteConflicts = paths,
            ConflictScene = scene,
            Automatic = true,
        }).ConfigureAwait(true);
        return end is RibbonPushEnd.Rewritten or RibbonPushEnd.NothingToDo;
    }
}
