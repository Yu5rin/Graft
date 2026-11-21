using Graft.Core;
using Graft.Core.Vba;
using Graft.Platform;

namespace Graft.UiTests.Vba;

/// <summary>
/// <see cref="IVbaBridge"/> のメモリ上の偽物（仕様書_VBA連携 3.3・10.2）。ブックとモジュールをメモリ上に持ち、
/// 失敗・遅延・時間切れ・VBE の整形差を注入できる。Windows の COM に触れないので、Linux の CI で
/// 作成フロー・Pull・Push・未保存確認などの画面とフローを通せる。
/// <para>
/// 本物のブリッジ（<c>WindowsVbaBridge</c>）の契約を写している: 書き込みは 5.5 の手順どおり
/// 「前提検査 → 控え → 全文置き換え → 読み直し → <see cref="VbaWriteVerifier"/> で判定」で、別内容なら
/// 控えへ書き戻して <see cref="VbaWriteStatus.RolledBack"/>、戻せなければ E809 の
/// <see cref="VbaWriteStatus.Failed"/>。接続できない・ロック・デザインモードでない場合は 1件も書かずに全体を失敗にする。
/// </para>
/// </summary>
public sealed class FakeVbaBridge : IVbaBridge, IVbaBridgeTimeouts
{
    /// <summary>メモリ上のブック。</summary>
    public sealed class Workbook
    {
        public Workbook(string fullName) => FullName = fullName;

        public string FullName { get; }

        public string Name => Path.GetFileName(FullName.Replace('\\', '/'));

        /// <summary>一覧での利用可否（OneDrive の URL・保護ビューなどを再現する）。</summary>
        public VbaWorkbookAvailability Availability { get; set; } = VbaWorkbookAvailability.Available;

        public string? UnavailableReason { get; set; }

        public bool IsReadOnly { get; set; }

        /// <summary>VBA プロジェクトがロックされている（E803）。</summary>
        public bool Locked { get; set; }

        /// <summary>デザインモードか。false ならマクロの実行中・中断中（E813）。</summary>
        public bool DesignMode { get; set; } = true;

        /// <summary>モジュール（コンポーネント名 → 内容）。</summary>
        public List<Module> Modules { get; } = new();

        public int SaveCount { get; set; }

        public Module? Find(string name)
            => Modules.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>メモリ上のコンポーネント。</summary>
    public sealed class Module
    {
        public Module(string name, VbaModuleKind kind, string code, string? caption = null)
        {
            Name = name;
            Kind = kind;
            Code = code;
            Caption = caption;
        }

        public string Name { get; }

        public VbaModuleKind Kind { get; }

        /// <summary>本文（<c>CodeModule.Lines</c> 相当。末尾に改行を持たない）。</summary>
        public string Code { get; set; }

        public string? Caption { get; }
    }

    private readonly Dictionary<string, Workbook> _workbooks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GraftIssue> _failNext = new(StringComparer.Ordinal);

    public bool IsSupported { get; set; } = true;

    public string? UnsupportedReason => IsSupported ? null : "Excel VBA 連携は、この環境では使えません。";

    /// <summary>呼び出しの記録（<c>List</c>／<c>Read:パス</c>／<c>Write:パス:名前,名前</c>／<c>Save</c>／<c>Show</c>／<c>Activate</c>）。</summary>
    public List<string> Calls { get; } = new();

    /// <summary>Write の要求の記録（呼び出しごと）。</summary>
    public List<IReadOnlyList<VbaComponentWrite>> WriteRequests { get; } = new();

    /// <summary>VBE が書き込み時に行う整形の再現。null なら整形しない（そのまま読み直せる）。</summary>
    public Func<string, string>? FormatOnWrite { get; set; }

