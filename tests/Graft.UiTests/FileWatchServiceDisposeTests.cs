using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FluentAssertions;
using Graft.Editor;

namespace Graft.UiTests;

/// <summary>
/// <see cref="FileWatchService"/>を<c>Dispose()</c>した後で<c>Start()</c>が呼ばれても、監視を
/// 始めない（＝誰も止めないinotifyのインスタンスを残さない）ことの回帰テスト。
///
/// 実際に起きていた形: <c>ExplorerViewModel.SetProjectAsync</c>はツリーの列挙を待ってから
/// <c>Start()</c>を呼ぶ。その待ちの最中に<c>ShellViewModel.Dispose()</c>が走ると、Disposeの後で
/// Startが呼ばれ、止める者のいない<see cref="FileSystemWatcher"/>が残る。UiTestsの全件実行では
/// これが5回起きていた。
/// </summary>
public class FileWatchServiceDisposeTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-filewatch-dispose", Guid.NewGuid().ToString("N"));

    public FileWatchServiceDisposeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 後始末の失敗は検証結果に影響しない。
        }

        GC.SuppressFinalize(this);
    }

    [AvaloniaFact(DisplayName = "対照: 破棄していなければ、Start後のファイル作成が通知される（以下のテストが空振りでないことの確認）")]
    public async Task 対照_破棄前のStartは変更を通知する()
    {
        using var service = new FileWatchService();
        var changed = 0;
        service.DirectoriesChanged += (_, _) => Interlocked.Increment(ref changed);

        service.Start(_root).IsSuccess.Should().BeTrue();
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "x").ConfigureAwait(true);

        await WaitUntilAsync(() => Volatile.Read(ref changed) > 0).ConfigureAwait(true);
        Volatile.Read(ref changed).Should().BeGreaterThan(0, "監視が動いていれば、デバウンス後に通知が届く");
    }

    [AvaloniaFact(DisplayName = "Dispose後にStartしても監視は始まらず、ファイルを作っても通知されない")]
    public async Task Dispose後のStartは監視を始めない()
    {
        var service = new FileWatchService();
        var changed = 0;
        service.DirectoriesChanged += (_, _) => Interlocked.Increment(ref changed);

        service.Dispose();
        var result = service.Start(_root);

        result.IsSuccess.Should().BeTrue("破棄後のStartは何もせず、失敗（E704）としても報告しない");
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "x").ConfigureAwait(true);

        // 「通知されないこと」は条件が成り立つのを待てないため、対照のテストで通知が届くのに
        // 要する時間（デバウンス250ms＋余裕）より十分長く、固定時間だけ待つ。
        var until = DateTime.UtcNow.AddMilliseconds(1500);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20).ConfigureAwait(true);
        }

        Volatile.Read(ref changed).Should().Be(0, "破棄済みのサービスが監視を始めてはならない（止める者がいない監視が残る）");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10).ConfigureAwait(true);
        }
    }
}
