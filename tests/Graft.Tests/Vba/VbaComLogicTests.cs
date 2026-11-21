using System.Runtime.InteropServices;
using FluentAssertions;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Platform.Windows;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// COM 層（<c>WindowsVbaBridge</c>）のうち、COM に触れずに確かめられる判定のテスト（仕様書_VBA連携 5章・10.1）。
/// ROT の表示名からのブック候補の選択、HRESULT の分類、プロジェクトの状態値の解釈、再試行の時間の扱い、
/// 専用スレッドの直列実行と時間切れ（スレッドを捨てて作り直す）、控えのファイル名。
/// 実際の COM の呼び出し順・解放・Excel と VBE の挙動は、Linux では確かめられない（Windows 実機検証）。
/// </summary>
public class VbaComLogicTests
{
    // ------------------------------------------------------------------
    // ROT の表示名
    // ------------------------------------------------------------------

    [Theory(DisplayName = "ROT の表示名: ブックの拡張子で終わるものだけが候補（大文字小文字は区別しない）")]
    [InlineData(@"C:\Work\計画.xlsm", true)]
    [InlineData(@"C:\Work\計画.XLSB", true)]
    [InlineData(@"C:\Addins\tool.xlam", true)]
    [InlineData(@"C:\Work\old.xls", true)]
    [InlineData(@"C:\Work\plain.xlsx", true)]
    [InlineData(@"C:\Work\memo.docx", false)]
    [InlineData(@"!{00020812-0000-0000-C000-000000000046}", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void 表示名の判定(string? displayName, bool expected)
        => VbaRotNames.IsWorkbookDisplayName(displayName).Should().Be(expected);

    [Fact(DisplayName = "FindMatches: 大文字小文字・区切り文字・.. の違いは同じパスとみなし、別の場所の同名ブックは別物")]
    public void 同じパスの候補だけが選ばれる()
    {
        var names = new string?[]
        {
            @"C:\Other\計画.xlsm",
            @"c:\work\計画.XLSM",
            @"C:\Work\sub\..\計画.xlsm",
            @"C:\Work\memo.docx",
            null,
        };

        VbaRotNames.FindMatches(names, @"C:\Work\計画.xlsm").Should().Equal(1, 2);
    }

    [Fact(DisplayName = "FindMatches: 見つからなければ空（Excel を起動して開いたりしない。呼び出し側が E804 にする）")]
    public void 見つからなければ空()
        => VbaRotNames.FindMatches(new string?[] { @"C:\A\a.xlsm" }, @"C:\B\b.xlsm").Should().BeEmpty();

    [Fact(DisplayName = "UNC パスとドライブ文字の違いは解決しない（別物として扱う。取扱説明書に書く制限）")]
    public void UNCとドライブ文字は別物()
        => VbaRotNames.SamePath(@"\\server\share\a.xlsm", @"Z:\a.xlsm").Should().BeFalse();

    [Theory(DisplayName = "OneDrive／SharePoint の URL は非対応として検出する")]
    [InlineData("https://contoso.sharepoint.com/sites/x/a.xlsm", true)]
    [InlineData("HTTP://example.com/a.xlsm", true)]
    [InlineData(@"C:\Users\me\OneDrive\a.xlsm", false)]
    [InlineData(null, false)]
    public void URLの判定(string? fullName, bool expected)
        => VbaRotNames.IsUrlLocation(fullName).Should().Be(expected);

    [Theory(DisplayName = "マクロを持てない形式は .xlsx（E806）。.xls／.xlsm／.xlsb／.xlam は持てる")]
    [InlineData(@"C:\a.xlsx", true)]
    [InlineData(@"C:\a.XLSX", true)]
    [InlineData(@"C:\a.xlsm", false)]
    [InlineData(@"C:\a.xls", false)]
    [InlineData(@"C:\a.xlsb", false)]
    [InlineData(@"C:\a.xlam", false)]
    public void マクロ不可の形式(string fullName, bool expected)
        => VbaRotNames.IsNoMacroFormat(fullName).Should().Be(expected);

    // ------------------------------------------------------------------
    // HRESULT の分類
    // ------------------------------------------------------------------

    [Theory(DisplayName = "HRESULT の分類: 拒否（0x800AC472 を含む）は再試行、切断は E801、それ以外は Other")]
    [InlineData(0x80010001u, VbaComFailure.RetryLater)]
    [InlineData(0x8001010Au, VbaComFailure.RetryLater)]
    [InlineData(0x800AC472u, VbaComFailure.RetryLater)]
    [InlineData(0x800706BAu, VbaComFailure.Disconnected)]
    [InlineData(0x80010108u, VbaComFailure.Disconnected)]
    [InlineData(0x800706BEu, VbaComFailure.Disconnected)]
    [InlineData(0x800A03ECu, VbaComFailure.Other)]
    [InlineData(0x80004005u, VbaComFailure.Other)]
    public void HRESULTの分類(uint hresult, VbaComFailure expected)
        => VbaComHResult.Classify(unchecked((int)hresult)).Should().Be(expected);

    [Theory(DisplayName = "VBProject の取得で COMException: 既知の値（0x800A03EC）だけが「アクセスを信頼していない」の E802。切断もそれ以外も E801（HRESULT つき）")]
    [InlineData(0x800706BAu, ErrorCode.E801)]
    [InlineData(0x80010108u, ErrorCode.E801)]
    [InlineData(0x800A03ECu, ErrorCode.E802)]
    [InlineData(0x80004005u, ErrorCode.E801)]
    [InlineData(0x800AC472u, ErrorCode.E801)]
    public void VBProject取得の失敗の分類(uint hresult, ErrorCode expected)
        => VbaComHResult.ClassifyVbProjectAccess(unchecked((int)hresult)).Should().Be(expected);

    [Fact(DisplayName = "不具合7: 0x800AC472（セル編集中の VBA_E_IGNORE）は ComRetry が再試行し、尽きたら ComBusyException（E805 になる）。包まれた例外でも同じ")]
    public void セル編集中の拒否は再試行される()
    {
        var rejected = new COMException("VBA_E_IGNORE", unchecked((int)0x800AC472));
        ComRetry.IsRetryable(rejected).Should().BeTrue();
        ComRetry.IsRetryable(new System.Reflection.TargetInvocationException(rejected)).Should().BeTrue();
        VbaComHResult.IsRetryLater(unchecked((int)0x800AC472)).Should().BeTrue();

        var attempts = 0;
        var elapsed = TimeSpan.Zero;
        var result = ComRetry.Run(
            () => ++attempts < 3 ? throw rejected : "ok", TimeSpan.FromSeconds(5), CancellationToken.None,
            sleep: t => elapsed += t, elapsed: () => elapsed);
        result.Should().Be("ok");
        attempts.Should().Be(3);

        var act = () => ComRetry.Run<string>(
            () => throw rejected, TimeSpan.FromSeconds(1), CancellationToken.None, sleep: t => elapsed += t, elapsed: () => elapsed);
        act.Should().Throw<ComBusyException>();
    }

    [Fact(DisplayName = "ComFailures: 切断は「接続が切れた」、時間切れ・拒否は E805、HRESULT が詳細に載る")]
    public void COMの失敗が問題へ写る()
    {
        var disconnected = ComFailures.FromException(new COMException("rpc", unchecked((int)0x800706BA)));
        disconnected.Code.Should().Be(ErrorCode.E801);
        disconnected.Detail.Should().Contain("接続が切れました").And.Contain("800706BA");

        ComFailures.FromException(new COMException("x", unchecked((int)0x800A03EC))).Detail.Should().Contain("800A03EC");
        ComFailures.Unresponsive(TimeSpan.FromSeconds(30)).Code.Should().Be(ErrorCode.E805);
        ComFailures.Unresponsive(TimeSpan.FromSeconds(30)).Detail.Should().Contain("30");
        ComFailures.Busy().Code.Should().Be(ErrorCode.E805);
    }

    // ------------------------------------------------------------------
    // プロジェクトの状態値
    // ------------------------------------------------------------------

    [Fact(DisplayName = "VBProject.Protection==1 がロック。VBProject.Mode は VBIDE の定義どおり Design=2 だけが書き込み可（Run=0／Break=1 は E813）")]
    public void プロジェクトの状態値()
    {
        VbaProjectState.IsLocked(1).Should().BeTrue();
        VbaProjectState.IsLocked(0).Should().BeFalse();

        VbaProjectState.IsDesignMode(2).Should().BeTrue();
        VbaProjectState.IsDesignMode(0).Should().BeFalse("実行中");
        VbaProjectState.IsDesignMode(1).Should().BeFalse("中断中（仕様書は Design=1 と書いているが VBIDE の定義は Break=1）");
    }

    [Fact(DisplayName = "控えのファイル名: <モジュール名>-<日時>.txt。ファイル名に使えない文字は _ にする")]
    public void 控えのファイル名()
    {
        var at = new DateTimeOffset(2026, 11, 21, 10, 5, 9, TimeSpan.FromHours(9));

        VbaRescueNaming.BuildFileName("Module1", at).Should().Be("Module1-20261121-100509.txt");
        VbaRescueNaming.BuildFileName("a:b/c", at).Should().Be("a_b_c-20261121-100509.txt");
    }

    // ------------------------------------------------------------------
    // 再試行
    // ------------------------------------------------------------------

    private static COMException Rejected(uint hr = 0x80010001u) => new("rejected", unchecked((int)hr));

    [Fact(DisplayName = "ComRetry: 拒否が続いても、通るまで 200ms 間隔で再試行し、通れば結果を返す")]
    public void 拒否のあと通れば成功()
    {
        var clock = TimeSpan.Zero;
        var sleeps = new List<TimeSpan>();
        var attempts = 0;

        var result = ComRetry.Run(
            () => ++attempts < 4 ? throw Rejected() : "ok",
            TimeSpan.FromSeconds(5), CancellationToken.None,
            sleep: t => { sleeps.Add(t); clock += t; },
            elapsed: () => clock);

        result.Should().Be("ok");
        attempts.Should().Be(4);
        sleeps.Should().HaveCount(3).And.AllBeEquivalentTo(TimeSpan.FromMilliseconds(200));
    }

    [Fact(DisplayName = "ComRetry: 上限（ComTimeoutMs）まで拒否され続けたら ComBusyException（E805）。上限を超えて待たない")]
    public void 上限まで拒否されたら諦める()
    {
        var clock = TimeSpan.Zero;
        var attempts = 0;

        var act = () => ComRetry.Run<int>(
            () => { attempts++; throw Rejected(0x8001010Au); },
            TimeSpan.FromSeconds(1), CancellationToken.None,
            sleep: t => clock += t,
            elapsed: () => clock);

        act.Should().Throw<ComBusyException>().WithInnerException<COMException>();
        clock.Should().BeLessOrEqualTo(TimeSpan.FromSeconds(1), "上限を超えて待たない");
        attempts.Should().Be(6, "0, 200, 400, 600, 800 ms の5回の待ちのあと、6回目で上限に達して諦める");
    }

    [Fact(DisplayName = "ComRetry: 切断・その他の失敗は再試行せず、そのまま投げる")]
    public void 再試行しない失敗()
    {
        var attempts = 0;
        var act = () => ComRetry.Run<int>(
            () => { attempts++; throw new COMException("gone", unchecked((int)0x800706BA)); },
            TimeSpan.FromSeconds(5), CancellationToken.None, _ => { }, () => TimeSpan.Zero);

        act.Should().Throw<COMException>();
        attempts.Should().Be(1);

        ComRetry.IsRetryable(new InvalidOperationException()).Should().BeFalse();
        ComRetry.IsRetryable(new System.Reflection.TargetInvocationException(Rejected())).Should().BeTrue("リフレクション経由で包まれても見る");
    }

    [Fact(DisplayName = "ComRetry: 見捨てられた呼び出し（キャンセル）は、再試行を続けずに止まる")]
    public void キャンセルで再試行が止まる()
    {
        using var cts = new CancellationTokenSource();
        var attempts = 0;

        var act = () => ComRetry.Run<int>(
            () => { attempts++; cts.Cancel(); throw Rejected(); },
            TimeSpan.FromSeconds(5), cts.Token, _ => { }, () => TimeSpan.Zero);

        act.Should().Throw<OperationCanceledException>();
        attempts.Should().Be(1);
    }

    // ------------------------------------------------------------------
    // 専用スレッド
    // ------------------------------------------------------------------

    [Fact(DisplayName = "StaWorker: 呼び出しは専用の1本のスレッドで直列に実行され、呼び出し元のスレッドとは別")]
    public async Task 専用スレッドで直列に実行される()
    {
        using var worker = new StaWorker(ApartmentState.Unknown);
        var callerThread = Environment.CurrentManagedThreadId;

        var ids = new List<int>();
        var running = 0;
        var maxRunning = 0;
        var tasks = Enumerable.Range(0, 5).Select(_ => worker.RunAsync(_ =>
        {
            var now = Interlocked.Increment(ref running);
            maxRunning = Math.Max(maxRunning, now);
            Thread.Sleep(10);
            lock (ids) ids.Add(Environment.CurrentManagedThreadId);
            Interlocked.Decrement(ref running);
            return 1;
        }, TimeSpan.FromSeconds(10))).ToArray();

        await Task.WhenAll(tasks);

        maxRunning.Should().Be(1, "直列に処理する");
        ids.Distinct().Should().HaveCount(1);
        ids[0].Should().NotBe(callerThread);
    }

    [Fact(DisplayName = "StaWorker: 例外はそのまま呼び出し元へ伝わり、次の呼び出しは通る")]
    public async Task 例外は伝わる()
    {
        using var worker = new StaWorker(ApartmentState.Unknown);

        var failing = () => worker.RunAsync<int>(_ => throw new InvalidOperationException("boom"), TimeSpan.FromSeconds(5));
        await failing.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");

        (await worker.RunAsync(_ => 7, TimeSpan.FromSeconds(5))).Should().Be(7);
        worker.AbandonedCount.Should().Be(0);
    }

    [Fact(DisplayName = "StaWorker: 戻ってこない呼び出しは時間切れ（TimeoutException）で諦め、スレッドを捨てて、次の呼び出しは新しいスレッドで通る")]
    public async Task 時間切れでスレッドを捨てて作り直す()
    {
        using var worker = new StaWorker(ApartmentState.Unknown);
        using var release = new ManualResetEventSlim(false);
        var stuckThread = 0;
        var stuckToken = CancellationToken.None;

        var stuck = () => worker.RunAsync(token =>
        {
            stuckThread = Environment.CurrentManagedThreadId;
            stuckToken = token;
            release.Wait(); // 戻ってこない COM 呼び出しの代わり。
            return 0;
        }, TimeSpan.FromMilliseconds(200));

        await stuck.Should().ThrowAsync<TimeoutException>();
        worker.AbandonedCount.Should().Be(1);
        stuckToken.IsCancellationRequested.Should().BeTrue("捨てた仕事の中の繰り返しが、続きを進めないための合図");

        var next = await worker.RunAsync(_ => Environment.CurrentManagedThreadId, TimeSpan.FromSeconds(5));
        next.Should().NotBe(stuckThread, "固まったスレッドではなく、作り直したスレッドで動く");

        release.Set(); // 後始末。捨てたスレッドが戻っても、結果は使われない。
    }

    [Fact(DisplayName = "StaWorker: 固まった仕事の後ろで待っていた仕事は、新しいスレッドへ移って実行される（時間内なら）")]
    public async Task 待ち行列の仕事は新しいスレッドへ移る()
    {
        using var worker = new StaWorker(ApartmentState.Unknown);
        using var release = new ManualResetEventSlim(false);

        var stuck = worker.RunAsync(_ => { release.Wait(); return 0; }, TimeSpan.FromMilliseconds(300));
        var queued = worker.RunAsync(_ => "done", TimeSpan.FromSeconds(10));

        await ((Func<Task>)(() => stuck)).Should().ThrowAsync<TimeoutException>();
        (await queued).Should().Be("done");

        release.Set();
    }

    [Fact(DisplayName = "StaWorker: 呼び出し側のキャンセルは OperationCanceledException（時間切れ扱いにはしない）")]
    public async Task キャンセルは時間切れではない()
    {
        using var worker = new StaWorker(ApartmentState.Unknown);
        using var release = new ManualResetEventSlim(false);
        using var cts = new CancellationTokenSource();

        var call = worker.RunAsync(_ => { release.Wait(); return 0; }, TimeSpan.FromSeconds(30), cts.Token);
        cts.CancelAfter(100);

        await ((Func<Task>)(() => call)).Should().ThrowAsync<OperationCanceledException>();
        worker.AbandonedCount.Should().Be(0);
        release.Set();
    }
}
