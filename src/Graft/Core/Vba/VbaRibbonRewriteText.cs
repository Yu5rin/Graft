using System.Globalization;

namespace Graft.Core.Vba;

/// <summary>
/// リボンの書き換えの確認ダイアログ（仕様書_ブックの情報と依頼の観点 16.9）と、結果の知らせの文言（純ロジック）。
/// 画面（<c>VbaRibbonRewriteWindow</c>）から切り離して置き、文面の出し分けを <c>Graft.Tests</c> で固定する。
/// <para>
/// 【文面の方針】 ボタンを動詞にし、何が起きるかを手順の番号で示す（16.9）。出し分けは 4 通り（開いていない／開いていて未保存なし／
/// 開いていて未保存あり／VBA の反映に続けて）。開いていないブックの手順は「控えを残す → XML を差し替える」の 2 つで、マクロ・
/// 「元に戻す」の行は出さない（ブックを閉じも開きもしないため）。開いているブックは「保存して閉じる → 控え → 差し替え → 開き直す」の 4 つで、
/// Excel の画面が一度消えること・マクロが動くことを、実行の前に利用者が分かるようにする。
/// </para>
/// </summary>
public static class VbaRibbonRewriteText
{
    /// <summary>確認ダイアログの題。</summary>
    public const string Title = "リボンをブックへ反映";

    /// <summary>実行のボタン（開いていないブック。16.9 の表の「開いていない」の行）。</summary>
    public const string RewriteButton = "書き換える";

    /// <summary>
    /// 実行のボタン（開いているブック。未保存の変更の有無にかかわらず同じ。保存は必ず行うので、文面を変えて嘘を書かない。16.9）。
    /// </summary>
    public const string SaveAndRewriteButton = "保存して書き換える";

    /// <summary>実行のボタンの文言（開いているかで変える）。</summary>
    public static string ConfirmButtonText(VbaRibbonRewritePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.IsOpen ? SaveAndRewriteButton : RewriteButton;
    }

    /// <summary>中止のボタン（既定。Enter と Esc もこれ）。</summary>
    public const string CancelButton = "中止";

    /// <summary>差分を見るボタン。</summary>
    public const string DiffButton = "差分を見る";

    /// <summary>警告の一覧に出す件数の上限（16.9。超えた分は「ほか N 件」にまとめ、全件は「すべて見る」で開く）。</summary>
    public const int MaxWarningsShown = 5;

    /// <summary>Excel で開いているアドイン（.xlam）を止めるときの案内（E819 の詳細。16.16-2）。</summary>
    public static string OpenAddinReason(string workbookName)
        => $"「{workbookName}」は Excel でアドインとして開いています。Excel でアドインを閉じて（[開発] タブの [Excel アドイン] でチェックを外すか、Excel を終了して）から、もう一度「リボンをブックへ反映…」を選んでください。";

