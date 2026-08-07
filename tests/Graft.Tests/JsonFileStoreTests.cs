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
}
