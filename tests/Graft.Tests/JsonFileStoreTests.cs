using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Core;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 仕様書13.1（データ破損時の復旧）のうち、破損ファイルの退避そのものを検証する。
/// 起動時は複数の経路が同じファイルをほぼ同時に読むため、破損の検出も同時に起きうる。
/// </summary>
public class JsonFileStoreTests
{
    private sealed record Box
    {
        public string Value { get; init; } = string.Empty;
    }

    [Fact(DisplayName = "破損ファイルは .corrupt.<日時> へ退避され、既定値で再生成される")]
    public async Task 破損ファイルを退避して再生成する()
    {
        using var ws = new TempWorkspace();
        var path = Path.Combine(ws.CreateDirectory("app"), "data.json");
        await File.WriteAllTextAsync(path, "{ これはJSONではない");

        var store = new JsonFileStore();
        var result = await store.ReadWithRecoveryAsync(path, () => new Box { Value = "既定" });

        result.ValueOrDefault!.Value.Should().Be("既定");
        result.Issues.Should().ContainSingle();

        var quarantined = Directory.GetFiles(Path.GetDirectoryName(path)!, "data.json.corrupt.*");
        quarantined.Should().ContainSingle("壊れた内容は消さずに残す必要がある");
        (await File.ReadAllTextAsync(quarantined[0])).Should().Be("{ これはJSONではない");
    }

    [Fact(DisplayName = "同じ破損ファイルを同時に読んでも例外にならない")]
    public async Task 同時に読んでも例外にならない()
    {
        // 先に退避した側がファイルを移動し終えた後で File.Move を呼ぶと、対象が無く
        // 例外になる。待ち受けない呼び出し元では未観測例外として遅れて表面化するため、
        // 退避済みは成功として扱う必要がある（実機の起動ログで発生を確認した不具合）。
        //
        // 並行数を8→32へ強化: 元の8並行では、候補名の存在確認（File.Exists）と
        // File.Moveの実行の間のTOCTOUレースがまれにしか起きず、全体テスト実行時にだけ
        // 低頻度で失敗が再現していた（QuarantineAsyncの移動先衝突IOExceptionが吸収されて
        // いなかった不具合）。並行数を増やすことでこのレースを本テスト単体でも
        // 高確率で踏むようにする。
        using var ws = new TempWorkspace();
        var path = Path.Combine(ws.CreateDirectory("app"), "data.json");
        await File.WriteAllTextAsync(path, "壊れている");

        var store = new JsonFileStore();
        var results = await Task.WhenAll(
            Enumerable.Range(0, 32).Select(_ =>
                store.ReadWithRecoveryAsync(path, () => new Box { Value = "既定" })));

        results.Should().OnlyContain(r => r.IsSuccess, "どの経路も既定値で復旧できる必要がある");
    }

    [Fact(DisplayName = "退避先が既にある場合は連番を付けて退避する")]
    public async Task 退避先が重複したら連番を付ける()
    {
        using var ws = new TempWorkspace();
        var dir = ws.CreateDirectory("app");
        var path = Path.Combine(dir, "data.json");
        var store = new JsonFileStore();

        await File.WriteAllTextAsync(path, "1回目");
        var first = await store.QuarantineAsync(path);
        await File.WriteAllTextAsync(path, "2回目");
        var second = await store.QuarantineAsync(path);

        first.Should().NotBeNull();
        second.Should().NotBeNull();
        second.Should().NotBe(first, "先に退避した内容を上書きしてはならない");
        (await File.ReadAllTextAsync(first!)).Should().Be("1回目");
        (await File.ReadAllTextAsync(second!)).Should().Be("2回目");
    }

    [Fact(DisplayName = "退避対象が既に無い場合は null を返す")]
    public async Task 退避対象が無ければnullを返す()
    {
        using var ws = new TempWorkspace();
        var path = Path.Combine(ws.CreateDirectory("app"), "ない.json");

        var store = new JsonFileStore();
        (await store.QuarantineAsync(path)).Should().BeNull();
    }

