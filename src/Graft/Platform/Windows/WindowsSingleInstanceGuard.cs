using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using CoreGuard = Graft.Core.SingleInstanceGuard;

namespace Graft.Platform.Windows;

/// <summary>
/// <see cref="ISingleInstanceGuard"/> のWindows実装。ロックの取得・解放そのものは
/// <c>Core/SingleInstanceGuard.cs</c>（名前付きMutex。プラットフォームを問わず動作するため
/// ロジックは変更せずそのまま利用する）に委譲する。既存ウィンドウの前面化
/// （<c>FindWindow</c>・<c>ShowWindow</c>・<c>SetForegroundWindow</c>）は移設元
/// <c>Views/StartupCoordinator.cs</c> のロジックをそのまま移す。
///
/// Mutex名には "Global\" プレフィックスが付与される（Core/SingleInstanceGuard.cs参照）。
/// Windowsでは元々 "Global\" はターミナルサービスのグローバル名前空間を指す正規の構文で、
/// 通常の名前付きMutexとして機能するため、この変更によるWindows側の挙動差は無い
/// （実機調査で判明したのはUnix版ランタイムの挙動の誤解であり、Windows側の実装・前提に
/// 誤りは無かった）。Global\ の作成が権限で拒否される限られた環境向けの縮退・
/// 判定不能時に起動を止めない安全側の倒し方も、Core/SingleInstanceGuard.cs側で
/// プラットフォームを問わず共通に対応する。
///
/// 不具合修正: <see cref="ActivateWindowHandle"/>で、ハンドル直接指定に切り替えた後の実機検証で
/// 「タスクバーは点滅するが前面には出ない」（<c>SetForegroundWindow</c>がOSのフォーカス窃取
/// 防止に拒否される）ことが判明したため、<c>AttachThreadInput</c>による回避策を追加した
/// （詳細は<see cref="ActivateWindowHandle"/>・<c>TryActivateViaThreadAttach</c>のコメント参照）。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSingleInstanceGuard : ISingleInstanceGuard
{
    private const int SwRestore = 9;

    private CoreGuard? _guard;
    private bool _disposed;

    public bool IsSupported => true;

    public string? UnsupportedReason => null;

    public bool TryAcquire(string name)
    {
        _guard = CoreGuard.TryAcquire(name);
        return _guard is not null;
    }

    public bool ActivateExistingInstance(string mainWindowTitle)
    {
        var hwnd = FindWindow(null, mainWindowTitle);
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        ShowWindow(hwnd, SwRestore);

        // 機能追加: クリップボード監視での前面化はこの戻り値を見て縮退をログに残す
        // （呼び出し側のコメント参照）。SetForegroundWindowはOSのフォーカス窃取防止により
        // 他アプリが作業中だと拒否されることがあり、その場合はタスクバーのアイコン点滅に
        // 縮退する（falseを返す）。多重起動検出時の前面化はこれまでどおり戻り値を見ないため、
        // この変更によるWindows側の既存挙動の差は無い。
        return SetForegroundWindow(hwnd);
    }

    public bool ActivateWindowHandle(IntPtr handle, string fallbackWindowTitle)
    {
        // 不具合修正: 実機検証で、Graftが背面表示中（非最小化）の状態からActivateExistingInstance
        // （FindWindowでタイトルから探し直す経路）を呼んでも前面に出ないことが判明した
        // （ISingleInstanceGuard.ActivateWindowHandleのコメント参照）。一方、自分が既に持っている
        // Windowオブジェクトへ直接作用する経路（Window.Activate()）は同じ状況で成功していた。
        // そのため、自分のウィンドウを前面化するときはタイトル検索を経由せず、渡された
        // ハンドルへ直接ShowWindow・SetForegroundWindowを呼ぶ。
        if (handle != IntPtr.Zero)
        {
            ShowWindow(handle, SwRestore);

            if (SetForegroundWindow(handle))
            {
                return true;
            }

            // 不具合修正: 上のSetForegroundWindow単体では、実機検証でOS側の制約
            // （フォーカス窃取防止）により拒否されることが確認された。このとき対象ウィンドウ
            // 自体は正しく特定できており（タスクバーのアイコンが点滅する＝Windowsはこちらが
            // 前面化を要求してきたこと自体は認識している）、単に「他アプリを操作中のユーザーの
            // 意図しない割り込み」とみなされ拒否されているだけである。そのため、まずは
            // AttachThreadInputによる回避策を試みる（詳細はTryActivateViaThreadAttach参照）。
            // これでも成功しない場合にのみ、タイトル検索へのフォールバックは行わずfalseを
            // そのまま返す（見つからなかったのではなく拒否されただけであり、タイトル再検索
            // しても結果は変わらないため）。呼び出し側のDegraded判定に委ねる
            // （要件6: この判定を壊さないこと）。
            return TryActivateViaThreadAttach(handle);
        }

        // ハンドルが取得できなかった場合のみ、保険として従来のタイトル検索経路へ縮退する。
        // Window.Show()済みのウィンドウでは通常発生しない想定だが、万一に備える。
        return ActivateExistingInstance(fallbackWindowTitle);
    }

    /// <summary>
    /// 不具合修正: SetForegroundWindow単体がOSのフォーカス窃取防止に拒否された場合の回避策。
    /// 現在前面にあるウィンドウの入力スレッドと自分の入力スレッドを一時的にAttachThreadInputで
    /// 紐づけると、Windowsは前面化の要求元を「ユーザーが今操作している側と同じ入力キューに
    /// 属するスレッド」とみなし、フォーカス窃取防止の対象から外す（Win32の既知の回避策）。
    ///
    /// 紐づけを試みる価値があるかどうかの判定自体は<see cref="ForegroundActivationDecision.
    /// ShouldAttachThreadInput"/>（P/Invokeを含まない純粋ロジック）に切り出しており、
    /// 単体テストはそちらで行う（本メソッド自体はWin32 API呼び出しを含むため実機以外では
    /// 検証できない。WindowsSingleInstanceGuardのクラスコメント参照）。
    /// </summary>
    private bool TryActivateViaThreadAttach(IntPtr handle)
    {
        var foregroundWindow = GetForegroundWindow();
        var myThreadId = GetCurrentThreadId();
        var foregroundThreadId = foregroundWindow == IntPtr.Zero
            ? 0u
            : GetWindowThreadProcessId(foregroundWindow, IntPtr.Zero);

        if (!ForegroundActivationDecision.ShouldAttachThreadInput(foregroundWindow, handle, foregroundThreadId, myThreadId))
        {
            // 紐づける価値が無い（前面ウィンドウが取得できない・既に自分自身・既に同じ
            // スレッド）。AttachThreadInputは呼ばず、そのまま失敗として扱う。
            return false;
        }

        var attached = AttachThreadInput(myThreadId, foregroundThreadId, true);
        try
        {
            if (attached)
            {
                SetForegroundWindow(handle);
                BringWindowToTop(handle);
            }
        }
        finally
        {
            // AttachThreadInputの解除漏れは、紐づけたままの前面ウィンドウ側スレッドの入力
            // キューを道連れにし続けることになり、他アプリ（メモ帳など）のキーボード入力・
            // IME変換に悪影響が及びかねない。そのため、AttachThreadInputに成功した場合は
            // このあとの処理で例外が飛んだ場合も含めて、finallyで必ず解除する
            // （このtry/finallyが無いとGetForegroundWindow/SetForegroundWindow/
            // BringWindowToTopのいずれかが予期せず失敗しただけで解除漏れが発生し得る）。
            if (attached)
            {
                AttachThreadInput(myThreadId, foregroundThreadId, false);
            }
        }

        // SetForegroundWindowの戻り値だけに頼らず、実際に前面ウィンドウが入れ替わったかを
        // GetForegroundWindowで確認する（戻り値がtrueでも実際には切り替わっていない・
        // 逆に厳密には失敗扱いの戻り値でも切り替わっている、といったズレを避けるため）。
        return GetForegroundWindow() == handle;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _guard?.Dispose();
        _guard = null;
        _disposed = true;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    // 以下、AttachThreadInputによる前面化回避策のための追加宣言（TryActivateViaThreadAttach参照）。

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    // lpdwProcessIdはプロセスIDの受け取り先だが、ここではスレッドID（戻り値）だけが必要なため
    // IntPtr.Zero（NULL）を渡す。Win32では第2引数にNULLを渡すことが許されている。
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);
}
