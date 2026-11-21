using System.Runtime.InteropServices;
using Graft.Core;
using Graft.Core.Vba;

namespace Graft.Platform.Windows;

/// <summary>
/// <c>CodeModule</c> の読み書きの窓口。実装は Windows の <c>WindowsVbaBridge</c> が遅延バインディング（<c>dynamic</c>）で
/// 作り、テストは偽物に差し替える。メソッドは COM の呼び出し 1 回に対応し、再試行や時間切れは呼び出し側
/// （<see cref="CodeModuleRewriter"/>）が受け持つ。
/// </summary>
public interface ICodeModuleAccess
{
    /// <summary><c>CodeModule.CountOfLines</c>。</summary>
    int CountOfLines();

    /// <summary><c>CodeModule.Lines(start, count)</c>。</summary>
    string Lines(int startLine, int count);

    /// <summary><c>CodeModule.DeleteLines(start, count)</c>。</summary>
    void DeleteLines(int startLine, int count);

    /// <summary><c>CodeModule.AddFromString(code)</c>。</summary>
    void AddFromString(string code);
}

/// <summary><see cref="CodeModuleRewriter.Rewrite"/> の結果。</summary>
/// <param name="Status">書き込みの結果（<see cref="VbaWriteOutcome"/> と同じ意味）。</param>
/// <param name="ReadBack">読み直した本文。読み直せなかったときは null。</param>
/// <param name="Issue">失敗または警告の理由。なければ null。</param>
/// <param name="BackupCode">書き戻しに失敗したときの、元の本文（呼び出し側が <c>.rescue/</c> へ書く）。</param>
/// <param name="NeedsRemoval">
/// 新規に作ったモジュールの書き込みが失敗した。モジュール自体を取り除くのは呼び出し側（<c>VBComponents.Remove</c>）の仕事。
/// </param>
/// <param name="ShouldAbort">
/// 着手した直後（控えを取る段階）で、Excel が応答しない・接続が切れた。以後のモジュールも固まる可能性が高いので、
/// 呼び出し側は残りの書き込みを中断する（仕様 5.3）。
/// </param>
public sealed record CodeModuleRewriteResult(
    VbaWriteStatus Status, string? ReadBack, GraftIssue? Issue, string? BackupCode = null, bool NeedsRemoval = false,
    bool ShouldAbort = false);

/// <summary>
/// 1 モジュールの全文置き換え（仕様書_VBA連携 5.5 の手順 2〜4）: 控えを取る → 消す → 書く → 読み直して判定する。
/// 別内容・例外なら控えを書き戻す。COM に直接触れず <see cref="ICodeModuleAccess"/> を通すので、Linux でテストできる。
/// <para>
/// 【途中でキャンセルで中断しない理由（不具合 6）】 以前は、時間切れで見捨てられたスレッドが <c>DeleteLines</c> の
/// 固まりから戻ってきたあとの次の呼び出しで、<c>ComRetry.Run</c> の先頭の <c>ThrowIfCancellationRequested</c> が
/// <see cref="OperationCanceledException"/> を投げていた。<c>AddFromString</c> の手前で止まるうえ、例外は書き戻しの
/// <c>catch</c>（COM の例外だけ）を素通りするので、モジュールは空のまま残り、控えの文字列も捨てたスレッドとともに消えた。
/// 「控え→消す→書く→読み直す」は、始めたら最後まで（書き戻しまで）進めないと、Excel 側が壊れたまま残る。
/// そこで見捨てられたかどうかは<b>モジュールに着手する前の 1 回だけ</b>見て（モジュールの境目）、着手したあとの
/// COM の呼び出しはすべて取り消し不可のトークンで行う。書き戻しも同じ。時間切れ後に戻ってきた仕事は、このモジュール
/// だけ終えて、次の境目で止まる。
/// </para>
/// </summary>
public static class CodeModuleRewriter
{
    /// <summary>取り消し不可で COM の呼び出しを再試行つきで実行する入れ物。</summary>
    private sealed class Runner
    {
        private readonly int _comTimeoutMs;

        public Runner(int comTimeoutMs) => _comTimeoutMs = comTimeoutMs;

        // CancellationToken.None: 着手後は見捨てられても中断しない。拒否の再試行（comTimeoutMs）だけが上限になる。
        public T Run<T>(Func<T> call) => ComRetry.Run(call, _comTimeoutMs, CancellationToken.None);
    }

