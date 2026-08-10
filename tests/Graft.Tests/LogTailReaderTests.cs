using System.IO;
using System.Linq;
using FluentAssertions;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 機能2（ログの参照手段）「最新のログを表示」の回帰テスト。<see cref="LogTailReader"/>の
/// 「最新のログファイルの探索」「末尾の切り出し」を検証する。
/// </summary>
public class LogTailReaderTests
{
    [Fact(DisplayName = "logs/ディレクトリが無ければnullを返す")]
    public void ディレクトリが無ければnull()
    {
        using var ws = new TempWorkspace();
        var missing = ws.Combine("no-such-logs");

        LogTailReader.FindLatestLogFile(missing).Should().BeNull();
    }

    [Fact(DisplayName = "ログファイルが1件も無ければnullを返す")]
    public void ファイルが無ければnull()
    {
        using var ws = new TempWorkspace();
        var logsDir = ws.CreateDirectory("logs");

        LogTailReader.FindLatestLogFile(logsDir).Should().BeNull();
    }

    [Fact(DisplayName = "yyyyMMdd.log形式のファイル名の中から日付が最も新しいものを選ぶ")]
    public void 日付が最も新しいファイルを選ぶ()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("logs/20260101.log", "{\"a\":1}");
        ws.WriteText("logs/20260215.log", "{\"a\":2}");
        ws.WriteText("logs/20260110.log", "{\"a\":3}");
        var logsDir = ws.Combine("logs");

        var latest = LogTailReader.FindLatestLogFile(logsDir);

        latest.Should().Be(Path.Combine(logsDir, "20260215.log"));
    }

    [Fact(DisplayName = "行数が上限以下ならすべての行をそのままの順序で返す")]
    public void 上限以下ならすべて返す()
    {
        using var ws = new TempWorkspace();
        var path = ws.WriteText("logs/20260101.log", "line1" + Environment.NewLine + "line2" + Environment.NewLine + "line3");

        var tail = LogTailReader.ReadTail(path, maxLines: 200);

        tail.Should().Be("line1" + Environment.NewLine + "line2" + Environment.NewLine + "line3");
    }

    [Fact(DisplayName = "行数が上限を超える場合は末尾maxLines行だけを元の順序で返す")]
    public void 上限を超えたら末尾だけ返す()
    {
        using var ws = new TempWorkspace();
        var lines = Enumerable.Range(1, 500).Select(i => $"line{i}");
        var path = ws.WriteText("logs/20260101.log", string.Join(Environment.NewLine, lines));

        var tail = LogTailReader.ReadTail(path, maxLines: 200);
        var tailLines = tail.Split(Environment.NewLine);

        tailLines.Should().HaveCount(200);
        tailLines.First().Should().Be("line301", "先頭は500-200+1=301行目のはず");
        tailLines.Last().Should().Be("line500", "末尾は最後の行のはず");
    }

    [Fact(DisplayName = "1行1JSONの内容をそのまま返す（整形しない）")]
    public void JSON行をそのまま返す()
    {
        using var ws = new TempWorkspace();
        var json = "{\"timestamp\":\"2026-01-01T00:00:00+09:00\",\"level\":\"Info\",\"eventType\":\"startup\",\"result\":\"ok\"}";
        var path = ws.WriteText("logs/20260101.log", json);

        LogTailReader.ReadTail(path).Should().Be(json, "整形せずそのまま返すこと（クラスコメント参照）");
    }

    [Fact(DisplayName = "maxLinesに0以下を渡すと空文字を返す")]
    public void 上限0以下は空文字()
    {
        using var ws = new TempWorkspace();
        var path = ws.WriteText("logs/20260101.log", "line1");

        LogTailReader.ReadTail(path, maxLines: 0).Should().BeEmpty();
    }
}
