using System.Globalization;

namespace Graft.Core.Vba;

/// <summary>
/// リボンの書き換え（仕様書_ブックの情報と依頼の観点 16.5）で、ブックへ書く 1 形式（<c>CustomUI/customUI14.xml</c> など）。
/// 確認ダイアログ（16.9）に見せる材料と、書き換えの直前に「確認のあいだにミラーが変わっていないか」を確かめる材料を兼ねる。
/// </summary>
/// <param name="Format">形式。</param>
/// <param name="MirrorPath">ミラーの正規の相対パス（<c>CustomUI/customUI14.xml</c>）。</param>
/// <param name="Xml">書く本文（ミラーのファイルの本文そのまま。検査を通ったもの）。</param>
/// <param name="MirrorHash">本文のハッシュ（<see cref="VbaTextNormalizer.Hash"/>）。同期基準にもこの値を書く。</param>
/// <param name="BookXml">いまのブックの部品の本文（差分を見せるため）。新しく足すときは null。</param>
/// <param name="IsNewPart">ブックにその形式の部品が無く、新しく足す（customUI の無いブックへの追加）。</param>
public sealed record VbaRibbonRewriteTarget(
    VbaRibbonFormat Format, string MirrorPath, string Xml, string MirrorHash, string? BookXml, bool IsNewPart);

/// <summary>確認ダイアログに出す検査の警告 1 件（どのファイルの警告か付き）。</summary>
/// <param name="MirrorPath">ミラーの相対パス。</param>
/// <param name="Finding">検査の結果（重さは警告）。</param>
public sealed record VbaRibbonRewriteWarning(string MirrorPath, VbaRibbonFinding Finding)
{
    /// <summary>一覧に出す 1 行（「customUI14.xml 12 行 5 列: …」）。</summary>
    public string ToDisplayText() => $"{VbaRibbonRewriteText.FileNameOf(MirrorPath)} {Finding.ToDisplayText()}";
}

/// <summary>
/// 書き換えの計画（16.5 の段階 1〜3 を済ませたもの）。確認ダイアログ（16.9）の材料で、利用者が「書き換える」を選んだら、
/// そのまま <c>VbaRibbonSyncService.RewriteAsync</c> へ渡す。
/// <para>
/// 【確認のあいだの変化を見張る値を持つ理由】 確認ダイアログを出しているあいだに、利用者がミラーを直したり、Excel でブックを
/// 開いて保存したりしうる。確認したのと違う内容を黙って書かないよう、本文のハッシュ（<see cref="VbaRibbonRewriteTarget.MirrorHash"/>）と
/// ブックの大きさ・更新日時（<see cref="BookSize"/>・<see cref="BookModifiedUtc"/>）を持ち、書く直前に比べ直す。
/// </para>
/// </summary>
/// <param name="ProjectId">プロジェクトの Id。</param>
/// <param name="WorkbookPath">書き換えるブックのファイル（フルパス）。</param>
/// <param name="WorkbookName">ブックのファイル名（表示用。<c>計画.xlsm</c>）。</param>
/// <param name="Targets">書く形式（1〜2 件。新しい形式が先）。</param>
/// <param name="Warnings">検査の警告（エラーがあれば計画にならない）。</param>
/// <param name="BackupDirectory">控えの置き場（<c>vba-sync/&lt;Id&gt;.workbooks/</c>）。確認ダイアログに出す。</param>
/// <param name="BookSize">計画を作ったときのブックの大きさ（バイト）。</param>
/// <param name="BookModifiedUtc">計画を作ったときのブックの更新日時（UTC）。</param>
/// <param name="OpenState">
/// 計画を作ったときの、ブックの Excel での状態（段階 F4。16.6）。開いていなければ null か <see cref="VbaWorkbookRewriteState.NotOpen"/>。
/// 確認ダイアログの文面（開いている／未保存の有無）と、書き換えの直前の「確認のあいだに状態が変わっていないか」の比べ直しに使う。
/// </param>
/// <param name="PrecedingVbaModules">
/// 同じ操作で、先に Excel へ反映した VBA のモジュールの数（0 なら冒頭の一文を出さない。16.9「VBA の反映に続けて出すとき」）。
/// 段階 F5 の呼び出し側が <c>plan with { PrecedingVbaModules = n }</c> で渡す。
/// </param>
public sealed record VbaRibbonRewritePlan(
    string ProjectId,
    string WorkbookPath,
    string WorkbookName,
    IReadOnlyList<VbaRibbonRewriteTarget> Targets,
    IReadOnlyList<VbaRibbonRewriteWarning> Warnings,
    string BackupDirectory,
    long BookSize,
    DateTime BookModifiedUtc,
    VbaWorkbookRewriteState? OpenState = null,
    int PrecedingVbaModules = 0,
    IReadOnlyList<string>? AutoMacros = null)
{
    /// <summary>
    /// ブックの標準モジュールにある <c>Auto_Open</c>・<c>Auto_Close</c>（ミラーの VBA を走査して分かったもの。無ければ空）。
    /// 実機で、COM の <c>Workbooks.Open</c>・<c>Close</c> ではこの 2 つが動かないと確かめた（2026-11-24。Windows実機検証手順 6-34 の 2-a・2-c）ので、
    /// 開いているブックの確認ダイアログに、動かないことを添える（<see cref="VbaRibbonRewriteText.OpenNotices"/>）。
    /// </summary>
    public IReadOnlyList<string> AutoMacroNames => AutoMacros ?? Array.Empty<string>();

    /// <summary>計画を作ったとき、ブックは Excel で開いていたか（閉じて開き直す経路か。16.6）。</summary>
    public bool IsOpen => OpenState is { IsOpen: true };

    /// <summary>
    /// 開いていて、保存していない変更があると見えるか（<c>Workbook.Saved</c> が False。確認ダイアログの ⚠ の段落を出す）。
    /// <b>文面を変えるためだけ</b>に使い、「保存するか」は決めない（必ず保存する。16.6「<c>Saved</c> の限界」）。
    /// </summary>
    public bool HasUnsavedChanges => OpenState is { IsOpen: true, Saved: false };

    /// <summary>新しく足す形式があるか（確認ダイアログの手順の言い方を変える）。</summary>
    public bool AddsNewPart => Targets.Any(t => t.IsNewPart);
}

