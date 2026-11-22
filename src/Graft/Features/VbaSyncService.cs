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

    /// <summary>VBE が空白・大文字小文字を整形しただけで書けたモジュールのコンポーネント名（課題にはしない）。</summary>
    public IReadOnlyList<string> Reformatted { get; init; } = Array.Empty<string>();

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
public sealed record VbaLocalStatus(int UnpushedCount, int DeletePendingCount)
{
    /// <summary>
    /// 要確認（書き戻しに失敗したままの）モジュールの数。件数だけを比べる呼び出し側のために、等価の比較には含めない
    /// （<see cref="UnpushedPaths"/> と同じ扱い）。既定は 0。
    /// </summary>
    public int NeedsReviewCount { get; init; }

    /// <summary>
    /// 未反映のモジュールの相対パス（<see cref="UnpushedCount"/> の中身）。数えていないとき（読めなかったときの前回の値など）は null。
    /// 保存時の自動反映が付けた「衝突 N」の印を、未反映でなくなったモジュールについて外すために使う。
    /// 件数だけを比べる呼び出し側のために、<b>等価の比較には含めない</b>（<c>VbaLocalStatus(0, 0)</c> と比べられる）。
    /// </summary>
    public IReadOnlyList<string>? UnpushedPaths { get; init; }

