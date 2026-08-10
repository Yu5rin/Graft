using FluentAssertions;
using Graft.Platform.Windows;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 不具合対応: Windowsのフォーカス窃取防止でSetForegroundWindowが拒否される問題への
/// AttachThreadInput回避策（<see cref="WindowsSingleInstanceGuard.ActivateWindowHandle"/>）の
/// 単体テスト。
///
/// AttachThreadInput自体を含む前面化の成否はWin32 API呼び出しに依存するため、Windows実機
/// 以外では検証できない（実機での確認手順はdocsの新規ドキュメント参照）。ここでは
/// 「AttachThreadInputを試みる価値があるかどうか」の判定ロジック（<see cref="
/// ForegroundActivationDecision.ShouldAttachThreadInput"/>。P/Invokeを一切含まない純粋な
/// 分岐のみで構成される）だけを切り出して検証する。LinuxSingleInstanceGuardTests.csと同じ
/// 「実際のOS呼び出しは実機・純粋ロジックは単体テスト」という役割分担。
/// </summary>
public class ForegroundActivationDecisionTests
{
    private static readonly IntPtr MyWindow = new(0x1000);
    private static readonly IntPtr ForegroundWindow = new(0x2000);

    [Fact(DisplayName = "前面ウィンドウが取得できない(IntPtr.Zero)場合はAttachThreadInputを試みない")]
    public void 前面ウィンドウが取得できない場合はfalse()
    {
        ForegroundActivationDecision.ShouldAttachThreadInput(
            foregroundWindow: IntPtr.Zero, targetWindow: MyWindow,
            foregroundThreadId: 111, myThreadId: 222)
            .Should().BeFalse("紐づけ先の前面ウィンドウが存在しないため");
    }

    [Fact(DisplayName = "前面ウィンドウが既に自分自身の場合はAttachThreadInputを試みない")]
    public void 前面ウィンドウが自分自身の場合はfalse()
    {
        ForegroundActivationDecision.ShouldAttachThreadInput(
            foregroundWindow: MyWindow, targetWindow: MyWindow,
            foregroundThreadId: 111, myThreadId: 222)
            .Should().BeFalse("既に自分自身が前面にあるはずの状況で、自分自身への紐づけは無意味なため");
    }

    [Fact(DisplayName = "前面ウィンドウのスレッドが既に自分と同じ場合はAttachThreadInputを試みない")]
    public void スレッドIDが同じ場合はfalse()
    {
        ForegroundActivationDecision.ShouldAttachThreadInput(
            foregroundWindow: ForegroundWindow, targetWindow: MyWindow,
            foregroundThreadId: 999, myThreadId: 999)
            .Should().BeFalse("既に同じ入力キューに属しており紐づけが無意味・自分自身への紐づけは危険なため");
    }

    [Fact(DisplayName = "前面ウィンドウが他者かつ別スレッドの場合はAttachThreadInputを試みてよい")]
    public void 通常ケースではtrue()
    {
        ForegroundActivationDecision.ShouldAttachThreadInput(
            foregroundWindow: ForegroundWindow, targetWindow: MyWindow,
            foregroundThreadId: 111, myThreadId: 222)
            .Should().BeTrue("他アプリが前面にあり自分と別スレッドの、想定どおりの回避策対象ケースのため");
    }
}