/// <summary>書き換えの準備（16.5 の段階 1〜3）の結果の種類。</summary>
public enum VbaRibbonRewritePreparationKind
{
    /// <summary>書き換えの計画ができた（確認ダイアログを出す）。</summary>
    Ready,

    /// <summary>ブックへ反映するリボンの変更が無い（衝突だけのときも、ここ。衝突は <see cref="VbaRibbonRewritePreparation.Notes"/> に入る）。</summary>
    NothingToDo,

    /// <summary>未保存のリボンのファイルの保存を断られた（16.5 の段階 1。何も変えていない）。</summary>
    Declined,
}

/// <summary>書き換えの準備の結果（止めるべき問題は <see cref="GraftResult{T}"/> の失敗で返す）。</summary>
/// <param name="Kind">結果の種類。</param>
/// <param name="Plan">計画（<see cref="VbaRibbonRewritePreparationKind.Ready"/> のとき）。</param>
/// <param name="Notes">
/// 知らせておくこと（反映しない衝突〔E812〕、読めない形式〔E810〕など）。止める理由ではない。
/// </param>
/// <param name="Recovery">準備の最初に見つけて片付けた、途中で終わった前回の書き換え（無ければ null。16.11）。</param>
public sealed record VbaRibbonRewritePreparation(
    VbaRibbonRewritePreparationKind Kind, VbaRibbonRewritePlan? Plan, IReadOnlyList<GraftIssue> Notes, VbaRibbonRecovery? Recovery = null);

/// <summary>書き換えが済んだときの結果。</summary>
/// <param name="WorkbookPath">書き換えたブック。</param>
/// <param name="BackupPath">書き換える前のブックの控え（フルパス）。</param>
/// <param name="Written">書いたミラーのパス（<c>CustomUI/customUI14.xml</c>）。</param>
/// <param name="AddedNew">新しく足した形式があったか（customUI の無いブックへの追加）。</param>
/// <param name="WasOpen">書き換える前、ブックは Excel で開いていたか（Graft が保存して閉じ、開き直す経路だったか）。</param>
/// <param name="Reopened">
/// 開き直して、ブックが Excel で開いているか（<see cref="WasOpen"/> が true のときだけ意味がある）。false なら E822 の警告が付く。
/// 開いていなかったブックは Graft が開かないので false のまま。完了の通知に「Excel で開く」を添えるかの材料。
/// </param>
public sealed record VbaRibbonRewriteOutcome(
    string WorkbookPath, string BackupPath, IReadOnlyList<string> Written, bool AddedNew, bool WasOpen = false, bool Reopened = false);

