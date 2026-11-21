namespace Graft.Core.Vba;

/// <summary>COM の失敗の分類（<see cref="VbaComHResult.Classify"/>）。</summary>
public enum VbaComFailure
{
    /// <summary>相手が一時的に呼び出しを拒否している（セル編集中・ダイアログ表示中）。待てば通る可能性がある。</summary>
    RetryLater,

    /// <summary>相手が居なくなった・接続が切れた（Excel の終了、RPC サーバー利用不可）。</summary>
    Disconnected,

    /// <summary>上記以外。文脈によって意味が変わる（VBProject の取得なら「アクセスを信頼していない」）。</summary>
    Other,
}

/// <summary>
/// COM の HRESULT の分類（仕様書_VBA連携 5.3・5.4）。純ロジックなので Linux でテストできる。
/// <para>
/// 【再試行する値を3つに絞る理由】 Excel がセル編集中やダイアログ表示中だと、COM の呼び出しは
/// <c>RPC_E_CALL_REJECTED</c>（0x80010001）または <c>RPC_E_SERVERCALL_RETRYLATER</c>（0x8001010A）で
/// 拒否される。さらに Excel は、セル編集中のオブジェクトモデルの呼び出しに <c>VBA_E_IGNORE</c>（0x800AC472）を
/// 返すことで知られている（不具合 7。Excel 自身が「今は受け付けない」と答えるときの値で、実行はされていない）。
/// この3つは「実行されていない」ことが保証されるので、書き込み系の呼び出しでも安全に再試行できる。
/// 切断系は待っても戻らないので、再試行せずに E801 にする。
/// </para>
/// </summary>
public static class VbaComHResult
{
    /// <summary><c>RPC_E_CALL_REJECTED</c>。</summary>
    public const int CallRejected = unchecked((int)0x80010001);

    /// <summary><c>RPC_E_SERVERCALL_RETRYLATER</c>。</summary>
    public const int ServerCallRetryLater = unchecked((int)0x8001010A);

    /// <summary>
    /// <c>VBA_E_IGNORE</c>（0x800AC472）。Excel がセル編集中などで、オブジェクトモデルの呼び出しを受け付けないときに返す。
    /// 実機での確認は <c>docs/Windows実機検証手順.md</c> の 6-9。
    /// </summary>
    public const int VbaIgnore = unchecked((int)0x800AC472);

    /// <summary>
    /// 「VBA プロジェクト オブジェクト モデルへのアクセスを信頼する」がオフのときに <c>Workbook.VBProject</c> が返す値
    /// （0x800A03EC。「プログラムによる Visual Basic プロジェクトへのアクセスは信頼性に欠けます」）。
    /// </summary>
    public const int VbProjectNotTrusted = unchecked((int)0x800A03EC);

    /// <summary><c>RPC_S_SERVER_UNAVAILABLE</c>（RPC サーバー利用不可。Excel が終了した場合など）。</summary>
    public const int RpcServerUnavailable = unchecked((int)0x800706BA);

    /// <summary><c>RPC_S_CALL_FAILED</c>（RPC の呼び出し失敗）。</summary>
    public const int RpcCallFailed = unchecked((int)0x800706BE);

    /// <summary><c>RPC_E_DISCONNECTED</c>（オブジェクトが接続を切断した）。</summary>
    public const int RpcDisconnected = unchecked((int)0x80010108);

    /// <summary><c>CO_E_OBJNOTCONNECTED</c>（オブジェクトが接続されていない）。</summary>
    public const int ObjectNotConnected = unchecked((int)0x800401FD);

    /// <summary>HRESULT を分類する。</summary>
    public static VbaComFailure Classify(int hresult) => hresult switch
    {
        CallRejected or ServerCallRetryLater or VbaIgnore => VbaComFailure.RetryLater,
        RpcServerUnavailable or RpcCallFailed or RpcDisconnected or ObjectNotConnected => VbaComFailure.Disconnected,
        _ => VbaComFailure.Other,
    };

    /// <summary>再試行してよい失敗か。</summary>
    public static bool IsRetryLater(int hresult) => Classify(hresult) == VbaComFailure.RetryLater;

    /// <summary>
    /// <c>Workbook.VBProject</c> の取得などで COMException になったとき、どのエラーコードにするか。
    /// 切断なら E801。「アクセスを信頼していない」ときの既知の値（<see cref="VbProjectNotTrusted"/>）だけを E802
    /// （トラストセンターの案内）にし、それ以外は HRESULT つきの E801 にする。
    /// <para>
    /// 【既知の値だけを E802 にする理由】 以前は切断以外をすべて E802 にしていたので、セル編集中の拒否のような
    /// 別の原因の失敗まで「トラストセンターで許可してください」と案内して、利用者を的外れな設定へ誘導していた
    /// （不具合 7）。原因が分からない失敗は、HRESULT を添えた E801 で正直に伝える。
    /// 再試行切れ（RetryLater が最後まで続いた）は呼び出し側が E805 にするので、ここでは扱わない。
    /// </para>
    /// </summary>
    public static ErrorCode ClassifyVbProjectAccess(int hresult)
        => hresult == VbProjectNotTrusted ? ErrorCode.E802 : ErrorCode.E801;
}
