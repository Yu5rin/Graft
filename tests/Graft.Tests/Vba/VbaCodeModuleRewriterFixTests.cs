using System.Runtime.InteropServices;
using FluentAssertions;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Platform;
using Graft.Platform.Windows;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// 不具合 6（時間切れのあとに戻ってきた書き込みが、モジュールを空のまま残す）の回帰テスト。
/// 修正前は、<c>DeleteLines</c> が固まって時間切れで見捨てられ、そのあと戻ってくると、次の <c>AddFromString</c> の手前の
/// <c>ComRetry.Run</c> が <see cref="OperationCanceledException"/> を投げ、書き戻し（COM の例外しか捕まえない）も通らずに
/// モジュールが空のまま残った。ここでは本物の COM の代わりに、メモリ上の <see cref="ICodeModuleAccess"/> で
/// 「途中で見捨てられても、着手したモジュールは最後まで進む」ことを確かめる。
/// </summary>
public class VbaCodeModuleRewriterFixTests
{
    private const string Original = "Sub A()\r\nEnd Sub";
    private const string Updated = "Sub A()\r\n  Debug.Print 1\r\nEnd Sub";

    /// <summary>メモリ上の CodeModule。各操作の前に呼ぶフックで、見捨て（キャンセル）や失敗を起こす。</summary>
    private sealed class MemoryCodeModule : ICodeModuleAccess
    {
        private string _code;

        public MemoryCodeModule(string code) => _code = code;

        public string Code => _code;

        public Action<string>? OnCall { get; set; }

        public Func<string, string, string>? Transform { get; set; }

        public int CountOfLines()
        {
            OnCall?.Invoke("CountOfLines");
            return _code.Length == 0 ? 0 : _code.Split("\r\n").Length;
        }

        public string Lines(int startLine, int count)
        {
            OnCall?.Invoke("Lines");
            return _code;
        }

        public void DeleteLines(int startLine, int count)
        {
            OnCall?.Invoke("DeleteLines");
            _code = string.Empty;
        }

        public void InsertLines(int startLine, string code)
        {
            OnCall?.Invoke("InsertLines");
            _code = Transform?.Invoke(_code, code) ?? code;
        }
    }

    [Fact(DisplayName = "不具合6 再現: DeleteLines の最中に時間切れで見捨てられても、着手したモジュールは最後まで書き終える（空のまま残らない）")]
    public void 途中で見捨てられても書き終える()
    {
        using var abandoned = new CancellationTokenSource();
        var cm = new MemoryCodeModule(Original)
        {
            // DeleteLines が固まり、その間に時間切れ（見捨て）が起きた、という状況。
            OnCall = op => { if (op == "DeleteLines") abandoned.Cancel(); },
        };

        var result = CodeModuleRewriter.Rewrite(cm, "Module1", Updated, created: false, comTimeoutMs: 1000, abandoned.Token);

        result.Status.Should().Be(VbaWriteStatus.Written);
        cm.Code.Should().Be(Updated, "InsertLines まで進めた。以前は OperationCanceledException でここが空のままだった");
    }

    [Fact(DisplayName = "不具合6 再現: 書き込みが失敗して書き戻すときに見捨てられていても、書き戻しは取り消さずに終える（控えの本文に戻る）")]
    public void 書き戻しも取り消し不可()
    {
        using var abandoned = new CancellationTokenSource();
        var failedOnce = false;
        var cm = new MemoryCodeModule(Original)
        {
            OnCall = op =>
            {
                if (op == "InsertLines" && !failedOnce)
                {
                    failedOnce = true;
                    abandoned.Cancel();
                    throw new COMException("VBE が拒否", unchecked((int)0x800A9C68));
                }
            },
        };

        var result = CodeModuleRewriter.Rewrite(cm, "Module1", Updated, created: false, comTimeoutMs: 1000, abandoned.Token);

        result.Status.Should().Be(VbaWriteStatus.RolledBack);
        cm.Code.Should().Be(Original, "控えを書き戻せた。以前は書き戻しの前に OperationCanceledException で空のままだった");
        result.BackupCode.Should().BeNull();
    }

