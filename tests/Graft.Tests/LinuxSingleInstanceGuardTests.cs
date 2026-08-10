using FluentAssertions;
using Graft.Platform.Linux;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 追加調査対応: wmctrlが入っていない環境での「既存ウィンドウの前面化」縮退
/// （<see cref="LinuxSingleInstanceGuard.ActivateExistingInstance"/>・
/// <see cref="X11WindowActivator"/>）の回帰テスト。
///
/// 実際にウィンドウマネージャが動いている環境でないと前面化の成否そのものは検証できないため
/// （CIやこのXvfb環境の多くはウィンドウマネージャ無しで動く）、ここでは
/// 「wmctrlが無くても・対象ウィンドウが無くても例外を投げず静かに終わること」だけを確認する。
/// X11に一切接続できない環境（DISPLAY未設定・Wayland専用等）でも同様に確認できる
/// （X11ClipboardReaderIntegrationTestsと同じ方針の簡易スキップ判定は不要で、
/// 例外を投げないことの確認自体はどの環境でも成立する）。
/// </summary>
public class LinuxSingleInstanceGuardTests
{
    [Fact(DisplayName = "ActivateExistingInstanceはwmctrl未導入・対象ウィンドウ無しでも例外を投げない")]
    public void 前面化に失敗しても例外を投げない()
    {
        using var guard = new LinuxSingleInstanceGuard();

        // 実在しないタイトルを指定し、wmctrl・X11直接送出のいずれも「見つからない」経路を通す。
        var act = () => guard.ActivateExistingInstance("Graft-存在しないはずのテスト用タイトル-" + Guid.NewGuid());

        act.Should().NotThrow();
    }

    [Fact(DisplayName = "X11WindowActivator.TryActivateはX11に接続できない・対象ウィンドウが無い環境でも例外を投げずfalseを返せる")]
    public void TryActivateは例外を投げない()
    {
        var act = () => X11WindowActivator.TryActivate("Graft-存在しないはずのテスト用タイトル-" + Guid.NewGuid());

        act.Should().NotThrow();
    }

    /// <summary>
    /// 不具合1の回帰: 自分のウィンドウを前面化するActivateWindowHandleは、ハンドルが無効
    /// （IntPtr.Zero。この環境ではEWMHの実ウィンドウIDを渡せないため代わりに使う）でも
    /// 例外を投げず、従来のタイトル検索経路（ActivateExistingInstanceと同じ処理）へ
    /// 確実に縮退できることを確認する。Linuxは実機で前面化が機能しているため、
    /// この縮退経路が壊れるとLinuxの挙動を悪化させてしまう。
    /// </summary>
    [Fact(DisplayName = "ActivateWindowHandleはハンドルが無効でも例外を投げずタイトル検索へ縮退できる")]
    public void ActivateWindowHandleはハンドル無効時にタイトル検索へ縮退する()
    {
        using var guard = new LinuxSingleInstanceGuard();

        var act = () => guard.ActivateWindowHandle(IntPtr.Zero, "Graft-存在しないはずのテスト用タイトル-" + Guid.NewGuid());

        act.Should().NotThrow();
    }

    [Fact(DisplayName = "X11WindowActivator.TryActivateHandleはハンドルが無効・X11に接続できない環境でも例外を投げずfalseを返せる")]
    public void TryActivateHandleは例外を投げない()
    {
        var act = () => X11WindowActivator.TryActivateHandle(IntPtr.Zero);

        act.Should().NotThrow();
        act().Should().BeFalse("IntPtr.Zeroは無効なハンドルのため常に失敗として扱う");
    }
}
