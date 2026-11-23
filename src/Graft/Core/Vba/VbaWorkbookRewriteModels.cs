using System.Globalization;

namespace Graft.Core.Vba;

/// <summary>
/// リボンの書き換えの前に、Excel で開いているブックの状態を読んだもの（仕様書_ブックの情報と依頼の観点 16.6 の
/// <c>IVbaBridge.InspectWorkbookForRewriteAsync</c>。段階 F4）。読むだけで、何も変えない。
/// <para>
/// 【Core に置く理由】 「止めるブックか」の判定（<see cref="VbaWorkbookRewriteGuard"/>）を COM から切り離して <c>Graft.Tests</c> で
/// 固めるため。ブリッジは COM から値を読んでこの形に詰めるだけにする（<c>IVbaBridge</c> の「読む・書く・見せる」に絞る方針）。
/// </para>
/// </summary>
/// <param name="IsOpen">Excel（ROT）で開いているか。false なら、ほかの値は意味を持たない（<see cref="NotOpen"/>）。</param>
/// <param name="Saved">
/// <c>Workbook.Saved</c>。<b>確認ダイアログの文面とボタンを変えるためだけに使う</b>（16.6「<c>Workbook.Saved</c> の限界」）。
/// マクロが True を書けるうえ、VBE での編集で False になるかは実機で要確認なので、この値で「保存するか」を決めない（必ず保存する）。
/// </param>
/// <param name="ReadOnly"><c>Workbook.ReadOnly</c>。</param>
/// <param name="IsAddin"><c>Workbook.IsAddin</c>（開いているアドイン。.xlam）。</param>
/// <param name="MultiUserEditing"><c>Workbook.MultiUserEditing</c>（共有ブック）。</param>
/// <param name="AutoSaveOn"><c>Workbook.AutoSaveOn</c>（無い版の Excel では false として読む）。</param>
/// <param name="IsProtectedView">保護ビューで開いている（既存の <c>ProtectedViewWindows</c> の探し方）。</param>
/// <param name="IsUrlLocation"><c>FullName</c> が URL（OneDrive／SharePoint）。</param>
/// <param name="ProjectMode">
/// <c>VBProject.Mode</c>（Run=0・Break=1・Design=2）。VBA プロジェクトに触れない（トラストセンター E802・ロック E803）ときは null で、
/// そのときは E813 の検査を省いて進む（リボンの書き換えに VBA プロジェクトへのアクセスは要らないため。16.6 の 1）。
/// </param>
public sealed record VbaWorkbookRewriteState(
    bool IsOpen,
    bool Saved = true,
    bool ReadOnly = false,
    bool IsAddin = false,
    bool MultiUserEditing = false,
    bool AutoSaveOn = false,
    bool IsProtectedView = false,
    bool IsUrlLocation = false,
    int? ProjectMode = null)
{
    /// <summary>Excel で開いていない（COM を使わずにファイルだけを書き換える経路。段階 F3）。</summary>
    public static VbaWorkbookRewriteState NotOpen { get; } = new(IsOpen: false);
}

/// <summary>
/// 閉じる前に控えた、ブックの見た目（16.6「保存して閉じる」の 2）。開き直したあとに「できれば」戻す。戻せなくても警告にもしない。
/// </summary>
/// <param name="ActiveSheetName">ブックのアクティブなシートの名前（取れなければ null）。</param>
/// <param name="WindowState">ブックの最初のウィンドウの <c>WindowState</c>（<see cref="VbaWorkbookViewRestore"/> の定数。取れなければ null）。</param>
/// <param name="Left">ウィンドウの位置（ポイント）。</param>
/// <param name="Top">ウィンドウの位置（ポイント）。</param>
/// <param name="Width">ウィンドウの大きさ（ポイント）。</param>
/// <param name="Height">ウィンドウの大きさ（ポイント）。</param>
/// <param name="WasActive">閉じる前に、このブックが Excel のアクティブなブックだったか。</param>
/// <param name="PreviouslyActiveWorkbook">アクティブでなかったとき、そのときアクティブだったブックの <c>FullName</c>（前面へ戻す相手）。</param>
public sealed record VbaWorkbookViewState(
    string? ActiveSheetName = null,
    int? WindowState = null,
    double? Left = null,
    double? Top = null,
    double? Width = null,
    double? Height = null,
    bool WasActive = true,
    string? PreviouslyActiveWorkbook = null);

