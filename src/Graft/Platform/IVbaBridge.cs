using Graft.Core;
using Graft.Core.Vba;

namespace Graft.Platform;

/// <summary>
/// Excel VBA 連携の COM 層の抽象（仕様書_VBA連携 5.1）。実装は Windows の <c>WindowsVbaBridge</c>
/// （遅延バインディングの <c>dynamic</c>。PIA は参照しない）と、何もしない <c>NullVbaBridge</c>。
/// <para>
/// 【ブリッジの責務を「読む・書く・見せる」に絞る理由】 COM は Linux の CI で確かめられない。
/// 判定や検証（三方比較・書き込み後の一致判定・CP932 の検査）は <c>Core/Vba</c> の純ロジックに置いて
/// テストで固め、ブリッジはそれらが使う本文の読み書きだけを担う。ただし「別内容なら控えを書き戻す」処理は
/// COM の状態に依存するため、ブリッジの中で <see cref="VbaWriteVerifier"/> を呼んでよい。
/// </para>
/// <para>
/// 失敗は例外ではなく <see cref="GraftResult{T}"/> の問題（E801〜E813）で返す。利用できない環境
/// （<see cref="IPlatformService.IsSupported"/> が false）でも例外を投げず、E706 で失敗を返す。
/// </para>
/// </summary>
public interface IVbaBridge : IPlatformService
{
    /// <summary>起動中の全 Excel から、開いている保存済みブックを列挙する。選べないものは理由つきで返す。</summary>
    Task<GraftResult<IReadOnlyList<VbaWorkbookInfo>>> ListOpenWorkbooksAsync(CancellationToken ct = default);

    /// <summary>ブックのコンポーネントの種別・名前・本文を読む。names が null なら全件、指定があればその名前だけ。</summary>
    Task<GraftResult<IReadOnlyList<VbaComponentSnapshot>>> ReadComponentsAsync(
        string workbookFullName, IReadOnlyCollection<string>? names, CancellationToken ct = default);

    /// <summary>
    /// 指定したコンポーネントの本文を全文置き換えする（書き込み方式は仕様 5.5。必要なら内部で行範囲方式に切り替える）。
    /// 存在しない標準/クラスモジュールは作成する。結果はモジュール別。
    /// 接続できない・VBA プロジェクトに触れない・デザインモードでない場合は、1件も書かずに全体を失敗にする。
    /// </summary>
    Task<GraftResult<IReadOnlyList<VbaWriteOutcome>>> WriteComponentsAsync(
        string workbookFullName, IReadOnlyList<VbaComponentWrite> writes, CancellationToken ct = default);

    /// <summary>ブックを保存する（<c>SaveWorkbookAfterPush</c> のときだけ呼ぶ）。</summary>
    Task<GraftResult<bool>> SaveWorkbookAsync(string workbookFullName, CancellationToken ct = default);

    /// <summary>VBE で指定モジュールのコードウィンドウを開き、指定行へ移動して前面化する。</summary>
    Task<GraftResult<bool>> ShowInVbeAsync(string workbookFullName, string componentName, int line, CancellationToken ct = default);

    /// <summary>ブックの Excel ウィンドウを前面化する。</summary>
    Task<GraftResult<bool>> ActivateWorkbookAsync(string workbookFullName, CancellationToken ct = default);
}

/// <summary>起動中のブック1件。選べない場合は <see cref="Availability"/> と <see cref="UnavailableReason"/> に理由が入る。</summary>
/// <param name="FullName">ブックのフルパス。</param>
/// <param name="Name">ブックのファイル名。</param>
/// <param name="Availability">利用できるか、できないならなぜか。</param>
/// <param name="UnavailableReason">利用できない理由（日本語。<see cref="VbaWorkbookAvailability.Available"/> なら null）。</param>
/// <param name="IsReadOnly">読み取り専用で開いているか（反映はできるが、保存はできない）。</param>
public sealed record VbaWorkbookInfo(
    string FullName, string Name, VbaWorkbookAvailability Availability, string? UnavailableReason, bool IsReadOnly);

/// <summary>
/// ブックの利用可否。ロック中は <see cref="Locked"/> に集約している（旧案の <c>IsProtected</c> は廃止）。
/// </summary>
public enum VbaWorkbookAvailability
{
    /// <summary>利用できる。</summary>
    Available,

    /// <summary>マクロを持てない形式（.xlsx など。E806）。</summary>
    NoMacroFormat,

    /// <summary>VBA プロジェクトがパスワードでロックされている（E803）。</summary>
    Locked,

    /// <summary>「VBA プロジェクト オブジェクト モデルへのアクセスを信頼する」がオフ（E802）。</summary>
    AccessNotTrusted,

    /// <summary>OneDrive／SharePoint 上のブック（<c>FullName</c> が URL）。非対応。</summary>
    UrlLocation,

    /// <summary>保護ビューで開いている。VBProject に触れない。</summary>
    ProtectedView,

    /// <summary>
    /// Excel が応答しない（セル編集中・ダイアログ表示中など）。P2 で追加した値で、一覧の取得中に
    /// 再試行が尽きたブックを「アクセス未許可」と取り違えて案内しないために分けている（E805）。
    /// </summary>
    Unresponsive,
}

