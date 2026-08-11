using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using Xunit;

namespace Graft.UiTests;

/// <summary>
/// 不具合2（「再起動」ボタンで終了はするが再起動しない）の回帰テスト。
///
/// 真因: <see cref="Graft.App.RequestRestart"/>は以前
/// <see cref="IClassicDesktopStyleApplicationLifetime.Shutdown"/>を呼んでいたが、Avalonia側の
/// 実装（<c>ClassicDesktopStyleApplicationLifetime.Shutdown</c>）はこれを常に<c>force: true</c>
/// として扱い、<c>force</c>がtrueだと<see cref="IClassicDesktopStyleApplicationLifetime.
/// ShutdownRequested"/>イベントを一切発火しない（Avalonia.Controls.dllの逆コンパイルで確認）。
/// <see cref="Graft.App.OnShutdownRequested"/>はこのイベントを購読して初めて後始末・
/// <see cref="Graft.Core.RestartSequencer"/>経由の新プロセス起動を行うため、<c>Shutdown</c>を
/// 呼ぶとその経路へ一切到達しないまま（後始末も新プロセス起動も行われないまま）プロセスが
/// 強制終了していた。
///
/// このテストはApp.axaml.cs個別のロジックではなく、Avaloniaが提供する
/// <c>Shutdown</c>と<c>TryShutdown</c>の挙動差そのものを固定する。将来<c>App.RequestRestart</c>が
/// うっかり<c>Shutdown</c>へ戻されても、この回帰テスト自体は変わらず「TryShutdownでなければ
/// ShutdownRequestedは発火しない」という前提が崩れていないことを示すが、App側の
/// 誤戻りは検知できない点に注意（App自体はAvaloniaのフル起動シーケンスに依存し単体テストしにくい
/// ため、実機/Xvfbでの再起動確認・tests/Graft.UiTests/RestartMutexRetryTests.csと合わせて
/// 検証する）。
/// </summary>
public class DesktopShutdownSemanticsTests
{
    [AvaloniaFact(DisplayName = "不具合2回帰: TryShutdownはShutdownRequestedを発火し、Cancel=trueで実際の終了を保留できる")]
    public void TryShutdownはShutdownRequestedを発火してCancelできる()
    {
        var lifetime = new ClassicDesktopStyleApplicationLifetime();
        var raised = false;

        lifetime.ShutdownRequested += (_, e) =>
        {
            raised = true;
            e.Cancel = true; // App.OnShutdownRequestedが最初の呼び出しで必ず行う（後始末を先に済ませるため）。
        };

        var proceeded = lifetime.TryShutdown();

        raised.Should().BeTrue(
            "TryShutdownはShutdownRequestedを発火しなければならない。これが発火しないと" +
            "App.OnShutdownRequested（後始末・RestartSequencer経由の新プロセス起動）へ到達できない");
        proceeded.Should().BeFalse("Cancel=trueにした場合、TryShutdownは実際の終了処理へ進まずfalseを返すはず");
    }
}
