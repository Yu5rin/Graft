namespace Graft.Core.Vba;

/// <summary>
/// 衝突ダイアログ（仕様書_VBA連携 7.5）を出す場面。場面によって選べる項目が変わる
/// （<see cref="VbaConflictRequest.CanAdoptExcel"/> など）。
/// </summary>
public enum VbaConflictScene
{
    /// <summary>解析（ドライラン）の前の自動 Pull（7.3-1）。</summary>
    BeforeAnalysis,

    /// <summary>解析後・適用直前の再検査（7.3-3）。</summary>
    Reanalysis,

    /// <summary>プロジェクトの右クリック「Excel から取込」（7.2）。</summary>
    ManualPull,

    /// <summary>
    /// コンテキスト収集（依頼文コピーを含む）の走査の前の自動 Pull（7.2）。「Excel 側を採用」を出し、
    /// 「中止」なら収集は止めずにミラーの現在の内容で続ける。
    /// </summary>
    ContextCollect,

    /// <summary>プロジェクトの右クリック「Excel へ反映」（7.2b）。</summary>
    ManualPush,

    /// <summary>適用後の自動 Push の直前（7.3-4-3）。パッチは既にミラーへ適用済み。</summary>
    AfterApply,

    /// <summary>取り消し・復元（「ここまで戻す」・フック失敗の巻き戻しを含む）の後の Push の直前（7.4）。</summary>
    AfterRestore,
}

/// <summary>衝突したモジュール1件に対する利用者の選択。</summary>
public enum VbaConflictChoice
{
    /// <summary>まだ選んでいない。</summary>
    Undecided,

    /// <summary>「Graft 側で上書き」。ミラーの本文を Excel へ Push する（Excel 側の本文は <c>.rescue/</c> に控える）。</summary>
    OverwriteExcel,

    /// <summary>「Excel 側を採用」。Excel の本文をミラーへ Pull する。</summary>
    AdoptExcel,

    /// <summary>「反映しない」。何もせず、未反映の印を付ける。</summary>
    Skip,
}

/// <summary>
/// 衝突した 1 件（モジュール、またはリボン。表示用に両側の本文を持つ）。
/// <para>
/// 【リボンも同じ型で運ぶ理由（段階 F5。仕様書_ブックの情報と依頼の観点 16.4）】 衝突ダイアログは、一覧・差分・選択肢（上書き／採用／
/// 反映しない）がモジュールと同じで、違うのは「相手が Excel の本文か、ブックのファイルの部品か」だけ。リボン用に別の型・別の窓を作ると、
/// 場面ごとの出し分け（<see cref="VbaConflictRequest"/>）を二重に持つことになる。そこで型は変えず、<see cref="IsRibbon"/>
/// （パスが <c>CustomUI/customUI14.xml</c>・<c>CustomUI/customUI.xml</c> か。パスだけで決まるので持ち回りの旗は要らない）で見分け、
/// リボンの項目は <see cref="Kind"/> に意味を持たせない（<see cref="VbaModuleKind.Standard"/> を入れる。<see cref="ForRibbon"/> を使う）。
/// 「Excel 側」の本文は、リボンではブックのファイルの部品の XML。
/// </para>
/// </summary>
/// <param name="RelativePath">ミラー内の正規の相対パス（<c>Modules/Module1.bas</c>・<c>CustomUI/customUI14.xml</c>）。</param>
/// <param name="Kind">種別（リボンでは意味を持たない）。</param>
/// <param name="Component">コンポーネント名（リボンではファイル名）。</param>
/// <param name="ExcelText">Excel 側の現在の本文（リボンではブックのファイルの部品の XML）。</param>
/// <param name="MirrorText">ミラー（Graft 側）の現在の本文。</param>
public sealed record VbaConflictItem(
    string RelativePath, VbaModuleKind Kind, string Component, string ExcelText, string MirrorText)
{
    /// <summary>リボン（customUI）の衝突か。</summary>
    public bool IsRibbon => VbaRibbonMirrorLayout.TryParse(RelativePath, out _);

    /// <summary>リボンの衝突の項目を作る（<paramref name="mirrorPath"/> は <c>CustomUI/…</c> の正規のパス。<paramref name="bookXml"/> はブックの部品の XML）。</summary>
    public static VbaConflictItem ForRibbon(string mirrorPath, string bookXml, string mirrorXml, VbaConflictReason reason)
        => new(mirrorPath, VbaModuleKind.Standard, System.IO.Path.GetFileName(mirrorPath), bookXml, mirrorXml) { Reason = reason };

    /// <summary>Excel 側の本文のハッシュ（解析後の再検査で「上書きでよい」と承認した時点の Excel 側かを見分けるのに使う）。</summary>
    public string ExcelHash => VbaTextNormalizer.Hash(ExcelText);

    /// <summary>衝突の理由（両側の変更・Excel 側が空・要確認）。文言の出し分けに使う。</summary>
    public VbaConflictReason Reason { get; init; } = VbaConflictReason.BothChanged;
}