    /// <inheritdoc />
    public bool Equals(VbaLocalStatus? other)
        => other is not null && UnpushedCount == other.UnpushedCount && DeletePendingCount == other.DeletePendingCount;

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(UnpushedCount, DeletePendingCount);
}

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
        // 控え（仕様書_ブックの情報と依頼の観点 7章）。ミラーへ書くときに Push・Pull と同じゲートで直列にするため、
        // このサービスのゲートを渡す。
        Rescue = new VbaRescueService(paths, states, _clock, _gate);
    }

    /// <summary>
    /// Excel 側の控え（<c>.rescue/</c>）の窓口。反映前の自動控え（<see cref="PushAsync"/> が自動で残す）の一覧・差分・
    /// ミラーへの取り込み・掃除を担う。世代数・容量の設定（<c>Vba.RescueMaxGenerations</c>・<c>Vba.RescueMaxTotalMB</c>）は
    /// <c>MainViewModel</c> がここへ渡す。
    /// </summary>
    public VbaRescueService Rescue { get; }

    private Logger? _logger;

    /// <summary>Pull や Push を記録する窓口（<c>MainViewModel</c> が渡す）。</summary>
    public Logger? Logger
    {
        get => _logger;
        set
        {
            _logger = value;
            Rescue.Logger = value;
        }
    }

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
        var adopt = Scope(adoptConflicts);

        // 【ゲートを持ったまま利用者のダイアログを待たない（疑わしい点の修正）】 以前は、ゲートを持ったまま未保存の確認
        // （BeforePullAsync）を待っていた。利用者が確認を放置している間、ほかの取込・反映・検査が全部待たされる。
        // そこで「ゲートの中で計画を作り、確認の要るファイルが見つかったらゲートを手放して確認し、もう一度ゲートの中で
        // 計画を作り直して実行する」を繰り返す。確認を待つ間に Excel やミラーが変わっても、実行するのは常に
        // ゲートの中で作った最新の計画。確認を済ませたファイルは覚えておき、同じファイルを二度聞かない。
        var confirmed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var declined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var round = 0; ; round++)
        {
            IReadOnlyList<string> toConfirm;
            await _gate.WaitAsync(ct).ConfigureAwait(true);
            try
            {
                // 何度確認しても新しい未確認のファイルが現れ続けるときは、打ち切って、未確認のものは取り込まない。
                var step = await PullCoreAsync(
                    project, link, only, restoreDeletePending, adopt, confirmed, declined, giveUpConfirming: round >= MaxPullConfirmRounds, ct)
                    .ConfigureAwait(true);
                if (step.Result is not null) return step.Result;
                toConfirm = step.NeedsConfirmation!;
            }
            finally
            {
                _gate.Release();
            }

            // ゲートの外で確認する（保存するとミラーが変わるので、戻ったら計画を作り直す）。
            var outcome = await BeforePullAsync!(toConfirm).ConfigureAwait(true);
            foreach (var path in toConfirm) confirmed.Add(path);
            foreach (var path in outcome.Declined) declined.Add(path);
        }
    }

    private const int MaxPullConfirmRounds = 5;

    /// <summary>Pull の 1 回分の結果。未保存の確認が要るなら <see cref="NeedsConfirmation"/>、そうでなければ <see cref="Result"/>。</summary>
    private sealed record PullStep(GraftResult<VbaPullResult>? Result, IReadOnlyList<string>? NeedsConfirmation);

    private async Task<PullStep> PullCoreAsync(
        Project project, VbaLink link, IReadOnlyCollection<string>? only, bool restoreDeletePending,
        HashSet<string>? adopt, HashSet<string> confirmed, HashSet<string> declined, bool giveUpConfirming, CancellationToken ct)
    {
        var scope = Scope(only);
        var snapshot = await BuildPlanAsync(project, link, scope, ct).ConfigureAwait(true);
        if (!snapshot.IsSuccess) return new PullStep(GraftResult<VbaPullResult>.Fail(snapshot.Issues), null);
        var work = snapshot.Value;
        var issues = new List<GraftIssue>(snapshot.Issues);

        var skipped = new List<string>();
        var pullPaths = PullTargets(work.Plan, restoreDeletePending, adopt);

        // 取り込み先に未保存の編集があれば、先に保存してもらう（ゲートの外で。PullAsync 参照）。
        if (BeforePullAsync is not null)
        {
            var existing = pullPaths
                .Select(p => Path.Combine(project.Root, p.Replace('/', Path.DirectorySeparatorChar)))
                .Where(File.Exists)
                .ToList();
            var unconfirmed = existing.Where(f => !confirmed.Contains(f)).ToList();
            if (unconfirmed.Count > 0)
            {
                if (!giveUpConfirming) return new PullStep(null, unconfirmed);

                // 確認が収まらない。安全側に、確認できていないファイルは取り込まない。
                foreach (var file in unconfirmed) declined.Add(file);
            }

            foreach (var declinedFile in declined)
            {
                var relative = RelativeOf(project.Root, declinedFile);
                var index = pullPaths.FindIndex(p => string.Equals(p, relative, StringComparison.OrdinalIgnoreCase));
                if (index < 0) continue;
                pullPaths.RemoveAt(index);
                skipped.Add(relative);
                issues.Add(GraftIssue.Of(
                    ErrorCode.E702, $"{relative}: 未保存の編集があるため、Excel からの取り込みをスキップしました。",
                    path: relative, severity: Severity.Warning));
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
                ErrorCode.E812, $"{conflict}: {ConflictSentence(work, conflict, "取り込みませんでした")}",
                path: conflict, severity: Severity.Warning));
        }

        await SaveStateIfChangedAsync(project.Id, work.State, state, ct).ConfigureAwait(true);
        return new PullStep(
            GraftResult<VbaPullResult>.Ok(
                new VbaPullResult(pulled, conflicts, skipped) { ConflictItems = ConflictItemsOf(work, conflicts) }, issues),
            null);
    }

    /// <summary>衝突の理由に応じた警告の文（E812）。<paramref name="outcome"/> は「取り込みませんでした」「反映しませんでした」。</summary>
    private static string ConflictSentence(Work work, string path, string outcome)
    {
        var reason = work.Plan.Modules
            .First(m => string.Equals(m.RelativePath, path, StringComparison.OrdinalIgnoreCase)).ConflictReason;
        return reason switch
        {
            VbaConflictReason.ExcelEmpty =>
                $"Excel 側のモジュールが空（0 行）で、ファイル側には内容があるため、{outcome}。空のまま取り込むとファイルも空になります。どちらを残すか選んでください。",
            VbaConflictReason.NeedsReview =>
                $"前回の書き込みで Excel 側を元に戻せなかった「要確認」のモジュールのため、{outcome}。Excel 側の内容を VBE で確かめて、どちらを残すか選んでください。",
            _ => $"Excel 側とファイル側の両方が変更されているため、{outcome}。",
        };
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
            items.Add(new VbaConflictItem(path, module.Kind, module.Component, excel?.Code ?? string.Empty, mirror ?? string.Empty)
            {
                Reason = module.ConflictReason,
            });
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
        BeginWriting();
        try
        {
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
        finally
        {
            EndWriting();
        }
    }

    /// <summary>
    /// 保存時の自動反映（機能 D。仕様書_ブックの情報と依頼の観点 8章）。保存した<b>モジュールだけ</b>を Excel から読んで
    /// 反映する。衝突は送らずに結果へ載せる（ダイアログは出さない。呼び出し側がバッジの印にする）。
    /// <para>
    /// 【<see cref="PushAsync"/> と分けた理由】 <see cref="PushAsync"/> は <paramref name="only"/> で対象を絞っても、
    /// Excel の全モジュールとミラーの全ファイルを読む（計画を全体で作るため）。保存のたびにそれをすると、約 100 モジュールの
    /// ブックで Excel を数秒占有し、利用者の操作を妨げる（8.4）。ここでは、対象のモジュールの名前だけを
    /// <see cref="IVbaBridge.ReadComponentsAsync"/> に渡し、ミラーも対象のファイルだけ読む。
    /// 手動の「Excel へ反映」・適用後の Push は従来どおり全体を読む（この絞り込みは D だけが使う）。
    /// </para>
    /// <para>
    /// 反映の前の自動控え（7章）・基準の更新・<c>SaveWorkbookAfterPush</c> は、同じ <see cref="PushCoreAsync"/> を通るので
    /// 手動の反映と同じに働く。<see cref="IsWritingToExcel"/> の旗も立てるので、ウィンドウを閉じる確認などの既存の排他と整合する。
    /// </para>
    /// </summary>
    /// <param name="saved">保存したモジュールの相対パス（<c>Modules/Module1.bas</c>）。</param>
    public async Task<GraftResult<VbaPushResult>> PushSavedAsync(
        Project project, IReadOnlyCollection<string> saved, CancellationToken ct = default)
    {
        var link = RequireLink(project);
        BeginWriting();
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(true);
            try
            {
                return await PushCoreAsync(project, link, saved, null, ct, narrowRead: true).ConfigureAwait(true);
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            EndWriting();
        }
    }

    // 「Excel へ書いている最中」の旗（不具合 9）。Push の呼び出しの数を数える（ゲートの順番待ちも含める: 順番が来れば
    // すぐ書くため）。適用中の旗（MainViewModel の _isApplyInProgress）は適用の全体を覆うが、取り消し・復元の後の反映、
    // プロジェクトペインの手動の反映、コンテキスト収集の「Graft 側で上書き」は適用ではないので、ウィンドウを閉じる確認の
    // 待ち・プロジェクトペインのコマンド・設定の反映の保留は、この旗を見る。書き込みの途中（消したあと・書く前）で
    // プロセスが終わるとモジュールが空のまま残り、反映中のプロジェクトの削除はミラーが消える途中で同期状態を書いてしまう。
    private int _writing;

    /// <summary>いま Excel へ書いている最中（または書く順番を待っている）か。</summary>
    public bool IsWritingToExcel => Volatile.Read(ref _writing) > 0;

    /// <summary><see cref="IsWritingToExcel"/> が変わったとき（呼び出し側の同期コンテキストで）。</summary>
    public event EventHandler? WritingChanged;

    private void BeginWriting()
    {
        if (Interlocked.Increment(ref _writing) == 1) WritingChanged?.Invoke(this, EventArgs.Empty);
    }

    private void EndWriting()
    {
        if (Interlocked.Decrement(ref _writing) == 0) WritingChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task<GraftResult<VbaPushResult>> PushCoreAsync(
        Project project, VbaLink link, IReadOnlyCollection<string>? only, HashSet<string>? overwrite, CancellationToken ct,
        bool narrowRead = false)
    {
        var snapshot = await BuildPlanAsync(project, link, Scope(only), ct, narrowRead).ConfigureAwait(true);
        if (!snapshot.IsSuccess) return GraftResult<VbaPushResult>.Fail(snapshot.Issues);
        var work = snapshot.Value;
        var issues = new List<GraftIssue>(snapshot.Issues);
        issues.AddRange(work.Plan.Modules.Where(m => m.Issue is not null).Select(m => m.Issue!));

        var failed = new List<string>();
        var writes = new List<VbaComponentWrite>();
        var writeSources = new Dictionary<string, (VbaModulePlan Plan, string Text)>(StringComparer.OrdinalIgnoreCase);
        // 反映前の自動控え（7.2）。上書きされる各モジュールの、いま Excel にある本文。
        var autoCopies = new List<(VbaModulePlan Plan, string ExcelCode)>();

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
                        $"{module.RelativePath}: Excel 側の本文を控えに{VbaSaveNoticeThrottle.NotOverwrittenPhrase}。{kept[1..]}",
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
                        $"控えを{VbaSaveNoticeThrottle.NotOverwrittenPhrase}。{kept[1..]}",
                        path: module.RelativePath));
                    failed.Add(module.RelativePath);
                    continue;
                }

                Logger?.Info("vba-push", $"Excel 側の本文が前回の同期から変わっているため、上書きの前に控えを残しました（{kept}）: {module.RelativePath}",
                    targetPath: Path.Combine(project.Root, module.RelativePath));
            }
            else if (module.Action == VbaSyncAction.Push && work.Excel.TryGetValue(module.RelativePath, out var overwritten))
            {
                // 通常の反映: 上書きされる前の Excel 側の本文を、毎回自動控えにする。本文は計画のために既に読んである
                // ものを使う（追加の COM 呼び出しはしない）。上の 2 つの分岐は従来の控え（Legacy）をすでに残しているので、
                // 同じ内容を重ねて作らない。PushCreate は残す本文が無いので対象外。
                autoCopies.Add((module, overwritten.Code));
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
                ErrorCode.E812, $"{conflict}: {ConflictSentence(work, conflict, "反映しませんでした")}",
                path: conflict, severity: Severity.Warning));
        }

        var conflictItems = ConflictItemsOf(work, conflicts);
        var deletePending = work.Plan.Where(VbaSyncAction.DeletePending).Select(m => m.RelativePath).ToList();
        var state = work.State;
        var pushed = new List<string>();
        var created = new List<string>();
        var mayBeBroken = new List<string>();
        var reformatted = new List<string>();
        var saved = false;

        // 【全モジュールの控えを書き終えてから Excel へ書く】 書き始めたあとに控えを書くと、途中の失敗で
        // 上書き済みのモジュールだけ控えが無くなる。控えを書けなくても反映は止めない（E816 は警告）: 通常の反映では
        // Excel 側の本文は直前の同期と同じことがほとんどで（変わっていれば衝突として別に止まる）、控えを書けない
        // だけで作業を止めるのは釣り合わないため。衝突の上書きだけは上で、書けなければ上書きしない。
        if (writes.Count > 0 && autoCopies.Count > 0)
        {
            await KeepAutoCopiesAsync(project, autoCopies, writeSources, issues, throttlePrune: narrowRead).ConfigureAwait(true);
        }

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

                if (outcome.TrimmedLines is { Count: > 0 } trimmedLines)
                {
                    // 書き込み・書き戻しのあとに末尾へ余分な行が付いていたので取り除いた（CodeModuleRewriter 参照）。
                    // VBE の挙動に由来する異常なので、内容つきで残す。反映の成否は変えない軽い警告（E809 にはしない）。
                    Logger?.Warn(
                        "vba-push", $"{relative}: 書き込みのあと、モジュールの末尾に余分な行が付いたため取り除きました: {new VbaTrailingExtra(0, trimmedLines).Describe()}",
                        targetPath: Path.Combine(project.Root, relative));
                }

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
                    if (outcome.Reformatted) reformatted.Add(source.Plan.Component);
                    if (outcome.Issue is not null) issues.Add(outcome.Issue);
                    Logger?.Info("vba-push", $"Excel へ反映しました: {relative}", targetPath: Path.Combine(project.Root, relative));
                    continue;
                }

                failed.Add(relative);

                if (outcome.IsRollbackFailed)
                {
                    // 書き戻しに失敗した。Excel 側の本文はもう基準のハッシュと対応しないので、「要確認」を同期状態に残す
                    // （以後の自動の取込・反映は、衝突として利用者に選ばせる。VbaSyncPlanner 参照）。
                    state = state.WithModule(relative, MarkNeedsReview(state, source.Plan, now));
                }

                // 書き戻しに失敗した（E809）、または時間切れ・中断で状態が分からない（E805）モジュールは、Excel 側の本文が
                // 空のまま・途中のまま残っているかもしれない。書き戻せた（RolledBack）ものや、書く前に止めたもの
                // （E808・E811）は元のままなので、保存してよい。
                if (outcome.Status == VbaWriteStatus.Failed && outcome.Issue?.Code is ErrorCode.E809 or ErrorCode.E805)
                {
                    mayBeBroken.Add(relative);
                }

                if (outcome.Issue is not null)
                {
                    issues.Add(await AttachRescueAsync(project, outcome).ConfigureAwait(true));
                }
            }

            if (reformatted.Count > 0)
            {
                // 【整形差は 1 回の反映につき 1 行の info】 VBE の整形は通常の動きなので、モジュールごとの警告にしない
                // （15 モジュールで 15 件の警告が並び、本当の失敗と見分けにくかった）。
                Logger?.Info(
                    "vba-push", $"VBE が {reformatted.Count} 個のモジュールを整形しました（{string.Join("、", reformatted)}）",
                    targetPath: project.Root);
            }

            if (link.SaveWorkbookAfterPush && pushed.Count + created.Count > 0)
            {
                if (mayBeBroken.Count > 0)
                {
                    // 不具合 4: 壊れたまま・状態不明のモジュールがあるのにブックを保存すると、「保存せずに閉じれば元に戻る」
                    // という最後の逃げ道が無くなる。保存せず、その旨を警告に添える（控えは .rescue/ にある）。
                    issues.Add(GraftIssue.Of(
                        ErrorCode.E809,
                        $"書き戻しに失敗した、または状態が分からないモジュールがあるため、ブックを保存しませんでした（{string.Join("、", mayBeBroken)}）。" +
                        "VBE で内容を確かめてください。保存せずにブックを閉じれば、Excel 側は反映前の状態に戻ります。",
                        severity: Severity.Warning));
                }
                else
                {
                    var save = await _bridge.SaveWorkbookAsync(link.WorkbookFullName, ct).ConfigureAwait(true);
                    saved = save.IsSuccess;
                    if (!save.IsSuccess) issues.AddRange(save.Issues);
                }
            }
        }

        state = ApplyLocalChanges(work.Plan, state, pushed.Concat(created).ToList());
        await SaveStateIfChangedAsync(project.Id, work.State, state, ct).ConfigureAwait(true);
        return GraftResult<VbaPushResult>.Ok(
            new VbaPushResult(pushed, created, failed, conflicts, saved) { ConflictItems = conflictItems, DeletePending = deletePending, Reformatted = reformatted },
            issues);
    }

    /// <summary>
    /// Excel 側の本文を控えのフォルダへ書く。成功したらそのパス、失敗したら <c>!</c> で始まる理由を返す
    /// （戻り値を文字列1つで返すのは、呼び出し側が「控えを残せたか」だけを見るため）。
    /// </summary>
    private async Task<string> KeepRescueCopyAsync(Project project, string component, string code)
    {
        var saved = await Rescue.SaveAsync(project, component, code, VbaRescueKind.Legacy).ConfigureAwait(true);
        return saved.Status == VbaRescueSaveStatus.Failed ? "!" + saved.Failure : saved.Path!;
    }

    /// <summary>保存時の自動反映での、控えの掃除の最小の間隔。</summary>
    private static readonly TimeSpan AutoPrunePeriod = TimeSpan.FromSeconds(30);

    // プロジェクト Id → 最後に掃除した時刻（保存時の自動反映の間引き用。メモリだけ。ゲートの中でしか触らない）。
    private readonly Dictionary<string, DateTimeOffset> _lastAutoPrune = new(StringComparer.Ordinal);

    /// <summary>
    /// 反映前の自動控えを書く（仕様書_ブックの情報と依頼の観点 7.2）。同じモジュールの最新の自動控えと本文が同じなら書かない。
    /// 書けなかったモジュールは、E816（警告）にして反映は続ける。書いたあとに、世代数・容量の掃除をする
    /// （掃除の失敗は反映の結果に影響させず、ログに残すだけ）。
    /// </summary>
    /// <param name="throttlePrune">
    /// true なら、掃除（控えのフォルダの走査と削除）を <see cref="AutoPrunePeriod"/> に 1 回までに間引く（保存時の自動反映用）。
    /// <para>
    /// 【間引く理由】 保存時の自動反映は、保存のたびに反映するので、反映のたびに控えのフォルダ全体を走査すると、
    /// 控えが数百件溜まったフォルダで、保存のたびにディレクトリ走査とファイルサイズの取得が走る。控えの世代数・容量の上限は
    /// 「溜まりすぎない」ための目安で、数十秒のあいだ少し超えても実害が無い（超過分は次の掃除でまとめて消える）。
    /// 一方で手動の反映は、頻度が低く、上限どおりに保たれることを優先して毎回掃除する（従来どおり）。
    /// </para>
    /// </param>
    private async Task KeepAutoCopiesAsync(
        Project project, List<(VbaModulePlan Plan, string ExcelCode)> copies,
        Dictionary<string, (VbaModulePlan Plan, string Text)> writeSources, List<GraftIssue> issues, bool throttlePrune = false)
    {
        var wroteAny = false;
        foreach (var (plan, excelCode) in copies)
        {
            // E811 などで書かないモジュールは、上書きされないので控えも要らない。
            if (!writeSources.ContainsKey(plan.Component)) continue;

            var saved = await Rescue.SaveAsync(project, plan.Component, excelCode, VbaRescueKind.Auto, skipIfSameAsLatestAuto: true)
                .ConfigureAwait(true);
            if (saved.Status == VbaRescueSaveStatus.Saved) wroteAny = true;
            if (saved.Status != VbaRescueSaveStatus.Failed) continue;

            issues.Add(GraftIssue.Of(
                ErrorCode.E816,
                $"{plan.RelativePath}: 反映前の Excel 側の本文を控えに保存できませんでした（反映は続けました）。{saved.Failure} 控えの保存先: {Rescue.DirectoryOf(project)}",
                path: plan.RelativePath,
                severity: Severity.Warning));
        }

        if (!wroteAny) return;
        if (throttlePrune)
        {
            var now = _clock();
            if (_lastAutoPrune.TryGetValue(project.Id, out var last) && now - last < AutoPrunePeriod) return;
            _lastAutoPrune[project.Id] = now;
        }

        try
        {
            await Rescue.PruneCoreAsync(project).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger?.Warn("vba-rescue", $"控えの掃除に失敗しました: {ExceptionMessages.Describe(ex)}");
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
        var (status, _) = await ComputeLocalStatusAsync(project, ct).ConfigureAwait(true);
        return status;
    }

    /// <summary>
    /// 「Excel から復元」の対象になりうるモジュール（記録があるのに、ミラーのファイルが無いもの）の相対パス。
    /// <see cref="GetLocalStatusAsync"/> の削除保留の件数と同じ基準で拾う。
    /// <para>
    /// 【同期状態の <see cref="VbaModuleState.DeletePending"/> を見ない理由】 この印は、ファイルを消したあとに全体の
    /// 取込か反映が走るまで保存されない。エクスプローラーで誤って消した直後や、自動反映オフで新規モジュール入りの
    /// 適用を取り消した直後は、バッジが「削除保留 1」なのに印がまだ無く、印で拾うと復元が 0 件になっていた
    /// （不具合 5）。バッジと同じ「記録があってファイルが無い」で拾えば、両者は必ず一致する。
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<string>> GetRestorablePathsAsync(Project project, CancellationToken ct = default)
    {
        var (_, paths) = await ComputeLocalStatusAsync(project, ct).ConfigureAwait(true);
        return paths;
    }

    private async Task<(VbaLocalStatus Status, IReadOnlyList<string> PendingPaths)> ComputeLocalStatusAsync(
        Project project, CancellationToken ct)
    {
        var link = RequireLink(project);
        var mirror = await ReadMirrorAsync(project.Root, ct).ConfigureAwait(true);
        var loaded = await _states.LoadAsync(project.Id, link.WorkbookFullName, ct).ConfigureAwait(true);
        var records = loaded.Value.Modules;

        var unpushed = 0;
        var needsReview = 0;
        var unpushedPaths = new List<string>();
        var pendingPaths = new List<string>();
        foreach (var (key, record) in records)
        {
            if (mirror.Unreadable.Contains(key)) continue; // 読めないだけで、無いわけではない。
            if (record.NeedsReview) needsReview++;

            if (!mirror.Modules.TryGetValue(key, out var module))
            {
                // ミラーのファイルが無いのに記録がある＝削除保留。
                pendingPaths.Add(key);
                continue;
            }

            if (!string.Equals(module.Hash, record.MirrorHash, StringComparison.Ordinal))
            {
                unpushed++;
                unpushedPaths.Add(key);
            }
        }

        // 記録がなく、ミラーにだけあるモジュール（新規作成の Push 待ち）も未反映。
        var created = mirror.Modules.Values
            .Where(m => !records.ContainsKey(m.RelativePath) && VbaModuleKinds.CanCreateOnExcel(m.Kind))
            .Select(m => m.RelativePath)
            .ToList();
        unpushed += created.Count;
        unpushedPaths.AddRange(created);
        return (new VbaLocalStatus(unpushed, pendingPaths.Count) { UnpushedPaths = unpushedPaths, NeedsReviewCount = needsReview }, pendingPaths);
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

        /// <summary>
        /// 読めなかったファイルの相対パス（ロック中・権限なしなど）。<see cref="Modules"/> には入らないが、「無い」わけではない。
        /// 計画（<see cref="VbaSyncPlanner"/>）に渡すと「ミラーのファイルが無い」と取り違えるので、計画から外す。
        /// </summary>
        public HashSet<string> Unreadable { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<GraftIssue> Issues { get; } = new();
    }

    /// <param name="narrowRead">
    /// true なら、<paramref name="scope"/> のモジュールだけを Excel から読み（<c>ReadComponentsAsync(names)</c>）、ミラーも
    /// そのファイルだけ読む（保存時の自動反映用。仕様書_ブックの情報と依頼の観点 8.4）。<paramref name="scope"/> が null の
    /// ときは意味を持たず、従来どおり全体を読む。
    /// <para>
    /// 絞った計画は、対象のモジュールの判定だけを返す。削除保留・記録の破棄のように全体を見ないと出せない判定は、
    /// 計画の入力（ミラー・Excel・同期状態）を対象に絞る既存の仕組み（<c>InScope</c>）のおかげで、対象外のモジュールには
    /// 起きない（絞った読み取りで「無い」と取り違えて、対象外の記録を破棄したり、削除保留の印を付けたりしない）。
    /// </para>
    /// </param>
    private async Task<GraftResult<Work>> BuildPlanAsync(
        Project project, VbaLink link, HashSet<string>? scope, CancellationToken ct, bool narrowRead = false)
    {
        var narrow = narrowRead && scope is not null;
        IReadOnlyCollection<string>? names = null;
        if (narrow)
        {
            // 相対パス → コンポーネント名。名前にできないパス（同期の対象外のファイル）は読む対象に入れない。
            names = scope!
                .Select(p => VbaMirrorLayout.TryParse(p, out var parsed, out _) && parsed is not null ? parsed.ComponentName : null)
                .Where(n => n is not null)
                .Select(n => n!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var excel = await _bridge.ReadComponentsAsync(link.WorkbookFullName, names, ct).ConfigureAwait(true);
        if (!excel.IsSuccess) return GraftResult<Work>.Fail(excel.Issues);

        var mirror = narrow
            ? await ReadMirrorFilesAsync(project.Root, scope!, ct).ConfigureAwait(true)
            : await ReadMirrorAsync(project.Root, ct).ConfigureAwait(true);
        var loaded = await _states.LoadAsync(project.Id, link.WorkbookFullName, ct).ConfigureAwait(true);

        var excelByPath = new Dictionary<string, VbaComponentSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var component in excel.Value)
        {
            if (!VbaNameRules.CanMirror(component.Name)) continue; // 計画側で E810 にするため、別に数える。
            excelByPath[VbaMirrorLayout.ToRelativePath(component.Kind, component.Name)] = component;
        }

        // 読めなかったミラーのファイルは、ミラー・Excel・記録のどれからも外す（「無い」と扱わない）。
        bool InScope(string key) => (scope is null || scope.Contains(key)) && !mirror.Unreadable.Contains(key);

        var excelModules = excel.Value
            .Where(c => !VbaNameRules.CanMirror(c.Name) || InScope(VbaMirrorLayout.ToRelativePath(c.Kind, c.Name)))
            .Select(c => new VbaExcelModule(
                c.Name, c.Kind, VbaTextNormalizer.Hash(c.Code), VbaTextNormalizer.LooseHash(c.Code), c.Caption,
                VbaTextNormalizer.CountLines(c.Code) == 0))
            .ToList();
        var mirrorModules = mirror.Modules.Values.Where(m => InScope(m.RelativePath)).ToList();

        var state = loaded.Value;
        var scopedState = scope is null && mirror.Unreadable.Count == 0
            ? state
            : state with
            {
                Modules = state.Modules.Where(kv => InScope(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase),
            };

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

        await ReadMirrorEntriesAsync(root, relatives, contents, ct).ConfigureAwait(true);
        return contents;
    }

    /// <summary>
    /// 指定の相対パスのファイルだけを読む（保存時の自動反映用。<see cref="ReadMirrorAsync"/> の絞り込み版）。
    /// <para>
    /// 【ディレクトリを走査しない理由】 全体を走査すると、ミラーのファイルが増えるほど保存のたびの仕事が増える。
    /// 保存したファイルの場所は分かっているので、そこだけ読む。対象外のファイルへの E810 の警告は、全体を見る
    /// 手動の操作に任せる（保存のたびに同じ警告を出さない）。ファイルが無ければ「ミラーに無い」として扱う
    /// （保存した直後に消された場合は、計画が削除保留と判定して反映しない）。
    /// </para>
    /// </summary>
    private static async Task<MirrorContents> ReadMirrorFilesAsync(string root, IEnumerable<string> relatives, CancellationToken ct)
    {
        var contents = new MirrorContents();
        if (!Directory.Exists(root)) return contents;

        var existing = relatives
            .Where(r => File.Exists(Path.Combine(root, r.Replace('/', Path.DirectorySeparatorChar))))
            .ToList();
        await ReadMirrorEntriesAsync(root, existing, contents, ct).ConfigureAwait(true);
        return contents;
    }

    private static async Task ReadMirrorEntriesAsync(string root, IEnumerable<string> relatives, MirrorContents contents, CancellationToken ct)
    {
        foreach (var relative in relatives)
        {
            if (!VbaMirrorLayout.TryParse(relative, out var parsed, out _) || parsed is null) continue;

            var read = await FileTextIO.ReadAsync(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)), ct)
                .ConfigureAwait(true);
            if (!read.IsSuccess)
            {
                // 読めなかったファイルを「無い」ものとして計画に渡すと、記録があれば削除保留の印が付き、記録が無く
                // Excel にあるモジュールは取り込み（既存のファイルを新規の形で上書き）になってしまう。
                // 読めるようになるまでこのモジュールは計画から外し、その旨を警告に添える。
                contents.Unreadable.Add(parsed.RelativePath);
                contents.Issues.AddRange(read.Issues.Select(i => i with
                {
                    Severity = Severity.Warning,
                    Detail = $"{i.Detail} 読めるようになるまで、このモジュール（{parsed.RelativePath}）は同期の対象から外します。",
                }));
                continue;
            }

            var text = read.Value.Text;
            contents.Modules[parsed.RelativePath] = new VbaMirrorModule(
                parsed.RelativePath, parsed.Kind, parsed.ComponentName,
                VbaTextNormalizer.Hash(text), VbaTextNormalizer.LooseHash(text), VbaTextNormalizer.CountLines(text) == 0);
            contents.Texts[parsed.RelativePath] = text;
        }
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

    /// <summary>
    /// 書き戻しに失敗したモジュールの新しい記録（要確認つき）。記録が既にあればそれに印を足し、無ければ（衝突の上書きなど）
    /// ハッシュを持たない記録を作る（ハッシュが空なら、次の計画は必ず「変わった」と見て、要確認として衝突になる）。
    /// </summary>
    private static VbaModuleState MarkNeedsReview(VbaSyncState state, VbaModulePlan plan, DateTimeOffset now)
        => state.Modules.TryGetValue(plan.RelativePath, out var record)
            ? record with { NeedsReview = true }
            : new VbaModuleState { Kind = plan.Kind, Component = plan.Component, NeedsReview = true, SyncedAt = now };

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
    internal static async Task<GraftResult<bool>> WriteMirrorFileAsync(
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
