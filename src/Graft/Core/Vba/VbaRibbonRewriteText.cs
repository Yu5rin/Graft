using System.Globalization;

namespace Graft.Core.Vba;

/// <summary>
/// リボンの書き換えの確認ダイアログ（仕様書_ブックの情報と依頼の観点 16.9）と、結果の知らせの文言（純ロジック）。
/// 画面（<c>VbaRibbonRewriteWindow</c>）から切り離して置き、文面の出し分けを <c>Graft.Tests</c> で固定する。
/// <para>
/// 【文面の方針】 ボタンを動詞にし、何が起きるかを手順の番号で示す（16.9）。段階 F3 は Excel で開いていないブックだけを
/// 書き換える経路なので、手順は「控えを残す → XML を差し替える」の 2 つで、マクロ・「元に戻す」の行は出さない
/// （ブックを閉じも開きもしないため。開いているブックの文面は F4 で足す）。
/// </para>
/// </summary>
public static class VbaRibbonRewriteText
{
    /// <summary>確認ダイアログの題。</summary>
    public const string Title = "リボンをブックへ反映";

    /// <summary>実行のボタン（開いていないブック。16.9 の表の「開いていない」の行）。</summary>
    public const string RewriteButton = "書き換える";

    /// <summary>中止のボタン（既定。Enter と Esc もこれ）。</summary>
    public const string CancelButton = "中止";

    /// <summary>差分を見るボタン。</summary>
    public const string DiffButton = "差分を見る";

    /// <summary>警告の一覧に出す件数の上限（16.9。超えた分は「ほか N 件」にまとめ、全件は「すべて見る」で開く）。</summary>
    public const int MaxWarningsShown = 5;

    /// <summary>Excel で開いているブックを止めるときの案内（E819 の詳細）。</summary>
    public static string OpenWorkbookReason(string workbookName)
        => $"「{workbookName}」は Excel で開いています。Excel でブックを閉じてから、もう一度「リボンをブックへ反映…」を選んでください（閉じたブックのファイルを書き換えます。Excel の画面は変わりません）。";

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

    /// <summary>冒頭の文（何をするか）。</summary>
    public static string Lead(VbaRibbonRewritePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return $"「{plan.WorkbookName}」のリボン（customUI）を書き換えます。" + Environment.NewLine +
               "ブックは Excel で開いていません。ファイルを書き換えます（Excel で開き直すことはしません）。";
    }

    /// <summary>
    /// 手順（番号つき）。開いていないブックは「控えを残す → XML を差し替える（新しく足す）」の 2 つ。
    /// 新旧の 2 形式を書くときも 1 回の書き換えにまとめる（16.5）ので、手順は増やさず、括弧の中にファイルを並べる。
    /// </summary>
    public static IReadOnlyList<string> Steps(VbaRibbonRewritePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var replaced = plan.Targets.Where(t => !t.IsNewPart).Select(t => t.MirrorPath).ToList();
        var added = plan.Targets.Where(t => t.IsNewPart).Select(t => t.MirrorPath).ToList();
        var parts = new List<string>();
        if (replaced.Count > 0) parts.Add($"リボンの XML を差し替える（{string.Join("・", replaced)}）");
        if (added.Count > 0) parts.Add($"リボンの XML を新しく足す（{string.Join("・", added)}）");
        return new[]
        {
            "1. 書き換える前のブックを、控えとして丸ごと残す",
            "2. " + string.Join("、", parts),
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
        var text = Lead(plan) + nl + nl + string.Join(nl, Steps(plan).Select(s => "  " + s)) + nl + nl + "・" + BackupLine(plan);
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

    /// <summary>
    /// 初めて足したときの添え書き（16.8「Excel が実際にリボンを読み込めたかは確かめられない」。Excel の設定で知らせを出せる）。
    /// </summary>
    public const string FirstAddHint =
        "Excel がリボンを読み込めたかは、Graft からは確かめられません。Excel の［ファイル］→［オプション］→［詳細設定］→［全般］の" +
        "「アドイン ユーザー インターフェイスのエラーを表示する」をオンにすると、読み込めないときに Excel が知らせます。";
}