    /// <summary>パスの最後の部分（ファイル名）。ブックのパスは Windows の形で来るので、<c>/</c> と <c>\</c> の両方で切る。</summary>
    public static string FileNameOf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path[(path.LastIndexOfAny(new[] { '/', '\\' }) + 1)..];
    }

    /// <summary>アドイン（.xlam）か（拡張子で見る）。</summary>
    public static bool IsAddinPath(string path)
        => FileNameOf(path).EndsWith(".xlam", StringComparison.OrdinalIgnoreCase);

    /// <summary>VBA の反映に続けて出すときの冒頭の一文（0 件なら null。16.9）。</summary>
    public static string? PrecedingVbaLine(VbaRibbonRewritePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.PrecedingVbaModules > 0
            ? $"VBA のモジュール {plan.PrecedingVbaModules.ToString(CultureInfo.InvariantCulture)} 件は Excel へ反映しました。続けてリボンを反映します。"
            : null;
    }

    /// <summary>冒頭の文（何をするか。開いているかで言い方を変える）。</summary>
    public static string Lead(VbaRibbonRewritePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var nl = Environment.NewLine;
        var head = PrecedingVbaLine(plan) is { } preceding ? preceding + nl : string.Empty;
        return plan.IsOpen
            ? head + $"「{plan.WorkbookName}」のリボン（customUI）を書き換えます。" + nl +
              "Excel で開いているブックは書き換えられないため、Graft が次の順に行います。"
            : head + $"「{plan.WorkbookName}」のリボン（customUI）を書き換えます。" + nl +
              "ブックは Excel で開いていません。ファイルを書き換えます（Excel で開き直すことはしません）。";
    }

    /// <summary>
    /// 未保存の変更の注意（開いていて <c>Saved</c> が False のときだけ。それ以外は null）。「再計算だけでもこの表示になります」を
    /// 添えるのは、<c>NOW()</c>・<c>RAND()</c> を含むブックは何も編集しなくても未保存と見えるため（16.6 の表）。
    /// </summary>
    public static string? UnsavedWarning(VbaRibbonRewritePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.HasUnsavedChanges
            ? "⚠ このブックには、まだ保存していない変更があります。続けると、その変更も保存されます（再計算だけでも、この表示になります）。"
            : null;
    }

    /// <summary>
    /// 開いているブックの注意（マクロが動くこと・戻らないもの。開いていないときは空）。ブックを閉じも開きもしない経路では、
    /// どちらも起きないので出さない（16.9 の表）。
    /// </summary>
    public static IReadOnlyList<string> OpenNotices(VbaRibbonRewritePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.IsOpen) return Array.Empty<string>();
        return new[]
        {
            "閉じるとき・開くときに、ブックのマクロ（Workbook_BeforeClose・Workbook_Open など）が動きます。",
            "Excel の「元に戻す」の履歴と、選択していたセルは戻りません。",
        };
    }

    /// <summary>
    /// 手順（番号つき）。開いていないブックは「控えを残す → XML を差し替える（新しく足す）」の 2 つ、開いているブックは
    /// 「保存して閉じる → 控え → 差し替え → 開き直す」の 4 つ。新旧の 2 形式を書くときも 1 回の書き換えにまとめる（16.5）ので、
    /// 手順は増やさず、括弧の中にファイルを並べる。
    /// </summary>
    public static IReadOnlyList<string> Steps(VbaRibbonRewritePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var replaced = plan.Targets.Where(t => !t.IsNewPart).Select(t => t.MirrorPath).ToList();
        var added = plan.Targets.Where(t => t.IsNewPart).Select(t => t.MirrorPath).ToList();
        var parts = new List<string>();
        if (replaced.Count > 0) parts.Add($"リボンの XML を差し替える（{string.Join("・", replaced)}）");
        if (added.Count > 0) parts.Add($"リボンの XML を新しく足す（{string.Join("・", added)}）");
        var rewrite = string.Join("、", parts);
        if (!plan.IsOpen)
        {
            return new[] { "1. 書き換える前のブックを、控えとして丸ごと残す", "2. " + rewrite };
        }

        return new[]
        {
            "1. ブックを保存して閉じる",
            "2. 書き換える前のブックを、控えとして丸ごと残す",
            "3. " + rewrite,
            "4. ブックを開き直す",
        };
    }

    /// <summary>控えの場所の行（ブック全体が残ることを明記する。14 章の「シートの値をディスクに書かない」の例外のため。16.11）。</summary>
    public static string BackupLine(VbaRibbonRewritePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return $"控えの場所: {plan.BackupDirectory}（ブック全体の写しです。シートの値も含みます）";
    }

    /// <summary>警告の見出し（無ければ null）。</summary>
    public static string? WarningsHeading(VbaRibbonRewritePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.Warnings.Count == 0 ? null : $"検査の警告: {plan.Warnings.Count.ToString(CultureInfo.InvariantCulture)} 件（書き換えは止めません）";
    }

    /// <summary>警告の一覧（先頭 <see cref="MaxWarningsShown"/> 件と「ほか N 件」。<paramref name="all"/> なら全件）。</summary>
    public static IReadOnlyList<string> WarningLines(VbaRibbonRewritePlan plan, bool all = false)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var lines = plan.Warnings.Select(w => w.ToDisplayText()).ToList();
        if (all || lines.Count <= MaxWarningsShown) return lines;
        var shown = lines.Take(MaxWarningsShown).ToList();
        shown.Add($"ほか {(lines.Count - MaxWarningsShown).ToString(CultureInfo.InvariantCulture)} 件");
        return shown;
    }

    /// <summary>ダイアログ全体の文面（読み上げ・テスト・ログ用に 1 つの文字列にしたもの）。</summary>
    public static string FullText(VbaRibbonRewritePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var nl = Environment.NewLine;
        var text = Lead(plan) + nl + nl + string.Join(nl, Steps(plan).Select(s => "  " + s));
        if (UnsavedWarning(plan) is { } unsaved) text += nl + nl + unsaved;
        text += nl;
        foreach (var notice in OpenNotices(plan)) text += nl + "・" + notice;
        text += nl + "・" + BackupLine(plan);
        if (WarningsHeading(plan) is { } heading)
        {
            text += nl + "・" + heading + nl + string.Join(nl, WarningLines(plan).Select(l => "    - " + l));
        }

        return text;
    }

    /// <summary>成功の知らせ（ステータスの通知。16.10）。</summary>
    public static string SucceededNotice(VbaRibbonRewriteOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return $"リボンをブックへ反映しました（{string.Join("・", outcome.Written)}。控え: {FileNameOf(outcome.BackupPath)}）。";
    }

    /// <summary>処理中の表示に出す段階の名前（16.5。<c>VbaRibbonSyncService.Progress</c> へ渡す）。</summary>
    public const string ProgressSaveAndClose = "ブックを保存して閉じています";

    /// <summary>処理中の表示: 控えを残している。</summary>
    public const string ProgressBackup = "控えを残しています";

    /// <summary>処理中の表示: XML を差し替えている。</summary>
    public const string ProgressRewrite = "リボンを書き換えています";

    /// <summary>処理中の表示: 開き直している。</summary>
    public const string ProgressReopen = "ブックを開き直しています";

    /// <summary>
    /// 失敗の案内に添える、「Excel で開く」を促す一文。画面はこの文を含む E820 と E822 に［Excel で開く］を出す
    /// （<see cref="OffersOpenInExcel"/>）。
    /// </summary>
    public const string OpenInExcelSentence = "Excel でブックを開いてください。";

    /// <summary>控えから戻せなかったとき（E823）に添える一文（開き直さない理由）。</summary>
    public const string ClosedLeftNote =
        "Excel ではブックを閉じたままにしています（壊れているかもしれないブックで、マクロを動かさないため）。控えのファイルで置き換えてから、Excel で開いてください。";

    /// <summary>
    /// 利用者が直してすぐ押し直せる一時的な失敗か（16.10。失敗の知らせに［もう一度試す］を出す）: Excel が応答しない・セル編集中（E805）、
    /// マクロの実行中・中断中（E813）、ほかのプログラムが使用中（E821）。F3 で出していた「Excel で開いている（E819）」は、閉じ開きの
    /// 経路に置き換わったので外した（読み取り専用・共有などの E819 は、利用者が Excel で直す必要があり、押し直しても同じなので出さない）。
    /// </summary>
    public static bool IsRetryable(GraftIssue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        return issue.Code is ErrorCode.E805 or ErrorCode.E813
               || (issue.Code == ErrorCode.E821 && (issue.Detail ?? string.Empty).Contains("ほかのプログラムが使っている", StringComparison.Ordinal));
    }

    /// <summary>
    /// 失敗の知らせに［Excel で開く］を出すか（開き直せなかった E822、時間切れのあとで閉じられていた E820）。
    /// </summary>
    public static bool OffersOpenInExcel(GraftIssue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        return issue.Code == ErrorCode.E822
               || (issue.Code == ErrorCode.E820 && (issue.Detail ?? string.Empty).Contains(OpenInExcelSentence, StringComparison.Ordinal));
    }

    /// <summary>「Excel で開く」のボタンの文言（開き直せなかったとき・閉じたままのとき。16.6）。</summary>
    public const string OpenInExcelButton = "Excel で開く";

    /// <summary>閉じたあとに止まった失敗の知らせに添える一文（すべて開き直したことを伝える。16.11 の表「閉じていたなら開き直す」）。</summary>
    public const string ReopenedNote = "ブックは Excel で開き直しました。";

    /// <summary>
    /// 初めて足したときの添え書き（16.8「Excel が実際にリボンを読み込めたかは確かめられない」。Excel の設定で知らせを出せる）。
    /// </summary>
    public const string FirstAddHint =
        "Excel がリボンを読み込めたかは、Graft からは確かめられません。Excel の［ファイル］→［オプション］→［詳細設定］→［全般］の" +
        "「アドイン ユーザー インターフェイスのエラーを表示する」をオンにすると、読み込めないときに Excel が知らせます。";
}
