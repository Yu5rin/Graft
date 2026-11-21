using System.IO;
using System.Text;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Infra;
using Graft.Platform;

namespace Graft.Features;

/// <summary>Pull 前の未保存確認の結果（<see cref="VbaSyncService.BeforePullAsync"/>）。</summary>
/// <param name="Declined">利用者が保存を断った（または保存に失敗した）ファイルのフルパス。これらは Pull しない。</param>
/// <param name="SavedAny">1つでも保存した（ディスク上のミラーが変わったので、計画を作り直す必要がある）。</param>
public sealed record VbaPullSaveOutcome(IReadOnlyCollection<string> Declined, bool SavedAny)
{
    /// <summary>未保存の編集が無かった。</summary>
    public static readonly VbaPullSaveOutcome Nothing = new(Array.Empty<string>(), false);
}

/// <summary>Pull の結果。いずれもミラー内の正規の相対パス（<c>Modules/Module1.bas</c>）。</summary>
/// <param name="Pulled">Excel から取り込んでミラーを書いたもの。</param>
/// <param name="Conflicts">衝突のため取り込まなかったもの（衝突ダイアログで利用者に選んでもらう。7.5）。</param>
/// <param name="Skipped">未保存の編集を保存してもらえなかったため取り込まなかったもの。</param>
public sealed record VbaPullResult(
    IReadOnlyList<string> Pulled, IReadOnlyList<string> Conflicts, IReadOnlyList<string> Skipped)
{
    /// <summary>衝突の詳細（両側の本文つき）。<see cref="Conflicts"/> と同じ順・同じ件数。</summary>
    public IReadOnlyList<VbaConflictItem> ConflictItems { get; init; } = Array.Empty<VbaConflictItem>();

    /// <summary>何も起きなかった。</summary>
    public bool IsEmpty => Pulled.Count == 0 && Conflicts.Count == 0 && Skipped.Count == 0;
}

/// <summary>Push の結果。いずれもミラー内の正規の相対パス。</summary>
/// <param name="Pushed">既存のモジュールへ書けたもの。</param>
/// <param name="Created">Excel 側にモジュールを新規作成して書けたもの。</param>
/// <param name="Failed">書けなかったもの（E811 で除外したもの、書き込みの失敗、書き戻し済みを含む）。</param>
/// <param name="Conflicts">衝突のため反映しなかったもの（衝突ダイアログで利用者に選んでもらう。7.5）。</param>
/// <param name="WorkbookSaved"><c>SaveWorkbookAfterPush</c> でブックを保存したか。</param>
public sealed record VbaPushResult(
    IReadOnlyList<string> Pushed, IReadOnlyList<string> Created, IReadOnlyList<string> Failed,
    IReadOnlyList<string> Conflicts, bool WorkbookSaved)
{
    /// <summary>衝突の詳細（両側の本文つき）。<see cref="Conflicts"/> と同じ順・同じ件数。</summary>
    public IReadOnlyList<VbaConflictItem> ConflictItems { get; init; } = Array.Empty<VbaConflictItem>();

    /// <summary>
    /// ミラーのファイルが無く、Excel 側にはモジュールが残っている（削除保留）もの。新規モジュールを含む適用を
    /// 取り消したあとに現れる（仕様 6.5・7.3-4-6）。呼び出し側が「Excel 側に残っています」と知らせるのに使う。
    /// </summary>
    public IReadOnlyList<string> DeletePending { get; init; } = Array.Empty<string>();

    /// <summary>Excel へ書けた件数。</summary>
    public int WrittenCount => Pushed.Count + Created.Count;
}

/// <summary>
/// Excel とミラーを突き合わせるだけで何も書かない検査の結果（<see cref="VbaSyncService.CheckAsync"/>）。
/// 解析の前後に「取り込むべき変更があるか」「衝突しているか」を、書き込みの前に知るために使う。
/// </summary>
/// <param name="Pull">Excel 側だけが変わっているもの。</param>
/// <param name="Push">ミラー側だけが変わっているもの（新規作成を含む）。</param>
/// <param name="Conflicts">両側が変わっていて本文が違うもの。</param>
public sealed record VbaSyncCheck(
    IReadOnlyList<string> Pull, IReadOnlyList<string> Push, IReadOnlyList<VbaConflictItem> Conflicts)
{
    /// <summary>取り込むもの・衝突のどちらも無い。</summary>
    public bool IsClean => Pull.Count == 0 && Conflicts.Count == 0;
}

/// <summary>ミラー作成（プロジェクト作成フロー）の結果。</summary>
/// <param name="MirrorRoot">ミラーフォルダ。</param>
/// <param name="ModuleCount">書いたモジュールの数。</param>
public sealed record VbaMirrorCreation(string MirrorRoot, int ModuleCount);

/// <summary>ミラーだけから分かる状態（Excel に接続せずに計算する）。メニューの出し分けなどに使う。</summary>
/// <param name="UnpushedCount">Push 判定（ミラー側だけが変わった）のモジュール数。</param>
/// <param name="DeletePendingCount">削除保留のモジュール数。</param>
public sealed record VbaLocalStatus(int UnpushedCount, int DeletePendingCount);

