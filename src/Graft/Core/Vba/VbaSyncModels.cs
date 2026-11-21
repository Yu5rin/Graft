using System.Text.Json.Serialization;

namespace Graft.Core.Vba;

/// <summary>
/// 同期基準（仕様書_VBA連携 4.3）の1モジュール分。三方比較（<see cref="VbaSyncPlanner"/>）の「基準」。
/// <para>
/// 【基準を1本ではなく2本（ミラー側・Excel 側）持つ理由】 VBE は書き込み時に空白とキーワードの
/// 大文字小文字を直すので、Push したあとの Excel 側の本文は、書いたミラーの本文と一致するとは限らない。
/// 一致させるためにミラーを書き戻すと、履歴の <c>HashAfter</c> と食い違い、取り消しが毎回 E301 になる。
/// そこで基準を <see cref="MirrorHash"/>（最後に同期したときのミラー側）と <see cref="ExcelHash"/>
/// （そのときの Excel 側）の2本に分け、ミラーは <see cref="MirrorHash"/> と、Excel は
/// <see cref="ExcelHash"/> と別々に比べる。整形差は「変更」にならない。
/// </para>
/// </summary>
public sealed record VbaModuleState
{
    /// <summary>モジュール種別。JSON では名前（<c>Standard</c> など）で保存する。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public VbaModuleKind Kind { get; init; }

    /// <summary>VBA のコンポーネント名。</summary>
    public string Component { get; init; } = string.Empty;

    /// <summary>
    /// シートの表示名（「集計」など）。ドキュメントモジュールのファイル名は <c>Sheet1</c> のような
    /// コンポーネント名なので、利用者が見分けるための補足情報としてだけ持つ（UI のツールチップ用）。
    /// </summary>
    public string? Caption { get; init; }

    /// <summary>最後に同期したときのミラー側の本文のハッシュ（<see cref="VbaTextNormalizer.Hash"/>）。</summary>
    public string MirrorHash { get; init; } = string.Empty;

    /// <summary>最後に同期したときの Excel 側の本文のハッシュ。</summary>
    public string ExcelHash { get; init; } = string.Empty;

    /// <summary>
    /// そのときの Excel 側の本文の「ゆるい形」のハッシュ（<see cref="VbaTextNormalizer.LooseHash"/>）。
    /// VBE がプロジェクト全体で揃える識別子の大文字小文字の差や空白の差を、Excel 側の「変更」と
    /// 誤認しないために使う（別のモジュールへの Push で、無関係なモジュールの本文が変わることがある）。
    /// </summary>
    public string ExcelLooseHash { get; init; } = string.Empty;

    /// <summary>
    /// <see cref="ExcelLooseHash"/> を作った「ゆるい形」の算法の版（<see cref="VbaTextNormalizer.LooseFormVersion"/>）。
    /// 版の記録がない古い同期状態は 0（旧算法）として読まれる。旧算法のハッシュは文字列・コメントの中の差も
    /// 消していたので、新しい算法の値とは比べられない。比べると、旧算法で消えていた差が全モジュールで一斉に
    /// 「Excel 側の変更」に見えてしまうため、版が古い記録では <see cref="ExcelLooseHash"/> を使わず、
    /// 厳密なハッシュだけで判定する（<see cref="VbaSyncPlanner"/> 参照）。
    /// </summary>
    public int LooseFormVersion { get; init; }

    /// <summary>
    /// 削除保留。ミラーのファイルが無くなったが Excel 側にはモジュールが残っている状態。
    /// 自動 Pull で復活させないための印で、「Excel から復元」で解除する。
    /// </summary>
    public bool DeletePending { get; init; }

    /// <summary>最後に同期（または基準を更新）した日時。</summary>
    public DateTimeOffset SyncedAt { get; init; }

    /// <summary>ミラーの本文と Excel の本文から、同期直後の基準を作る。</summary>
    public static VbaModuleState Create(
        VbaModuleKind kind, string component, string? caption,
        string mirrorHash, string excelHash, string excelLooseHash, DateTimeOffset syncedAt)
        => new()
        {
            Kind = kind,
            Component = component,
            Caption = caption,
            MirrorHash = mirrorHash,
            ExcelHash = excelHash,
            ExcelLooseHash = excelLooseHash,
            LooseFormVersion = VbaTextNormalizer.LooseFormVersion,
            DeletePending = false,
            SyncedAt = syncedAt,
        };
}

