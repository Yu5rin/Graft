using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Platform;
using Graft.Platform.Linux;
using Graft.Views;
using Xunit;

namespace Graft.UiTests;

/// <summary>
/// 機能追加: クリップボード監視でパッチ形式を検知したら前面化する
/// （<see cref="StartupCoordinator.ActivateWindowOnPatchDetected"/>、既定オン）の回帰テスト。
///
/// HotkeyReapplyTests.csと同じ理由・同じ形で、実際のOS資源（Windows/Linuxの具象
/// <see cref="ISingleInstanceGuard"/>）に触れる経路は既存方針（ClipboardWatchTests.csの
/// コメント参照）どおり単体テストでは呼ばず実機（Xvfb）検証に委ねる。ここでは
/// <see cref="ISingleInstanceGuard"/>のフェイクを使い、「前面化の実行自体」（実際のOS呼び出し）は
/// フェイクへ差し替えたうえで、設定オン/オフでの要求有無・既に前面にある場合のちらつき回避・
/// 最小化やトレイからの復帰・OS側の縮退のログ記録要否を検証する。
/// </summary>
public class ClipboardActivationTests
{
    private const string MainWindowTitle = "Graft";

    [AvaloniaFact(DisplayName = "設定オンなら前面化が要求される（多重起動検出時と同じISingleInstanceGuard.ActivateExistingInstanceが呼ばれる）")]
    public void 設定オンで前面化が要求される()
    {
        var singleInstance = new FakeSingleInstanceGuard();
        var window = new Window();
        window.Show();
        window.WindowState = WindowState.Minimized; // 最小化状態を模す。

        var outcome = StartupCoordinator.ActivateWindowOnPatchDetected(
            singleInstance, window, MainWindowTitle, activateOnDetectSetting: true, isAlreadyForeground: false);

        outcome.Should().Be(ClipboardActivationOutcome.Activated);
        singleInstance.ActivateCallCount.Should().Be(1, "設定オンなら多重起動時の前面化と同じ経路が呼ばれるはず");
        singleInstance.LastRequestedTitle.Should().Be(MainWindowTitle);
    }

    [AvaloniaFact(DisplayName = "設定オフなら前面化は要求されない")]
    public void 設定オフでは要求されない()
    {
        var singleInstance = new FakeSingleInstanceGuard();
        var window = new Window();
        window.Show();

        var outcome = StartupCoordinator.ActivateWindowOnPatchDetected(
            singleInstance, window, MainWindowTitle, activateOnDetectSetting: false, isAlreadyForeground: false);

        outcome.Should().Be(ClipboardActivationOutcome.Disabled);
        singleInstance.ActivateCallCount.Should().Be(0, "設定オフの間はOS呼び出しを一切行ってはならない");
    }

    [AvaloniaFact(DisplayName = "既に前面にある場合は何もしない（ちらつき・フォーカス移動を避ける）")]
    public void 既に前面にある場合は要求されない()
    {
        var singleInstance = new FakeSingleInstanceGuard();
        var window = new Window();
        window.Show();

        var outcome = StartupCoordinator.ActivateWindowOnPatchDetected(
            singleInstance, window, MainWindowTitle, activateOnDetectSetting: true, isAlreadyForeground: true);

        outcome.Should().Be(ClipboardActivationOutcome.AlreadyForeground);
        singleInstance.ActivateCallCount.Should().Be(0, "既に前面にあるならOS呼び出し自体を行わず、ちらつきを起こさない");
    }

    [AvaloniaFact(DisplayName = "最小化されていれば通常状態へ戻してから前面化する")]
    public void 最小化されていれば元に戻してから前面化する()
    {
        var singleInstance = new FakeSingleInstanceGuard();
        var window = new Window();
        window.Show();
        window.WindowState = WindowState.Minimized;

        StartupCoordinator.ActivateWindowOnPatchDetected(
            singleInstance, window, MainWindowTitle, activateOnDetectSetting: true, isAlreadyForeground: false);

        window.WindowState.Should().Be(WindowState.Normal, "最小化されたままでは前面化しても見えないため、通常状態へ戻す必要がある");
    }

    [AvaloniaFact(DisplayName = "タスクトレイに隠れている（非表示）場合はまず表示してから前面化する")]
    public void トレイに隠れていれば表示してから前面化する()
    {
        var singleInstance = new FakeSingleInstanceGuard();
        var window = new Window();
        window.Show();
        window.Hide(); // 「閉じたときの動作＝トレイに常駐」でウィンドウマネージャから見えなくなった状態を模す。

        window.IsVisible.Should().BeFalse();

        var outcome = StartupCoordinator.ActivateWindowOnPatchDetected(
            singleInstance, window, MainWindowTitle, activateOnDetectSetting: true, isAlreadyForeground: false);

        window.IsVisible.Should().BeTrue("トレイに隠れていた場合、前面化の前にまず表示し直す必要がある");
        outcome.Should().Be(ClipboardActivationOutcome.Activated);
        singleInstance.ActivateCallCount.Should().Be(1);
    }