/// <summary>
/// 衝突ダイアログに渡す要求。場面（<see cref="Scene"/>）から、選べる項目が決まる
/// （仕様 7.5 の表）。
/// </summary>
public sealed record VbaConflictRequest(VbaConflictScene Scene, IReadOnlyList<VbaConflictItem> Items)
{
    /// <summary>
    /// 「Excel 側を採用」を出すか。適用の後と復元の後は出さない: ミラーを Excel の内容に差し替えると、
    /// AI のパッチ（または復元の意図）を打ち消し、履歴の <c>HashAfter</c> とも食い違うため。
    /// </summary>
    public bool CanAdoptExcel => Scene is not (VbaConflictScene.AfterApply or VbaConflictScene.AfterRestore);

    /// <summary>「反映しない」を出すか（適用の後と復元の後。ファイルの変更は既に済んでいるので、未反映の印を付けて残す）。</summary>
    public bool CanSkip => !CanAdoptExcel;

    /// <summary>「中止」を出すか（適用の前、手動の Pull／Push。何もせずに戻れる場面）。</summary>
    public bool CanAbort => CanAdoptExcel;

    /// <summary>
    /// 「この適用（rN）を取り消す」を出すか（適用直後の Push だけ。7.3-4-3）。<b>リボンの衝突だけのときは出さない</b>:
    /// リボンの書き換えが失敗・見送りになっても、適用の取り消しは提案しない（仕様 16.10・16.16-12。リボンだけ戻したいときは履歴から）。
    /// </summary>
    public bool CanUndoApply => Scene == VbaConflictScene.AfterApply && Items.Any(i => !i.IsRibbon);

    /// <summary>リボンの衝突を含むか（窓の文面にリボンの説明を足す）。</summary>
    public bool HasRibbon => Items.Any(i => i.IsRibbon);

    /// <summary>
    /// 窓を選ばずに閉じた（Esc・×）ときの扱い。何もせず戻れる場面では中止、ファイルが既に変わっている場面では
    /// 「反映しない」。ダイアログが配線されていない環境（テストなど）でも同じ安全側の動きにする。
    /// </summary>
    public VbaConflictDecision DefaultDecision => CanAbort
        ? VbaConflictDecision.Abort
        : new VbaConflictDecision(false, false, Items.ToDictionary(i => i.RelativePath, _ => VbaConflictChoice.Skip, StringComparer.OrdinalIgnoreCase));
}

/// <summary>衝突ダイアログの結果。</summary>
/// <param name="Aborted">中止した（何もしない）。</param>
/// <param name="UndoApply">「この適用を取り消す」を選んだ（<see cref="VbaConflictScene.AfterApply"/> だけ）。</param>
/// <param name="Choices">モジュールごとの選択（キーは相対パス）。</param>
public sealed record VbaConflictDecision(bool Aborted, bool UndoApply, IReadOnlyDictionary<string, VbaConflictChoice> Choices)
{
    /// <summary>中止。</summary>
    public static VbaConflictDecision Abort { get; } =
        new(true, false, new Dictionary<string, VbaConflictChoice>(StringComparer.OrdinalIgnoreCase));

    /// <summary>全件に同じ選択を付けた結果。</summary>
    public static VbaConflictDecision All(VbaConflictRequest request, VbaConflictChoice choice)
        => new(false, false, request.Items.ToDictionary(i => i.RelativePath, _ => choice, StringComparer.OrdinalIgnoreCase));

    /// <summary>指定の選択をしたモジュールのパス。</summary>
    public IReadOnlyList<string> PathsWith(VbaConflictChoice choice)
        => Choices.Where(kv => kv.Value == choice).Select(kv => kv.Key).ToList();
}

/// <summary>選択肢つきの確認ダイアログ（Push 失敗など、ボタンの文言を指定したい場面）の1つのボタン。</summary>
/// <param name="Id">結果として返す識別子。</param>
/// <param name="Label">ボタンの文言。</param>
/// <param name="IsDefault">Enter で押されるボタンか（1つだけ）。窓を選ばずに閉じたときの結果にもなる。</param>
public sealed record VbaChoiceOption(string Id, string Label, bool IsDefault = false);

/// <summary>選択肢つきの確認ダイアログの要求。</summary>
/// <param name="Title">タイトル。</param>
/// <param name="Message">本文。</param>
/// <param name="Options">ボタン（左から。既定のボタンは1つ）。</param>
/// <param name="OpenWorkbookPath">
/// 紐付けたブックが開いていない（E804）ときに、「Excel で開く」ボタンを足すためのブックのフルパス
/// （仕様書_VBA連携 9章）。null ならボタンを出さない。このボタンは窓を閉じず、ブックを開くだけで、
/// 選択（<see cref="Options"/>）は別に残る。
/// </param>
public sealed record VbaChoiceRequest(
    string Title, string Message, IReadOnlyList<VbaChoiceOption> Options, string? OpenWorkbookPath = null)
{
    /// <summary>既定のボタン。ダイアログが配線されていない環境や、選ばずに閉じたときの結果。</summary>
    public VbaChoiceOption Default => Options.FirstOrDefault(o => o.IsDefault) ?? Options[0];
}
