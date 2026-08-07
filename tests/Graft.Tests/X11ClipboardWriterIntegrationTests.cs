using System.Diagnostics;
using System.Text;
using FluentAssertions;
using Graft.Platform.Linux;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// Linuxクリップボード書き込み不具合（AvaloniaのSetTextAsyncは例外なく完了するが、
/// Xサーバー上でCLIPBOARDセレクションの所有者が一度も現れず、他アプリから貼り付けできない）
/// の再発防止を検証する統合テスト。X11実機に依存するため、DISPLAY環境変数が無い・X11サーバーに
/// 接続できない環境（多くのCI）では検証しようがなく、その場合は何もせず正常終了する
/// （xunit拡張無しでの簡易スキップ。純粋ロジック部分の検証は<see cref="X11ClipboardWriterTextTests"/>
/// を参照）。
///
/// リーダー（<see cref="X11ClipboardReader"/>）とライター（<see cref="X11ClipboardWriter"/>）は
/// 意図的に別のX11接続・別スレッドで動く設計のため、ここでの「自己読み戻し」テストは
/// 両者の相互作用（デッドロックしないこと・正しく読み戻せること）そのものの検証も兼ねる。
/// </summary>
public class X11ClipboardWriterIntegrationTests
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan GuardTimeout = TimeSpan.FromSeconds(20);

    [Fact(DisplayName = "TryCreateはX11に接続できない環境でも例外を投げない")]
    public void 接続できない環境でも例外を投げない()
    {
        var act = () =>
        {
            using var writer = X11ClipboardWriter.TryCreate();
        };

        act.Should().NotThrow();
    }

    [Fact(DisplayName = "SetTextで書き込んだ内容は自前リーダーで読み戻すと一致する（UTF-8日本語含む）")]
    public async Task 書き込んだ内容を読み戻すと一致する()
    {
        if (!HasDisplay()) return;

        using var writer = X11ClipboardWriter.TryCreate();
        using var reader = X11ClipboardReader.TryCreate();
        if (writer is null || reader is null) return; // DISPLAYはあるがX11サーバーへ接続できない環境。

        const string text = "パッチ適用テスト：クリップボード書き込み確認 🎉";

        var written = await WithBoundedWaitAsync(writer.SetTextAsync(text, OperationTimeout));
        written.Completed.Should().BeTrue("書き込み要求は時間内に完了するはず");
        written.Value.Should().BeTrue("所有権の取得に成功するはず");

        var read = await WithBoundedWaitAsync(reader.ReadTextAsync(OperationTimeout));
        read.Completed.Should().BeTrue("読み取りは時間内に完了するはず");
        read.Value.Should().Be(text);
    }

    [Fact(DisplayName = "SetTextを2回連続で呼ぶと、読み戻せるのは最後に書き込んだ値になる")]
    public async Task 連続書き込みは最後の値が読める()
    {
        if (!HasDisplay()) return;

        using var writer = X11ClipboardWriter.TryCreate();
        using var reader = X11ClipboardReader.TryCreate();
        if (writer is null || reader is null) return;

        var first = await WithBoundedWaitAsync(writer.SetTextAsync("1回目の内容", OperationTimeout));
        first.Completed.Should().BeTrue();
        first.Value.Should().BeTrue();

        var second = await WithBoundedWaitAsync(writer.SetTextAsync("2回目の内容（こちらが残るはず）", OperationTimeout));
        second.Completed.Should().BeTrue();
        second.Value.Should().BeTrue();

        var read = await WithBoundedWaitAsync(reader.ReadTextAsync(OperationTimeout));
        read.Completed.Should().BeTrue();
        read.Value.Should().Be("2回目の内容（こちらが残るはず）");
    }

    [Fact(DisplayName = "大きめのテキスト（1MB程度）はINCR転送で書き込んでも読み戻すと一致する")]
    public async Task 大きなテキストもINCR経由で読み戻せる()
    {
        if (!HasDisplay()) return;

        using var writer = X11ClipboardWriter.TryCreate();
        using var reader = X11ClipboardReader.TryCreate();
        if (writer is null || reader is null) return;

        var large = BuildLargeText();
        Encoding.UTF8.GetByteCount(large).Should().BeGreaterThan(256 * 1024, "この閾値を超えないと通常送出のみで完結しINCR経路を検証できない");

        var written = await WithBoundedWaitAsync(writer.SetTextAsync(large, OperationTimeout));
        written.Completed.Should().BeTrue("大きなテキストでも書き込み要求自体は時間内に完了するはず");
        written.Value.Should().BeTrue();

        var read = await WithBoundedWaitAsync(reader.ReadTextAsync(OperationTimeout));
        read.Completed.Should().BeTrue("INCR転送を含む読み取りが時間内に完了するはず");
        read.Value.Should().Be(large);
    }

    [Fact(DisplayName = "外部プロセス（xclip）からも書き込んだ内容を読み取れる（実機で確認された不具合そのものの再現防止）")]
    public async Task xclipから読み取れる()
    {
        if (!HasDisplay()) return;

        using var writer = X11ClipboardWriter.TryCreate();
        if (writer is null) return;

        const string text = "xclip読み取り確認：日本語を含む本文";

        var written = await WithBoundedWaitAsync(writer.SetTextAsync(text, OperationTimeout));
        written.Completed.Should().BeTrue();
        written.Value.Should().BeTrue();

        var xclipOutput = await TryReadWithXclipAsync();
        if (xclipOutput is null) return; // xclipが無い環境ではこの検証はできない。

        xclipOutput.Should().Be(text, "実機で確認された不具合（xclipでの読み出しが常に空になる）が再発していないこと");
    }

    /// <summary>
    /// xclipコマンドでCLIPBOARDの内容を読み取る。xclip自体が無い・起動できない環境ではnullを返す
    /// （その場合はこの検証自体をスキップする）。
    /// </summary>
    private static async Task<string?> TryReadWithXclipAsync()
    {
        try
        {
            var startInfo = new ProcessStartInfo("xclip", "-selection clipboard -o")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            using var process = Process.Start(startInfo);
            if (process is null) return null;

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var exited = await WithBoundedWaitAsync(process.WaitForExitAsync().ContinueWith(_ => true));
            if (!exited.Completed || process.ExitCode != 0) return null;

            return await outputTask;
        }
        catch (Exception)
        {
            return null; // xclipが無い環境（Win32Exception等）。
        }
    }

    /// <summary>UTF-8で1MBを超える程度になるよう、日本語を含む文字列を繰り返して作る。</summary>
    private static string BuildLargeText()
    {
        const string unit = "0123456789ABCDEFGHIJ日本語テストデータ・INCR転送確認用の繰り返し文字列。\n";
        var builder = new StringBuilder(unit.Length * 15000);
        for (var i = 0; i < 15000; i++)
        {
            builder.Append(unit);
        }
        return builder.ToString();
    }

    private static bool HasDisplay() => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));

    /// <summary>テストコード自体が想定外にハングした場合でも無限に待たず、失敗として報告できるようにする。</summary>
    private static async Task<(bool Completed, T? Value)> WithBoundedWaitAsync<T>(Task<T> task)
    {
        var guard = Task.Delay(GuardTimeout);
        var completed = await Task.WhenAny(task, guard).ConfigureAwait(true);
        return completed == task ? (true, await task.ConfigureAwait(true)) : (false, default);
    }
}
