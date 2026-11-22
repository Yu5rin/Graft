using FluentAssertions;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Platform;
using Graft.Platform.Windows;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// 実機で見つかった「行継続の次の行が <c>()</c> で始まる Declare を含む本文を書くと、末尾に行が増える」不具合の回帰テスト
/// （2026-11-22・Microsoft 365・Windows 10.0.26200 の実機の結果を <see cref="LineModule"/> で再現する）。
/// <list type="bullet">
/// <item><c>AddFromString</c>: 3 行目に <c>()</c> が付く（書くたびに増える。書き戻しでも同じ）。</item>
/// <item><c>InsertLines(1, 本文)</c>: 3 行目に空行が付くだけ。</item>
/// </list>
/// 修正前は <c>AddFromString</c> で書いていたため、書き込みも書き戻しも照合に失敗して E809 のまま残った。
/// </summary>
public class VbaCodeModuleRewriterTrailingExtraTests
{
    /// <summary>行ごとに持つ CodeModule。書き込みのたびに、末尾へ決まった行を足す VBE の挙動を再現する。</summary>
    private sealed class LineModule : ICodeModuleAccess
    {
        private readonly List<string> _lines = new();

        public LineModule(string code)
        {
            if (code.Length > 0) _lines.AddRange(code.Split("\r\n"));
        }

        /// <summary>書き込み（InsertLines）のたびに末尾へ足す行。実機の <c>AddFromString</c> なら <c>()</c>、<c>InsertLines</c> なら空行。</summary>
        public string? AppendOnWrite { get; init; }

        /// <summary>true なら、1 行目を書き換える（途中の行を VBE が変えた場合）。</summary>
        public bool CorruptFirstLine { get; init; }

        public int InsertCalls { get; private set; }

        public string Code => string.Join("\r\n", _lines);

        public int CountOfLines() => _lines.Count;

        public string Lines(int startLine, int count) => string.Join("\r\n", _lines.Skip(startLine - 1).Take(count));

        public void DeleteLines(int startLine, int count) => _lines.RemoveRange(startLine - 1, count);

        public void InsertLines(int startLine, string code)
        {
            InsertCalls++;
            _lines.InsertRange(startLine - 1, code.Split("\r\n"));
            if (AppendOnWrite is not null) _lines.Add(AppendOnWrite);
            if (CorruptFirstLine) _lines[0] = "'壊れた";
        }
    }

    private const string Declare = "Private Declare PtrSafe Function GetActiveWindow Lib \"USER32.dll\" _\r\n    () As LongPtr";
    private const string Original = "Sub Old()\r\nEnd Sub";

    [Fact(DisplayName = "実機の再現: 書き込みが末尾に () を足しても、取り除いて一致扱いにし、取り除いた行を知らせる（修正前は E809 で書き戻しも失敗）")]
    public void 書き込みで増えた行を取り除く()
    {
        var cm = new LineModule(Original) { AppendOnWrite = "()" };

        var result = CodeModuleRewriter.Rewrite(cm, "frmProgressBar2", Declare, created: false, comTimeoutMs: 1000, CancellationToken.None);

        result.Status.Should().Be(VbaWriteStatus.Written);
        result.Issue.Should().BeNull();
        result.TrimmedLines.Should().Equal("()");
        cm.Code.Should().Be(Declare, "余分な () は取り除かれ、2 行のまま");
        cm.CountOfLines().Should().Be(2);
    }

    [Fact(DisplayName = "実機の再現: 書き込みが別内容で書き戻すときも () が付く。取り除いて控えと一致させ、RolledBack にできる")]
    public void 書き戻しで増えた行も取り除く()
    {
        // 1 回目の書き込みは先頭行を壊して () も足す（取り除けない形＝別内容）。書き戻しは末尾に () を足すだけ。
        var cm = new RollbackOnlyExtraModule(Original);

        var result = CodeModuleRewriter.Rewrite(cm, "Module1", Declare, created: false, comTimeoutMs: 1000, CancellationToken.None);

        result.Status.Should().Be(VbaWriteStatus.RolledBack, "書き込みは別内容だが、書き戻しは余分な行を取り除いて成功した");
        result.Issue!.Code.Should().Be(ErrorCode.E809);
        result.Issue.Detail.Should().Contain("最初に違う行", "E809 の詳細に差分の要約が付く");
        result.TrimmedLines.Should().Equal("()");
        cm.Code.Should().Be(Original);
    }