/// <summary>
/// 同期状態ファイル（<c>vba-sync/&lt;プロジェクトId&gt;.json</c>、仕様書_VBA連携 4.3）のルート。
/// キーはミラー内の正規の相対パス（<c>Modules/Module1.bas</c>）。
/// </summary>
public sealed record VbaSyncState
{
    /// <summary>現在のファイル形式の版。将来の形式変更に備える（この版で新設したので移行処理はない）。</summary>
    public const int CurrentVersion = 1;

    /// <summary>形式の版。</summary>
    public int Version { get; init; } = CurrentVersion;

    /// <summary>紐付けたブックのフルパス。</summary>
    public string WorkbookFullName { get; init; } = string.Empty;

    /// <summary>モジュールごとの基準。キーは正規の相対パス。</summary>
    public IReadOnlyDictionary<string, VbaModuleState> Modules { get; init; } =
        new Dictionary<string, VbaModuleState>();

    /// <summary>指定モジュールの基準を追加または置き換えた新しい状態を返す。</summary>
    public VbaSyncState WithModule(string relativePath, VbaModuleState module)
    {
        var copy = new Dictionary<string, VbaModuleState>(Modules, StringComparer.OrdinalIgnoreCase)
        {
            [relativePath] = module,
        };
        return this with { Modules = copy };
    }

    /// <summary>指定モジュールの基準を取り除いた新しい状態を返す。</summary>
    public VbaSyncState WithoutModule(string relativePath)
    {
        var copy = new Dictionary<string, VbaModuleState>(Modules, StringComparer.OrdinalIgnoreCase);
        copy.Remove(relativePath);
        return this with { Modules = copy };
    }
}

/// <summary>
/// 三方比較の入力のうち、ミラー側の1モジュール。本文そのものは持たず、ハッシュだけを渡す
/// （<see cref="VbaSyncPlanner"/> は COM にもファイルにも触れない純ロジックにするため）。
/// </summary>
/// <param name="RelativePath">ミラー内の正規の相対パス（<see cref="VbaMirrorLayout.ToRelativePath"/>）。</param>
/// <param name="Kind">種別。</param>
/// <param name="Component">コンポーネント名。</param>
/// <param name="Hash">本文の正規化ハッシュ（<see cref="VbaTextNormalizer.Hash"/>）。</param>
/// <param name="LooseHash">本文のゆるいハッシュ（<see cref="VbaTextNormalizer.LooseHash"/>）。</param>
public sealed record VbaMirrorModule(
    string RelativePath, VbaModuleKind Kind, string Component, string Hash, string LooseHash);

/// <summary>三方比較の入力のうち、Excel 側の1モジュール。</summary>
/// <param name="Component">コンポーネント名。</param>
/// <param name="Kind">種別。</param>
/// <param name="Hash">本文の正規化ハッシュ。</param>
/// <param name="LooseHash">本文のゆるいハッシュ。</param>
/// <param name="Caption">シートの表示名（ドキュメントモジュールのみ。なければ null）。</param>
public sealed record VbaExcelModule(
    string Component, VbaModuleKind Kind, string Hash, string LooseHash, string? Caption = null);

/// <summary>1モジュールについて、同期で何をするか（仕様書_VBA連携 6.5 の表）。</summary>
public enum VbaSyncAction
{
    /// <summary>何もしない（基準の更新だけが要る場合は <see cref="VbaModulePlan.Baseline"/> に入る）。</summary>
    None,

    /// <summary>Excel → ミラー。Excel 側だけが変わった、または記録がなく Excel にだけある。</summary>
    Pull,

    /// <summary>ミラー → Excel。ミラー側だけが変わった（Excel に既存のモジュール）。「未反映」とも呼ぶ。</summary>
    Push,

    /// <summary>ミラー → Excel に、モジュールを新規作成して書く（標準・クラスのみ）。</summary>
    PushCreate,

