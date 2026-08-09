using Avalonia.Controls;
using Graft.Platform;

namespace Graft.Views;

/// <summary>
/// <see cref="StartupCoordinator"/> の分割ファイル（1ファイル400行上限のため。
/// StartupCoordinator.Hotkey.csと同じ理由）。
///
/// 機能追加: クリップボード監視でパッチ形式を検知したら、Graftのウィンドウを前面に表示する
/// （設定 <see cref="Infra.Settings.ClipboardWatch"/>.<see cref="Infra.ClipboardWatchSettings.
/// ActivateOnDetect"/>、既定オン）。
///
/// 【要件1: 既存の前面化の仕組みを再利用する】
/// 前面化そのものは多重起動検出時の前面化（<see cref="TryAcquireSingleInstance"/>が false を
/// 返したときに呼ぶ<see cref="ISingleInstanceGuard.ActivateExistingInstance"/>。実体は
/// <see cref="Platform.Windows.WindowsSingleInstanceGuard"/>・<see cref="Platform.Linux.
/// LinuxSingleInstanceGuard"/>）と全く同じ経路を呼ぶ。Windowsのフォーカス窃取防止への縮退
/// （<c>SetForegroundWindow</c>が拒否されタスクバー点滅になる）・Linuxのwmctrl/X11直接送出
/// フォールバックは既にそちらで作り込まれており、ここでは並行実装を持たない。
///
/// 【要件2: 最小化・タスクトレイ常駐からの復帰】
/// 最小化されていれば通常状態へ戻し、トレイに隠れていれば（「閉じたときの動作」＝トレイ常駐や
/// 最小化時の格納で<c>window.Hide()</c>が呼ばれ、ウィンドウマネージャの管理対象一覧から
/// 外れているため、wmctrl/X11の_NET_CLIENT_LISTからは見つけられない）まず表示してから
/// 前面化する。
///
/// 【要件4: 自動解析の設定との関係】
/// <see cref="Infra.ClipboardWatchSettings.AutoParse"/>の有無に関わらず、この設定がオンの間は
/// 常に前面化する（検知したこと自体を伝えるのが目的であり、解析の有無は別軸のため）。
/// オフの場合は<see cref="StartupCoordinator.OnClipboardPatchDetected"/>側で、従来どおり
/// 自動解析した結果を確認できるよう前面化するほか、それ以外は「反応時の挙動」設定
/// （<see cref="Infra.ClipboardWatchSettings.Action"/>）に従う。
///
/// 【要件5: 既に前面にある場合は何もしない】
/// <paramref name="isAlreadyForeground"/>で判定する。無用なちらつき・フォーカス移動を避けるため、
/// この場合はWindowの状態変更もOS呼び出しも一切行わない。
///
/// 【要件6: Windowsの制約への配慮】
/// <see cref="ISingleInstanceGuard.ActivateExistingInstance"/>がfalseを返した（OS側の制約で
/// 前面化が拒否されタスクバー通知等へ縮退した）場合、呼び出し側（<see cref="OnClipboardPatchDetected"/>）は
/// それをエラー扱いにせず（ダイアログ等は出さない）、ログにのみ記録する。多重起動検出時の
/// 前面化はこの戻り値を見ないため、その経路の挙動はこれまでどおり変わらない。
///
/// 【テスト容易性】
/// StartupCoordinator.Hotkey.cs（<see cref="ReapplyHotkey"/>）と同じ理由・同じ形で、実際のOS資源
/// （Windows/Linuxの具象<see cref="ISingleInstanceGuard"/>）に触れる経路は既存方針
/// （ClipboardWatchTests.csのコメント参照）どおり単体テストでは呼ばず実機（Xvfb）検証に委ねるが、
/// 前面化の要否判定・Window状態の復元・ログ記録の要否というロジック自体は<see cref="ISingleInstanceGuard"/>
/// のフェイクだけで検証できるよう、<see cref="ActivateWindowOnPatchDetected"/>をstatic・
/// インターフェース引数受け取りの純粋な形に切り出している（ClipboardActivationTests.cs参照）。
/// ヘッドレスAvalonia環境では<c>Window.IsActive</c>が実際のOSフォーカス状態を再現できず常にfalseに
/// なるため（実機とは異なる）、「既に前面にあるか」も<paramref name="isAlreadyForeground"/>として
/// 明示的な引数にしている。
/// </summary>
public sealed partial class StartupCoordinator
{
    /// <summary>
    /// クリップボード監視でパッチ形式を検知したときの前面化本体。<see cref="OnClipboardPatchDetected"/>
    /// から呼ぶ。
    /// </summary>
    /// <param name="singleInstance">実際の前面化呼び出し先。本番は<c>_platform.SingleInstance</c>。</param>
    /// <param name="window">対象ウィンドウ。</param>
    /// <param name="mainWindowTitle">前面化の対象を特定するウィンドウタイトル（<see cref="MainWindowTitle"/>）。</param>
    /// <param name="activateOnDetectSetting">「検知したら前面に表示する」設定の現在値。</param>
    /// <param name="isAlreadyForeground">既にGraftのウィンドウが前面（表示中・非最小化・アクティブ）かどうか。</param>
    public static ClipboardActivationOutcome ActivateWindowOnPatchDetected(
        ISingleInstanceGuard singleInstance, Window window, string mainWindowTitle,
        bool activateOnDetectSetting, bool isAlreadyForeground)
    {
        if (!activateOnDetectSetting) return ClipboardActivationOutcome.Disabled;
        if (isAlreadyForeground) return ClipboardActivationOutcome.AlreadyForeground; // 要件5: ちらつき・フォーカス移動を避ける。

        if (!window.IsVisible) window.Show(); // 要件2: トレイに隠れていればまず表示する。
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal; // 要件2: 最小化なら戻す。

        // 要件1: 多重起動検出時の前面化と全く同じ経路を再利用する。
        return singleInstance.ActivateExistingInstance(mainWindowTitle)
            ? ClipboardActivationOutcome.Activated
            : ClipboardActivationOutcome.Degraded;
    }
}

/// <summary>
/// <see cref="StartupCoordinator.ActivateWindowOnPatchDetected"/>の結果。
/// </summary>
public enum ClipboardActivationOutcome
{
    /// <summary>「検知したら前面に表示する」設定がオフのため、このパスでは処理しなかった。</summary>
    Disabled,

    /// <summary>既に前面にあったため、何もしなかった（要件5）。</summary>
    AlreadyForeground,

    /// <summary>前面化を要求し、成功した。</summary>
    Activated,

    /// <summary>
    /// 前面化を要求したが、OS側の制約等で縮退した（要件6）。Windowsではフォーカス窃取防止で
    /// <c>SetForegroundWindow</c>が拒否されタスクバーのアイコン点滅になったケースが典型。
    /// エラー扱いにはせず、呼び出し側でログにのみ記録する。
    /// </summary>
    Degraded,
}