    /// <summary>1 回目の書き込みは先頭行を壊し、2 回目（書き戻し）は末尾に () を足す。</summary>
    private sealed class RollbackOnlyExtraModule : ICodeModuleAccess
    {
        private readonly List<string> _lines;
        private int _inserts;

        public RollbackOnlyExtraModule(string code) => _lines = code.Split("\r\n").ToList();

        public string Code => string.Join("\r\n", _lines);

        public int CountOfLines() => _lines.Count;

        public string Lines(int startLine, int count) => string.Join("\r\n", _lines.Skip(startLine - 1).Take(count));

        public void DeleteLines(int startLine, int count) => _lines.RemoveRange(startLine - 1, count);

        public void InsertLines(int startLine, string code)
        {
            _inserts++;
            _lines.InsertRange(startLine - 1, code.Split("\r\n"));
            if (_inserts == 1) _lines[0] = "'壊れた";
            else _lines.Add("()");
        }
    }

    [Fact(DisplayName = "先頭部分が違うときは末尾を取り除かない（安全側）。書き戻しも失敗すれば E809 のまま、差分の要約つき")]
    public void 先頭が違えば取り除かない()
    {
        // 書き込みも書き戻しも先頭行を壊す。どちらも取り除かず、書き戻しの失敗（E809）になる。
        var cm = new LineModule(Original) { CorruptFirstLine = true };

        var result = CodeModuleRewriter.Rewrite(cm, "Module1", Declare, created: false, comTimeoutMs: 1000, CancellationToken.None);

        result.Status.Should().Be(VbaWriteStatus.Failed);
        result.BackupCode.Should().Be(Original);
        result.TrimmedLines.Should().BeNull();
        result.Issue!.Detail.Should().Contain("書き戻した内容が控えと一致しませんでした").And.Contain("最初に違う行").And.Contain("1 行目");
    }

    [Fact(DisplayName = "実機の再現: InsertLines が末尾に空行を足すだけなら一致扱いで成功し、空行は取り除いて見た目を元と同じにする")]
    public void 末尾の空行は一致扱いで取り除く()
    {
        var cm = new LineModule(Original) { AppendOnWrite = string.Empty };

        var result = CodeModuleRewriter.Rewrite(cm, "Module1", Declare, created: false, comTimeoutMs: 1000, CancellationToken.None);

        result.Status.Should().Be(VbaWriteStatus.Written);
        result.Issue.Should().BeNull();
        result.Reformatted.Should().BeFalse();
        result.TrimmedLines.Should().BeNull("空行は照合に含めないので、取り除いた行としては知らせない");
        cm.Code.Should().Be(Declare, "末尾の空行は後始末で取り除く");
    }

    [Fact(DisplayName = "照合: 末尾の空行の違いは無視される（Verify が Match）")]
    public void 末尾の空行は照合に含まれない()
        => VbaWriteVerifier.Verify(Declare, Declare + "\r\n").Should().Be(VbaWriteVerdict.Match);

    [Fact(DisplayName = "書き込みは InsertLines(1, 本文) の 1 回で行う")]
    public void 書き込みはInsertLinesで行う()
    {
        var cm = new LineModule(Original);

        CodeModuleRewriter.Rewrite(cm, "Module1", Declare, created: false, comTimeoutMs: 1000, CancellationToken.None);

        cm.InsertCalls.Should().Be(1);
        cm.Code.Should().Be(Declare);
    }
}
