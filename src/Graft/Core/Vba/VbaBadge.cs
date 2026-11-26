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

    /// <summary>リボンに未反映の変更があるときの補足行の文言（仕様書_ブックの情報と依頼の観点 16.4）。</summary>
    public const string RibbonUnpushedText = "リボン未反映";

    /// <summary>
    /// 「リボン未反映」のツールチップ（16.13 の UI の表）。リボンはブックを閉じて開き直して反映する、重い操作であることを先に伝える。
    /// </summary>
    public const string RibbonUnpushedTip = "リボン（CustomUI/）に、ブックへまだ反映していない変更があります（ブックを閉じて開き直して反映します）";

    /// <summary>エクスプローラーの行に付ける、未反映のモジュールの文字バッジ（プロジェクトの補足行の「未反映 N」と同じ語）。</summary>
    public const string ModuleUnpushedBadge = "未反映";

    /// <summary>未反映のモジュール（1 つ）の行のツールチップ。プロジェクトのバッジのツールチップと同じ「右クリックの「Excel へ反映」で送れます」を使う。</summary>
    public const string ModuleUnpushedFileTip = "未反映のモジュール（右クリックの「Excel へ反映」で送れます）";

    /// <summary>未反映のものが中にあるフォルダ（<c>CustomUI</c> など）の行に付ける小さな印のツールチップ。</summary>
    public const string FolderContainsUnpushedTip = "中に、Excel（ブック）へまだ反映していないファイルがあります";

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
    /// <param name="saveConflictCount">
    /// 保存時の自動反映（機能 D。仕様書_ブックの情報と依頼の観点 8.5）が、衝突のため送らなかったモジュール数。
    /// 起動をまたいで残さない（メモリだけの印。次に同じモジュールが衝突なしで反映できたら消える）。既定は 0 で、
    /// 既存の呼び出しと表示は変わらない。
    /// </param>
    /// <param name="needsReviewCount">
    /// 要確認（書き戻しに失敗したままの）モジュール数。同期状態に残るので、起動をまたいで出る。既定は 0 で、既存の表示は変わらない。
    /// </param>
    /// <param name="ribbonUnpushed">
    /// リボン（<c>CustomUI/</c>）に、ブックへまだ反映していない変更があるか（仕様書_ブックの情報と依頼の観点 16.4）。補足行に
    /// 「リボン未反映」を出す。<b>件数は出さない</b>（多くても 2 ファイルで、数に意味が無い）。既定は false で、既存の表示は変わらない。
    /// </param>
    public static VbaBadgeState Compute(
        bool isSupported, bool isEnabled, bool? workbookOpen, int unpushedCount, int deletePendingCount, int saveConflictCount = 0,
        int needsReviewCount = 0, bool ribbonUnpushed = false)
    {
        if (!isSupported) return VbaBadgeState.Hidden;

        var unpushed = Math.Max(0, unpushedCount);
        var pending = Math.Max(0, deletePendingCount);
        var conflicts = Math.Max(0, saveConflictCount);
        var review = Math.Max(0, needsReviewCount);
        var status = BuildStatusText(unpushed, pending, conflicts, review, ribbonUnpushed);

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
        if (conflicts > 0)
        {
            // 衝突は、保存のたびにダイアログを出さない代わりに、ここへ印だけ残す。解決は手動の「Excel へ反映」の衝突画面で行う。
            lines.Add($"保存時の自動反映をしなかった衝突: {conflicts} 件（右クリックの「Excel へ反映」で、どちらを残すか選べます）");
        }
        if (review > 0)
        {
            lines.Add(
                $"要確認のモジュール: {review} 件（前回の書き込みで Excel 側を元に戻せませんでした。VBE で内容を確かめ、" +
                "右クリックの「Excel から取込」「Excel へ反映」で、どちらを残すか選んでください。控えは .rescue/ にあります）");
        }
        if (ribbonUnpushed) lines.Add(RibbonUnpushedTip);
        if (lines.Count == 0) lines.Add("Excel VBA プロジェクトです。");

        return new VbaBadgeState(true, "VBA", workbookOpen == false, status, string.Join(Environment.NewLine, lines));
    }

    private static string BuildStatusText(int unpushed, int pending, int conflicts, int review, bool ribbonUnpushed)
    {
        var parts = new List<string>();
        if (unpushed > 0) parts.Add($"未反映 {unpushed}");
        // モジュールの未反映の隣に置く（どちらも「ブックへまだ届いていない」の知らせなので、並べて読めるように）。
        if (ribbonUnpushed) parts.Add(RibbonUnpushedText);
        if (pending > 0) parts.Add($"削除保留 {pending}");
        if (conflicts > 0) parts.Add($"衝突 {conflicts}");
        if (review > 0) parts.Add($"要確認 {review}");
        return string.Join("・", parts);
    }
}