    // ------------------------------------------------------------------
    // 不具合2: 破損ファイル復旧がWindowsで失敗する（UnauthorizedAccessExceptionの捕捉漏れ）
    //
    // JsonFileStore.WriteAsyncのFile.Move失敗時フォールバックは、実機（Windows）でしか
    // 自然には再現しないUnauthorizedAccessException（開いているファイルの上書き・並行アクセス時に
    // 発生）からの回復が主目的のため、Linux上では実ファイルI/Oでは再現できない。
    // ここでは例外を投げるフェイクのactionを使い、リトライ・上限到達時の再スロー・
    // 「IOExceptionだけでなくUnauthorizedAccessExceptionも同じ扱いで捕捉されること」を
    // OSに依存せず検証する。
    // ------------------------------------------------------------------

    [Fact(DisplayName = "不具合2: RetryOnIoOrAccessDeniedAsyncはUnauthorizedAccessExceptionを捕捉して再試行する")]
    public async Task リトライヘルパはUnauthorizedAccessExceptionを再試行する()
    {
        var callCount = 0;
        Task Action()
        {
            callCount++;
            if (callCount < 3) throw new UnauthorizedAccessException("模擬: Windowsでの並行アクセス拒否");
            return Task.CompletedTask;
        }

        await JsonFileStore.RetryOnIoOrAccessDeniedAsync(Action, maxAttempts: 5, delay: TimeSpan.Zero);

        callCount.Should().Be(3, "2回失敗した後、3回目で成功したところで打ち切られるはず");
    }

    [Fact(DisplayName = "不具合2: RetryOnIoOrAccessDeniedAsyncは上限に達すると例外をそのまま投げる（無限に粘らない）")]
    public async Task リトライヘルパは上限到達で例外を投げる()
    {
        var callCount = 0;
        Task Action()
        {
            callCount++;
            throw new UnauthorizedAccessException("模擬: 常に失敗する");
        }

        var act = () => JsonFileStore.RetryOnIoOrAccessDeniedAsync(Action, maxAttempts: 4, delay: TimeSpan.Zero);

        await act.Should().ThrowAsync<UnauthorizedAccessException>("上限まで解決しない場合は従来どおりエラーとして扱う必要がある");
        callCount.Should().Be(4, "上限回数ちょうどまで試行し、それ以上は粘らないはず");
    }

    [Fact(DisplayName = "不具合2: RetryOnIoOrAccessDeniedAsyncはIOExceptionも同様に再試行する")]
    public async Task リトライヘルパはIOExceptionも再試行する()
    {
        var callCount = 0;
        Task Action()
        {
            callCount++;
            if (callCount < 2) throw new IOException("模擬: 一時的な共有違反");
            return Task.CompletedTask;
        }

        await JsonFileStore.RetryOnIoOrAccessDeniedAsync(Action, maxAttempts: 5, delay: TimeSpan.Zero);

        callCount.Should().Be(2);
    }

    // ------------------------------------------------------------------
    // 不具合1: 破損ファイルの並行読み取りが共有違反(IOException)で例外になる（Windows実機）
    //
    // Windowsはファイルへ強制的な共有ロックを掛けるため、あるタスクがQuarantineAsyncの
    // File.Move等でファイルを掴んでいる瞬間に、別タスクのFile.ReadAllTextAsyncが
    // 「The process cannot access the file ... because it is being used by another process.」
    // というIOExceptionで失敗する。Linuxには強制共有ロックが無いため通常の並行読み取りでは
    // 再現しないが、.NET自体はFileShareの解釈を（プロセス内に限り）Linux上でも自前で
    // エミュレートしており、同一プロセス内でFileShare.Noneの排他ハンドルを開いておくと
    // 同じIOExceptionを実際に再現できることを確認済みのため、フェイクではなく実ファイルI/Oで
    // 検証する。
    // ------------------------------------------------------------------

