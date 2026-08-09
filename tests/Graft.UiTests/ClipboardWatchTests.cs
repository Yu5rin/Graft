using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Platform;
using Graft.Platform.Linux;
using Xunit;

namespace Graft.UiTests;

/// <summary>
/// クリップボード監視（仕様書9章・10章）の回帰テスト。9件目の不具合修正:
/// 実装（<see cref="LinuxClipboardMonitor"/>・Windows側の<c>WindowsClipboardMonitor</c>）
/// 自体は以前から存在していたが、設定画面での有効/無効切り替えが実際の開始・停止へ
/// 配線されておらず、ステータスバーに監視状態の表示も無かった（詳細はStartupCoordinator.
/// ApplyLiveSettingsChange・ShellViewModel.ClipboardWatch.csのコメント参照）。
///
/// 実際のX11ディスプレイ（<see cref="Graft.Platform.Linux.X11ClipboardReader"/>）には
/// 依存させず、<see cref="IClipboardAccess"/>のフェイクで開始・停止・パッチ検知・
/// 無反応を検証する（<see cref="LinuxClipboardMonitor"/>はOSに依存しないAvalonia標準の
/// <c>DispatcherTimer</c>のみを使うため、ヘッドレスでも実時間のポーリングがそのまま動く）。
///
/// 設定画面のトグルが実際にStart/Stopを呼ぶこと自体（StartupCoordinator.
/// ApplyLiveSettingsChange）は、既存方針（ShutdownLoggingTests等のコメント参照:
/// トレイ・ホットキー・クリップボード監視などOS資源に触れるStartAsyncは単体テストで
/// 呼ばず実機（Xvfb）検証に委ねる）に合わせ、ここでは検証しない。
/// </summary>
public class ClipboardWatchTests
{
    /// <summary>実際のクリップボードに触れないフェイク。値を直接差し替えるだけ。</summary>
    private sealed class FakeClipboardAccess : IClipboardAccess
    {
        private string? _text;

        public void SetText(string text) => _text = text;

        public Task<string?> GetTextAsync() => Task.FromResult(_text);
    }

    [AvaloniaFact(DisplayName = "Start()で監視が有効になり、Stop()で無効に戻る（設定オン→監視開始、オフ→停止に対応）")]
    public void 開始と停止()
    {
        var clipboard = new FakeClipboardAccess();
        using var monitor = new LinuxClipboardMonitor(clipboard);

        monitor.IsEnabled.Should().BeFalse("Start()を呼ぶまでは監視していない");

        var startResult = monitor.Start();
        startResult.Value.Should().BeTrue();
        monitor.IsEnabled.Should().BeTrue("設定オンに対応する操作（Start）で実際に監視状態になる必要がある");

        monitor.Stop();
        monitor.IsEnabled.Should().BeFalse("設定オフに対応する操作（Stop）で実際に監視が止まる必要がある");
    }

    [AvaloniaFact(DisplayName = "Stop()してもDispose()しても監視スレッド（DispatcherTimer）は残らず、二重に呼んでも例外にならない")]
    public void 停止と破棄は冪等()
    {
        var clipboard = new FakeClipboardAccess();
        var monitor = new LinuxClipboardMonitor(clipboard);

        monitor.Start();
        monitor.Stop();
        monitor.Stop(); // 二重に呼んでも例外にならない。
        monitor.IsEnabled.Should().BeFalse();

        monitor.Dispose();
        monitor.Dispose(); // 終了経路での多重Disposeでも例外にならない（終了時ブロッキング防止）。
    }

    [AvaloniaFact(DisplayName = "パッチ形式のテキストがコピーされるとPatchDetectedが発火する")]
    public async Task パッチ形式は検知される()
    {
        var clipboard = new FakeClipboardAccess();
        using var monitor = new LinuxClipboardMonitor(clipboard);
        string? detected = null;
        monitor.PatchDetected += (_, text) => detected = text;

        monitor.Start();

        // 監視開始時点の内容は「変化」とみなさない仕様（LinuxClipboardMonitor._isFirstTick）
        // のため、初回巡回が済んでからパッチ形式のテキストを置く。
        await WaitUntilAsync(() => monitor.IsEnabled);
        await Task.Delay(1100); // 初回巡回（1秒間隔）を消化させる。

        clipboard.SetText("<<<< FILE: src/a.cs\nusing System;\n");

        await WaitUntilAsync(() => detected is not null, TimeSpan.FromSeconds(5));

        detected.Should().NotBeNull("ブロックヘッダを含むテキストは検知されなければならない");
        detected.Should().Contain("<<<< FILE:");
    }

