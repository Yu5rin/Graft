using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FluentAssertions;
using Xunit;

namespace Graft.UiTests;

/// <summary>
/// 不具合1（AvaloniaEdit内部の未処理例外でアプリが落ちる不具合）の回帰テスト。
///
/// App.axaml.csが採用した対処（<c>Dispatcher.UIThread.UnhandledException</c>を購読し、
/// <c>e.Handled = true</c>にする）そのものがAvaloniaの想定どおりに機能することを、
/// <see cref="Graft.App"/>を経由せず最小構成で検証する。判定ロジック自体
/// （<see cref="Graft.Core.AvaloniaEditExceptionGuard"/>）は純粋関数のため
/// tests/Graft.Tests/AvaloniaEditExceptionGuardTests.cs側でカバーする。
/// </summary>
public class DispatcherUnhandledExceptionTests
{
    [AvaloniaFact(DisplayName = "不具合1回帰: UIスレッドのジョブ内の例外はHandled=trueにすれば外へ伝播しない")]
    public void UnhandledExceptionでHandledにするとジョブの例外が伝播しない()
    {
        Exception? seen = null;
        void Handler(object? s, DispatcherUnhandledExceptionEventArgs e)
        {
            seen = e.Exception;
            e.Handled = true;
        }

        Dispatcher.UIThread.UnhandledException += Handler;
        try
        {
            // DispatcherTimerのTick・レイアウト/描画パスと同様、awaitされない「投げっぱなし」の
            // ジョブとしてPostする（実機での折りたたみの不具合と同じ経路）。
            Dispatcher.UIThread.Post(() => throw new InvalidOperationException("AvaloniaEdit内部からの例外を模したテスト"));

            var act = () => Dispatcher.UIThread.RunJobs();
            act.Should().NotThrow("UnhandledExceptionでHandled=trueにしたジョブの例外はアプリを継続させなければならない");
            seen.Should().NotBeNull("UnhandledExceptionイベント自体は必ず発火しなければならない");
        }
        finally
        {
            Dispatcher.UIThread.UnhandledException -= Handler;
        }
    }

    [AvaloniaFact(DisplayName = "不具合1回帰（対照）: Handled=falseのままだとジョブの例外はそのまま伝播する")]
    public void UnhandledExceptionでHandledにしないとジョブの例外が伝播する()
    {
        void Handler(object? s, DispatcherUnhandledExceptionEventArgs e)
        {
            // 意図的にHandledへ触れない（既定はfalse）。
        }

        Dispatcher.UIThread.UnhandledException += Handler;
        try
        {
            Dispatcher.UIThread.Post(() => throw new InvalidOperationException("Handledにしない場合の対照テスト"));

            var act = () => Dispatcher.UIThread.RunJobs();
            act.Should().Throw<InvalidOperationException>(
                "対照実験として、Handledにしなければ従来どおり例外が伝播すること（＝Handled=trueが効いていることの裏付け）を確認する");
        }
        finally
        {
            Dispatcher.UIThread.UnhandledException -= Handler;
        }
    }
}
