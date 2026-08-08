using System.ComponentModel;
using System.Diagnostics;
using CoreGuard = Graft.Core.SingleInstanceGuard;

namespace Graft.Platform.Linux;

/// <summary>
/// <see cref="ISingleInstanceGuard"/> のLinux実装（仕様書v2.1 19章 L4）。
/// ロックの取得・解放は <c>Core/SingleInstanceGuard.cs</c>（名前付きMutex、"Global\" プレフィックスを
/// 必ず付与する。理由・実機検証結果はそちらのクラスコメントを参照）へ委譲する。
///
/// 既存ウィンドウの前面化は、まずX11環境で広く入っている <c>wmctrl</c> を試す。それが使えない
/// 環境（未導入・タイムアウト・対象ウィンドウ未検出）では、<see cref="X11WindowActivator"/>
/// （自前のXlib接続でEWMHの _NET_ACTIVE_WINDOW を直接送る）へ縮退する。それも効かない場合
/// （Wayland、非EWMH対応のウィンドウマネージャ等）は、原因追跡のために標準エラー出力へ
/// 1行だけ記録して諦める。多重起動の防止自体（Mutexの取得）はこれらと独立して機能しており、
/// 前面化はあくまで利用者体験のための縮退である点に注意（多重起動防止それ自体は常に効く）。
///
/// 標準エラー出力にしているのは、この時点（<see cref="Views.StartupCoordinator.TryAcquireSingleInstance"/>は
/// <see cref="Views.StartupCoordinator.StartAsync"/> より前に呼ばれる）ではまだ
/// <c>Logger</c>（logs/配下への書き込み）が初期化されていないため。ダブルクリック起動では
/// 誰にも見えないが、ターミナルからの起動やsystemd等でログ収集される環境では追跡できる。
/// </summary>
public sealed class LinuxSingleInstanceGuard : ISingleInstanceGuard
{
    private CoreGuard? _guard;
    private bool _disposed;

    public bool IsSupported => true;

    public string? UnsupportedReason => null;

    public bool TryAcquire(string name)
    {
        _guard = CoreGuard.TryAcquire(name);
        return _guard is not null;
    }

    public void ActivateExistingInstance(string mainWindowTitle)
    {
        if (TryActivateWithWmctrl(mainWindowTitle)) return;
        if (X11WindowActivator.TryActivate(mainWindowTitle)) return;

        // ここまで来ると前面化はできなかった（多重起動の防止自体は成立済み）。
        // 利用者からは「ダブルクリックしても何も起きない」ように見えるため、
        // 少なくとも原因を追える形でだけ記録しておく。
        Console.Error.WriteLine(
            $"Graft: 既存ウィンドウ「{mainWindowTitle}」の前面化に失敗しました" +
            "（wmctrl未導入、またはウィンドウマネージャがEWMHの_NET_ACTIVE_WINDOWに未対応の可能性があります）。" +
            "多重起動の防止自体は機能しています。");
    }

    /// <summary>wmctrlでの前面化を試みる。成功したら true。</summary>
    private static bool TryActivateWithWmctrl(string mainWindowTitle)
    {
        try
        {
            var info = new ProcessStartInfo("wmctrl")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            info.ArgumentList.Add("-a");
            info.ArgumentList.Add(mainWindowTitle);

            using var process = Process.Start(info);
            if (process is null) return false;
            if (!process.WaitForExit(2000)) return false; // タイムアウト。X11直接送出へ縮退する。
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            // wmctrl が入っていない環境（典型的には Win32Exception: コマンドが見つからない）。
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _guard?.Dispose();
        _guard = null;
    }
}
