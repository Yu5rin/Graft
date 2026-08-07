using FluentAssertions;
using Graft.Platform.Linux;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// Linuxクリップボード読み取り不具合（一度タイムアウトすると、そのプロセスでは以後すべての
/// 読み取りが失敗し続ける）の再発防止を検証する統合テスト。X11実機に依存するため、
/// DISPLAY環境変数が無い・X11サーバーに接続できない環境（多くのCI）では検証しようがなく、
/// その場合は何もせず正常終了する（xunit拡張無しでの簡易スキップ。純粋ロジック部分の検証は
/// <see cref="X11ClipboardTextTests"/> を参照）。
/// </summary>
public class X11ClipboardReaderIntegrationTests
{
    [Fact(DisplayName = "TryCreateはX11に接続できない環境でも例外を投げない")]
    public void 接続できない環境でも例外を投げない()
    {
        // Waylandのみ・libX11が無い等、接続できない環境を直接再現するのは難しいため、
        // ここでは「例外を投げないこと」だけを確認する（成功してもnullでも構わない）。
        // 生成に成功した場合は後始末のためDisposeする。
        var act = () =>
        {
            using var reader = X11ClipboardReader.TryCreate();
        };

        act.Should().NotThrow();
    }

    [Fact(DisplayName = "X11に接続できる環境では、1回の読み取りが失敗しても次の読み取りに持ち越されない")]
    public async Task 失敗が次回に持ち越されない()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
        {
            return; // X11実機が無い環境では検証できないためスキップ扱いとする。
        }

        using var reader = X11ClipboardReader.TryCreate();
        if (reader is null)
        {
            return; // DISPLAYはあるがX11サーバーへ接続できない環境（例: Xvfb未起動）。
        }

        var timeout = TimeSpan.FromMilliseconds(500);

        // CLIPBOARDの所有者が居るかどうかは実行環境次第で問わない。ここで検証したいのは、
        // 1回目の読み取り（タイムアウト・拒否のいずれでも）の後、2回目の読み取りが
        // ハングせず一定時間内に完了すること（本不具合が再発していれば2回目が永久に
        // 完了せず、下のWithBoundedWaitAsyncがfalseを返す）。
        var first = await WithBoundedWaitAsync(reader.ReadTextAsync(timeout));
        var second = await WithBoundedWaitAsync(reader.ReadTextAsync(timeout));

        first.Completed.Should().BeTrue("1回目の読み取りは（結果の有無に関わらず）時間内に完了するはず");
        second.Completed.Should().BeTrue("2回目の読み取りが1回目の詰まりを引きずってハングしないはず");
    }

    /// <summary>テストコード自体が想定外にハングした場合でも無限に待たず、失敗として報告できるようにする。</summary>
    private static async Task<(bool Completed, string? Value)> WithBoundedWaitAsync(Task<string?> task)
    {
        var guard = Task.Delay(TimeSpan.FromSeconds(5));
        var completed = await Task.WhenAny(task, guard).ConfigureAwait(true);
        return completed == task ? (true, await task.ConfigureAwait(true)) : (false, null);
    }
}