/// <summary>作業の記録の段階（16.5 の段階 5〜12 のどこまで進んだか。途中で落ちたときの後始末の材料）。</summary>
public enum VbaRibbonRewriteStage
{
    /// <summary>記録を書いた（まだ何も変えていない）。</summary>
    Started,

    /// <summary>控えを書いた（ブックはまだ元のまま）。</summary>
    BackedUp,

    /// <summary>一時ファイルへ書き、置き換えに入る（置き換えは原子的なので、ブックは元か新のどちらか）。</summary>
    Replacing,

    /// <summary>置き換えた（仕上げの照合・基準の更新の前）。</summary>
    Replaced,

    /// <summary>仕上げの照合が合わず、控えから戻している最中。</summary>
    Restoring,
}

/// <summary>
/// リボンの書き換えの作業の記録（<c>vba-sync/&lt;プロジェクトId&gt;.ribbon-job.json</c>。16.5 の段階 5・16.11）。
/// 書き換えの最中だけ存在し、終われば消す。残っていれば、Graft が途中で落ちた印として次の起動・次のリボンの操作で後始末する。
/// <para>
/// 【書くもの・書かないもの】 ブックのパス・控えのパスとハッシュ・一時ファイルのパス・段階・書く本文のハッシュ。<b>XML の本文や
/// シートの値は書かない</b>（ログと同じ方針。16.12）。後始末に要るのは「ブックがいま元のままか、書き換え後か」の判定だけで、
/// それはハッシュで足りる。
/// </para>
/// </summary>
public sealed record VbaRibbonRewriteJob
{
    /// <summary>記録の形の版（いまは 1）。読めない版の記録は、後始末で中身を信用せずに捨てる。</summary>
    public int Version { get; init; } = 1;

    /// <summary>プロジェクトの Id。</summary>
    public string ProjectId { get; init; } = string.Empty;

    /// <summary>書き換えるブックのファイル（フルパス）。</summary>
    public string WorkbookPath { get; init; } = string.Empty;

    /// <summary>控えのパス（書く予定の場所。控えを書き終える前に落ちたときは、作りかけのファイルがここに残りうる）。</summary>
    public string? BackupPath { get; init; }

    /// <summary>控えの SHA-256（16 進の小文字。控えを書いて照合し終えてから入る）。</summary>
    public string? BackupSha256 { get; init; }

    /// <summary>一時ファイルのパス（ブックと同じフォルダ）。</summary>
    public string? TempPath { get; init; }

    /// <summary>控えから戻すときの一時ファイルのパス（ブックと同じフォルダ）。</summary>
    public string? RestoreTempPath { get; init; }

    /// <summary>段階。</summary>
    public VbaRibbonRewriteStage Stage { get; init; }

    /// <summary>書く本文のハッシュ（ミラーのパス → <see cref="VbaTextNormalizer.Hash"/>）。書き換え済みかの判定に使う。</summary>
    public Dictionary<string, string> ExpectedHashes { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Graft が Excel でブックを閉じたか（段階 F4 の、開いているブックの経路だけ true）。後始末の知らせに「Excel では閉じたままです」を
    /// 添えるかどうかに使う（F3 の経路では、もともと閉じていたブックなので添えない）。
    /// </summary>
    public bool ClosedWorkbook { get; init; }

    /// <summary>始めた日時。</summary>
    public DateTimeOffset StartedAt { get; init; }
}

/// <summary>途中で終わった書き換えのあと、ブックがどうなっているか。</summary>
public enum VbaRibbonInterruptedState
{
    /// <summary>元のまま（置き換えの前に止まった、または控えから戻せていた）。</summary>
    Original,

    /// <summary>書き換え済み（置き換えまで済んでいた）。</summary>
    Rewritten,

    /// <summary>元とも書き換え後とも違う（落ちたあとに、ほかで保存された見込み）、または読めない。</summary>
    Unknown,