    [AvaloniaFact(DisplayName = "OS側の制約で前面化が拒否された場合はDegradedを返し、エラーにはならない")]
    public void OS側の制約で縮退してもエラーにはならない()
    {
        var singleInstance = new FakeSingleInstanceGuard(activateSucceeds: false);
        var window = new Window();
        window.Show();

        var act = () => StartupCoordinator.ActivateWindowOnPatchDetected(
            singleInstance, window, MainWindowTitle, activateOnDetectSetting: true, isAlreadyForeground: false);

        var outcome = act.Should().NotThrow(
            "Windowsのフォーカス窃取防止でSetForegroundWindowが拒否される縮退はエラー扱いにしない").Which;
        outcome.Should().Be(ClipboardActivationOutcome.Degraded);
        singleInstance.ActivateCallCount.Should().Be(1, "呼び出し自体は行い、結果として縮退したことを戻り値で伝える");
    }

    [AvaloniaFact(DisplayName = "通常のテキストをコピーしてもPatchDetectedが発火せず、前面化は要求されない")]
    public async Task 通常テキストでは前面化が要求されない()
    {
        // StartupCoordinator.WirePlatformServicesと同じ配線: 前面化はIClipboardMonitor.
        // PatchDetectedが発火したときだけ要求する（NonPatchTextChangedからは呼ばれない）。
        // ここでは実際の前面化呼び出しをフェイクのカウンタに置き換え、パッチ形式でない
        // テキストをコピーしてもカウンタが増えないことを確認する。
        var clipboard = new FakeClipboardAccess();
        using var monitor = new LinuxClipboardMonitor(clipboard);
        var singleInstance = new FakeSingleInstanceGuard();
        var window = new Window();
        window.Show();
        var activationRequested = 0;
        monitor.PatchDetected += (_, _) =>
        {
            activationRequested++;
            StartupCoordinator.ActivateWindowOnPatchDetected(
                singleInstance, window, MainWindowTitle, activateOnDetectSetting: true, isAlreadyForeground: false);
        };

        monitor.Start();
        await Task.Delay(1100); // 初回巡回を消化させる（ClipboardWatchTests.csと同じ理由）。

        clipboard.SetText("これはふつうの文章です。パッチ形式のヘッダは含みません。");
        await Task.Delay(2200); // 2回以上の巡回を待っても発火しないことを確認する。

        activationRequested.Should().Be(0, "パッチ形式と判定できない通常のコピー内容では前面化を要求してはならない");
        singleInstance.ActivateCallCount.Should().Be(0);
    }

    [AvaloniaFact(DisplayName = "回帰修正: 前面化がOS側の制約で縮退し反応時の挙動が既定（トレイ通知のみ）なら、通知は届く")]
    public void 縮退時に反応時の挙動が既定ならトレイ通知が届く()
    {
        // 0bb1b4eの回帰: OnClipboardPatchDetectedはActivateOnDetectがオンの間、Disabled以外
        // （＝Degradedも含む）ならすべて早期returnしてしまい、「反応時の挙動＝トレイ通知のみ」を
        // 選んでいる利用者に検知が一切伝わらなかった（前面化にも通知にも失敗する
        // 「何も起きない」状態）。ここでは実際の分岐（StartupCoordinator.
        // ActivateOrFallBackOnPatchDetected、OnClipboardPatchDetected本体が呼ぶのと同じもの）を
        // 直接呼び、前面化がDegradedになる状況でもトレイ通知まで届くことを確認する。
        var singleInstance = new FakeSingleInstanceGuard(activateSucceeds: false);
        var tray = new FakeTrayIcon();
        var window = new Window();
        window.Show();

        var activation = StartupCoordinator.ActivateOrFallBackOnPatchDetected(
            singleInstance, tray, window, MainWindowTitle,
            activateOnDetectSetting: true, isAlreadyForeground: false, autoParsed: false, action: "notify");

        activation.Should().Be(ClipboardActivationOutcome.Degraded);
        tray.BalloonCallCount.Should().Be(1, "前面化が縮退した代わりに、従来どおりトレイ通知だけは届く必要がある");
    }