/// <summary>
/// 「保存して閉じる」から「開き直す」までを結ぶ券（16.6。<c>IVbaBridge.SaveAndCloseWorkbookAsync</c> が返し、
/// <c>ReopenWorkbookAsync</c> か <c>DiscardReopenTicketAsync</c> に 1 回だけ渡す）。
/// <para>
/// 【<c>Application</c> の参照を券そのものに持たせない理由】 COM の参照（RCW）は作ったスレッド（アパートメント）に結び付き、同じ COM
/// オブジェクトの RCW は 1 つを共有するので、ほかの呼び出しが <c>FinalReleaseComObject</c> すると券の参照まで無効になる。参照はブリッジの
/// 中に置き、券は識別子と控えた見た目だけを運ぶ（Linux のテストでも作れる、ただの値）。
/// </para>
/// </summary>
/// <param name="Id">ブリッジの中の参照を引く識別子。</param>
/// <param name="WorkbookFullName">閉じたブックの <c>FullName</c>（開き直すパス）。</param>
/// <param name="View">閉じる前の見た目。</param>
public sealed record VbaWorkbookReopenTicket(string Id, string WorkbookFullName, VbaWorkbookViewState View);

/// <summary>開き直した結果（16.6「開き直す」）。失敗は <c>GraftResult</c> の問題（E822）で返す。</summary>
/// <param name="ViewRestored">控えた見た目を全部戻せたか（戻せなかった項目は <paramref name="Notes"/> に。ログにだけ残す）。</param>
/// <param name="Notes">戻せなかった項目などの覚え書き（ログ用。利用者には見せない）。</param>
public sealed record VbaWorkbookReopenOutcome(bool ViewRestored, IReadOnlyList<string> Notes);

/// <summary>
/// 「止めるブック」の判定（16.6 の表。純ロジック）。開いているブックの状態から、書き換えを始める前に止める問題を返す
/// （E819: 1 文字も書かず、閉じもしない／E813: マクロの実行中・中断中）。止めなければ null。
/// <para>
/// 準備（16.5 の段階 3）・確認のあとの比べ直し・ブリッジの「保存して閉じる」の直前で、同じこの判定を使う（ブリッジは COM から読んだ値で
/// もう一度呼ぶ。確認のあいだに読み取り専用で開き直された、などを最後に止めるため）。
/// </para>
/// </summary>
public static class VbaWorkbookRewriteGuard
{
    /// <summary>
    /// 止める問題を返す。並びは「利用者がすることが大きいもの」が先（URL → 保護ビュー → アドイン → 読み取り専用 → 共有 → 自動保存 →
    /// マクロの実行中）。複数当てはまっても 1 つだけ返す（1 つ直したら、次の試みで次のものを案内する）。
    /// </summary>
    /// <param name="state">ブックの状態。開いていなければ null を返す（F3 の経路）。</param>
    /// <param name="workbookFullName">ブックのフルパス（問題の <c>Path</c> と、アドインかの判定に使う）。</param>
    public static GraftIssue? Check(VbaWorkbookRewriteState state, string workbookFullName)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(workbookFullName);
        if (!state.IsOpen) return null;

