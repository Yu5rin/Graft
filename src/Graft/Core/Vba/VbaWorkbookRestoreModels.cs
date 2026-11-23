using System.Globalization;

namespace Graft.Core.Vba;

/// <summary>
/// ブックの丸ごとの控え 1 件（<c>vba-sync/&lt;プロジェクトId&gt;.workbooks/</c> のファイル。仕様書_ブックの情報と依頼の観点 16.11）。
/// 「Excel 側の控えを見る」窓の「ブックの控え」の一覧の 1 行。
/// </summary>
/// <param name="FileName">ファイル名（フォルダを含まない。<see cref="VbaWorkbookBackupNaming"/> の形）。</param>
/// <param name="FullPath">フルパス。</param>
/// <param name="Name">解析したファイル名（ブック名・日時・連番・元の拡張子）。</param>
/// <param name="Size">バイト数。</param>
public sealed record VbaWorkbookBackupItem(string FileName, string FullPath, VbaWorkbookBackupFileName Name, long Size)
{
    /// <summary>日時（秒まで。一覧の表示と並びに使う）。</summary>
    public DateTime At => Name.At;

    /// <summary>日時の表示（<c>2026-11-23 10:00:00</c>）。</summary>
    public string TimeText => Name.At.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}

/// <summary>
/// 「この控えでブックを置き換える…」の計画（準備〔<c>VbaRibbonSyncService.PrepareRestoreFromBackupAsync</c>〕の結果。確認ダイアログの材料）。
/// 控えを取ったあとの<b>シートの編集・VBA の変更もすべて戻る</b>強い操作なので、確認の文面に、何が戻るか・開いているブックはどう扱うかを載せる
/// （<see cref="VbaWorkbookRestoreText"/>）。
/// </summary>
/// <param name="ProjectId">プロジェクトの Id。</param>
/// <param name="WorkbookPath">置き換えるブックのファイル（フルパス）。</param>
/// <param name="WorkbookName">ブックのファイル名。</param>
/// <param name="BackupPath">戻す控えのフルパス。</param>
/// <param name="BackupFileName">戻す控えのファイル名。</param>
/// <param name="BackupAt">控えを取った日時。</param>
/// <param name="BackupSize">控えの大きさ。</param>
/// <param name="BackupSha256">控えの SHA-256（16 進の小文字。置き換えのたびに照合する）。</param>
/// <param name="BackupDirectory">控えのフォルダ（いまのブックも、ここへ控えてから置き換える）。</param>
/// <param name="BookSize">いまのブックの大きさ（確認のあいだに変わっていないかを比べる）。</param>
/// <param name="BookModifiedUtc">いまのブックの更新日時。</param>
/// <param name="OpenState">いまのブックの Excel での状態（開いているときだけ。閉じて開き直す経路になる）。</param>
public sealed record VbaWorkbookRestorePlan(
    string ProjectId,
    string WorkbookPath,
    string WorkbookName,
    string BackupPath,
    string BackupFileName,
    DateTime BackupAt,
    long BackupSize,
    string BackupSha256,
    string BackupDirectory,
    long BookSize,
    DateTime BookModifiedUtc,
    VbaWorkbookRewriteState? OpenState = null)
{
    /// <summary>
    /// 控えを取ったブックのフルパスが、いま置き換えるブックと違うとき、その控えのブックのフルパス（同じ、または分からないときは null）。
    /// 控えを取ったときに残した記録、記録が無い控えは同期状態に残っている紐付け先と比べる。
    /// <para>
    /// 【止めずに強く確認する理由】 名前と拡張子が同じなので、結び直す前のブックや、ブックを別の場所へ移した場合は、実際に同じブックの控え
    /// でありうる（止めると、その正当な使い方を塞ぐ）。いっぽう名前だけが同じ別のブックなら、まったく別の内容に置き換わる。どちらかを
    /// Graft は判断できないので、利用者に判断を委ねる。ただし、取り返しがつかなくならないよう、確認は既定が「やめる」の強い文面にし、置き換える前の
    /// ブックは必ず控えに残す（置き換えたあとでも元に戻せる）。
    /// </para>
    /// </summary>
    public string? OtherLocationWorkbook { get; init; }

    /// <summary>計画を作ったとき、ブックは Excel で開いていたか（保存して閉じ、置き換えて、開き直す経路か）。</summary>
    public bool IsOpen => OpenState is { IsOpen: true };

    /// <summary>開いていて、保存していない変更があると見えるか（文面の注意。保存は必ず行う）。</summary>
    public bool HasUnsavedChanges => OpenState is { IsOpen: true, Saved: false };
}

