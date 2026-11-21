using System.Runtime.InteropServices;
using Graft.Core;
using Graft.Core.Vba;

namespace Graft.Platform.Windows;

// 書き込み（仕様 5.5）とブックの保存。
public sealed partial class WindowsVbaBridge
{
    /// <summary>
    /// 書き込みの途中経過。時間切れで諦めた後でも、書き終えたモジュールの結果を返せるように、
    /// 仕事を実行するスレッドと待つ側で共有する（ロックで守る）。
    /// </summary>
    private sealed class WriteProgress
    {
        private readonly object _gate = new();
        private readonly List<VbaWriteOutcome> _outcomes = new();
        private bool _started;

        /// <summary>前提検査を通って、書き込みの繰り返しに入ったか。</summary>
        public void MarkStarted()
        {
            lock (_gate) _started = true;
        }

        public void Add(VbaWriteOutcome outcome)
        {
            lock (_gate) _outcomes.Add(outcome);
        }

        /// <summary>名前で結果を引く（同じ名前が複数あれば最後のもの）。</summary>
        public VbaWriteOutcome Find(string name)
        {
            lock (_gate)
            {
                return _outcomes.LastOrDefault(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase))
                       ?? new VbaWriteOutcome(name, VbaWriteStatus.Failed, null,
                           GraftIssue.Of(ErrorCode.E805, "結果が記録されていません。"));
            }
        }