/// <summary>
/// 「このブックは使えない」理由の文言と、使えないブックを前提検査（<c>OpenProject</c>）で弾くための問題の作り方。
/// 選択画面（一覧の取得）と、読み書きの前提検査が<b>同じ判定・同じ文言</b>になるよう、ここに 1 つだけ置く。
/// <para>
/// 【なぜ前提検査でも弾くか】 以前は選択画面だけが OneDrive／SharePoint 上のブックと保護ビューのブックを弾き、
/// 追加した後にそうなったブックは、前提検査が <c>VBProject</c> の取得で失敗するまで進んで、トラストセンターの設定（E802）と
/// 誤った案内になっていた。実際の原因（保護ビュー・URL）を、選択画面と同じ言葉で伝える。
/// </para>
/// </summary>
public static class VbaWorkbookAvailabilityIssues
{
    /// <summary>OneDrive／SharePoint 上のブック（<c>FullName</c> が URL）を使えない理由。</summary>
    public const string UrlLocationReason = "OneDrive／SharePoint 上のブックは使えません。ローカルのフォルダへ保存してから開き直してください。";

    /// <summary>保護ビューで開いているブックを使えない理由。</summary>
    public const string ProtectedViewReason = "保護ビューで開いています。「編集を有効にする」を押してから、もう一度開いてください。";

    /// <summary>
    /// <paramref name="availability"/> が「前提検査で弾くべきもの」（URL・保護ビュー）なら、その問題（E801）を返す。
    /// それ以外は null（ほかの状態は、それぞれの前提検査が別の問題で扱う）。
    /// </summary>
    public static GraftIssue? ToIssue(VbaWorkbookAvailability availability, string fullName) => availability switch
    {
        VbaWorkbookAvailability.UrlLocation => GraftIssue.Of(ErrorCode.E801, $"{fullName}: {UrlLocationReason}", path: fullName),
        VbaWorkbookAvailability.ProtectedView => GraftIssue.Of(ErrorCode.E801, $"{fullName}: {ProtectedViewReason}", path: fullName),
        _ => null,
    };
}

/// <summary>Excel から読んだコンポーネント1件。</summary>
/// <param name="Name">コンポーネント名。</param>
/// <param name="Kind">種別。</param>
/// <param name="Code">本文（<c>CodeModule.Lines</c>。エクスポート形式のヘッダは含まない）。</param>
/// <param name="Caption">シートの表示名（ドキュメントモジュールのみ。なければ null）。</param>
public sealed record VbaComponentSnapshot(string Name, VbaModuleKind Kind, string Code, string? Caption);

/// <summary>Excel へ書き込むコンポーネント1件。</summary>
/// <param name="Name">コンポーネント名。</param>
/// <param name="Kind">種別。</param>
/// <param name="Code">書き込む本文（改行は CRLF）。</param>
/// <param name="CreateIfMissing">存在しないとき作成してよいか（標準・クラスのみ。それ以外は E808）。</param>
public sealed record VbaComponentWrite(string Name, VbaModuleKind Kind, string Code, bool CreateIfMissing);

/// <summary>
/// モジュール別の書き込み結果。検証（一致／整形差／別内容）は Core の <see cref="VbaWriteVerifier"/> が行い、
/// ブリッジは読み直した本文（<see cref="ReadBackCode"/>）を返すだけにする。
/// </summary>
/// <param name="Name">コンポーネント名。</param>
/// <param name="Status">書き込みの結果。</param>
/// <param name="ReadBackCode">書き込み後に読み直した本文。読み直せなかったときは null。</param>
/// <param name="Issue">失敗または警告の理由。なければ null。</param>
/// <param name="BackupCode">
/// 書き込み前に取った控え（Excel 側の元の本文）。控えを書き戻せなかったとき（E809）だけ入れる。
/// ブリッジはプロジェクトの Id もデータ保存先も知らないため、<c>.rescue/</c> への書き出しは呼び出し側
/// （<c>VbaSyncService</c>）が、この値を使って行う。
/// </param>
public sealed record VbaWriteOutcome(
    string Name, VbaWriteStatus Status, string? ReadBackCode, GraftIssue? Issue, string? BackupCode = null);

/// <summary>書き込みの結果。</summary>
public enum VbaWriteStatus
{
    /// <summary>既存のモジュールに書けた。</summary>
    Written,

    /// <summary>モジュールを新規作成して書けた。</summary>
    Created,

    /// <summary>書き込みに失敗したが、控えを書き戻して元の状態に戻せた。</summary>
    RolledBack,

    /// <summary>失敗。戻せなかったときは <see cref="VbaWriteOutcome.Issue"/> が E809。</summary>
    Failed,
}

/// <summary>
/// COM の時間切れの設定を受け取れるブリッジ（<c>Vba.ComTimeoutMs</c>／<c>Vba.CallTimeoutMs</c>。仕様書_VBA連携 8章）。
/// <see cref="IVbaBridge"/> 自体に足さないのは、Null 実装など時間切れの概念が無い実装に空のメソッドを
/// 強いないため。設定の変更は即時に反映する（次の呼び出しから）。
/// </summary>
public interface IVbaBridgeTimeouts
{
    /// <summary>設定を反映する。範囲の検証は <c>SettingsStore</c> が済ませている前提。</summary>
    void Configure(int comTimeoutMs, int callTimeoutMs);
}
