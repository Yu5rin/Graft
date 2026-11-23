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

        /// <summary>
        /// <see cref="IVbaBridge.ReadWorkbookInfoAsync"/> が返す内容の元（ブックの情報。仕様書_ブックの情報と依頼の観点 5.5）。
        /// null なら、モジュール名だけを持つ空の構造を返す。各シートの <c>HeaderRow</c>・<c>SampleRows</c> は「シートの先頭の
        /// セル」として扱い、受け取った <c>options.Views</c>（シートごとの見せ方）に合わせて返す側が詰め直す
        /// （本物のブリッジと同じく、値が渡る見せ方のシートにしか値を含めない。読む範囲の計画は Core の
        /// <see cref="VbaSheetReadPlanner"/> で、本物と同じものを使う）。
        /// </summary>
        public VbaWorkbookInfoSnapshot? Info { get; set; }

        /// <summary>
        /// シートの全セル（コード名 → 行×列の文字列。使用範囲の左上から）。大きなシートで「全部」「範囲を指定」を
        /// 確かめるために使う。無いシートは、<c>Info</c> の <c>HeaderRow</c>＋<c>SampleRows</c> を全セルとして扱う。
        /// </summary>
        public Dictionary<string, string[][]> SheetGrids { get; } = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>ブックの情報の読み取り（<c>Info</c>）の呼び出しごとに受け取った <c>options</c> の記録。</summary>
    public List<VbaWorkbookInfoOptions> InfoRequests { get; } = new();

    /// <summary>ブックの情報の取得（<see cref="ReadWorkbookInfoAsync"/>）の頭で呼ばれる。通し番号を受け、返したタスクが終わるまで待たせる。</summary>
    public Func<int, Task>? InfoHook { get; set; }

    /// <summary>
    /// ブックの情報の読み取りで、<b>セルの値を実際に読んだシート</b>の記録（呼び出しごとに 1 件ずつ追加）。
    /// 「値が要るシートだけ読む」（構成だけ・出さないのシートは読まない）ことを確かめるために使う。
    /// 各要素は、コード名 → 読んだ見せ方（<c>見出し＋N 行</c>・<c>全部</c>・<c>範囲を指定</c>）。
    /// </summary>
    public List<IReadOnlyDictionary<string, VbaSheetViewMode>> InfoValueReads { get; } = new();

    /// <summary>ブックの情報の読み取りで、テーブルを読んだシート（「出さない」のシートは読まない）の記録。</summary>
    public List<IReadOnlyList<string>> InfoTableReads { get; } = new();

    /// <summary>
    /// <c>ReadComponentsAsync</c> の呼び出しごとの <c>names</c>（null は全件読み）の記録。保存時の自動反映（仕様書_ブックの
    /// 情報と依頼の観点 8.4）が「保存したモジュールだけ」を読み、全件読みにならないことを固定するために使う。
    /// </summary>
    public List<IReadOnlyList<string>?> ReadNameRequests { get; } = new();

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
        ReadNameRequests.Add(names?.ToList());
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

    public async Task<GraftResult<VbaWorkbookInfoSnapshot>> ReadWorkbookInfoAsync(
        string workbookFullName, VbaWorkbookInfoOptions options, CancellationToken ct = default)
    {
        Calls.Add($"Info:{workbookFullName}");
        InfoRequests.Add(options);
        // テストが取得の進み具合を操れるようにする入口（取得の途中で利用者が操作したときの競合の再現用）。
        // 呼び出しの通し番号（1 始まり）を渡し、返ったタスクが終わるまで、この呼び出しを待たせる。
        if (InfoHook is { } hook) await hook(InfoRequests.Count).ConfigureAwait(false);
        var gate = await EnterAsync<VbaWorkbookInfoSnapshot>("Info", ct).ConfigureAwait(false);
        if (gate is not null) return gate;

        // 本物のブリッジの前提検査と同じ: 開いていない・URL・保護ビューは弾く。ただし VBProject には依存しないので、
        // トラストセンターがオフ（E802）・ロック中（E803）でもシート・テーブル・名前は読め、参照設定だけが省かれる。
        if (!_workbooks.TryGetValue(workbookFullName, out var workbook))
        {
            return GraftResult<VbaWorkbookInfoSnapshot>.Fail(GraftIssue.Of(ErrorCode.E804, $"次のブックが開いていません: {workbookFullName}"));
        }

        var unavailable = workbook.Availability is VbaWorkbookAvailability.UrlLocation or VbaWorkbookAvailability.ProtectedView
            ? VbaWorkbookAvailabilityIssues.ToIssue(workbook.Availability, workbookFullName)
            : null;
        if (unavailable is not null) return GraftResult<VbaWorkbookInfoSnapshot>.Fail(unavailable);

        var source = workbook.Info ?? new VbaWorkbookInfoSnapshot(
            workbook.Name, DateTimeOffset.Now, Array.Empty<VbaSheetInfo>(), Array.Empty<VbaTableInfo>(),
            Array.Empty<VbaNamedRangeInfo>(), Array.Empty<VbaReferenceInfo>(), Array.Empty<string>());

        // シートごとの見せ方に合わせて、値を詰め直す。値が渡る見せ方のシートだけ読み、それ以外は値を持たない構造だけを返す
        // （本物のブリッジの再現。読む範囲の計画は Core の VbaSheetReadPlanner。）
        var valueReads = new Dictionary<string, VbaSheetViewMode>(StringComparer.OrdinalIgnoreCase);
        var sheets = source.Sheets.Select(sheet =>
        {
            var structure = sheet with
            {
                HeaderRow = Array.Empty<string>(), SampleRows = Array.Empty<IReadOnlyList<string>>(), OmittedColumns = 0, Block = null,
            };
            var view = options.Views.For(sheet.CodeName);
            var used = VbaA1Range.FromAddress(sheet.UsedRange);
            var plan = VbaSheetReadPlanner.Plan(used, view, options.MaxColumns, options.AllMaxRows, options.AllMaxColumns);
            if (plan.Mode is not (VbaSheetViewMode.HeaderRows or VbaSheetViewMode.All or VbaSheetViewMode.Range)) return structure;

            if (plan.ReadsCells) valueReads[sheet.CodeName] = plan.Mode;
            var cells = workbook.SheetGrids.TryGetValue(sheet.CodeName, out var grid)
                ? grid.Select(r => (IReadOnlyList<string>)r).ToArray()
                : new[] { (IReadOnlyList<string>)sheet.HeaderRow }.Concat(sheet.SampleRows).ToArray();
            var slice = used is { } u && plan.ReadsCells ? Slice(cells, plan, u, view.IncludeFormulas) : Array.Empty<IReadOnlyList<string>>();
            return VbaSheetReadPlanner.Assemble(structure, plan, slice, slice);
        }).ToArray();
        InfoValueReads.Add(valueReads);

        // 「出さない」のシートのテーブルは読まない（出力に出ないので）。
        var skipped = sheets.Where(sh => options.Views.For(sh.CodeName).Mode == VbaSheetViewMode.None).Select(sh => sh.Name).ToArray();
        InfoTableReads.Add(sheets.Select(sh => sh.Name).Except(skipped).ToArray());
        var tables = source.Tables.Where(t => !skipped.Contains(t.SheetName, StringComparer.OrdinalIgnoreCase)).ToArray();

        var omitted = source.Omitted.ToList();
        var references = source.References;
        var referencesUnreadable = workbook.Availability == VbaWorkbookAvailability.AccessNotTrusted || workbook.Locked;
        if (!options.IncludeReferences || referencesUnreadable)
        {
            references = Array.Empty<VbaReferenceInfo>();
            if (options.IncludeReferences && referencesUnreadable) omitted.Add(VbaWorkbookInfoReadRules.OmittedReferencesMessage);
        }

        // 本物のブリッジと同じく、全シートの名前（表示名とコード名）は、値を読まないシート・読めなかったシートも含めて返す。
        // 取得結果の元（source）が AllSheets を持つなら、それを使う（シートの一部だけが載った取得結果の再現）。
        var allSheets = source.AllSheets ?? source.Sheets.Select(sh => new VbaSheetIdentity(sh.Name, sh.CodeName)).ToArray();
        return GraftResult<VbaWorkbookInfoSnapshot>.Ok(
            source with { Sheets = sheets, Tables = tables, References = references, Omitted = omitted, AllSheets = allSheets });
    }

    /// <summary>
    /// シートの全セルから、計画の範囲を切り出す（使用範囲の左上が先頭）。数式を含めない指示のときは、
    /// <c>=式 → 値</c> の形のセルを値だけにする（本物は <c>Value2</c> が値で、数式を含めるときだけ <c>Formula</c> も読む）。
    /// </summary>
    private static IReadOnlyList<IReadOnlyList<string>> Slice(
        IReadOnlyList<IReadOnlyList<string>> cells, VbaSheetReadPlan plan, VbaA1Range used, bool includeFormulas)
    {
        var rows = new List<IReadOnlyList<string>>();
        for (var r = 0; r < plan.RowCount; r++)
        {
            // 手元のセルの表が使用範囲より小さいときは、あるところまでを返す（空の行を足さない）。
            var sourceRow = plan.StartRow - used.Row1 + r;
            if (sourceRow >= cells.Count) break;

            var row = new List<string>();
            for (var c = 0; c < plan.ColumnCount; c++)
            {
                var sourceColumn = plan.StartColumn - used.Column1 + c;
                if (sourceColumn >= cells[sourceRow].Count) break;

                var text = cells[sourceRow][sourceColumn];
                if (!includeFormulas && text.StartsWith('=') && text.IndexOf(" → ", StringComparison.Ordinal) is var arrow and >= 0)
                {
                    text = text[(arrow + 3)..];
                }

                row.Add(text);
            }

            rows.Add(row);
        }

        return rows;
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
        // 本物のブリッジの前提検査と同じ: URL・保護ビューのブックは、選択画面と同じ理由で弾く。
        var unavailable = VbaWorkbookAvailabilityIssues.ToIssue(found.Availability, fullName);
        if (unavailable is not null) return unavailable;
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
            // 整形差は課題にせず印だけ返す（本物の CodeModuleRewriter と同じ）。
            return new VbaWriteOutcome(
                write.Name, created ? VbaWriteStatus.Created : VbaWriteStatus.Written, readBack, null,
                Reformatted: verdict == VbaWriteVerdict.FormattingDifference);
        }

        var issue = VbaWriteVerifier.ToIssue(verdict, write.Name, VbaWriteVerifier.Describe(write.Code, readBack))!;
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