/// <summary>
/// 三方比較（<see cref="VbaSyncPlanner"/>）と <see cref="IVbaBridge"/>・<see cref="VbaSyncStateStore"/>・
/// ミラーのファイル I/O をつないで、Pull（Excel → ミラー）と Push（ミラー → Excel）を実行する
/// （仕様書_VBA連携 3.1・6.5・7.2・7.2b）。
/// <para>
/// 【判断を持たない薄い層にする理由】 何をするかの判断（どれが Pull か Push か衝突か）は純ロジックの
/// <see cref="VbaSyncPlanner"/> にあり、Excel とのやり取りは <see cref="IVbaBridge"/> にある。ここは
/// それらを順につなぎ、結果を同期基準へ反映するだけにして、<c>FakeVbaBridge</c> を差し込んだ画面テストで
/// 全経路を確かめられるようにする。
/// </para>
/// <para>
/// 【履歴に残さない】 Pull はミラーを書き換えるが、既存の履歴（リビジョン）には記録しない。履歴は「Graft が適用した
/// 変更」だけを持つ。代わりに <see cref="Logger"/> へ残す（仕様 13章）。履歴に入れると、Excel 側で起きた変更が
/// 「取り消せる適用」に見えてしまう。
/// </para>
/// <para>
/// 【ミラーを Push 後に書き換えない理由】 VBE は書き込み時に空白とキーワードの大文字小文字を整える。ミラーを
/// 読み直した Excel の本文に合わせて書き戻すと、履歴の <c>HashAfter</c> と食い違い、取り消しが毎回 E301 になる。
/// 代わりに同期基準を <c>mirrorHash</c>（書いたミラー）と <c>excelHash</c>（読み直した Excel）の2本で持つ。
/// </para>
/// 呼び出しは直列化する（同期状態ファイルを読み→更新→書く流れの競合を避けるため）。
/// </summary>
public sealed class VbaSyncService
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly IVbaBridge _bridge;
    private readonly VbaSyncStateStore _states;
    private readonly AppPaths _paths;
    private readonly ITrashService? _trash;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="bridge">Excel との窓口。</param>
    /// <param name="states">同期基準の保存先。</param>
    /// <param name="paths">データ保存先（控えのフォルダの場所に使う）。</param>
    /// <param name="trash">「作り直す」で既存のミラーを送るごみ箱。null なら作り直しは失敗にする。</param>
    /// <param name="clock">現在時刻（テストで固定するため）。</param>
    public VbaSyncService(
        IVbaBridge bridge, VbaSyncStateStore states, AppPaths paths, ITrashService? trash = null, Func<DateTimeOffset>? clock = null)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _states = states ?? throw new ArgumentNullException(nameof(states));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _trash = trash;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    /// <summary>Pull や Push を記録する窓口（<c>MainViewModel</c> が渡す）。</summary>
    public Logger? Logger { get; set; }

    /// <summary>この環境で機能が使えるか（<see cref="IVbaBridge.IsSupported"/>）。</summary>
    public bool IsSupported => _bridge.IsSupported;

    /// <summary>
    /// Pull の前に、取り込み先ファイルの未保存の編集を確認して保存させる（仕様 7.2）。引数は取り込み先のうち
    /// 既に存在するファイルのフルパス。未設定なら確認なしで続ける。
    /// <para>
    /// 未保存の編集は三方比較に入らない（ディスク上のミラーは基準と同じに見えるので Pull され、利用者は E702 の
    /// バーで初めて気付く）ため、<c>BeforeApplyAsync</c> と同じ確認で先に保存してもらう。保存すると
    /// ミラーが変わるので、保存したときは計画を作り直す。
    /// </para>
    /// </summary>
    public Func<IReadOnlyList<string>, Task<VbaPullSaveOutcome>>? BeforePullAsync { get; set; }

    // ------------------------------------------------------------------
    // ブックの一覧・プロジェクト作成（7.1）
    // ------------------------------------------------------------------

    /// <summary>ブックの Excel ウィンドウを前面化する（右クリックの「ブックを前面化」）。</summary>
    public Task<GraftResult<bool>> ActivateWorkbookAsync(string workbookFullName, CancellationToken ct = default)
        => _bridge.ActivateWorkbookAsync(workbookFullName, ct);

    /// <summary>VBE で指定モジュールのコードウィンドウを開き、指定行へ移動して前面化する。</summary>
    public Task<GraftResult<bool>> ShowInVbeAsync(string workbookFullName, string componentName, int line, CancellationToken ct = default)
        => _bridge.ShowInVbeAsync(workbookFullName, componentName, line, ct);

    /// <summary>起動中の保存済みブックを列挙する。</summary>
    public Task<GraftResult<IReadOnlyList<VbaWorkbookInfo>>> ListWorkbooksAsync(CancellationToken ct = default)
        => _bridge.ListOpenWorkbooksAsync(ct);

    /// <summary>ブックのフルパスから既定のミラーフォルダを求める（<c>&lt;データ保存先&gt;/vba-mirrors/&lt;ブック名&gt;-&lt;短いハッシュ&gt;</c>）。</summary>
    public string DefaultMirrorRoot(string workbookFullName)
        => Path.Combine(_paths.VbaMirrorsDirectory, VbaMirrorFolderName.Build(workbookFullName));

    /// <summary>
    /// 指定のフォルダの、ミラーとしての状態。作成ダイアログで「既存のミラーを使う」「作り直す」を選ばせるかどうかと、
    /// 「作り直す」を出してよいかの判断に使う。
    /// </summary>
    /// <param name="Exists">使えるミラーがある（同じ Id の同期状態がある、または同期対象のモジュールのファイルがある）。</param>
    /// <param name="HasForeignFiles">
    /// ミラーのものではないファイル・フォルダ（<c>Modules/</c>・<c>Classes/</c>・<c>Forms/</c>・<c>Documents/</c> と、
    /// 先頭が「.」のもの以外）がある。あれば「作り直す」は出さない。
    /// </param>
    public sealed record MirrorFolderInfo(bool Exists, bool HasForeignFiles)
    {
        /// <summary>「作り直す」を選ばせてよいか。</summary>
        public bool CanRebuild => Exists && !HasForeignFiles;
    }

    /// <summary>
    /// フォルダのミラーとしての状態を調べる。
    /// <para>
    /// 【判定を絞った理由】 以前は「フォルダに何か 1 つでもある」だけで既存のミラーと見なし、「作り直す」でフォルダ
    /// ごとごみ箱へ送っていた。ミラーの場所に「ドキュメント」や既存のリポジトリを選んで「作り直す」を選ぶと、
    /// 関係のないファイルまで全部ごみ箱へ行く（不具合 3）。同期対象のファイル（<c>Modules/*.bas</c> など）か同じ Id の
    /// 同期状態がある場合だけを「既存のミラー」とし、ミラーのもの以外が混じっているフォルダでは作り直しを出さない。
    /// 先頭が「.」の項目（バージョン管理の隠しフォルダなど）は、同期の対象外として数えない
    /// （<see cref="ReadMirrorAsync"/> と同じ扱い）。
    /// </para>
    /// </summary>
    public MirrorFolderInfo InspectMirrorFolder(string mirrorRoot)
    {
        if (string.IsNullOrWhiteSpace(mirrorRoot)) return new MirrorFolderInfo(false, false);
        var full = Path.GetFullPath(mirrorRoot);
        var hasState = File.Exists(_paths.GetVbaSyncFilePath(ProjectStore.CreateId(full)));
        if (!Directory.Exists(full)) return new MirrorFolderInfo(hasState, false);

        var mirrorFolderNames = Enum.GetValues<VbaModuleKind>()
            .Select(VbaModuleKinds.FolderName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var foreign = false;
        foreach (var entry in Directory.EnumerateFileSystemEntries(full))
        {
            var name = Path.GetFileName(entry);
            if (name.StartsWith('.')) continue;
            if (!(Directory.Exists(entry) && mirrorFolderNames.Contains(name))) foreign = true;
        }

        return new MirrorFolderInfo(hasState || SyncTargetFiles(full).Count > 0, foreign);
    }

    /// <summary>指定のミラーフォルダに、既に使えるミラーがあるか（<see cref="InspectMirrorFolder"/>）。</summary>
    public bool MirrorExists(string mirrorRoot) => InspectMirrorFolder(mirrorRoot).Exists;

    /// <summary>ミラーのフォルダの、4 つのフォルダの直下にある同期対象のファイル（フルパス）。</summary>
    private static List<string> SyncTargetFiles(string root)
    {
        var files = new List<string>();
        foreach (var kind in Enum.GetValues<VbaModuleKind>())
        {
            var folder = Path.Combine(root, VbaModuleKinds.FolderName(kind));
            if (!Directory.Exists(folder)) continue;
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (VbaMirrorLayout.TryParse(relative, out _, out _)) files.Add(file);
            }
        }

        return files;
    }

    /// <summary>
    /// 全コンポーネントを Excel から読み、ミラーへ書いて、同期基準を作る（7.1 の 4）。
    /// <paramref name="rebuild"/> が true なら、既存のミラーの<b>同期対象のファイルだけ</b>をごみ箱へ送ってから作り直す
    /// （フォルダごとは送らない。ミラーのものではないファイルが混じるフォルダでは作り直さない。不具合 3）。
    /// </summary>
    public async Task<GraftResult<VbaMirrorCreation>> CreateMirrorAsync(
        string workbookFullName, string mirrorRoot, bool rebuild, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workbookFullName);
        ArgumentException.ThrowIfNullOrWhiteSpace(mirrorRoot);
        var root = Path.GetFullPath(mirrorRoot);
        var projectId = ProjectStore.CreateId(root);

        await _gate.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            var read = await _bridge.ReadComponentsAsync(workbookFullName, null, ct).ConfigureAwait(true);
            if (!read.IsSuccess) return GraftResult<VbaMirrorCreation>.Fail(read.Issues);

            if (rebuild)
            {
                if (InspectMirrorFolder(root).HasForeignFiles)
                {
                    return GraftResult<VbaMirrorCreation>.Fail(
                        ErrorCode.E402,
                        $"フォルダ（{root}）にミラーのものではないファイルがあるため、作り直せません。空のフォルダか、ミラーだけのフォルダを選んでください。",
                        path: root);
                }

                // フォルダごとではなく、同期対象のファイルを 1 つずつ送る。ごみ箱から戻せる単位を小さく保ち、
                // 万一ミラーのように見えて中身が違うフォルダを選んでいても、関係のないファイルは巻き込まない。
                foreach (var file in SyncTargetFiles(root))
                {
                    if (_trash is null || !_trash.Send(file))
                    {
                        return GraftResult<VbaMirrorCreation>.Fail(
                            ErrorCode.E402, $"既存のミラーのファイル（{file}）をごみ箱へ送れなかったため、作り直せません。", path: file);
                    }
                }

                _states.Delete(projectId);
            }

            var issues = new List<GraftIssue>(read.Issues);
            var state = new VbaSyncState { WorkbookFullName = workbookFullName };
            var now = _clock();
            var count = 0;
            foreach (var snapshot in read.Value)
            {
                var relative = VbaMirrorLayout.ToRelativePath(snapshot.Kind, snapshot.Name);
                if (!VbaNameRules.CanMirror(snapshot.Name))
                {
                    issues.Add(GraftIssue.Of(
                        ErrorCode.E810, $"Excel のコンポーネント「{snapshot.Name}」はミラーのファイルにできない名前のため、同期の対象外です。",
                        path: relative, severity: Severity.Warning));
                    continue;
                }

                var written = await WriteNewMirrorFileAsync(root, relative, snapshot.Code, ct).ConfigureAwait(true);
                if (!written.IsSuccess) return GraftResult<VbaMirrorCreation>.Fail(written.Issues);

                state = state.WithModule(relative, VbaModuleState.Create(
                    snapshot.Kind, snapshot.Name, snapshot.Caption,
                    VbaTextNormalizer.Hash(snapshot.Code), VbaTextNormalizer.Hash(snapshot.Code),
                    VbaTextNormalizer.LooseHash(snapshot.Code), now));
                count++;
            }

            Directory.CreateDirectory(root);
            await _states.SaveAsync(projectId, state, ct).ConfigureAwait(true);
            Logger?.Info("vba-create", $"Excel ブックからミラーを作成しました（{count}件）: {workbookFullName}", targetPath: root);
            return GraftResult<VbaMirrorCreation>.Ok(new VbaMirrorCreation(root, count), issues);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ------------------------------------------------------------------
    // Pull（Excel → ミラー）
    // ------------------------------------------------------------------

    /// <summary>
    /// Excel 側だけが変わったモジュールをミラーへ取り込む（7.2）。衝突は取り込まずに結果へ載せる
    /// （呼び出し側が衝突ダイアログで利用者に選んでもらい、「Excel 側を採用」なら <paramref name="adoptConflicts"/> に
    /// 挙げて呼び直す）。削除保留のモジュールは取り込まない。
    /// </summary>
    /// <param name="only">対象を絞る相対パス。null なら全モジュール。</param>
    /// <param name="restoreDeletePending">
    /// true なら <paramref name="only"/> に挙げた削除保留のモジュールを Excel から取り込み直して保留を解除する
    /// （右クリックの「Excel から復元」）。
    /// </param>
    /// <param name="adoptConflicts">
    /// 衝突していても Excel の本文でミラーを置き換えるモジュール（衝突ダイアログの「Excel 側を採用」）。
    /// 衝突していないモジュールを挙げても何も起きない。
    /// </param>
    public async Task<GraftResult<VbaPullResult>> PullAsync(
        Project project, IReadOnlyCollection<string>? only = null, bool restoreDeletePending = false,
        IReadOnlyCollection<string>? adoptConflicts = null, CancellationToken ct = default)
    {
        var link = RequireLink(project);
        await _gate.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            return await PullCoreAsync(project, link, only, restoreDeletePending, Scope(adoptConflicts), ct).ConfigureAwait(true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<GraftResult<VbaPullResult>> PullCoreAsync(
        Project project, VbaLink link, IReadOnlyCollection<string>? only, bool restoreDeletePending,
        HashSet<string>? adopt, CancellationToken ct)
    {
        var scope = Scope(only);
        var snapshot = await BuildPlanAsync(project, link, scope, ct).ConfigureAwait(true);
        if (!snapshot.IsSuccess) return GraftResult<VbaPullResult>.Fail(snapshot.Issues);
        var work = snapshot.Value;
        var issues = new List<GraftIssue>(snapshot.Issues);

        var skipped = new List<string>();
        var pullPaths = PullTargets(work.Plan, restoreDeletePending, adopt);

        // 取り込み先に未保存の編集があれば、先に保存してもらう。保存するとミラーが変わるので、計画を作り直す。
        if (BeforePullAsync is not null)
        {
            var existing = pullPaths
                .Select(p => Path.Combine(project.Root, p.Replace('/', Path.DirectorySeparatorChar)))
                .Where(File.Exists)
                .ToList();
            if (existing.Count > 0)
            {
                var outcome = await BeforePullAsync(existing).ConfigureAwait(true);
                if (outcome.SavedAny)
                {
                    var again = await BuildPlanAsync(project, link, scope, ct).ConfigureAwait(true);
                    if (!again.IsSuccess) return GraftResult<VbaPullResult>.Fail(again.Issues);
                    work = again.Value;
                    pullPaths = PullTargets(work.Plan, restoreDeletePending, adopt);
                }

                foreach (var declined in outcome.Declined)
                {
                    var relative = RelativeOf(project.Root, declined);
                    var index = pullPaths.FindIndex(p => string.Equals(p, relative, StringComparison.OrdinalIgnoreCase));
                    if (index < 0) continue;
                    pullPaths.RemoveAt(index);
                    skipped.Add(relative);
                    issues.Add(GraftIssue.Of(
                        ErrorCode.E702, $"{relative}: 未保存の編集があるため、Excel からの取り込みをスキップしました。",
                        path: relative, severity: Severity.Warning));
                }
            }
        }

        var state = work.State;
        var pulled = new List<string>();
        var now = _clock();
        foreach (var path in pullPaths)
        {
            var module = work.Plan.Modules.First(m => string.Equals(m.RelativePath, path, StringComparison.OrdinalIgnoreCase));
            if (!work.Excel.TryGetValue(path, out var excel)) continue;

            var written = await WriteMirrorFileAsync(project.Root, path, excel.Code, ct).ConfigureAwait(true);
            if (!written.IsSuccess)
            {
                issues.AddRange(written.Issues);
                continue;
            }

            // Pull 後の基準は、ミラーも Excel も取り込んだ本文そのもの（2本とも更新する）。
            var hash = VbaTextNormalizer.Hash(excel.Code);
            state = state.WithModule(path, VbaModuleState.Create(
                module.Kind, module.Component, excel.Caption, hash, hash, VbaTextNormalizer.LooseHash(excel.Code), now));
            pulled.Add(path);
            Logger?.Info("vba-pull", $"Excel から取り込みました: {path}", targetPath: Path.Combine(project.Root, path));
        }

        // I/O を伴わない基準の更新（整形差だけの更新、削除保留の印、記録の破棄）を反映して保存する。
        // 取り込んだモジュールの基準は上で作り直しているので、Pull 対象には計画の Baseline を使わない。
        state = ApplyLocalChanges(work.Plan, state, pulled);
        issues.AddRange(work.Plan.Modules.Where(m => m.Issue is not null).Select(m => m.Issue!));

        // 「Excel 側を採用」と言われた衝突は取り込み済みなので、未解決の衝突から外す。
        var conflicts = work.Plan.Where(VbaSyncAction.Conflict)
            .Select(m => m.RelativePath)
            .Where(p => adopt is null || !adopt.Contains(p))
            .ToList();
        foreach (var conflict in conflicts)
        {
            issues.Add(GraftIssue.Of(
                ErrorCode.E812, $"{conflict}: Excel 側とファイル側の両方が変更されているため、取り込みませんでした。",
                path: conflict, severity: Severity.Warning));
        }

        await SaveStateIfChangedAsync(project.Id, work.State, state, ct).ConfigureAwait(true);
        return GraftResult<VbaPullResult>.Ok(
            new VbaPullResult(pulled, conflicts, skipped) { ConflictItems = ConflictItemsOf(work, conflicts) }, issues);
    }

    /// <summary>衝突したモジュールの、両側の本文つきの一覧（衝突ダイアログの表示用）。</summary>
    private static List<VbaConflictItem> ConflictItemsOf(Work work, IEnumerable<string> conflicts)
    {
        var items = new List<VbaConflictItem>();
        foreach (var path in conflicts)
        {
            var module = work.Plan.Modules.First(m => string.Equals(m.RelativePath, path, StringComparison.OrdinalIgnoreCase));
            work.Excel.TryGetValue(path, out var excel);
            work.MirrorTexts.TryGetValue(path, out var mirror);
            items.Add(new VbaConflictItem(path, module.Kind, module.Component, excel?.Code ?? string.Empty, mirror ?? string.Empty));
        }

        return items;
    }

    private static List<string> PullTargets(VbaSyncPlan plan, bool restoreDeletePending, HashSet<string>? adopt = null)
    {
        var paths = plan.Where(VbaSyncAction.Pull).Select(m => m.RelativePath).ToList();
        if (adopt is not null)
        {
            // 衝突の解決で「Excel 側を採用」と言われたものは、通常の取り込みと同じ経路で Excel の本文を書く。
            paths.AddRange(plan.Where(VbaSyncAction.Conflict).Select(m => m.RelativePath).Where(adopt.Contains));
        }

        if (restoreDeletePending)
        {
            // 削除保留は自動 Pull では復活させない。「Excel から復元」で名指しされたときだけ取り込む。
            paths.AddRange(plan.Where(VbaSyncAction.DeletePending).Select(m => m.RelativePath));
        }

        return paths;
    }

    // ------------------------------------------------------------------
    // Push（ミラー → Excel）
    // ------------------------------------------------------------------

    /// <summary>
    /// ミラー側だけが変わったモジュール（Push 判定）と、ミラーにだけあるモジュール（新規作成）を Excel へ反映する
    /// （7.2b）。衝突は反映せずに結果へ載せる。CP932 で表せない文字を含むモジュールは反映しない（E811）。
    /// </summary>
    /// <param name="only">対象を絞る相対パス。null なら Push 判定の全モジュール。</param>
    /// <param name="overwriteConflicts">
    /// 衝突していても Excel を上書きするモジュール（衝突ダイアログの「Graft 側で上書き」）。上書きの前に、Excel 側の
    /// 本文を <c>vba-sync/&lt;プロジェクトId&gt;.rescue/</c> へ控える。控えを書けなければ、そのモジュールは上書きしない。
    /// </param>
    public async Task<GraftResult<VbaPushResult>> PushAsync(
        Project project, IReadOnlyCollection<string>? only = null,
        IReadOnlyCollection<string>? overwriteConflicts = null, CancellationToken ct = default)
    {
        var link = RequireLink(project);
        await _gate.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            return await PushCoreAsync(project, link, only, Scope(overwriteConflicts), ct).ConfigureAwait(true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<GraftResult<VbaPushResult>> PushCoreAsync(
        Project project, VbaLink link, IReadOnlyCollection<string>? only, HashSet<string>? overwrite, CancellationToken ct)
    {
        var snapshot = await BuildPlanAsync(project, link, Scope(only), ct).ConfigureAwait(true);
        if (!snapshot.IsSuccess) return GraftResult<VbaPushResult>.Fail(snapshot.Issues);
        var work = snapshot.Value;
        var issues = new List<GraftIssue>(snapshot.Issues);
        issues.AddRange(work.Plan.Modules.Where(m => m.Issue is not null).Select(m => m.Issue!));

        var failed = new List<string>();
        var writes = new List<VbaComponentWrite>();
        var writeSources = new Dictionary<string, (VbaModulePlan Plan, string Text)>(StringComparer.OrdinalIgnoreCase);

        foreach (var module in work.Plan.Modules.Where(m => m.Action is VbaSyncAction.Push or VbaSyncAction.PushCreate
                     || (m.Action == VbaSyncAction.Conflict && overwrite is not null && overwrite.Contains(m.RelativePath))))
        {
            var text = work.MirrorTexts[module.RelativePath];

            // Push の直前の保険として、もう一度 CP932 で表せるかを検査する（解析を通らずに変わったモジュール用）。
            var hits = VbaCodePageGuard.Find(text);
            if (hits.Count > 0)
            {
                issues.Add(GraftIssue.Of(
                    ErrorCode.E811,
                    $"{module.RelativePath}: " + string.Join("、", hits.Take(10).Select(h => $"{h.Line}行{h.Column}列 {h.Display}")),
                    path: module.RelativePath));
                failed.Add(module.RelativePath);
                continue;
            }

            if (module.Action == VbaSyncAction.Conflict)
            {
                // 「Graft 側で上書き」: Excel 側の本文はここで失われるので、書く前に必ず控える（7.5）。
                // 控えを残せないまま上書きはしない（利用者の VBE 上の編集が取り戻せなくなるため）。
                var kept = work.Excel.TryGetValue(module.RelativePath, out var current)
                    ? await KeepRescueCopyAsync(project, module.Component, current.Code).ConfigureAwait(true)
                    : "!Excel 側の本文を読めませんでした。";
                if (kept.StartsWith('!'))
                {
                    issues.Add(GraftIssue.Of(
                        ErrorCode.E809,
                        $"{module.RelativePath}: Excel 側の本文を控えに保存できなかったため、上書きしませんでした。{kept[1..]}",
                        path: module.RelativePath));
                    failed.Add(module.RelativePath);
                    continue;
                }

                Logger?.Info("vba-push", $"衝突を「Graft 側で上書き」で解決しました（Excel 側の控え: {kept}）: {module.RelativePath}",
                    targetPath: Path.Combine(project.Root, module.RelativePath));
            }
            else if (module.Action == VbaSyncAction.Push
                     && work.Excel.TryGetValue(module.RelativePath, out var live)
                     && work.State.Modules.TryGetValue(module.RelativePath, out var baseline)
                     && !string.Equals(VbaTextNormalizer.Hash(live.Code), baseline.ExcelHash, StringComparison.Ordinal))
            {
                // 厳密なハッシュは基準と違うのに、計画は「Excel 側は変わっていない（整形差だけ）」と見て Push にした。
                // 整形差の判定は空白・大文字小文字だけを無視する（文字列・コメントの外）ので、多くは VBE の自動整形だが、
                // その判定の取りこぼしが万一あっても Excel 側の本文が控えなしに消えないよう、上書きの前に必ず控える
                // （不具合 2 の「最低限」の保険。控えを残せなければ、衝突の上書きと同じく書かない）。
                var kept = await KeepRescueCopyAsync(project, module.Component, live.Code).ConfigureAwait(true);
                if (kept.StartsWith('!'))
                {
                    issues.Add(GraftIssue.Of(
                        ErrorCode.E809,
                        $"{module.RelativePath}: Excel 側の本文が前回の同期から（整形の差だけだと判断できる範囲で）変わっていましたが、" +
                        $"控えを保存できなかったため、上書きしませんでした。{kept[1..]}",
                        path: module.RelativePath));
                    failed.Add(module.RelativePath);
                    continue;
                }

                Logger?.Info("vba-push", $"Excel 側の本文が前回の同期から変わっているため、上書きの前に控えを残しました（{kept}）: {module.RelativePath}",
                    targetPath: Path.Combine(project.Root, module.RelativePath));
            }

            writes.Add(new VbaComponentWrite(
                module.Component, module.Kind,
                // CRLF にそろえ、末尾の改行は除いて渡す。Excel 側の本文（CodeModule.Lines）は末尾に改行を持たず、
                // 末尾の改行を渡すと VBE の末尾に空行が残りうる。ハッシュは末尾の改行を見ないので比較には影響しない。
                VbaTextNormalizer.Canonicalize(text),
                module.Action == VbaSyncAction.PushCreate));
            writeSources[module.Component] = (module, text);
        }

        var conflicts = work.Plan.Where(VbaSyncAction.Conflict)
            .Select(m => m.RelativePath)
            .Where(p => overwrite is null || !overwrite.Contains(p))
            .ToList();
        foreach (var conflict in conflicts)
        {
            issues.Add(GraftIssue.Of(
                ErrorCode.E812, $"{conflict}: Excel 側とファイル側の両方が変更されているため、反映しませんでした。",
                path: conflict, severity: Severity.Warning));
        }

        var conflictItems = ConflictItemsOf(work, conflicts);
        var deletePending = work.Plan.Where(VbaSyncAction.DeletePending).Select(m => m.RelativePath).ToList();
        var state = work.State;
        var pushed = new List<string>();
        var created = new List<string>();
        var saved = false;

        if (writes.Count > 0)
        {
            var written = await _bridge.WriteComponentsAsync(link.WorkbookFullName, writes, ct).ConfigureAwait(true);
            if (!written.IsSuccess)
            {
                // 1件も書かずに全体が失敗（接続できない・ロック・デザインモードでない・時間切れ）。
                issues.AddRange(written.Issues);
                failed.AddRange(writes.Select(w => writeSources[w.Name].Plan.RelativePath));
                await SaveStateIfChangedAsync(project.Id, work.State, ApplyLocalChanges(work.Plan, state, Array.Empty<string>()), ct)
                    .ConfigureAwait(true);
                return GraftResult<VbaPushResult>.Fail(issues);
            }

            var now = _clock();
            foreach (var outcome in written.Value)
            {
                if (!writeSources.TryGetValue(outcome.Name, out var source)) continue;
                var relative = source.Plan.RelativePath;

                if (outcome.Status is VbaWriteStatus.Written or VbaWriteStatus.Created)
                {
                    // 基準: mirrorHash は書いたミラー、excelHash／excelLooseHash は読み直した Excel 側。
                    // ミラーは書き換えない（整形差はここで吸収する。クラスコメント参照）。
                    var readBack = outcome.ReadBackCode ?? source.Text;
                    state = state.WithModule(relative, VbaModuleState.Create(
                        source.Plan.Kind, source.Plan.Component, ExistingCaption(state, relative),
                        VbaTextNormalizer.Hash(source.Text), VbaTextNormalizer.Hash(readBack),
                        VbaTextNormalizer.LooseHash(readBack), now));
                    (outcome.Status == VbaWriteStatus.Created ? created : pushed).Add(relative);
                    if (outcome.Issue is not null) issues.Add(outcome.Issue);
                    Logger?.Info("vba-push", $"Excel へ反映しました: {relative}", targetPath: Path.Combine(project.Root, relative));
                    continue;
                }

                failed.Add(relative);
                if (outcome.Issue is not null)
                {
                    issues.Add(await AttachRescueAsync(project, outcome).ConfigureAwait(true));
                }
            }

            if (link.SaveWorkbookAfterPush && pushed.Count + created.Count > 0)
            {
                var save = await _bridge.SaveWorkbookAsync(link.WorkbookFullName, ct).ConfigureAwait(true);
                saved = save.IsSuccess;
                if (!save.IsSuccess) issues.AddRange(save.Issues);
            }
        }

        state = ApplyLocalChanges(work.Plan, state, pushed.Concat(created).ToList());
        await SaveStateIfChangedAsync(project.Id, work.State, state, ct).ConfigureAwait(true);
        return GraftResult<VbaPushResult>.Ok(
            new VbaPushResult(pushed, created, failed, conflicts, saved) { ConflictItems = conflictItems, DeletePending = deletePending },
            issues);
    }

    /// <summary>
    /// Excel 側の本文を控えのフォルダへ書く。成功したらそのパス、失敗したら <c>!</c> で始まる理由を返す
    /// （戻り値を文字列1つで返すのは、呼び出し側が「控えを残せたか」だけを見るため）。
    /// </summary>
    private async Task<string> KeepRescueCopyAsync(Project project, string component, string code)
    {
        try
        {
            var directory = _paths.GetVbaRescueDirectory(project.Id);
            Directory.CreateDirectory(directory);
            var fileName = VbaRescueNaming.BuildFileName(component, _clock());
            var path = Path.Combine(directory, fileName);
            for (var n = 2; File.Exists(path); n++)
            {
                path = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(fileName)}_{n}.txt");
            }

            await File.WriteAllTextAsync(path, code, Utf8NoBom).ConfigureAwait(true);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "!" + ExceptionMessages.Describe(ex);
        }
    }

    // ------------------------------------------------------------------
    // 検査（何も書かない）
    // ------------------------------------------------------------------

    /// <summary>
    /// Excel とミラーを突き合わせるだけで、何も書かずに結果を返す（仕様 7.3-1・7.3-3）。解析の前に
    /// 「取り込むべき変更があるか」「衝突しているか」を知り、確認を挟むかどうかを決めるために使う。
    /// </summary>
    /// <param name="only">対象を絞る相対パス。null なら全モジュール。</param>
    public async Task<GraftResult<VbaSyncCheck>> CheckAsync(
        Project project, IReadOnlyCollection<string>? only = null, CancellationToken ct = default)
    {
        var link = RequireLink(project);
        await _gate.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            var snapshot = await BuildPlanAsync(project, link, Scope(only), ct).ConfigureAwait(true);
            if (!snapshot.IsSuccess) return GraftResult<VbaSyncCheck>.Fail(snapshot.Issues);

            var work = snapshot.Value;
            var conflicts = work.Plan.Where(VbaSyncAction.Conflict).Select(m => m.RelativePath).ToList();
            var push = work.Plan.Modules
                .Where(m => m.Action is VbaSyncAction.Push or VbaSyncAction.PushCreate)
                .Select(m => m.RelativePath)
                .ToList();
            return GraftResult<VbaSyncCheck>.Ok(
                new VbaSyncCheck(work.Plan.Where(VbaSyncAction.Pull).Select(m => m.RelativePath).ToList(), push, ConflictItemsOf(work, conflicts)),
                snapshot.Issues);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 控えを書き戻せなかった失敗（E809 と <see cref="VbaWriteOutcome.BackupCode"/>）の本文を
    /// <c>vba-sync/&lt;プロジェクトId&gt;.rescue/</c> へ書き出し、パスをエラーの詳細に添える（仕様 5.5-3）。
    /// ブリッジはプロジェクトの Id もデータ保存先も知らないので、ここで書く。
    /// </summary>
    private async Task<GraftIssue> AttachRescueAsync(Project project, VbaWriteOutcome outcome)
    {
        var issue = outcome.Issue!;
        if (outcome.BackupCode is null) return issue;

        try
        {
            var directory = _paths.GetVbaRescueDirectory(project.Id);
            Directory.CreateDirectory(directory);
            var fileName = VbaRescueNaming.BuildFileName(outcome.Name, _clock());
            var path = Path.Combine(directory, fileName);
            // 同じ秒に同じモジュールを控えた場合は、連番を付けて上書きを避ける。
            for (var n = 2; File.Exists(path); n++)
            {
                path = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(fileName)}_{n}.txt");
            }

            await File.WriteAllTextAsync(path, outcome.BackupCode, Utf8NoBom).ConfigureAwait(true);
            return issue with { Detail = $"{issue.Detail} 控えの保存先: {path}" };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return issue with { Detail = $"{issue.Detail} 控えを保存できませんでした: {ExceptionMessages.Describe(ex)}" };
        }
    }

    // ------------------------------------------------------------------
    // ミラーだけから分かる状態
    // ------------------------------------------------------------------

    /// <summary>
    /// Excel に接続せずに、未反映（Push 判定）と削除保留の件数を数える。ミラーと同期基準だけを比べる。
    /// </summary>
    public async Task<VbaLocalStatus> GetLocalStatusAsync(Project project, CancellationToken ct = default)
    {
        var link = RequireLink(project);
        var mirror = await ReadMirrorAsync(project.Root, ct).ConfigureAwait(true);
        var loaded = await _states.LoadAsync(project.Id, link.WorkbookFullName, ct).ConfigureAwait(true);
        var records = loaded.Value.Modules;

        var unpushed = 0;
        var pending = 0;
        foreach (var (key, record) in records)
        {
            if (!mirror.Modules.TryGetValue(key, out var module))
            {
                // ミラーのファイルが無いのに記録がある＝削除保留。
                pending++;
                continue;
            }

            if (!string.Equals(module.Hash, record.MirrorHash, StringComparison.Ordinal)) unpushed++;
        }

        // 記録がなく、ミラーにだけあるモジュール（新規作成の Push 待ち）も未反映。
        unpushed += mirror.Modules.Values.Count(m => !records.ContainsKey(m.RelativePath) && VbaModuleKinds.CanCreateOnExcel(m.Kind));
        return new VbaLocalStatus(unpushed, pending);
    }

    /// <summary>
    /// 既に存在するコンポーネントの名前と種別を返す（ミラーのファイルと、読めれば Excel のコンポーネント）。
    /// 新しく作るファイルが「同名で種別違い」にならないかを、適用の前に確かめる（<see cref="VbaApplyGuard.CheckNameCollisions"/>）
    /// のに使う。ファイルの中身は読まず、名前だけを見る。Excel を読めなければ（閉じている・応答しない）ミラーだけで答える。
    /// 反映の段階の検査（<see cref="VbaSyncPlanner"/>）が最後の砦として残っているので、ここは取りこぼしてよい。
    /// </summary>
    public async Task<IReadOnlyList<VbaComponentRef>> ListKnownComponentsAsync(Project project, CancellationToken ct = default)
    {
        var link = RequireLink(project);
        var known = new List<VbaComponentRef>();
        if (Directory.Exists(project.Root))
        {
            foreach (var file in Directory.EnumerateFiles(project.Root, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(project.Root, file).Replace('\\', '/');
                if (relative.Split('/').Any(segment => segment.StartsWith('.'))) continue;
                if (VbaMirrorLayout.TryParse(relative, out var parsed, out _) && parsed is not null)
                {
                    known.Add(new VbaComponentRef(parsed.ComponentName, parsed.Kind));
                }
            }
        }

        var excel = await _bridge.ReadComponentsAsync(link.WorkbookFullName, null, ct).ConfigureAwait(true);
        if (excel.IsSuccess) known.AddRange(excel.Value.Select(c => new VbaComponentRef(c.Name, c.Kind)));
        return known;
    }

    // ------------------------------------------------------------------
    // 計画の組み立て
    // ------------------------------------------------------------------

    private sealed record Work(
        VbaSyncPlan Plan, VbaSyncState State,
        IReadOnlyDictionary<string, VbaComponentSnapshot> Excel,
        IReadOnlyDictionary<string, string> MirrorTexts);

    private sealed class MirrorContents
    {
        public Dictionary<string, VbaMirrorModule> Modules { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> Texts { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<GraftIssue> Issues { get; } = new();
    }

    private async Task<GraftResult<Work>> BuildPlanAsync(
        Project project, VbaLink link, HashSet<string>? scope, CancellationToken ct)
    {
        var excel = await _bridge.ReadComponentsAsync(link.WorkbookFullName, null, ct).ConfigureAwait(true);
        if (!excel.IsSuccess) return GraftResult<Work>.Fail(excel.Issues);

        var mirror = await ReadMirrorAsync(project.Root, ct).ConfigureAwait(true);
        var loaded = await _states.LoadAsync(project.Id, link.WorkbookFullName, ct).ConfigureAwait(true);

        var excelByPath = new Dictionary<string, VbaComponentSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var component in excel.Value)
        {
            if (!VbaNameRules.CanMirror(component.Name)) continue; // 計画側で E810 にするため、別に数える。
            excelByPath[VbaMirrorLayout.ToRelativePath(component.Kind, component.Name)] = component;
        }

        bool InScope(string key) => scope is null || scope.Contains(key);

        var excelModules = excel.Value
            .Where(c => !VbaNameRules.CanMirror(c.Name) || InScope(VbaMirrorLayout.ToRelativePath(c.Kind, c.Name)))
            .Select(c => new VbaExcelModule(
                c.Name, c.Kind, VbaTextNormalizer.Hash(c.Code), VbaTextNormalizer.LooseHash(c.Code), c.Caption))
            .ToList();
        var mirrorModules = mirror.Modules.Values.Where(m => InScope(m.RelativePath)).ToList();

        var state = loaded.Value;
        var scopedState = scope is null
            ? state
            : state with { Modules = state.Modules.Where(kv => scope.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value) };

        var plan = VbaSyncPlanner.Plan(mirrorModules, excelModules, scopedState, _clock());

        var issues = new List<GraftIssue>(excel.Issues);
        issues.AddRange(loaded.Issues);
        issues.AddRange(mirror.Issues);

        return GraftResult<Work>.Ok(new Work(plan, state, excelByPath, mirror.Texts), issues);
    }

    /// <summary>ミラーのフォルダを走査して、同期の対象のファイルを読む。対象外は E810 の警告にする。</summary>
    private static async Task<MirrorContents> ReadMirrorAsync(string root, CancellationToken ct)
    {
        var contents = new MirrorContents();
        if (!Directory.Exists(root)) return contents;

        var relatives = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(r => !r.Split('/').Any(segment => segment.StartsWith('.')))
            .ToList();
        contents.Issues.AddRange(VbaMirrorLayout.CollectUnsupported(relatives));

        foreach (var relative in relatives)
        {
            if (!VbaMirrorLayout.TryParse(relative, out var parsed, out _) || parsed is null) continue;

            var read = await FileTextIO.ReadAsync(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)), ct)
                .ConfigureAwait(true);
            if (!read.IsSuccess)
            {
                contents.Issues.AddRange(read.Issues.Select(i => i with { Severity = Severity.Warning }));
                continue;
            }

            var text = read.Value.Text;
            contents.Modules[parsed.RelativePath] = new VbaMirrorModule(
                parsed.RelativePath, parsed.Kind, parsed.ComponentName,
                VbaTextNormalizer.Hash(text), VbaTextNormalizer.LooseHash(text));
            contents.Texts[parsed.RelativePath] = text;
        }

        return contents;
    }

    /// <summary>
    /// 実行の済んだモジュール以外の、I/O を伴わない基準の更新（整形差だけの更新、削除保留、記録の破棄）を
    /// 反映する。Pull・Push で基準を作り直したモジュールは、計画の Baseline で上書きしない。
    /// </summary>
    private static VbaSyncState ApplyLocalChanges(VbaSyncPlan plan, VbaSyncState state, IReadOnlyCollection<string> executed)
    {
        foreach (var module in plan.Modules)
        {
            if (executed.Contains(module.RelativePath, StringComparer.OrdinalIgnoreCase)) continue;
            if (module.Action == VbaSyncAction.DiscardRecord)
            {
                state = state.WithoutModule(module.RelativePath);
            }
            else if (module.Baseline is not null)
            {
                state = state.WithModule(module.RelativePath, module.Baseline);
            }
        }

        return state;
    }

    private async Task SaveStateIfChangedAsync(string projectId, VbaSyncState before, VbaSyncState after, CancellationToken ct)
    {
        if (!StatesEqual(before, after)) await _states.SaveAsync(projectId, after, ct).ConfigureAwait(true);
    }

    private static bool StatesEqual(VbaSyncState a, VbaSyncState b)
    {
        if (a.Modules.Count != b.Modules.Count) return false;
        foreach (var (key, value) in a.Modules)
        {
            if (!b.Modules.TryGetValue(key, out var other) || other != value) return false;
        }

        return string.Equals(a.WorkbookFullName, b.WorkbookFullName, StringComparison.Ordinal);
    }

    private static string? ExistingCaption(VbaSyncState state, string relative)
        => state.Modules.TryGetValue(relative, out var record) ? record.Caption : null;

    // ------------------------------------------------------------------
    // ミラーのファイル I/O
    // ------------------------------------------------------------------

    /// <summary>新規のミラーのファイル: UTF-8（BOM なし）・CRLF・末尾に CRLF を1つ（仕様 4.2・6.3）。</summary>
    private static async Task<GraftResult<bool>> WriteNewMirrorFileAsync(
        string root, string relative, string code, CancellationToken ct)
    {
        var shape = new TextShape { Encoding = Utf8NoBom, HasBom = false, NewLine = "\r\n", EndsWithNewLine = true };
        return await WriteShapedAsync(root, relative, code, shape, ct).ConfigureAwait(true);
    }

    /// <summary>
    /// Pull の書き込み。既存のファイルは、文字コード・改行・末尾改行の有無を保つ（<c>FileTextIO.ReadAsync</c> が返す
    /// <see cref="TextShape"/> をそのまま渡す）。新規は <see cref="WriteNewMirrorFileAsync"/> の形。
    /// <c>NewFileEncoding</c> などの設定は使わない（Shift_JIS や BOM 付きになりうるため）。
    /// </summary>
    private static async Task<GraftResult<bool>> WriteMirrorFileAsync(
        string root, string relative, string code, CancellationToken ct)
    {
        var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(full))
        {
            var existing = await FileTextIO.ReadAsync(full, ct).ConfigureAwait(true);
            if (existing.IsSuccess)
            {
                return await WriteShapedAsync(root, relative, code, existing.Value.Shape, ct).ConfigureAwait(true);
            }
        }

        return await WriteNewMirrorFileAsync(root, relative, code, ct).ConfigureAwait(true);
    }

    private static async Task<GraftResult<bool>> WriteShapedAsync(
        string root, string relative, string code, TextShape shape, CancellationToken ct)
    {
        var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return GraftResult<bool>.Fail(ErrorCode.E204, ExceptionMessages.Describe(ex), path: relative);
        }

        var body = VbaTextNormalizer.ToCrLf(code).TrimEnd('\r', '\n');
        var text = shape.NewLine == "\r\n" ? body : body.Replace("\r\n", shape.NewLine);

        // Excel の本文は末尾に改行を持たない。ファイルの末尾改行の有無は既存のものを保つ（空のモジュールは空のまま）。
        if (body.Length > 0 && shape.EndsWithNewLine) text += shape.NewLine;

        return await FileTextIO.WriteAsync(full, text, shape, ct).ConfigureAwait(true);
    }

    // ------------------------------------------------------------------
    // 小さな補助
    // ------------------------------------------------------------------

    private static VbaLink RequireLink(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return project.Vba ?? throw new InvalidOperationException("VBA プロジェクトではありません。");
    }

    private static HashSet<string>? Scope(IReadOnlyCollection<string>? only)
        => only is null ? null : new HashSet<string>(only.Select(p => p.Replace('\\', '/')), StringComparer.OrdinalIgnoreCase);

    private static string RelativeOf(string root, string fullPath)
        => Path.GetRelativePath(root, fullPath).Replace('\\', '/');
}