    [AvaloniaFact(DisplayName = "前面化が成功したときはトレイ通知は呼ばれない")]
    public void 前面化成功時はトレイ通知が呼ばれない()
    {
        var singleInstance = new FakeSingleInstanceGuard(activateSucceeds: true);
        var tray = new FakeTrayIcon();
        var window = new Window();
        window.Show();

        var activation = StartupCoordinator.ActivateOrFallBackOnPatchDetected(
            singleInstance, tray, window, MainWindowTitle,
            activateOnDetectSetting: true, isAlreadyForeground: false, autoParsed: false, action: "notify");

        activation.Should().Be(ClipboardActivationOutcome.Activated);
        tray.BalloonCallCount.Should().Be(0, "前面化に成功した場合はトレイ通知を追加で出す必要が無い");
    }

    [AvaloniaFact(DisplayName = "既に前面にあるときはトレイ通知は呼ばれない")]
    public void 既に前面にある場合はトレイ通知が呼ばれない()
    {
        var singleInstance = new FakeSingleInstanceGuard(activateSucceeds: true);
        var tray = new FakeTrayIcon();
        var window = new Window();
        window.Show();

        var activation = StartupCoordinator.ActivateOrFallBackOnPatchDetected(
            singleInstance, tray, window, MainWindowTitle,
            activateOnDetectSetting: true, isAlreadyForeground: true, autoParsed: false, action: "notify");

        activation.Should().Be(ClipboardActivationOutcome.AlreadyForeground);
        tray.BalloonCallCount.Should().Be(0, "既に前面にある場合はトレイ通知を追加で出す必要が無い");
    }

    [AvaloniaFact(DisplayName = "縮退時、反応時の挙動が「アクティブ表示」でもRestoreWindowの二重呼び出しで例外は起きない")]
    public void 縮退時にアクティブ表示設定でも例外は起きない()
    {
        // 依頼3: DegradedのフォールバックでAction=="active"の場合、ActivateWindowOnPatchDetected
        // が既に行ったwindow.Show()・WindowState=Normalに続けてHandleClipboardActivation
        // Fallback側のRestoreWindow（Show/WindowState/Activateを再度呼ぶ）が実行される。
        // 冪等な操作の組み合わせのため例外は起きないことを確認する。
        var singleInstance = new FakeSingleInstanceGuard(activateSucceeds: false);
        var tray = new FakeTrayIcon();
        var window = new Window();
        window.Show();

        var act = () => StartupCoordinator.ActivateOrFallBackOnPatchDetected(
            singleInstance, tray, window, MainWindowTitle,
            activateOnDetectSetting: true, isAlreadyForeground: false, autoParsed: false, action: "active");

        var activation = act.Should().NotThrow(
            "RestoreWindowの二重呼び出しは冪等な操作の組み合わせであり、例外を起こしてはならない").Which;

        activation.Should().Be(ClipboardActivationOutcome.Degraded);
        window.WindowState.Should().Be(WindowState.Normal);
        tray.BalloonCallCount.Should().Be(0, "「アクティブ表示」設定ではトレイ通知は呼ばれず前面化のみ行われる");
    }

    /// <summary>実際のクリップボードに触れないフェイク（ClipboardWatchTests.csと同じもの）。</summary>
    private sealed class FakeClipboardAccess : IClipboardAccess
    {
        private string? _text;

        public void SetText(string text) => _text = text;

        public Task<string?> GetTextAsync() => Task.FromResult(_text);
    }

    /// <summary>実際のOS資源に一切触れないフェイク。ActivateExistingInstanceの成否・呼び出し回数を記録する。</summary>
    private sealed class FakeSingleInstanceGuard : ISingleInstanceGuard
    {
        private readonly bool _activateSucceeds;

        public FakeSingleInstanceGuard(bool activateSucceeds = true) => _activateSucceeds = activateSucceeds;

        public int ActivateCallCount { get; private set; }

        public string? LastRequestedTitle { get; private set; }

        public bool IsSupported => true;

        public string? UnsupportedReason => null;

        public bool TryAcquire(string name) => true;

        public bool ActivateExistingInstance(string mainWindowTitle)
        {
            ActivateCallCount++;
            LastRequestedTitle = mainWindowTitle;
            return _activateSucceeds;
        }

        public void Dispose()
        {
            // 何もしない。
        }
    }

    /// <summary>
    /// 実際のトレイ資源（D-Bus/NotifyIcon）に一切触れないフェイク。ShowBalloonの呼び出し回数だけを記録する。
    /// </summary>
    private sealed class FakeTrayIcon : ITrayIcon
    {
        public int BalloonCallCount { get; private set; }

        public bool IsSupported => true;

        public string? UnsupportedReason => null;

        public void Configure(TrayMenuDescriptor menu)
        {
            // 何もしない。
        }

        public void Show()
        {
            // 何もしない。
        }

        public void ShowBalloon(string title, string text) => BalloonCallCount++;

        public void Dispose()
        {
            // 何もしない。
        }
    }
}