    [AvaloniaFact(DisplayName = "通常のテキストがコピーされてもPatchDetectedは発火しない")]
    public async Task 通常テキストは無反応()
    {
        var clipboard = new FakeClipboardAccess();
        using var monitor = new LinuxClipboardMonitor(clipboard);
        var detectedCount = 0;
        monitor.PatchDetected += (_, _) => detectedCount++;

        monitor.Start();
        await Task.Delay(1100); // 初回巡回を消化させる。

        clipboard.SetText("これはふつうの文章です。パッチ形式のヘッダは含みません。");
        await Task.Delay(2200); // 2回以上の巡回を待っても発火しないことを確認する。

        detectedCount.Should().Be(0, "パッチ形式らしいと判定できない通常のコピー内容には一切反応してはならない");
    }

    [AvaloniaFact(DisplayName = "11件目の不具合修正: パッチ検知の後に通常のテキストへ変わるとNonPatchTextChangedが発火する")]
    public async Task パッチ検知の後に通常テキストへ変わると非パッチ通知が発火する()
    {
        var clipboard = new FakeClipboardAccess();
        using var monitor = new LinuxClipboardMonitor(clipboard);
        string? patchDetected = null;
        var nonPatchCount = 0;
        monitor.PatchDetected += (_, text) => patchDetected = text;
        monitor.NonPatchTextChanged += (_, _) => nonPatchCount++;

        monitor.Start();
        await WaitUntilAsync(() => monitor.IsEnabled);
        await Task.Delay(1100); // 初回巡回を消化させる。

        clipboard.SetText("<<<< FILE: src/a.cs\nusing System;\n");
        await WaitUntilAsync(() => patchDetected is not null, TimeSpan.FromSeconds(5));
        patchDetected.Should().NotBeNull();
        nonPatchCount.Should().Be(0, "パッチ検知の時点ではNonPatchTextChangedは発火しない");

        // 不具合の再現条件: パッチ検知の直後に通常テキストへコピーし直す。
        clipboard.SetText("これはふつうの文章です。パッチ形式のヘッダは含みません。");
        await WaitUntilAsync(() => nonPatchCount > 0, TimeSpan.FromSeconds(5));

        nonPatchCount.Should().Be(1, "通常テキストへ変化したら通知を消すためのシグナルが1回発火するはず");
    }

    [AvaloniaFact(DisplayName = "パッチ形式のテキストからパッチ形式のテキストへ変わってもNonPatchTextChangedは発火しない")]
    public async Task パッチからパッチへの変化では非パッチ通知は発火しない()
    {
        var clipboard = new FakeClipboardAccess();
        using var monitor = new LinuxClipboardMonitor(clipboard);
        var patchCount = 0;
        var nonPatchCount = 0;
        monitor.PatchDetected += (_, _) => patchCount++;
        monitor.NonPatchTextChanged += (_, _) => nonPatchCount++;

        monitor.Start();
        await WaitUntilAsync(() => monitor.IsEnabled);
        await Task.Delay(1100);

        clipboard.SetText("<<<< FILE: src/a.cs\nusing System;\n");
        await WaitUntilAsync(() => patchCount > 0, TimeSpan.FromSeconds(5));

        clipboard.SetText("<<<< FILE: src/b.cs\nusing System.Linq;\n");
        await WaitUntilAsync(() => patchCount > 1, TimeSpan.FromSeconds(5));

        nonPatchCount.Should().Be(0, "パッチ形式同士の変化ではNonPatchTextChangedを発火させない");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(2));
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }
    }
}
