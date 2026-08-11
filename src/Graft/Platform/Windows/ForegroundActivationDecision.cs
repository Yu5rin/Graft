namespace Graft.Platform.Windows;

/// <summary>
/// 不具合修正: <see cref="WindowsSingleInstanceGuard.ActivateWindowHandle"/>で
/// <c>SetForegroundWindow</c>がOSのフォーカス窃取防止に拒否された場合の回避策
/// （<c>AttachThreadInput</c>）を実際に試みるべきかどうかの判定だけを切り出したもの。
///
/// このクラス自体はWin32 P/Invokeを一切呼ばない純粋な判定ロジックのみで構成されるため、
/// <c>WindowsAutoStartService.cs</c>と同じ理由で<c>[SupportedOSPlatform("windows")]</c>は
/// 付けていない（どのOS上でもコンパイル・実行でき、単体テストでLinux上からも検証できる。
/// tests/Graft.Tests/Graft.Tests.csprojに直接取り込む）。
/// </summary>
internal static class ForegroundActivationDecision
{
    /// <summary>
    /// 前面ウィンドウへ<c>AttachThreadInput</c>で自分の入力スレッドを一時的に紐づける
    /// 価値があるかどうかを判定する。
    ///
    /// 【なぜAttachThreadInputが必要か】
    /// Windows実機検証で、<c>SetForegroundWindow</c>単体を呼んだ場合はOSのフォーカス窃取防止
    /// により拒否され、タスクバーのアイコンが点滅するだけで前面には出ないことが判明した
    /// （ウィンドウ自体は正しく特定できている証拠でもある）。<c>AttachThreadInput</c>で前面
    /// ウィンドウの入力スレッドと自分のスレッドを一時的に同じ入力キューへ紐づけると、
    /// Windowsはその状態にある間は前面化の要求元を「ユーザーが今操作している側と同じ入力
    /// キューに属するスレッド」とみなし、フォーカス窃取防止の対象から外す（Win32の既知の
    /// 回避策）。
    ///
    /// 【呼ばない方が良いケース】
    /// 次のいずれかに該当する場合は、紐づけを試みる意味が無い、または危険なため呼ばない。
    /// <list type="bullet">
    /// <item>前面ウィンドウが取得できない（<paramref name="foregroundWindow"/>が
    /// <see cref="IntPtr.Zero"/>）。紐づけ先が存在しない。</item>
    /// <item>前面ウィンドウが既に対象ウィンドウ自身（<paramref name="foregroundWindow"/>==
    /// <paramref name="targetWindow"/>）。既に前面にあるはずの状況であり、自分自身への
    /// 紐づけは無意味。</item>
    /// <item>前面ウィンドウの所属スレッドが既に自分のスレッドと同じ
    /// （<paramref name="foregroundThreadId"/>==<paramref name="myThreadId"/>）。既に同じ
    /// 入力キューに属しており紐づけは無意味なうえ、自分自身のスレッドIDを
    /// <c>AttachThreadInput</c>へ渡すのは解除処理の前提が崩れ危険。</item>
    /// </list>
    /// </summary>
    public static bool ShouldAttachThreadInput(
        IntPtr foregroundWindow, IntPtr targetWindow, uint foregroundThreadId, uint myThreadId)
    {
        if (foregroundWindow == IntPtr.Zero)
        {
            return false;
        }

        if (foregroundWindow == targetWindow)
        {
            return false;
        }

        if (foregroundThreadId == myThreadId)
        {
            return false;
        }

        return true;
    }
}
