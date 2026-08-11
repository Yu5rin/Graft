using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.ViewModels;
using Xunit;

namespace Graft.UiTests;

/// <summary>
/// Windows実機クラッシュ（設定→バージョン情報→「最新のログを表示」でアプリごと落ちた不具合、
/// 不具合1原因B）の構造的な再発防止テスト。
///
/// 真因: <see cref="AsyncRelayCommand.Execute"/>は<c>ICommand.Execute</c>の制約上
/// <c>async void</c>になる。旧実装はここで例外を捕まえず、SynchronizationContext経由で
/// <c>AppDomain.UnhandledException</c>へ伝播させる設計だった（「記録はできる」という想定）。
/// しかし<c>AppDomain.UnhandledException</c>はプロセスが終了することの通知に過ぎず、
/// ハンドラ内で記録はできてもプロセスの終了そのものは止められない。実機では
/// <c>LogTailReader.ReadTail</c>が投げた<c>IOException</c>がこの経路で最上位まで突き抜け、
/// アプリ全体が落ちた。
///
/// 対処: <see cref="AsyncRelayCommand.Execute"/>を<see cref="SafeHandler.RunAsync(string, Func{Task})"/>
/// と同じ作法に統一した（附録A.4）。ここではその契約——「実行中の例外は
/// <see cref="SafeHandler.OnUnexpected"/>へ委ねられ、外（呼び出し元・プロセス全体）へは
/// 一切漏れない」——を、<see cref="AsyncRelayCommand"/>を使う48箇所すべてに共通する構造として
/// 検証する（個々のコマンドを1つずつ検証するのではなく、共通の仕組み自体を検証する）。
///
/// <see cref="SafeHandler.OnUnexpected"/>はプロセス全体で共有する静的な差し込み口のため、
/// Avalonia.Headless.XUnit（<c>[assembly: AvaloniaTestApplication]</c>）がこのアセンブリの
/// テストをすべて単一のヘッドレスApplication上で直列実行することを前提に、書き換え・復元を
/// finallyで確実に行う（他テストとの競合を避けるため）。
/// </summary>
public class AsyncRelayCommandTests
{
    [AvaloniaFact(DisplayName = "実行中の想定外の例外はSafeHandler.OnUnexpected経由で捕捉され、外へは漏れない")]
    public async Task 実行中の例外は外へ漏れずSafeHandler経由で通知される()
    {
        var captured = new List<(string Context, Exception Exception)>();
        var previous = SafeHandler.OnUnexpected;
        SafeHandler.OnUnexpected = (context, ex) => captured.Add((context, ex));
        try
        {
            // LogTailReaderのIOExceptionと同じ「読み取り中に想定外の例外が飛ぶ」状況を模した、
            // 最小の再現ケース。
            var command = new AsyncRelayCommand(
                () => throw new IOException("テスト用の想定外の例外"), context: "テスト操作");

            // async voidであるExecute自体が例外を投げて呼び出し元（xUnit）を巻き込み
            // テストランナーごとクラッシュさせる、というのが修正前の危険な状態だった。
            command.Execute(null);

            await WaitUntilIdleAsync(command);

            captured.Should().ContainSingle("SafeHandler.OnUnexpected経由で1回だけ通知される必要がある");
            captured[0].Context.Should().Be("テスト操作", "AsyncRelayCommandのcontextがそのまま通知に使われる必要がある");
            captured[0].Exception.Should().BeOfType<IOException>();
            command.IsExecuting.Should().BeFalse("例外があってもIsExecutingは必ずfalseへ戻る（多重実行防止の解除）");
        }
        finally
        {
            SafeHandler.OnUnexpected = previous;
        }
    }

    [AvaloniaFact(DisplayName = "OperationCanceledExceptionは異常として扱わず、通知しない")]
    public async Task キャンセルは通知しない()
    {
        var captured = new List<(string Context, Exception Exception)>();
        var previous = SafeHandler.OnUnexpected;
        SafeHandler.OnUnexpected = (context, ex) => captured.Add((context, ex));
        try
        {
            var command = new AsyncRelayCommand(
                () => throw new OperationCanceledException(), context: "テスト操作");

            command.Execute(null);
            await WaitUntilIdleAsync(command);

            captured.Should().BeEmpty("取り消しは異常ではないため通知しない（SafeHandler.RunAsyncと同じ扱い）");
            command.IsExecuting.Should().BeFalse();
        }
        finally
        {
            SafeHandler.OnUnexpected = previous;
        }
    }

    [AvaloniaFact(DisplayName = "contextを省略しても既定の文言で通知され、例外は握り潰されない")]
    public async Task contextを省略しても既定文言で通知される()
    {
        var captured = new List<(string Context, Exception Exception)>();
        var previous = SafeHandler.OnUnexpected;
        SafeHandler.OnUnexpected = (context, ex) => captured.Add((context, ex));
        try
        {
            var command = new AsyncRelayCommand(() => throw new InvalidOperationException());

            command.Execute(null);
            await WaitUntilIdleAsync(command);

            captured.Should().ContainSingle();
            captured[0].Context.Should().NotBeNullOrWhiteSpace();
        }
        finally
        {
            SafeHandler.OnUnexpected = previous;
        }
    }

    [AvaloniaFact(DisplayName = "正常終了時は通知が発生せず、IsExecutingがfalseへ戻る")]
    public async Task 正常終了時は通知が発生しない()
    {
        var captured = new List<(string Context, Exception Exception)>();
        var previous = SafeHandler.OnUnexpected;
        SafeHandler.OnUnexpected = (context, ex) => captured.Add((context, ex));
        try
        {
            var executed = false;
            var command = new AsyncRelayCommand(() =>
            {
                executed = true;
                return Task.CompletedTask;
            }, context: "テスト操作");

            command.Execute(null);
            await WaitUntilIdleAsync(command);

            executed.Should().BeTrue();
            captured.Should().BeEmpty();
            command.IsExecuting.Should().BeFalse();
        }
        finally
        {
            SafeHandler.OnUnexpected = previous;
        }
    }

    private static async Task WaitUntilIdleAsync(AsyncRelayCommand command)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (command.IsExecuting)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("コマンドの完了待ちがタイムアウトしました。");
            }

            await Task.Delay(10);
        }
    }
}