        /// <summary>
        /// 時間切れの結果。前提検査の途中で戻らなかったなら、1件も書いていないので全体の失敗（E805）。
        /// 書き込みに入っていたなら、書き終えたものはそのまま、戻らなかった1件と残りは E805 で失敗にして返す
        /// （どのモジュールが Excel に入ったかを呼び出し側が知れるように。入ったものは同期基準が更新できる）。
        /// </summary>
        public GraftResult<IReadOnlyList<VbaWriteOutcome>> BuildTimeoutResult(IReadOnlyList<VbaComponentWrite> writes)
        {
            lock (_gate)
            {
                var timeoutIssue = GraftIssue.Of(
                    ErrorCode.E805,
                    "Excel の応答が時間内に戻りませんでした。このモジュールの Excel 側の状態は不明です（VBE で確かめてください）。");

                if (!_started)
                {
                    return GraftResult<IReadOnlyList<VbaWriteOutcome>>.Fail(timeoutIssue);
                }

                var done = new HashSet<string>(_outcomes.Select(o => o.Name), StringComparer.OrdinalIgnoreCase);
                var list = new List<VbaWriteOutcome>(_outcomes);
                foreach (var write in writes)
                {
                    if (done.Contains(write.Name)) continue;
                    list.Add(new VbaWriteOutcome(write.Name, VbaWriteStatus.Failed, null, timeoutIssue));
                }

                return GraftResult<IReadOnlyList<VbaWriteOutcome>>.Ok(list);
            }
        }
    }

    private GraftResult<IReadOnlyList<VbaWriteOutcome>> WriteComponentsCore(
        string fullName, IReadOnlyList<VbaComponentWrite> writes, WriteProgress progress, CancellationToken abandoned)
    {
        using var session = NewSession(abandoned);

        // 書き込みの前に1回だけ確認する: 接続できる、VBA プロジェクトに触れる、ロックされていない、
        // デザインモードである。どれかが違えば、1件も書かずに全体を失敗にする。
        var opened = OpenProject(session, fullName, requireDesignMode: true);
        if (opened.Issue is not null) return GraftResult<IReadOnlyList<VbaWriteOutcome>>.Fail(opened.Issue);

        dynamic project = opened.Project!;
        var compsObj = session.Own(session.Call(() => (object)project.VBComponents));
        if (compsObj is null)
        {
            return GraftResult<IReadOnlyList<VbaWriteOutcome>>.Fail(ErrorCode.E801, "VBComponents を取得できませんでした。");
        }

        var index = IndexComponents(session, compsObj);
        progress.MarkStarted();

        var aborted = false;
        foreach (var write in writes)
        {
            if (aborted || session.IsAbandoned)
            {
                // 一度でも時間切れ（または拒否され続けて E805）になったら、残りには進まず中断する。
                // 以後も固まる可能性が高く、待ち時間が積み上がるため（仕様 5.3）。
                progress.Add(new VbaWriteOutcome(write.Name, VbaWriteStatus.Failed, null, GraftIssue.Of(
                    ErrorCode.E805, "前のモジュールで Excel が応答しなかったため、書き込みを中断しました。")));
                continue;
            }

            VbaWriteOutcome outcome;
            try
            {
                outcome = WriteOne(session, compsObj, index, write);
            }
            catch (ComBusyException)
            {
                outcome = new VbaWriteOutcome(write.Name, VbaWriteStatus.Failed, null, ComFailures.Busy());
                aborted = true;
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                outcome = new VbaWriteOutcome(write.Name, VbaWriteStatus.Failed, null, ComFailures.FromException(ex));
                if (ex is COMException com && VbaComHResult.Classify(com.HResult) == VbaComFailure.Disconnected) aborted = true;
            }

            progress.Add(outcome);
        }

        // WriteProgress が呼び出しの順に積んでいる。ここで返す一覧は、渡された順のコピー。
        var result = new List<VbaWriteOutcome>();
        foreach (var write in writes)
        {
            result.Add(progress.Find(write.Name));
        }

        return GraftResult<IReadOnlyList<VbaWriteOutcome>>.Ok(result);
    }

    /// <summary>名前（大文字小文字を区別しない）→ <c>VBComponents.Item</c> の添字。</summary>
    private static Dictionary<string, int> IndexComponents(Session s, object compsObject)
    {
        dynamic comps = compsObject;
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var count = s.Call(() => (int)comps.Count);
        for (var i = 1; i <= count; i++)
        {
            using var item = new ComScope();
            var comp = item.Own(s.Call(() => (object)comps.Item(i)));
            if (comp is null) continue;
            dynamic component = comp;
            map[s.Call(() => (string)component.Name)] = i;
        }

        return map;
    }

    /// <summary>1モジュールの書き込み（仕様 5.5 の手順 1〜5）。</summary>
    private static VbaWriteOutcome WriteOne(
        Session s, object compsObject, Dictionary<string, int> index, VbaComponentWrite write)
    {
        dynamic comps = compsObject;
        using var one = new ComScope();
        object? componentObj;
        var created = false;

        // 1. 取得する。無ければ、作成を許されていて標準／クラスのときだけ作る。それ以外は E808。
        if (index.TryGetValue(write.Name, out var position))
        {
            componentObj = one.Own(s.Call(() => (object)comps.Item(position)));
        }
        else
        {
            var creation = TryCreate(s, one, compsObject, write);
            if (creation.Issue is not null) return new VbaWriteOutcome(write.Name, VbaWriteStatus.Failed, null, creation.Issue);
            componentObj = creation.Component;
            created = true;
            index[write.Name] = int.MaxValue; // 同じ名前を2回渡されても作り直さない（添字は使わない）。
        }

        if (componentObj is null)
        {
            return new VbaWriteOutcome(write.Name, VbaWriteStatus.Failed, null,
                GraftIssue.Of(ErrorCode.E808, $"{write.Name}: コンポーネントを取得できませんでした。"));
        }

        dynamic component = componentObj;
        var cmObj = one.Own(s.Call(() => (object)component.CodeModule));
        if (cmObj is null)
        {
            return new VbaWriteOutcome(write.Name, VbaWriteStatus.Failed, null,
                GraftIssue.Of(ErrorCode.E808, $"{write.Name}: CodeModule を取得できませんでした。"));
        }

        dynamic cm = cmObj;

        // 2. 控えを取る。DeleteLines の前に、現在の本文を読んで持つ。新規に作ったばかりのモジュールの控えは空。
        var backupLines = s.Call(() => (int)cm.CountOfLines);
        var backup = backupLines > 0 ? s.Call(() => (string)cm.Lines(1, backupLines)) : string.Empty;

        try
        {
            // 3. 全文置き換え（DeleteLines → AddFromString）。
            if (backupLines > 0) s.Call(() => { cm.DeleteLines(1, backupLines); return 0; });
            if (write.Code.Length > 0) s.Call(() => { cm.AddFromString(write.Code); return 0; });

            // 4. 読み直して、期待値と比べる（判定は Core の VbaWriteVerifier）。
            var readBack = ReadAll(s, cmObj);
            var verdict = VbaWriteVerifier.Verify(write.Code, readBack);
            if (verdict != VbaWriteVerdict.DifferentContent)
            {
                // 一致、または整形差（VBE が空白・大文字小文字を直した）。整形差は成功で、警告を添える。
                // ミラーは書き換えない（呼び出し側の同期基準が、書いたミラーと読み直した Excel 側を別々に持つ）。
                return new VbaWriteOutcome(
                    write.Name, created ? VbaWriteStatus.Created : VbaWriteStatus.Written, readBack,
                    VbaWriteVerifier.ToIssue(verdict, write.Name));
            }

            var different = VbaWriteVerifier.ToIssue(verdict, write.Name)!;
            return Rollback(s, compsObject, componentObj, cmObj, write, backup, created, different, readBack);
        }
        catch (Exception ex) when (ex is ComBusyException or COMException or InvalidCastException
                                       or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            // ここで例外になったら（E805 を含む）、控えを書き戻す。
            var cause = ex is ComBusyException ? ComFailures.Busy() : ComFailures.FromException(ex);
            return Rollback(s, compsObject, componentObj, cmObj, write, backup, created, cause, null);
        }
    }

    /// <summary>モジュールを新規作成する。標準／クラス以外・不正な名前・VBE の拒否は E808。</summary>
    private static (object? Component, GraftIssue? Issue) TryCreate(
        Session s, ComScope one, object compsObject, VbaComponentWrite write)
    {
        dynamic comps = compsObject;
        if (!write.CreateIfMissing || !VbaModuleKinds.CanCreateOnExcel(write.Kind))
        {
            return (null, GraftIssue.Of(
                ErrorCode.E808,
                $"{write.Name}: Excel 側に存在せず、{VbaModuleKinds.FolderName(write.Kind)}/ のモジュールは新規作成できません。"));
        }

        var check = VbaNameRules.ValidateNewName(write.Name);
        if (!check.IsValid)
        {
            return (null, GraftIssue.Of(ErrorCode.E808, $"{write.Name}: {check.Reason}"));
        }

        object? added = null;
        try
        {
            added = one.Own(s.Call(() => (object)comps.Add(VbaModuleKinds.ToComType(write.Kind))));
            if (added is null) return (null, GraftIssue.Of(ErrorCode.E808, $"{write.Name}: VBE がモジュールを作成しませんでした。"));

            dynamic fresh = added;
            s.Call(() => { fresh.Name = write.Name; return 0; });
            return (added, null);
        }
        catch (COMException ex) when (!ComRetry.IsRetryable(ex))
        {
            // 名前の設定を VBE が拒否した（予約語など）。作ったばかりの空のモジュールだけを消す。
            var removeNote = TryRemoveCreated(s, compsObject, added);
            return (null, GraftIssue.Of(
                ErrorCode.E808, $"{write.Name}: VBE がモジュールの作成または命名を拒否しました（0x{ex.HResult:X8}）。{removeNote}"));
        }
    }

    /// <summary>
    /// 失敗（例外または別内容）したので元に戻す。新規に作ったモジュールは、作ったばかりのそれだけを
    /// <c>VBComponents.Remove</c> で消す（既存のモジュールを消すことは決してしない）。既存のモジュールは控えを
    /// 書き戻す。戻せたら <see cref="VbaWriteStatus.RolledBack"/>、戻せなければ E809 の <see cref="VbaWriteStatus.Failed"/>
    /// （モジュールが空のまま残っている可能性。控えは呼び出し側が .rescue/ へ書く）。
    /// </summary>
    private static VbaWriteOutcome Rollback(
        Session s, object compsObject, object componentObject, object cmObject, VbaComponentWrite write,
        string backup, bool created, GraftIssue cause, string? readBack)
    {
        dynamic cm = cmObject;
        if (created)
        {
            var note = TryRemoveCreated(s, compsObject, componentObject);
            return new VbaWriteOutcome(write.Name, VbaWriteStatus.RolledBack, readBack,
                cause with { Detail = $"{cause.Detail} {note}".Trim() });
        }

        try
        {
            var lines = s.Call(() => (int)cm.CountOfLines);
            if (lines > 0) s.Call(() => { cm.DeleteLines(1, lines); return 0; });
            if (backup.Length > 0) s.Call(() => { cm.AddFromString(backup); return 0; });

            // 書き戻した結果も確かめる。控えと違えば「戻せた」とは言えない。
            var restored = ReadAll(s, cmObject);
            if (VbaWriteVerifier.Verify(backup, restored) == VbaWriteVerdict.DifferentContent)
            {
                return RollbackFailed(write, backup, cause, "書き戻した内容が控えと一致しませんでした。");
            }

            return new VbaWriteOutcome(write.Name, VbaWriteStatus.RolledBack, readBack, cause);
        }
        catch (Exception ex) when (ex is ComBusyException or COMException or InvalidCastException
                                       or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return RollbackFailed(write, backup, cause, ex.Message);
        }
    }

    private static VbaWriteOutcome RollbackFailed(VbaComponentWrite write, string backup, GraftIssue cause, string reason)
        => new(write.Name, VbaWriteStatus.Failed, null,
            GraftIssue.Of(
                ErrorCode.E809,
                $"{write.Name}: 書き込みに失敗し、元の内容の書き戻しにも失敗しました（{reason}）。" +
                "Excel 側のモジュールが空のまま、または途中の状態で残っている可能性があります。" +
                $"元の本文は控えとして保存します。最初の失敗の理由: {cause.Summary}。{cause.Detail}"),
            BackupCode: backup);

    private static string TryRemoveCreated(Session s, object compsObject, object? created)
    {
        if (created is null) return string.Empty;
        dynamic comps = compsObject;
        try
        {
            s.Call(() => { comps.Remove((dynamic)created); return 0; });
            return "作成したモジュールは取り除きました。";
        }
        catch (Exception ex) when (ex is ComBusyException or COMException or InvalidCastException
                                       or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return "作成したモジュールを取り除けませんでした。VBE で確かめてください。";
        }
    }

    private static string ReadAll(Session s, object cmObject)
    {
        dynamic cm = cmObject;
        var lines = s.Call(() => (int)cm.CountOfLines);
        return lines > 0 ? s.Call(() => (string)cm.Lines(1, lines)) : string.Empty;
    }

    // ------------------------------------------------------------------
    // ブックの保存
    // ------------------------------------------------------------------

    private GraftResult<bool> SaveWorkbookCore(string fullName, CancellationToken abandoned)
    {
        using var session = NewSession(abandoned);
        var workbook = FindWorkbook(session, fullName);
        if (workbook is null)
        {
            return GraftResult<bool>.Fail(ErrorCode.E804, $"次のブックが開いていません: {fullName}", path: fullName);
        }

        dynamic wb = workbook;
        if (session.Call(() => (bool)wb.ReadOnly))
        {
            // 反映は済んでいる旨は呼び出し側が添える。手動で別名保存してもらう。
            return GraftResult<bool>.Fail(ErrorCode.E807, $"{fullName} は読み取り専用で開いています。", path: fullName);
        }

        session.Call(() => { wb.Save(); return 0; });
        return GraftResult<bool>.Ok(true);
    }
}