    /// <summary>
    /// <paramref name="cm"/> の本文を <paramref name="code"/> に置き換える。
    /// </summary>
    /// <param name="cm">対象の CodeModule。</param>
    /// <param name="name">コンポーネント名（メッセージ用）。</param>
    /// <param name="code">書く本文。</param>
    /// <param name="created">このモジュールは書き込みのために新規に作ったばかりか。</param>
    /// <param name="comTimeoutMs">拒否されたときの再試行の上限（<c>Vba.ComTimeoutMs</c>）。</param>
    /// <param name="abandoned">
    /// 時間切れで見捨てられたことを表すトークン。<b>着手の前に 1 回だけ</b>見る（境目）。着手後は見ない。
    /// </param>
    /// <exception cref="OperationCanceledException">着手の前に、既に見捨てられていた（何も触っていない）。</exception>
    public static CodeModuleRewriteResult Rewrite(
        ICodeModuleAccess cm, string name, string code, bool created, int comTimeoutMs, CancellationToken abandoned)
    {
        ArgumentNullException.ThrowIfNull(cm);
        abandoned.ThrowIfCancellationRequested(); // モジュールの境目。ここを過ぎたら最後まで進める。

        var run = new Runner(comTimeoutMs);

        // 2. 控えを取る。DeleteLines の前に、現在の本文を読んで持つ。新規に作ったばかりのモジュールの控えは空。
        string backup;
        int backupLines;
        try
        {
            backupLines = run.Run(cm.CountOfLines);
            backup = backupLines > 0 ? run.Run(() => cm.Lines(1, backupLines)) : string.Empty;
        }
        catch (Exception ex) when (IsComFailure(ex))
        {
            // まだ何も変えていないので、戻すものは無い。
            return new CodeModuleRewriteResult(
                VbaWriteStatus.Failed, null, ToIssue(ex), NeedsRemoval: created,
                ShouldAbort: ex is ComBusyException || (ex is COMException com && VbaComHResult.Classify(com.HResult) == VbaComFailure.Disconnected));
        }

        try
        {
            // 3. 全文置き換え（DeleteLines → AddFromString）。
            if (backupLines > 0) run.Run(() => { cm.DeleteLines(1, backupLines); return 0; });
            if (code.Length > 0) run.Run(() => { cm.AddFromString(code); return 0; });

            // 4. 読み直して、期待値と比べる（判定は Core の VbaWriteVerifier）。
            var readBack = ReadAll(cm, run);
            var verdict = VbaWriteVerifier.Verify(code, readBack);
            if (verdict != VbaWriteVerdict.DifferentContent)
            {
                // 一致、または整形差（VBE が空白・大文字小文字を直した）。整形差は成功で、警告を添える。
                // ミラーは書き換えない（呼び出し側の同期基準が、書いたミラーと読み直した Excel 側を別々に持つ）。
                return new CodeModuleRewriteResult(VbaWriteStatus.Written, readBack, VbaWriteVerifier.ToIssue(verdict, name));
            }

            return Rollback(cm, run, name, backup, created, VbaWriteVerifier.ToIssue(verdict, name)!, readBack);
        }
        catch (Exception ex) when (IsComFailure(ex))
        {
            // ここで例外になったら（E805 を含む）、控えを書き戻す。
            return Rollback(cm, run, name, backup, created, ToIssue(ex), null);
        }
    }

    /// <summary>
    /// 失敗（例外または別内容）したので元に戻す。新規に作ったモジュールは本文を戻さず、取り除くよう呼び出し側に
    /// 頼む（既存のモジュールを消すことは決してしない）。既存のモジュールは控えを書き戻す。戻せたら
    /// <see cref="VbaWriteStatus.RolledBack"/>、戻せなければ E809 の <see cref="VbaWriteStatus.Failed"/>
    /// （モジュールが空のまま残っている可能性。控えは呼び出し側が <c>.rescue/</c> へ書く）。
    /// </summary>
    private static CodeModuleRewriteResult Rollback(
        ICodeModuleAccess cm, Runner run, string name, string backup, bool created, GraftIssue cause, string? readBack)
    {
        if (created) return new CodeModuleRewriteResult(VbaWriteStatus.RolledBack, readBack, cause, NeedsRemoval: true);

        try
        {
            var lines = run.Run(cm.CountOfLines);
            if (lines > 0) run.Run(() => { cm.DeleteLines(1, lines); return 0; });
            if (backup.Length > 0) run.Run(() => { cm.AddFromString(backup); return 0; });

            // 書き戻した結果も確かめる。控えと違えば「戻せた」とは言えない。
            var restored = ReadAll(cm, run);
            if (VbaWriteVerifier.Verify(backup, restored) == VbaWriteVerdict.DifferentContent)
            {
                return RollbackFailed(name, backup, cause, "書き戻した内容が控えと一致しませんでした。");
            }

            return new CodeModuleRewriteResult(VbaWriteStatus.RolledBack, readBack, cause);
        }
        catch (Exception ex) when (IsComFailure(ex))
        {
            return RollbackFailed(name, backup, cause, ex.Message);
        }
    }

    private static CodeModuleRewriteResult RollbackFailed(string name, string backup, GraftIssue cause, string reason)
        => new(VbaWriteStatus.Failed, null,
            GraftIssue.Of(
                ErrorCode.E809,
                $"{name}: 書き込みに失敗し、元の内容の書き戻しにも失敗しました（{reason}）。" +
                "Excel 側のモジュールが空のまま、または途中の状態で残っている可能性があります。" +
                $"元の本文は控えとして保存します。最初の失敗の理由: {cause.Summary}。{cause.Detail}"),
            BackupCode: backup);

    private static string ReadAll(ICodeModuleAccess cm, Runner run)
    {
        var lines = run.Run(cm.CountOfLines);
        return lines > 0 ? run.Run(() => cm.Lines(1, lines)) : string.Empty;
    }

    private static bool IsComFailure(Exception ex)
        => ex is ComBusyException or COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException;

    private static GraftIssue ToIssue(Exception ex)
        => ex is ComBusyException ? ComFailures.Busy() : ComFailures.FromException(ex);
}
