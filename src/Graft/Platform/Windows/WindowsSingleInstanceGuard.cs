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

            // SetForegroundWindowがOS側の制約（フォーカス窃取防止）で拒否された場合は、
            // 対象ウィンドウ自体は正しく特定できているため、タイトル検索へのフォールバックは
            // 行わない（見つからなかったのではなく拒否されただけであり、再検索しても結果は
            // 変わらない）。falseをそのまま返し、呼び出し側のDegraded判定に委ねる
            // （要件6: この判定を壊さないこと）。
            return SetForegroundWindow(handle);
        }

        // ハンドルが取得できなかった場合のみ、保険として従来のタイトル検索経路へ縮退する。
        // Window.Show()済みのウィンドウでは通常発生しない想定だが、万一に備える。
        return ActivateExistingInstance(fallbackWindowTitle);
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
}