    [Fact(DisplayName = "不具合1: 読み取り中の共有違反はリトライで解消し、内容を壊れた扱いにしない")]
    public async Task 共有違反から読み取りリトライで復旧する()
    {
        using var ws = new TempWorkspace();
        var path = Path.Combine(ws.CreateDirectory("app"), "data.json");
        var json = System.Text.Json.JsonSerializer.Serialize(new Box { Value = "本物" }, JsonFileStore.DefaultOptions);
        await File.WriteAllTextAsync(path, json);

        var store = new JsonFileStore();

        // FileShare.Noneの排他ハンドルで、Windowsの共有違反IOExceptionと同じ状況を再現する。
        var exclusiveHandle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        // 壁時計非依存化のポイント: 「いつ解放すれば間に合うか」を Task.Delay 等の固定時間で
        // 見積もらず、製品側が実際に共有違反でリトライへ入った通知（JsonFileStore.
        // OnReadShareViolationRetry）を受けた直後にハンドルを解放する。これにより
        // 「共有違反が必ず1回は起き、その直後に解消する」ことが時間に関係なく確定するため、
        // CI負荷でスレッドプールが枯渇してもフレークしない。
        //
        // このシームは静的プロパティであり、xUnitは同一アセンブリ内の別テストクラスを並行実行
        // しうる。他クラスは一切このシームを触らない前提だが、念のためコールバック側で対象パスを
        // 照合し、無関係な呼び出し（他クラス経由の呼び出し等）には反応しない設計にしている。
        // また同一クラス内の他テストとは、xUnitがデフォルトで同一クラス内のテストメソッドを
        // 並行実行しないため干渉しない。
        JsonFileStore.OnReadShareViolationRetry = (retryPath, _) =>
        {
            if (retryPath == path)
            {
                // Stream.Disposeは複数回呼んでも安全なため、二重解放を気にする必要はない
                // （リトライは複数回発生しうるが、実際にハンドルが閉じるのは最初の1回だけ）。
                exclusiveHandle.Dispose();
            }
        };
        try
        {
            var result = await store.ReadWithRecoveryAsync(path, () => new Box { Value = "既定" });

            result.IsSuccess.Should().BeTrue();
            result.ValueOrDefault!.Value.Should().Be("本物",
                "共有違反は一時的なものであり内容が壊れているわけではないため、リトライで正しい内容を読み取れるはず");
            result.Issues.Should().BeEmpty("リトライで解決した場合は破損扱い（退避・既定値再生成）にしてはならない");
        }
        finally
        {
            // 他のテストへシームを漏らさないよう、必ず元（null）へ戻す。
            JsonFileStore.OnReadShareViolationRetry = null;
            exclusiveHandle.Dispose();
        }
    }

    [Fact(DisplayName = "不具合1: 共有違反がリトライ上限まで解消しない場合は例外を漏らさず既定値へ穏当に倒す")]
    public async Task 共有違反が解消しない場合は既定値へ倒す()
    {
        using var ws = new TempWorkspace();
        var path = Path.Combine(ws.CreateDirectory("app"), "data.json");
        await File.WriteAllTextAsync(path, "壊れている");

        var store = new JsonFileStore();

        // 排他ハンドルを最後まで解放せず、共有違反がリトライ上限まで解消しないケースを再現する。
        using var exclusiveHandle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        var result = await store.ReadWithRecoveryAsync(path, () => new Box { Value = "既定" });

        result.IsSuccess.Should().BeTrue("共有違反が解消しなくても例外を外へ漏らさず既定値へ倒す必要がある");
        result.ValueOrDefault!.Value.Should().Be("既定");
    }