        var name = VbaRibbonRewriteText.FileNameOf(workbookFullName);
        string? reason = null;
        if (state.IsUrlLocation || VbaRotNames.IsUrlLocation(workbookFullName))
        {
            reason = $"「{name}」は OneDrive／SharePoint 上のブックのため、リボンを書き換えられません。ローカルのフォルダへ保存し直してから、もう一度反映してください。";
        }
        else if (state.IsProtectedView)
        {
            reason = $"「{name}」は保護ビューで開いています。Excel で「編集を有効にする」を押してから、もう一度反映してください。";
        }
        else if (state.IsAddin || VbaRibbonRewriteText.IsAddinPath(workbookFullName))
        {
            // 開いているアドインは Graft が閉じて開き直さない（16.16-2 の決定）。インストール済みの状態・ほかのブックからの参照があり、
            // 閉じ方と戻し方が通常のブックと違うため。閉じていれば、ファイルだけを書き換える経路（F3）で書ける。
            reason = VbaRibbonRewriteText.OpenAddinReason(name);
        }
        else if (state.ReadOnly)
        {
            reason = $"「{name}」は読み取り専用で開いています。保存できないため、閉じる前の保存で止まってしまいます。Excel でブックを閉じて、書き込める状態で開き直してから、もう一度反映してください。";
        }
        else if (state.MultiUserEditing)
        {
            reason = $"「{name}」は共有ブックです。Excel で共有を解除してから、もう一度反映してください。";
        }
        else if (state.AutoSaveOn)
        {
            reason = $"「{name}」は自動保存がオンです。Excel の左上の［自動保存］をオフにしてから、もう一度反映してください。";
        }

        if (reason is not null) return GraftIssue.Of(ErrorCode.E819, reason + " ブックは何も変えていません。", path: workbookFullName);

        if (state.ProjectMode is { } mode && !VbaProjectState.IsDesignMode(mode))
        {
            return GraftIssue.Of(ErrorCode.E813,
                $"「{name}」のマクロが実行中または中断中です（VBProject.Mode={mode.ToString(CultureInfo.InvariantCulture)}）。ブックを閉じるとマクロの途中の状態が失われるため、閉じずに止めました。ブックは何も変えていません。",
                path: workbookFullName);
        }

        return null;
    }
}

/// <summary>
/// 開き直したあとに見た目を戻す手順の決め方（16.6「開き直す」の 2。純ロジック）。ブリッジはこの結果のとおりに COM を呼ぶ。
/// <para>
/// 【位置と大きさは「通常」のときだけ戻す】 最大化・最小化のウィンドウに <c>Left</c> などを書くと、Excel は「通常」へ戻してから動かすので、
/// 利用者の見ていた最大化の表示が崩れる。最大化・最小化なら <c>WindowState</c> だけを戻す。
/// </para>
/// </summary>
public static class VbaWorkbookViewRestore
{
    /// <summary><c>xlMaximized</c>。</summary>
    public const int Maximized = -4137;

    /// <summary><c>xlMinimized</c>。</summary>
    public const int Minimized = -4140;

    /// <summary><c>xlNormal</c>。</summary>
    public const int Normal = -4143;

    /// <summary>ウィンドウの状態を書き戻すか（知っている 3 つの値のときだけ）。</summary>
    public static bool RestoresWindowState(VbaWorkbookViewState view)
        => view?.WindowState is Maximized or Minimized or Normal;

    /// <summary>位置と大きさを書き戻すか（「通常」で、4 つとも取れていて、大きさが正のときだけ）。</summary>
    public static bool RestoresBounds(VbaWorkbookViewState view)
        => view is { WindowState: Normal, Left: not null, Top: not null, Width: > 0, Height: > 0 };

    /// <summary>前にアクティブだったブックを前面へ戻すか（閉じる前にアクティブでなく、相手が分かっているときだけ）。</summary>
    public static bool ReactivatesPrevious(VbaWorkbookViewState view, string reopenedFullName)
        => view is { WasActive: false, PreviouslyActiveWorkbook: { Length: > 0 } previous }
           && !VbaRotNames.SamePath(previous, reopenedFullName);
}
