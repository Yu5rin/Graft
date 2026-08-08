using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
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
}