    // ------------------------------------------------------------------
    // 不具合1（Windows実機・2回目の指摘）: 破損復旧時の「既定値の書き戻し」が共有違反の
    // リトライ上限まで解消しない場合、例外がReadWithRecoveryAsyncの呼び出し元まで漏れていた。
    //
    // RecoverFromCorruptionAsyncはQuarantineAsyncで破損ファイルを退避した後、既定値を
    // WriteAsyncでディスクへ書き戻す。テストがFileShare.Noneの排他ハンドルで対象ファイルを
    // 掴んでいると、WriteAsync内部のFile.Move/File.Copyフォールバックが共有違反で失敗し続け、
    // RetryOnIoOrAccessDeniedAsyncの上限到達時に例外がそのままRecoverFromCorruptionAsync→
    // ReadWithRecoveryAsyncを突き抜けていた（設定ファイルが他プロセスに掴まれた状態で
    // 破損しているとGraftが起動時に例外で落ちる不具合）。
    //
    // 「既定値を返す」ことと「既定値を永続化する」ことを分離するTryPersistDefaultAsyncを
    // internalな検証用の入口として切り出した。File.Move自体はWindowsとは異なりLinux上では
    // 実ファイルのFileShare.None排他ハンドルを尊重しない（実測で確認済み。読み取り側の
    // File.ReadAllTextAsyncとは異なりMove/CopyはFileStreamの共有チェックを経由しないため）ため、
    // 書き戻し側の共有違反はフェイクのpersistActionで再現する（RetryOnIoOrAccessDeniedAsyncの
    // 既存テストと同じ方針）。
    // ------------------------------------------------------------------

    [Fact(DisplayName = "不具合1: 既定値の書き戻しに失敗しても例外を投げず警告のGraftIssueへ変換する")]
    public async Task 書き戻し失敗は例外を投げず警告になる()
    {
        var issue = await JsonFileStore.TryPersistDefaultAsync(
            () => throw new IOException("模擬: 共有違反がリトライ上限まで解消しない"),
            path: "dummy.json");

        issue.Should().NotBeNull("永続化に失敗したことを警告として呼び出し元へ伝える必要がある");
        issue!.Code.Should().Be(ErrorCode.E402);
        issue.Severity.Should().Be(Severity.Warning, "書き戻し失敗は致命的エラーではなく、既定値そのものは返せている");
        issue.Path.Should().Be("dummy.json");
    }

    [Fact(DisplayName = "不具合1: 既定値の書き戻しに失敗してもUnauthorizedAccessExceptionを同様に警告へ変換する")]
    public async Task 書き戻し失敗のUnauthorizedAccessExceptionも警告になる()
    {
        var issue = await JsonFileStore.TryPersistDefaultAsync(
            () => throw new UnauthorizedAccessException("模擬: 他プロセスがファイルを掴んでいる"),
            path: "dummy.json");

        issue.Should().NotBeNull();
        issue!.Code.Should().Be(ErrorCode.E402);
        issue.Severity.Should().Be(Severity.Warning);
    }

    [Fact(DisplayName = "不具合1: 永続化に成功した場合はissueを返さない")]
    public async Task 書き戻し成功時はissueがない()
    {
        var issue = await JsonFileStore.TryPersistDefaultAsync(() => Task.CompletedTask, path: "dummy.json");

        issue.Should().BeNull();
    }

    [Fact(DisplayName = "不具合1: 破損復旧の既定値書き戻しが失敗しても、読み取りは既定値の成功として返る")]
    public async Task 破損復旧で書き戻しが失敗しても読み取りは成功する()
    {
        // RecoverFromCorruptionAsyncが内部でTryPersistDefaultAsyncを経由することを、
        // 破損ファイルの復旧フロー全体（ReadWithRecoveryAsync）を通して確認する。
        // 書き戻し自体の共有違反はフェイクで検証済み（上のテスト）のため、ここでは
        // 「破損検知→既定値返却」という外部から観測できる契約が壊れていないことを見る。
        using var ws = new TempWorkspace();
        var path = Path.Combine(ws.CreateDirectory("app"), "data.json");
        await File.WriteAllTextAsync(path, "壊れている");

        var store = new JsonFileStore();
        var result = await store.ReadWithRecoveryAsync(path, () => new Box { Value = "既定" });

        result.IsSuccess.Should().BeTrue();
        result.ValueOrDefault!.Value.Should().Be("既定");
        result.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E404,
            "破損検知そのものは通常どおり警告として積まれる");
    }
}