    [Fact(DisplayName = "不具合6: 中断はモジュールの境目だけ。着手の前に見捨てられていたら、モジュールには一切触れずに OperationCanceledException")]
    public void 着手の前の見捨ては何も触らない()
    {
        using var abandoned = new CancellationTokenSource();
        abandoned.Cancel();
        var calls = new List<string>();
        var cm = new MemoryCodeModule(Original) { OnCall = calls.Add };

        var act = () => CodeModuleRewriter.Rewrite(cm, "Module1", Updated, created: false, comTimeoutMs: 1000, abandoned.Token);

        act.Should().Throw<OperationCanceledException>();
        calls.Should().BeEmpty();
        cm.Code.Should().Be(Original);
    }

    [Fact(DisplayName = "不具合6: 書き込み結果が別内容なら控えを書き戻して RolledBack（見捨てられていても）")]
    public void 別内容なら書き戻す()
    {
        using var abandoned = new CancellationTokenSource();
        var cm = new MemoryCodeModule(Original)
        {
            OnCall = op => { if (op == "DeleteLines") abandoned.Cancel(); },
            Transform = (_, code) => code.Contains("Debug") ? code + "'???" : code,
        };

        var result = CodeModuleRewriter.Rewrite(cm, "Module1", Updated, created: false, comTimeoutMs: 1000, abandoned.Token);

        result.Status.Should().Be(VbaWriteStatus.RolledBack);
        result.Issue!.Code.Should().Be(ErrorCode.E809);
        cm.Code.Should().Be(Original);
    }

    [Fact(DisplayName = "不具合6: 書き戻しにも失敗したら E809 の Failed で、元の本文を BackupCode として返す（控えが捨てられない）")]
    public void 書き戻しの失敗は控えを返す()
    {
        var addCalls = 0;
        var cm = new MemoryCodeModule(Original)
        {
            OnCall = op =>
            {
                if (op == "InsertLines" && ++addCalls >= 1) throw new COMException("拒否", unchecked((int)0x800A9C68));
            },
        };

        var result = CodeModuleRewriter.Rewrite(cm, "Module1", Updated, created: false, comTimeoutMs: 1000, CancellationToken.None);

        result.Status.Should().Be(VbaWriteStatus.Failed);
        result.Issue!.Code.Should().Be(ErrorCode.E809);
        result.BackupCode.Should().Be(Original);
    }

    [Fact(DisplayName = "不具合6: 新規に作ったばかりのモジュールの書き込みが失敗したら、本文は戻さず取り除くよう頼む（NeedsRemoval）")]
    public void 新規作成の失敗は取り除きを頼む()
    {
        var cm = new MemoryCodeModule(string.Empty)
        {
            OnCall = op => { if (op == "InsertLines") throw new COMException("拒否", unchecked((int)0x800A9C68)); },
        };

        var result = CodeModuleRewriter.Rewrite(cm, "New1", Updated, created: true, comTimeoutMs: 1000, CancellationToken.None);

        result.Status.Should().Be(VbaWriteStatus.RolledBack);
        result.NeedsRemoval.Should().BeTrue();
    }

    [Fact(DisplayName = "不具合6: 着手の直後に Excel が拒否し続けたら（再試行が尽きた）ShouldAbort で残りの書き込みを止めさせる。何も変えていない")]
    public void 着手直後の拒否は中断を促す()
    {
        var cm = new MemoryCodeModule(Original)
        {
            OnCall = _ => throw new COMException("拒否", unchecked((int)0x80010001)),
        };

        var result = CodeModuleRewriter.Rewrite(cm, "Module1", Updated, created: false, comTimeoutMs: 0, CancellationToken.None);

        result.Status.Should().Be(VbaWriteStatus.Failed);
        result.ShouldAbort.Should().BeTrue();
        result.Issue!.Code.Should().Be(ErrorCode.E805);
        cm.Code.Should().Be(Original);
    }

    [Fact(DisplayName = "不具合6: 整形差だけなら成功で、読み直した本文と整形差の印を返す（課題にはしない）")]
    public void 整形差は成功()
    {
        var cm = new MemoryCodeModule(Original)
        {
            Transform = (_, code) => code.Replace("Debug.Print 1", "debug.print  1"),
        };

        var result = CodeModuleRewriter.Rewrite(cm, "Module1", Updated, created: false, comTimeoutMs: 1000, CancellationToken.None);

        result.Status.Should().Be(VbaWriteStatus.Written);
        result.ReadBack.Should().Contain("debug.print  1");
        result.Issue.Should().BeNull("整形差は E809 の警告にしない");
        result.Reformatted.Should().BeTrue();
    }
}