/// <summary>
/// 「この控えでブックを置き換える…」の確認の文面（純ロジック。仕様書_ブックの情報と依頼の観点 16.11）。
/// <para>
/// 【文面の方針】 リボンの書き換え（<see cref="VbaRibbonRewriteText"/>）より強く書く。リボンの書き換えは「リボンだけを差し替える」ので、
/// 利用者の編集は残るが、この操作は<b>控えを取ったあとのすべて</b>（セルの値・シートの追加・VBA の変更・リボン）が戻る。
/// 既定のボタンは「やめる」。リボンだけを前の状態へ戻したいときは、これではなく履歴の取り消しを使う（ミラーの XML が戻り、もう一度反映する）と
/// 文中に書く。
/// </para>
/// </summary>
public static class VbaWorkbookRestoreText
{
    /// <summary>確認ダイアログの題。</summary>
    public const string Title = "この控えでブックを置き換えますか";

    /// <summary>実行のボタン。</summary>
    public const string ConfirmButton = "控えの内容で置き換える";

    /// <summary>中止のボタン（既定。Enter と Esc もこれ）。</summary>
    public const string CancelButton = "やめる";

    /// <summary>手順（番号つき）。開いているブックは 4 つ、開いていないブックは 2 つ。</summary>
    public static IReadOnlyList<string> Steps(VbaWorkbookRestorePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.IsOpen
            ? new[]
            {
                "1. ブックを保存して閉じる",
                "2. いまのブックを、控えとして丸ごと残す",
                "3. 選んだ控えの内容で、ブックを置き換える",
                "4. ブックを開き直す",
            }
            : new[]
            {
                "1. いまのブックを、控えとして丸ごと残す",
                "2. 選んだ控えの内容で、ブックを置き換える",
            };
    }

    /// <summary>本文（確認ダイアログの 1 つの文字列）。</summary>
    public static string Message(VbaWorkbookRestorePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var nl = Environment.NewLine;
        var text = plan.OtherLocationWorkbook is { } other
            ? "⚠ この控えは、別の場所のブックの控えです。" + nl +
              $"  控えを取ったブック: {other}" + nl +
              $"  いま置き換えるブック: {plan.WorkbookPath}" + nl +
              "  ブックを別の場所へ移した（結び直した）だけなら、同じブックの控えです。名前が同じだけの別のブックなら、まったく別の内容に置き換わります。" +
              "どちらか分からないときは、「やめる」を選んでください。" + nl + nl
            : string.Empty;
        text += $"「{plan.WorkbookName}」を、{plan.BackupAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} の控え" +
                   $"（{plan.BackupFileName}・{FormatSize(plan.BackupSize)}）の内容で置き換えます。" + nl + nl +
                   "⚠ 控えを取ったあとに行った変更は、すべて元に戻ります。" + nl +
                   "  ・シートの編集（セルの値・書式・シートの追加や削除など）" + nl +
                   "  ・VBA の変更（Graft の「Excel へ反映」で入れたものを含みます）" + nl +
                   "  ・リボンの変更" + nl + nl +
                   (plan.IsOpen
                       ? "このブックは Excel で開いているため、Graft が次の順に行います。"
                       : "このブックは Excel で開いていません。ファイルを置き換えます（Excel で開き直すことはしません）。") + nl +
                   string.Join(nl, Steps(plan).Select(s => "  " + s)) + nl;

        if (plan.HasUnsavedChanges)
        {
            text += nl + "⚠ このブックには、まだ保存していない変更があります。保存してから閉じ、その状態も控えに残してから置き換えます" +
                    "（置き換えたあとに戻したいときは、そのときの控えを使えます）。" + nl;
        }

        text += nl + "・置き換える前のブックは、控えとして丸ごと残します（この操作のあとでも、元に戻せます）。" +
                nl + "・控えの場所: " + plan.BackupDirectory + "（ブック全体の写しです。シートの値も含みます）";
        if (plan.IsOpen)
        {
            text += nl + "・閉じるとき・開くときに、ブックのマクロ（Workbook_BeforeClose・Workbook_Open など）が動きます。" +
                    nl + "・Excel の「元に戻す」の履歴と、選択していたセルは戻りません。";
        }

        text += nl + "・Graft の VBA のミラーと、リボンの同期の記録は変わりません。置き換えたあとの最初の取込で、ブックとミラーの違いを確かめます" +
                "（両方が違えば、衝突の確認が出ます）。" + nl + nl +
                "リボンだけを前の状態へ戻したいときは、これではなく、履歴の取り消しを使ってください（ミラーの XML が戻るので、" +
                "もう一度「リボンをブックへ反映…」を押します）。";
        return text;
    }

    /// <summary>成功の知らせ（ステータスの通知）。</summary>
    public static string SucceededNotice(string workbookName, string backupFileName, string preRestoreBackupFileName)
        => $"「{workbookName}」を控え（{backupFileName}）の内容で置き換えました。置き換える前のブックは、控え（{preRestoreBackupFileName}）に残しています。";

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.#} MB",
    };
}
