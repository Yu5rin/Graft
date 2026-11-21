namespace Graft.Core.Vba;

/// <summary>プロジェクトペインの「VBA」バッジに出す内容（仕様書_VBA連携 7.6）。</summary>
/// <param name="IsVisible">バッジを出すか（この環境で VBA 連携が使えないときは出さない）。</param>
/// <param name="Text">バッジの文言（「VBA」または「VBA 連携オフ」）。</param>
/// <param name="IsGray">グレー表示にするか（ブックが開いていない、または機能がオフ）。</param>
/// <param name="StatusText">名前の下の補足（「未反映 N・削除保留 N」。無ければ空）。</param>
/// <param name="Tooltip">ツールチップ（無ければ空）。</param>
public sealed record VbaBadgeState(bool IsVisible, string Text, bool IsGray, string StatusText, string Tooltip)
{
    /// <summary>出さない（Linux などで VBA 連携が使えない環境、または VBA プロジェクトではない行）。</summary>
    public static VbaBadgeState Hidden { get; } = new(false, string.Empty, false, string.Empty, string.Empty);
}

/// <summary>
/// バッジの状態を決める純ロジック（仕様書_VBA連携 7.6）。COM にもファイルにも触れない。
/// <para>
/// 【ここへ切り出した理由】 バッジは「機能の有無」「設定のオン・オフ」「ブックが開いているか」「未反映・削除保留の件数」の
/// 組み合わせで決まる。画面（ViewModel）の中に条件分岐を散らすと、組み合わせの抜けがテストで見えにくい。
/// 入力を値だけにして、全組み合わせを <c>Graft.Tests</c> で網羅できるようにしておく。
/// </para>
/// </summary>
public static class VbaBadgeLogic
{
    /// <summary>ブックが開いていないときのツールチップ（仕様 7.6 の文言そのまま）。</summary>
    public const string WorkbookClosedTip = "ブックが開いていません";

    /// <summary>設定で機能を切っているときのバッジの文言（仕様 7.6）。</summary>
    public const string OffText = "VBA 連携オフ";

    /// <summary>
    /// バッジの状態を求める。
    /// </summary>
    /// <param name="isSupported">この環境で VBA 連携が使えるか（Linux・Null ブリッジなら false で、バッジごと出さない）。</param>
    /// <param name="isEnabled">設定 <c>Vba.Enabled</c>。</param>
    /// <param name="workbookOpen">
    /// ブックが開いているか。まだ調べていない（またはブリッジが調べられなかった）ときは null で、
    /// その場合は「開いていない」と決めつけない（グレーにしない）。
    /// </param>
    /// <param name="unpushedCount">未反映（Push 判定）のモジュール数。</param>
    /// <param name="deletePendingCount">削除保留のモジュール数。</param>
    public static VbaBadgeState Compute(bool isSupported, bool isEnabled, bool? workbookOpen, int unpushedCount, int deletePendingCount)
    {
        if (!isSupported) return VbaBadgeState.Hidden;

        var unpushed = Math.Max(0, unpushedCount);
        var pending = Math.Max(0, deletePendingCount);
        var status = BuildStatusText(unpushed, pending);

        if (!isEnabled)
        {
            // 機能を切っていても件数は出す（ミラーは普通のファイルとして編集できるので、未反映が溜まることがあり、
            // 再び有効にしたときに送る必要があると分かるように）。ただし Excel には触れないので、開閉は見ない。
            return new VbaBadgeState(
                true, OffText, true, status,
                "設定の「VBA 連携」で機能を切っています。このプロジェクトは普通のファイルとして扱われ、Excel との同期は行いません。");
        }

        var lines = new List<string>();
        if (workbookOpen == false) lines.Add(WorkbookClosedTip);
        if (unpushed > 0) lines.Add($"未反映のモジュール: {unpushed} 件（右クリックの「Excel へ反映」で送れます）");
        if (pending > 0) lines.Add($"削除保留のモジュール: {pending} 件（右クリックの「Excel から復元」で戻せます）");
        if (lines.Count == 0) lines.Add("Excel VBA プロジェクトです。");

        return new VbaBadgeState(true, "VBA", workbookOpen == false, status, string.Join(Environment.NewLine, lines));
    }

    private static string BuildStatusText(int unpushed, int pending)
    {
        var parts = new List<string>();
        if (unpushed > 0) parts.Add($"未反映 {unpushed}");
        if (pending > 0) parts.Add($"削除保留 {pending}");
        return string.Join("・", parts);
    }
}