    /// <summary>両側が変わっていて本文が違う。衝突ダイアログで利用者に選んでもらう。</summary>
    Conflict,

    /// <summary>両側が変わっているが実質同じ本文。基準だけを更新する（I/O なし）。</summary>
    Rebaseline,

    /// <summary>
    /// 削除保留。ミラーのファイルが無いが Excel にはある。自動 Pull では復活させない。
    /// </summary>
    DeletePending,

    /// <summary>
    /// 作成不可。ミラーにだけあるドキュメントモジュールまたはフォーム。Excel 側に先に存在しないと作れない
    /// ので、Push の対象にしない（E808 の警告）。
    /// </summary>
    NotCreatable,

    /// <summary>同期の対象外（Excel 側のコンポーネント名がミラーのファイルにできない、など。E810 の警告）。</summary>
    Unsupported,

    /// <summary>両側とも無くなったので、記録を捨てる。</summary>
    DiscardRecord,
}

/// <summary>画面に補足を出すための印。動作（<see cref="VbaSyncAction"/>）とは別に付く。</summary>
public enum VbaSyncNotice
{
    /// <summary>補足なし。</summary>
    None,

    /// <summary>Excel 側でモジュールが無くなっている。</summary>
    ExcelModuleMissing,

    /// <summary>ミラーから削除されたモジュール（削除保留）。</summary>
    MirrorFileDeleted,
}

/// <summary>1モジュール分の同期計画。</summary>
/// <param name="RelativePath">ミラー内の正規の相対パス。</param>
/// <param name="Kind">種別。</param>
/// <param name="Component">コンポーネント名。</param>
/// <param name="Action">何をするか。</param>
/// <param name="Baseline">
/// I/O なしで保存してよい新しい基準（<see cref="VbaSyncAction.Rebaseline"/>・<see cref="VbaSyncAction.DeletePending"/>、
/// および整形差・削除保留の解除だけの <see cref="VbaSyncAction.None"/>）。Pull・Push・衝突の基準は、
/// 実行が成功してから呼び出し側が作る。変更がなければ null。
/// </param>
/// <param name="Notice">画面に出す補足。</param>
/// <param name="Issue">警告にする問題（E808・E810）。なければ null。</param>
public sealed record VbaModulePlan(
    string RelativePath,
    VbaModuleKind Kind,
    string Component,
    VbaSyncAction Action,
    VbaModuleState? Baseline = null,
    VbaSyncNotice Notice = VbaSyncNotice.None,
    GraftIssue? Issue = null);

/// <summary>全モジュールの同期計画。</summary>
public sealed record VbaSyncPlan(IReadOnlyList<VbaModulePlan> Modules)
{
    /// <summary>指定の動作のモジュール計画を返す。</summary>
    public IEnumerable<VbaModulePlan> Where(VbaSyncAction action) => Modules.Where(m => m.Action == action);

    /// <summary>衝突があるか。</summary>
    public bool HasConflicts => Modules.Any(m => m.Action == VbaSyncAction.Conflict);

    /// <summary>
    /// Pull・Push・衝突など、実際の I/O や利用者の判断を要するものが 1 つでもあるか。
    /// 基準の更新だけで済むものは含まない。
    /// </summary>
    public bool HasWork => Modules.Any(m => m.Action is VbaSyncAction.Pull or VbaSyncAction.Push
        or VbaSyncAction.PushCreate or VbaSyncAction.Conflict);

    /// <summary>
    /// I/O を伴わない基準の更新（<see cref="VbaModulePlan.Baseline"/>）と、記録の破棄
    /// （<see cref="VbaSyncAction.DiscardRecord"/>）を、同期状態へ反映した新しい状態を返す。
    /// </summary>
    public VbaSyncState ApplyLocalChanges(VbaSyncState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        foreach (var plan in Modules)
        {
            if (plan.Action == VbaSyncAction.DiscardRecord)
            {
                state = state.WithoutModule(plan.RelativePath);
            }
            else if (plan.Baseline is not null)
            {
                state = state.WithModule(plan.RelativePath, plan.Baseline);
            }
        }

        return state;
    }
}