    /// <summary>書き込みで「別内容」になる（読み直した内容が違う）ことにするモジュール名。控えへ書き戻す。</summary>
    public HashSet<string> CorruptOnWrite { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>その上で、控えの書き戻しにも失敗することにするモジュール名（E809 と BackupCode）。</summary>
    public HashSet<string> RollbackFails { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>1回の呼び出しごとの遅延（応答の遅い Excel の再現）。</summary>
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// true なら、呼び出しが戻ってこない Excel（マクロ実行中・VBE のモーダル表示中）を再現する。
    /// 本物のブリッジと同じく、呼び出し全体の時間切れ（<see cref="CallTimeoutMs"/>）で E805 を返す。
    /// </summary>
    public bool Hang { get; set; }

    /// <summary>
    /// 指定した操作（<c>List</c>／<c>Read</c>／<c>Write</c>／<c>Save</c>）だけ戻ってこないことにする。
    /// 解析と読み取りは通り、反映（Write）だけが固まる、という「適用の途中で Excel が固まる」状況を作るのに使う。
    /// </summary>
    public HashSet<string> HangOperations { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Write が呼ばれたとき、書き込みの前に待つ処理（呼び出しの記録の後）。Push の最中に別の操作をしたときの挙動
    /// （貼り付け・取り消し・プロジェクト切り替えを受け付けないこと）を確かめるために、テストが Push を止めておく。
    /// </summary>
    public Func<Task>? BeforeWrite { get; set; }

    /// <summary><see cref="Configure"/> で受け取った呼び出し全体の時間切れ（既定 30 秒）。</summary>
    public int CallTimeoutMs { get; private set; } = 30000;

    public int ComTimeoutMs { get; private set; } = 5000;

    public void Configure(int comTimeoutMs, int callTimeoutMs)
    {
        ComTimeoutMs = comTimeoutMs;
        CallTimeoutMs = callTimeoutMs;
    }

    // ------------------------------------------------------------------
    // テストからの操作
    // ------------------------------------------------------------------

    /// <summary>ブックを開く（ROT に載せる）。</summary>
    public Workbook AddWorkbook(string fullName, params Module[] modules)
    {
        var workbook = new Workbook(fullName);
        workbook.Modules.AddRange(modules);
        _workbooks[fullName] = workbook;
        return workbook;
    }

    /// <summary>ブックを閉じる（E804 の再現）。</summary>
    public void CloseWorkbook(string fullName) => _workbooks.Remove(fullName);

    public Workbook? Get(string fullName) => _workbooks.GetValueOrDefault(fullName);

    /// <summary>次の1回の呼び出し（操作名: <c>List</c>／<c>Read</c>／<c>Write</c>／<c>Save</c>）を、指定の問題で失敗させる。</summary>
    public void FailNext(string operation, GraftIssue issue) => _failNext[operation] = issue;

    public static Module Std(string name, string code) => new(name, VbaModuleKind.Standard, code);

    public static Module Cls(string name, string code) => new(name, VbaModuleKind.Class, code);

    public static Module Doc(string name, string code, string? caption = null) => new(name, VbaModuleKind.Document, code, caption);

    public static Module Form(string name, string code) => new(name, VbaModuleKind.Form, code);

    // ------------------------------------------------------------------
    // IVbaBridge
    // ------------------------------------------------------------------

    public async Task<GraftResult<IReadOnlyList<VbaWorkbookInfo>>> ListOpenWorkbooksAsync(CancellationToken ct = default)
    {
        Calls.Add("List");
        var gate = await EnterAsync<IReadOnlyList<VbaWorkbookInfo>>("List", ct).ConfigureAwait(false);
        if (gate is not null) return gate;

        var list = _workbooks.Values
            .Select(w => new VbaWorkbookInfo(w.FullName, w.Name, w.Availability, w.UnavailableReason, w.IsReadOnly))
            .OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return GraftResult<IReadOnlyList<VbaWorkbookInfo>>.Ok(list);
    }

    public async Task<GraftResult<IReadOnlyList<VbaComponentSnapshot>>> ReadComponentsAsync(
        string workbookFullName, IReadOnlyCollection<string>? names, CancellationToken ct = default)
    {
        Calls.Add($"Read:{workbookFullName}");
        var gate = await EnterAsync<IReadOnlyList<VbaComponentSnapshot>>("Read", ct).ConfigureAwait(false);
        if (gate is not null) return gate;

        var open = Open(workbookFullName, requireDesign: false, out var workbook);
        if (open is not null) return GraftResult<IReadOnlyList<VbaComponentSnapshot>>.Fail(open);

        var wanted = names is null ? null : new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var snapshots = workbook!.Modules
            .Where(m => wanted is null || wanted.Contains(m.Name))
            .Select(m => new VbaComponentSnapshot(m.Name, m.Kind, m.Code, m.Caption))
            .ToList();
        return GraftResult<IReadOnlyList<VbaComponentSnapshot>>.Ok(snapshots);
    }

    public async Task<GraftResult<IReadOnlyList<VbaWriteOutcome>>> WriteComponentsAsync(
        string workbookFullName, IReadOnlyList<VbaComponentWrite> writes, CancellationToken ct = default)
    {
        Calls.Add($"Write:{workbookFullName}:{string.Join(",", writes.Select(w => w.Name))}");
        WriteRequests.Add(writes.ToList());
        if (BeforeWrite is not null) await BeforeWrite().ConfigureAwait(false);
        var gate = await EnterAsync<IReadOnlyList<VbaWriteOutcome>>("Write", ct).ConfigureAwait(false);
        if (gate is not null) return gate;

        // 書き込みの前に1回だけ確認する。違えば、1件も書かずに全体を失敗にする。
        var open = Open(workbookFullName, requireDesign: true, out var workbook);
        if (open is not null) return GraftResult<IReadOnlyList<VbaWriteOutcome>>.Fail(open);

        var outcomes = new List<VbaWriteOutcome>();
        foreach (var write in writes)
        {
            outcomes.Add(WriteOne(workbook!, write));
        }

        return GraftResult<IReadOnlyList<VbaWriteOutcome>>.Ok(outcomes);
    }

    public async Task<GraftResult<bool>> SaveWorkbookAsync(string workbookFullName, CancellationToken ct = default)
    {
        Calls.Add("Save");
        var gate = await EnterAsync<bool>("Save", ct).ConfigureAwait(false);
        if (gate is not null) return gate;

        var open = Open(workbookFullName, requireDesign: false, out var workbook);
        if (open is not null) return GraftResult<bool>.Fail(open);
        if (workbook!.IsReadOnly) return GraftResult<bool>.Fail(ErrorCode.E807, "読み取り専用です。");

        workbook.SaveCount++;
        return GraftResult<bool>.Ok(true);
    }

    public async Task<GraftResult<bool>> ShowInVbeAsync(
        string workbookFullName, string componentName, int line, CancellationToken ct = default)
    {
        Calls.Add($"Show:{workbookFullName}:{componentName}:{line}");
        var gate = await EnterAsync<bool>("Show", ct).ConfigureAwait(false);
        if (gate is not null) return gate;

        var open = Open(workbookFullName, requireDesign: false, out _);
        return open is not null ? GraftResult<bool>.Fail(open) : GraftResult<bool>.Ok(true);
    }

    public async Task<GraftResult<bool>> ActivateWorkbookAsync(string workbookFullName, CancellationToken ct = default)
    {
        Calls.Add($"Activate:{workbookFullName}");
        var gate = await EnterAsync<bool>("Activate", ct).ConfigureAwait(false);
        if (gate is not null) return gate;

        var open = Open(workbookFullName, requireDesign: false, out _);
        return open is not null ? GraftResult<bool>.Fail(open) : GraftResult<bool>.Ok(true);
    }

    // ------------------------------------------------------------------
    // 内部
    // ------------------------------------------------------------------

    /// <summary>遅延・時間切れ・注入した失敗を処理する。失敗または時間切れなら結果を返し、続けてよければ null。</summary>
    private async Task<GraftResult<T>?> EnterAsync<T>(string operation, CancellationToken ct)
    {
        if (Hang || HangOperations.Contains(operation))
        {
            // 戻ってこない呼び出し。本物のブリッジの代わりに、ここで呼び出し全体の時間切れを再現する。
            await Task.Delay(TimeSpan.FromMilliseconds(CallTimeoutMs), ct).ConfigureAwait(false);
            return GraftResult<T>.Fail(ErrorCode.E805, "Excel の応答が時間内に戻りませんでした。");
        }

        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct).ConfigureAwait(false);
        if (_failNext.Remove(operation, out var issue)) return GraftResult<T>.Fail(issue);
        return null;
    }

    /// <summary>前提条件の検査（仕様 5.4）。違反は問題で返す。</summary>
    private GraftIssue? Open(string fullName, bool requireDesign, out Workbook? workbook)
    {
        workbook = null;
        if (VbaRotNames.IsNoMacroFormat(fullName)) return GraftIssue.Of(ErrorCode.E806, fullName);
        if (!_workbooks.TryGetValue(fullName, out var found)) return GraftIssue.Of(ErrorCode.E804, $"次のブックが開いていません: {fullName}");
        if (found.Availability == VbaWorkbookAvailability.AccessNotTrusted) return GraftIssue.Of(ErrorCode.E802);
        if (found.Locked) return GraftIssue.Of(ErrorCode.E803);
        if (requireDesign && !found.DesignMode) return GraftIssue.Of(ErrorCode.E813);
        workbook = found;
        return null;
    }

    private VbaWriteOutcome WriteOne(Workbook workbook, VbaComponentWrite write)
    {
        var module = workbook.Find(write.Name);
        var created = false;
        if (module is not null)
        {
            // 本物のブリッジと同じ最後の砦: 引いたコンポーネントの名前と種別が、書こうとしているものと違えば書かない。
            var mismatch = VbaWriteTargetGuard.Check(write.Name, write.Kind, module.Name, module.Kind);
            if (mismatch is not null) return new VbaWriteOutcome(write.Name, VbaWriteStatus.Failed, null, mismatch);
        }

        if (module is null)
        {
            if (!write.CreateIfMissing || !VbaModuleKinds.CanCreateOnExcel(write.Kind))
            {
                return new VbaWriteOutcome(write.Name, VbaWriteStatus.Failed, null,
                    GraftIssue.Of(ErrorCode.E808, $"{write.Name}: 作成できません。"));
            }

            var check = VbaNameRules.ValidateNewName(write.Name);
            if (!check.IsValid)
            {
                return new VbaWriteOutcome(write.Name, VbaWriteStatus.Failed, null, GraftIssue.Of(ErrorCode.E808, check.Reason));
            }

            module = new Module(write.Name, write.Kind, string.Empty);
            workbook.Modules.Add(module);
            created = true;
        }

        var backup = module.Code;
        var formatted = FormatOnWrite?.Invoke(write.Code) ?? write.Code;
        module.Code = CorruptOnWrite.Contains(write.Name) ? formatted + "'???" : formatted;
        var readBack = module.Code;

        var verdict = VbaWriteVerifier.Verify(write.Code, readBack);
        if (verdict != VbaWriteVerdict.DifferentContent)
        {
            return new VbaWriteOutcome(
                write.Name, created ? VbaWriteStatus.Created : VbaWriteStatus.Written, readBack,
                VbaWriteVerifier.ToIssue(verdict, write.Name));
        }

        var issue = VbaWriteVerifier.ToIssue(verdict, write.Name)!;
        if (created)
        {
            workbook.Modules.Remove(module); // 作ったばかりのモジュールだけを消す。
            return new VbaWriteOutcome(write.Name, VbaWriteStatus.RolledBack, readBack, issue);
        }

        if (RollbackFails.Contains(write.Name))
        {
            return new VbaWriteOutcome(write.Name, VbaWriteStatus.Failed, null,
                GraftIssue.Of(ErrorCode.E809, $"{write.Name}: 書き戻しにも失敗しました。"), BackupCode: backup);
        }

        module.Code = backup;
        return new VbaWriteOutcome(write.Name, VbaWriteStatus.RolledBack, readBack, issue);
    }
}