    /// <summary>ブックのファイルが見つからない。</summary>
    Missing,
}

/// <summary>途中で終わった書き換えの後始末の結果（<c>VbaRibbonSyncService.RecoverInterruptedAsync</c>）。</summary>
/// <param name="Job">見つかった作業の記録（読めなかったときは null）。</param>
/// <param name="State">ブックの状態。</param>
/// <param name="Message">利用者への知らせ（1〜数文）。</param>
/// <param name="RemovedTempFiles">消した一時ファイルの数。</param>
public sealed record VbaRibbonRecovery(VbaRibbonRewriteJob? Job, VbaRibbonInterruptedState State, string Message, int RemovedTempFiles);

/// <summary>
/// 途中で終わった書き換えの判定と知らせの文（純ロジック。16.11 の「Graft が途中で落ちた」の行）。
/// </summary>
public static class VbaRibbonRecoveryLogic
{
    /// <summary>
    /// ブックのいまの状態を決める。
    /// <list type="bullet">
    /// <item>控えのハッシュがまだ無い（控えを書き終える前に落ちた）: ブックには触れていない＝元のまま。</item>
    /// <item>ブックのハッシュが控えと同じ: 元のまま（置き換えの前に落ちた、または控えから戻せていた）。</item>
    /// <item>ブックのリボンの部品が、書く予定の本文と全部同じ: 書き換え済み。</item>
    /// <item>どれでもない: 分からない（落ちたあとに、Excel などで保存された見込み）。</item>
    /// </list>
    /// </summary>
    /// <param name="job">作業の記録。</param>
    /// <param name="bookSha256">いまのブックの SHA-256（ファイルが無ければ null）。</param>
    /// <param name="bookRibbonHashes">いまのブックのリボンの部品のハッシュ（ミラーのパス → ハッシュ。読めなければ null）。</param>
    public static VbaRibbonInterruptedState Judge(
        VbaRibbonRewriteJob job, string? bookSha256, IReadOnlyDictionary<string, string>? bookRibbonHashes)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (bookSha256 is null) return VbaRibbonInterruptedState.Missing;
        if (job.BackupSha256 is null) return VbaRibbonInterruptedState.Original;
        if (string.Equals(job.BackupSha256, bookSha256, StringComparison.OrdinalIgnoreCase)) return VbaRibbonInterruptedState.Original;
        if (bookRibbonHashes is not null && job.ExpectedHashes.Count > 0
            && job.ExpectedHashes.All(kv => bookRibbonHashes.TryGetValue(kv.Key, out var h) && string.Equals(h, kv.Value, StringComparison.Ordinal)))
        {
            return VbaRibbonInterruptedState.Rewritten;
        }

        return VbaRibbonInterruptedState.Unknown;
    }

    /// <summary>
    /// 後始末の知らせの文（16.11:「前回の書き換えは途中で終わりました。ブックは（元のまま／書き換え済み）です。Excel では閉じたままです」）。
    /// </summary>
    public static string Message(VbaRibbonRewriteJob? job, VbaRibbonInterruptedState state)
    {
        if (job is null)
        {
            return "前回のリボンの書き換えの記録を読めませんでした。記録は片付けました。ブックの中身は変えていません（念のため、Excel でブックを開いてリボンを確かめてください）。";
        }

        var name = VbaRibbonRewriteText.FileNameOf(job.WorkbookPath);
        var when = job.StartedAt == default ? string.Empty : job.StartedAt.ToLocalTime().ToString("（M月d日 H:mm）", CultureInfo.InvariantCulture);
        var head = $"前回のリボンの書き換え{when}は、途中で終わりました（Graft が終了したため）。";
        var body = state switch
        {
            VbaRibbonInterruptedState.Original => $"「{name}」は元のままです（リボンは未反映のまま残っています）。",
            VbaRibbonInterruptedState.Rewritten => $"「{name}」は書き換え済みです（リボンは新しい内容です）。",
            VbaRibbonInterruptedState.Missing => $"「{name}」が見つかりません。移動・削除していないか確かめてください。",
            _ => $"「{name}」は、書き換える前とも後とも違う内容です（その後、Excel などで保存された見込みです）。",
        };
        var backup = job.BackupSha256 is not null && job.BackupPath is not null
            ? $"書き換える前の控え: {job.BackupPath}"
            : string.Empty;
        var closed = job.ClosedWorkbook ? "Excel では閉じたままです。" + VbaRibbonRewriteText.OpenInExcelSentence : string.Empty;
        return string.Join(" ", new[] { head, body, closed, backup }.Where(s => s.Length > 0));
    }
}
