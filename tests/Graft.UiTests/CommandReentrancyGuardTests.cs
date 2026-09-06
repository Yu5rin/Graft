using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.ViewModels;
using Xunit;

namespace Graft.UiTests;

/// <summary>
/// 点検で実測された多重起動（<see cref="AsyncRelayCommand.Execute"/>に自己ガードが無い）の
/// 回帰テスト。
///
/// 【修正前に実測した壊れ方】 <see cref="AsyncRelayCommand.CanExecute"/>は実行中に
/// falseを返すのに、<see cref="AsyncRelayCommand.Execute"/>は<c>_isExecuting</c>も
/// <see cref="AsyncRelayCommand.CanExecute"/>も見ずに実行していた。そのため
/// <c>Execute(null)</c>を3回連続で呼ぶと、<see cref="AsyncRelayCommand.CanExecute"/>が
/// falseを返す一方でデリゲートは3回とも走った。
///
/// 【なぜキーボード経路だけが穴だったか】 ボタン経由はAvaloniaが押下前に
/// <see cref="AsyncRelayCommand.CanExecute"/>を見るため守られている。一方
/// ShellWindow.Keyboard.cs（Ctrl+Enter＝適用、Ctrl+Alt+Z＝取り消し、Ctrl+Shift+V＝
/// 貼り付けて解析ほか）や StartupCoordinator のグローバルホットキーは
/// <c>command.Execute(null)</c>を直呼びしており、素通しで多重に起動できてしまった。
///
/// 【対処】 呼び出し側48箇所を1つずつ直すのではなく、<see cref="AsyncRelayCommand.Execute"/>
/// の先頭で<see cref="AsyncRelayCommand.CanExecute"/>を見る1箇所の修正で全経路を守る。
/// 同期版（<see cref="RelayCommand"/>・<see cref="RelayCommand{T}"/>）にも同じ形のガードを
/// 置いた（詳しい理由はRelayCommand.csのコメント参照）。
/// </summary>
public class CommandReentrancyGuardTests
{
    [AvaloniaFact(DisplayName = "AsyncRelayCommand.Executeを3連打しても、デリゲートは1回しか走らない")]
    public async Task 非同期コマンドの3連打でデリゲートは1回しか走らない()
    {
        var started = 0;
        var release = new TaskCompletionSource();
        var command = new AsyncRelayCommand(async () =>
        {
            // 1回目を「まだ終わっていない」状態のまま保持し、実機で適用中に
            // Ctrl+Enterがもう一度押された状況を再現する。
            Interlocked.Increment(ref started);
            await release.Task.ConfigureAwait(true);
        }, context: "テスト操作");

        // 実測と同じ形（3回連続の直呼び）。
        command.Execute(null);
        command.Execute(null);
        command.Execute(null);

        command.CanExecute(null).Should().BeFalse("1回目が実行中なのでCanExecuteはfalseを返す");
        started.Should().Be(1, "修正前はCanExecuteがfalseでもデリゲートが3回とも走っていた");

        release.SetResult();
        await WaitUntilIdleAsync(command).ConfigureAwait(true);
        started.Should().Be(1, "待ち合わせを解いた後も、2回目・3回目が遅れて走ることはない");
    }

    [AvaloniaFact(DisplayName = "デグレ防止: 1回目が終わってからの2回目・3回目は従来どおり実行される")]
    public async Task 逐次的な連続操作は従来どおり通る()
    {
        var executed = 0;
        var command = new AsyncRelayCommand(() =>
        {
            executed++;
            return Task.CompletedTask;
        }, context: "テスト操作");

        for (var i = 0; i < 3; i++)
        {
            command.Execute(null);
            await WaitUntilIdleAsync(command).ConfigureAwait(true);
        }

        executed.Should().Be(3, "多重起動の防止であって、連続操作の禁止ではない");
    }

    [AvaloniaFact(DisplayName = "利用側のcanExecuteがfalseなら、Executeの直呼びでも実行されない")]
    public async Task canExecuteがfalseならExecuteの直呼びでも実行されない()
    {
        var executed = 0;
        var allowed = false;
        var command = new AsyncRelayCommand(
            () =>
            {
                executed++;
                return Task.CompletedTask;
            },
            canExecute: () => allowed,
            context: "テスト操作");

        command.Execute(null);
        await WaitUntilIdleAsync(command).ConfigureAwait(true);
        executed.Should().Be(0, "押せないボタンと同じ条件を、キーボード経路の直呼びにも適用する");

        allowed = true;
        command.Execute(null);
        await WaitUntilIdleAsync(command).ConfigureAwait(true);
        executed.Should().Be(1, "条件が満たされれば従来どおり実行される");
    }

    [AvaloniaFact(DisplayName = "同期版RelayCommandも、canExecuteがfalseならExecuteの直呼びで実行されない")]
    public void 同期コマンドもcanExecuteを見る()
    {
        var executed = 0;
        var allowed = false;
        var command = new RelayCommand(() => executed++, () => allowed);

        command.Execute(null);
        executed.Should().Be(0);

        allowed = true;
        command.Execute(null);
        command.Execute(null);
        executed.Should().Be(2, "同期版は呼び出しが戻るまでに完了するため、連続実行は従来どおり通る");
    }

    [AvaloniaFact(DisplayName = "同期版RelayCommand<T>も、canExecuteがfalseならExecuteの直呼びで実行されない")]
    public void パラメータ付き同期コマンドもcanExecuteを見る()
    {
        var received = new List<string?>();
        var allowed = false;
        var command = new RelayCommand<string>(v => received.Add(v), _ => allowed);

        command.Execute("弾かれる");
        received.Should().BeEmpty();

        allowed = true;
        command.Execute("通る");
        received.Should().BeEquivalentTo(new[] { "通る" });
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

            await Task.Delay(10).ConfigureAwait(true);
        }
    }
}
