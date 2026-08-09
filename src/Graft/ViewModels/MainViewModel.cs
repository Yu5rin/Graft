using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using Graft.Core;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;

namespace Graft.ViewModels;

/// <summary>中央ペイン（ブロック一覧・diff）の表示状態（仕様書8.8）。</summary>
public enum CenterPaneState
{
    Empty,
    Loading,
    Error,
    Content,
}

/// <summary>
/// メインウィンドウ全体を統括するViewModel。プロジェクト一覧・履歴・ブロック一覧・diffを束ね、
/// 貼り付け（Ctrl+V）から適用完了までの一連の操作（仕様書8.10）を提供する。
/// 依存はすべてコンストラクタ引数で受け取り、生成は起動処理担当が行う（附録A.3）。
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly ApplyEngine _applyEngine;
    private readonly RevisionStore _revisionStore;
    private readonly RevisionRestorer _revisionRestorer;
    private readonly SettingsStore _settingsStore;
    private readonly IDialogService _dialogs;
    private readonly IUiServices _ui;
    private readonly PatchParser _parser = new();
    private readonly HookRunner _hookRunner = new();
    private readonly Action _openSettingsRequested;

    private Settings _settings = new();
    private Patch? _currentPatch;
    private DryRunResult? _dryRun;
    private ApplyContext? _lastContext;
    private bool _dryRunFromQueue;

    // 課題1: 適用処理（ApplyAsync）が実行中かどうか。実行中はUpdateSettingsからの反映を
    // 保留する（MainViewModel.Apply.cs参照）。_pendingSettingsは保留中の最新値を1件だけ
    // 保持し、適用完了時にまとめて反映する（複数回変更されても最後の1回で十分なため）。
    private bool _isApplyInProgress;
    private Settings? _pendingSettings;

    private CenterPaneState _state = CenterPaneState.Empty;
    private GraftIssue? _centerError;
    private BlockItemViewModel? _selectedBlock;
    private string _filterText = string.Empty;
    private bool _syncingSelection;

    public MainViewModel(
        ApplyEngine applyEngine,
        ProjectStore projectStore,
        RevisionStore revisionStore,
        RevisionRestorer revisionRestorer,
        SettingsStore settingsStore,
        WindowLayoutStore layoutStore,
        IDialogService dialogService,
        Features.PatchQueue patchQueue,
        Action openSettingsRequested,
        IUiServices ui)
    {
        _applyEngine = applyEngine ?? throw new ArgumentNullException(nameof(applyEngine));
        ArgumentNullException.ThrowIfNull(projectStore);
        _revisionStore = revisionStore ?? throw new ArgumentNullException(nameof(revisionStore));
        _revisionRestorer = revisionRestorer ?? throw new ArgumentNullException(nameof(revisionRestorer));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        LayoutStore = layoutStore ?? throw new ArgumentNullException(nameof(layoutStore));
        _dialogs = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        ArgumentNullException.ThrowIfNull(patchQueue);
        _openSettingsRequested = openSettingsRequested ?? throw new ArgumentNullException(nameof(openSettingsRequested));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));

        ProjectPane = new ProjectPaneViewModel(projectStore, dialogService);
        History = new HistoryPaneViewModel(revisionStore, revisionRestorer, projectStore, dialogService);
        // DiffViewModelは構築時にSettingsを固定するため、設定読み込み前は既定値で仮に構築し、
        // 読み込み後にWordWrap/ShowWhitespaceのみ反映し直す（InitializeAsync参照）。
        Diff = new DiffViewModel(new Settings(), _ui);
        Diff.PropertyChanged += OnDiffPropertyChanged;
        // 修正1: 履歴差分タブ専用の表示状態。接ぎ木パネル（Diff/Blocks/State）とは完全に独立させ、
        // 履歴を閲覧しても接ぎ木パネルには一切触れないようにする（OnRevisionSelected参照）。
        HistoryDiff = new HistoryDiffViewModel(new Settings(), _ui);

        PatchQueue = patchQueue;
        Queue = new QueueViewModel(patchQueue, dialogService);
        Queue.MergeRequested += async (_, _) => await MergeQueueAndLoadAsync().ConfigureAwait(true);

        ProjectPane.ProjectSelected += OnProjectSelected;
        History.RevisionSelected += OnRevisionSelected;
        History.RevisionRestored += OnRevisionRestored;

        PasteAndParseCommand = new AsyncRelayCommand(PasteAndParseAsync);
        PreviewCommand = new AsyncRelayCommand(RunDryRunAsync, () => _currentPatch is not null);
        ApplyCommand = new AsyncRelayCommand(ApplyAsync, () => _dryRun is { ApplicableCount: > 0 });
        UndoCommand = new AsyncRelayCommand(UndoLastAsync);
        OpenSettingsCommand = new RelayCommand(() => _openSettingsRequested());
        // 修正3: 接ぎ木パネルのヘッダーに露出させる「破棄」ボタン用。解析結果が無いときは無効化する
        // （PreviewCommand/ApplyCommandと同じ、_currentPatch/_dryRunを見るだけの簡易判定。
        // CanExecuteの再評価はCommandRequery.Invalidateがポインタ・キー操作のたびに全コマンドへ
        // 促す既存の仕組みに乗る）。
        DiscardCommand = new RelayCommand(DiscardCurrentPatch, () => _currentPatch is not null);
        FocusSearchCommand = new RelayCommand(() => RequestFocusSearch?.Invoke(this, EventArgs.Empty));
        ShowHistoryCommand = new RelayCommand(() => RequestFocusHistory?.Invoke(this, EventArgs.Empty));
        AddCurrentPatchToQueueCommand = new AsyncRelayCommand(AddCurrentPatchToQueueAsync, () => _currentPatch is not null);
        OpenQueueCommand = new RelayCommand(() => RequestOpenQueue?.Invoke(this, EventArgs.Empty));
        CopyRecoveryPromptCommand = new AsyncRelayCommand(CopyRecoveryPromptAsync, () => Blocks.Any(b => !b.Plan.CanApply));
        ParseFromFileCommand = new AsyncRelayCommand(PickAndParseFileAsync); // 4.1（MainViewModel.FileParse.cs）。

        InitializePrompt(projectStore); // 4.8.4（MainViewModel.Prompt.cs）。
    }

    public ProjectPaneViewModel ProjectPane { get; }
    public HistoryPaneViewModel History { get; }
    public DiffViewModel Diff { get; }

    /// <summary>修正1: 履歴のリビジョン選択に連動する履歴差分タブの表示内容（Diffとは別インスタンス）。</summary>
    public HistoryDiffViewModel HistoryDiff { get; }

    public WindowLayoutStore LayoutStore { get; }

    // PatchQueue/Queue等はMainViewModel.Queue.cs、BeforeApplyAsync/AfterApplyAsync（4.8/7章）は
    // MainViewModel.Apply.csで宣言する（いずれも400行上限のための分割）。

    /// <summary>読み込み・保存済みのウィンドウ・ペインレイアウト。Viewが直接読み書きする。</summary>
    public WindowLayoutState Layout { get; private set; } = new();

    public ObservableCollection<BlockItemViewModel> Blocks { get; } = new();

    public CenterPaneState State
    {
        get => _state;
        private set => SetProperty(ref _state, value);
    }

    public GraftIssue? CenterError
    {
        get => _centerError;
        private set => SetProperty(ref _centerError, value);
    }

    /// <summary>
    /// 機能追加（クリップボード監視の自動解析）: 「未処理」とみなせる内容が残っているかどうか。
    /// 中央ペインに未適用の解析結果が残っている（<see cref="_currentPatch"/>が非null。
    /// パースしただけで「適用」も「破棄」もされていない状態）か、パッチキューに
    /// 未適用のブロックが残っている場合にtrue。自動解析（設定オン時にパッチ検知の瞬間
    /// 解析まで自動で行う機能）は、この間は先頭の内容を勝手に差し替えてしまわないよう
    /// 見送り、従来どおり通知のみに留める（呼び出し元: ShellViewModel.
    /// HandleClipboardPatchDetected）。適用完了直後は<see cref="DiscardCurrentPatch"/>で
    /// _currentPatchがnullへ戻るため、この判定にも自動で追従する。
    /// </summary>
    public bool HasUnprocessedResult => _currentPatch is not null || PatchQueue.Items.Count > 0;

    public BlockItemViewModel? SelectedBlock
    {
        get => _selectedBlock;
        set
        {
            var previous = _selectedBlock;
            if (!SetProperty(ref _selectedBlock, value)) return;
            if (previous is not null) previous.PropertyChanged -= OnSelectedBlockPropertyChanged;
            if (value is null)
            {
                Diff.Clear();
            }
            else
            {
                Diff.Load(value.Plan);
                value.PropertyChanged += OnSelectedBlockPropertyChanged;
            }
        }
    }

    /// <summary>ブロック一覧の絞り込み文字列（Ctrl+F）。実際の絞り込みはView側で行う。</summary>
    public string FilterText
    {
        get => _filterText;
        set => SetProperty(ref _filterText, value);
    }

    public ICommand PasteAndParseCommand { get; }
    /// <summary>4.1: ファイルを選んで解析する（MainViewModel.FileParse.cs）。</summary>
    public ICommand ParseFromFileCommand { get; }
    public ICommand PreviewCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand DiscardCommand { get; }
    public ICommand FocusSearchCommand { get; }
    public ICommand ShowHistoryCommand { get; }

    /// <summary>Ctrl+F。View側でどの検索ボックスへフォーカスするかを判断する。</summary>
    public event EventHandler? RequestFocusSearch;

    /// <summary>Ctrl+H・「履歴」ボタン。View側で履歴ペインへフォーカスする。</summary>
    public event EventHandler? RequestFocusHistory;

    /// <summary>
    /// 修正1: <see cref="HistoryDiff"/>の内容（Files/RevisionLabel）が変わったことの通知。
    /// HistoryDiffは選択のたびに同じインスタンスを使い回す（プロパティの参照自体は変わらない）
    /// ため、ShellViewModel側が「いつタブを開閉すべきか」を知るための専用イベントとして持つ
    /// （ShellViewModel.OnHistoryDiffChanged参照）。
    /// </summary>
    public event EventHandler? HistoryDiffChanged;

    /// <summary>起動直後の初期化。設定・レイアウト・プロジェクト一覧を読み込む。</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var settingsResult = await _settingsStore.LoadAsync(ct).ConfigureAwait(true);
        _settings = settingsResult.Value;
        // DiffはMainViewModelのコンストラクタで既定値のSettingsを仮に渡して構築済み（Diffの
        // コンストラクタ引数コメント参照）。ここで読み込み済みの実際の設定を反映し直す
        // （WordWrap/ShowWhitespaceだけでなくシンタックス・マッチング設定も含めて丸ごと。
        // DiffViewModel.UpdateSettings参照）。
        Diff.UpdateSettings(_settings);
        HistoryDiff.UpdateSettings(_settings);

        Layout = await LoadLayoutSafeAsync(ct).ConfigureAwait(true);
        await ProjectPane.LoadAsync(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// 課題1: 設定画面での変更（即時反映方式で保存が確定した内容）を実行中のアプリへ伝播させる。
    /// StartupCoordinatorがSettingsViewModelへ渡すコールバック経由で、保存成功のたびに呼ばれる
    /// （デバウンス中の中途半端な値がここへ届くことはない。SettingsViewModel.CommitAndSaveAsync
    /// 参照）。
    ///
    /// 差分表示の折り返し・空白表示は、既に開いているdiff画面の見た目そのものであり、適用処理の
    /// 正しさには一切関わらないため、適用処理の実行中かどうかに関わらず常にその場で反映する
    /// （要件: 既に開いている画面への反映。DiffViewModel.UpdateSettings参照）。
    ///
    /// それ以外（安全機構・マッチング・バックアップ・Git連携・適用後フックのタイムアウト・
    /// 適用モード等、ApplyAsyncの一連の処理が参照する設定）は、適用処理の実行中は反映を保留し、
    /// 完了後にまとめて反映する（<see cref="_isApplyInProgress"/>）。理由: 安全機構の閾値・
    /// 対象ファイル一覧はドライラン開始時に<see cref="ApplyContext"/>へ固定で焼き込まれ、
    /// 実際の書き込み（<see cref="ApplyEngine.ApplyAsync"/>）はそのコンテキストだけを見て動く
    /// ため直接の影響は無いが、マッチングしきい値は<see cref="ApplyEngine"/>内部の
    /// <see cref="MatchEngine"/>が構築時に固定で保持する値であり、書き込み中の
    /// ファイルごとの再解決（BlockResolver.ResolveFile）で都度参照される。ここへ書き込み中に
    /// 差し替えが割り込むと、同一リビジョン内で前半と後半のファイルが異なるしきい値で
    /// 処理されてしまいかねない。また適用後フックのタイムアウトやGit自動コミットの可否は
    /// <see cref="_settings"/>を適用完了の直前まで直接参照するため、フィールドごと差し替えを
    /// 保留し、「1回の適用操作は開始時点の設定のまま最後まで一貫して動く」ことを保証する。
    /// </summary>
    public void UpdateSettings(Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Diff.UpdateSettings(settings);
        HistoryDiff.UpdateSettings(settings);

        if (_isApplyInProgress)
        {
            _pendingSettings = settings;
            return;
        }

        ApplySettingsNow(settings);
    }

    /// <summary>
    /// _settingsフィールドと、それに連動する<see cref="ApplyEngine"/>のマッチング設定を実際に
    /// 差し替える。<see cref="UpdateSettings"/>（適用処理が実行中でない場合）と、
    /// MainViewModel.Apply.csのApplyAsync完了直後（保留していた場合の反映）の両方から呼ぶ。
    /// </summary>
    private void ApplySettingsNow(Settings settings)
    {
        _settings = settings;

        // マッチング（類似度しきい値・あいまい一致の可否・範囲警告行数）はApplyEngine内部の
        // MatchEngineが構築時に固定で受け取る値のため、_settingsを差し替えるだけでは
        // 次のドライランにも反映されない（DryRunPlanner/BlockResolverはApplyContext経由ではなく
        // このMatchEngineインスタンスを直接参照する。ApplyEngine.UpdateMatchOptions参照）。
        _applyEngine.UpdateMatchOptions(new MatchOptions
        {
            SimilarityThreshold = settings.Matching.SimilarityThreshold,
            AllowSimilarityMatch = settings.Matching.AllowSimilarityMatch,
            RangeWarningLines = settings.Matching.RangeWarningLines,
        });
    }

    /// <summary>
    /// レイアウト読み込みの防御層。JSON解析エラーは<see cref="WindowLayoutStore.LoadAsync"/>内で
    /// 既に退避・再生成済みだが、ファイルI/O自体の失敗（アクセス権等）は例外として上がってくる。
    /// レイアウトが読めない程度でプロジェクト一覧読み込み等まで中断させないよう既定値へ倒す
    /// （設計目標5・附録A.4）。想定外の例外は<see cref="SafeHandler.OnUnexpected"/>でログへ記録する。
    /// </summary>
    private async Task<WindowLayoutState> LoadLayoutSafeAsync(CancellationToken ct)
    {
        try
        {
            return await LayoutStore.LoadAsync(ct).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SafeHandler.OnUnexpected?.Invoke("レイアウトの読み込み", ex);
            return new WindowLayoutState();
        }
    }

    /// <summary>終了時に現在のレイアウトを保存する。</summary>
    public Task SaveLayoutAsync(CancellationToken ct = default) => LayoutStore.SaveAsync(Layout, ct);

    /// <summary>現在のプロジェクトに対応するペイン幅設定を取得する（無ければ既定値で作成）。</summary>
    public ProjectPaneLayout GetCurrentPaneLayout()
    {
        var projectId = ProjectPane.SelectedItem?.Project.Id ?? "_default";
        return WindowLayoutStore.GetOrCreatePaneLayout(Layout, projectId);
    }

    private async void OnProjectSelected(object? sender, Project project)
    {
        DiscardCurrentPatch();
        // 8.4: コード表示のフォントサイズはプロジェクトごとに記憶する。
        Diff.CodeFontSize = GetCurrentPaneLayout().CodeFontSize;
        RebuildPromptContext(project); // 4.8.4（MainViewModel.Prompt.cs）。
        await History.LoadAsync(project.Id, project.Root).ConfigureAwait(true);
        // 課題2対応: 以前はここでも6.3のin_progress検出（CheckInProgressAsync）を独自に行い、
        // 「r1が完了しないまま終了した可能性があります。バックアップフォルダを確認してください」
        // という対処不能な曖昧な通知を出していた。StartupCoordinator.RunStartupValidationAsyncが
        // 起動時に接続済み全プロジェクトを対象に同じ検出（RevisionStore.FindInProgressAsync）を
        // 行い、具体的なプロジェクト名・リビジョン番号を示したうえでロールバックを提案する
        // StartupReport経由の通知（StartupCoordinator.Validation.cs・OfferRollbackAsync）へ
        // 既に集約済みだったため、この経路は同じ事象について2枚目の重複ダイアログを出す
        // だけの死んだ経路になっていた（実機確認済み。1枚目でロールバックした後もこちらは
        // 状態を知らないまま「確認してください」と言い続ける）。起動時に検出された分は
        // 起動時の集約通知だけで十分カバーされる（起動時検証は接続済み全プロジェクトを
        // 走査するため、後からこのプロジェクトへ切り替えても取りこぼしは起きない）ため、
        // この呼び出しごと削除した。
        OnPropertyChanged(nameof(CurrentProjectName));
    }

    /// <summary>
    /// 修正1: 履歴のリビジョン選択に連動して<see cref="HistoryDiff"/>（履歴差分タブ専用の表示）を
    /// 更新する。以前はここで<see cref="Diff"/>・<see cref="Blocks"/>・<see cref="State"/>
    /// （＝接ぎ木パネル一式）を履歴の内容で置き換えていたため、「これから適用するもの」の
    /// 置き場である接ぎ木パネルに履歴の内容が流れ込み、適用不可の×印が誤って表示される・
    /// 現在のファイル内容と誤解される、という2つの混乱を生んでいた（今回の要望の発端）。
    /// 接ぎ木パネル側は一切触れず、履歴の閲覧中でも現在の解析結果がそのまま残るようにする
    /// （逆に、履歴を閲覧中に新しいパッチを解析してもこちらは接ぎ木パネル側だけを更新するため
    /// 衝突しない）。
    /// </summary>
    private async void OnRevisionSelected(object? sender, RevisionRowViewModel? row)
    {
        if (row is null)
        {
            HistoryDiff.Clear();
            HistoryDiffChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var plans = await History.BuildDiffPlansAsync(row, _settings.Diff.ContextLines).ConfigureAwait(true);
        // 選択が非同期待ちの間に別のリビジョンへ変わっていたら、後から選ばれた側の結果を
        // 古い結果で上書きしてしまわないよう、ここで打ち切る（History.SelectedItemが呼び出し
        // 時点のrowから変わっていないかで判定する）。
        if (!ReferenceEquals(History.SelectedItem, row)) return;

        HistoryDiff.Load(row, plans);
        HistoryDiffChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>diff側の変更を反映する。IsIncludedは選択ブロックへ、CodeFontSizeは8.4のペイン記憶へ。</summary>
    private void OnDiffPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DiffViewModel.CodeFontSize))
        {
            GetCurrentPaneLayout().CodeFontSize = Diff.CodeFontSize;
            return;
        }
        if (_syncingSelection || e.PropertyName != nameof(DiffViewModel.IsIncluded) || SelectedBlock is null) return;
        _syncingSelection = true;
        SelectedBlock.IsSelected = Diff.IsIncluded;
        _syncingSelection = false;
    }

    /// <summary>ブロック一覧側（Space・チェックボックス）での切り替えをdiff側へ反映する。</summary>
    private void OnSelectedBlockPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_syncingSelection || e.PropertyName != nameof(BlockItemViewModel.IsSelected)) return;
        if (sender is not BlockItemViewModel block || !ReferenceEquals(block, _selectedBlock)) return;
        _syncingSelection = true;
        Diff.IsIncluded = block.IsSelected;
        _syncingSelection = false;
    }

    private async void OnRevisionRestored(object? sender, EventArgs e)
    {
        if (ProjectPane.SelectedItem is null) return;
        await ProjectPane.LoadAsync().ConfigureAwait(true);
        DiscardCurrentPatch();
    }

    private async Task PasteAndParseAsync()
    {
        // IClipboardAccess.GetTextAsyncは取得できない場合にnullを返す契約のため、
        // 個別の例外保護は不要（クリップボードが他プロセスに占有されている場合もnullになる）。
        var text = await _ui.Clipboard.GetTextAsync().ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(text))
        {
            // 黙って戻ると「解析を押しても何も起きない」という状態になり、
            // 利用者は原因を知る手がかりを得られない。理由を中央ペインへ出す。
            CenterError = GraftIssue.Of(
                ErrorCode.E708,
                text is null
                    ? "クリップボードの内容を取得できませんでした。他のアプリがクリップボードを保持したままの可能性があります。AIの出力をコピーし直してから、もう一度お試しください。"
                    : "クリップボードが空です。AIの出力をコピーしてから、もう一度お試しください。");
            State = CenterPaneState.Error;
            return;
        }

        await ParseTextAndLoadAsync(text).ConfigureAwait(true);
    }

    /// <summary>
    /// テキストを受け取って解析する内部経路。クリップボード（<see cref="PasteAndParseAsync"/>）と
    /// ファイル選択・ドラッグ＆ドロップ（MainViewModel.FileParse.cs）の両方から共有する。
    /// </summary>
    private async Task ParseTextAndLoadAsync(string text)
    {
        var parsed = _parser.Parse(text);
        if (!parsed.IsSuccess)
        {
            CenterError = parsed.Errors.FirstOrDefault();
            State = CenterPaneState.Error;
            return;
        }

        // 4.10: パッチが途中で切れている場合は直接適用フローへ乗せず、キューへ積んで続きを依頼する。
        if (parsed.Value.IsTruncated)
        {
            await HandleTruncatedPatchAsync(parsed.Value).ConfigureAwait(true);
            return;
        }

        _currentPatch = parsed.Value;
        _dryRunFromQueue = false;
        await RunDryRunAsync().ConfigureAwait(true);
    }

    private async Task RunDryRunAsync()
    {
        if (_currentPatch is null) return;

        var project = ProjectPane.SelectedItem?.Project;
        if (project is null)
        {
            CenterError = GraftIssue.Of(ErrorCode.E303, "プロジェクトを選択してください");
            State = CenterPaneState.Error;
            return;
        }

        // 3.3/3.4: プロジェクト自動判定（MainViewModel.ProjectMatch.cs）。ドライランへ進む唯一の
        // 入口であるここに固定で組み込むことで、呼び出し側からの無効化・通し忘れを構造的に防ぐ。
        // falseが返るのはブロック、またはユーザーが確認ダイアログをキャンセルした場合で、
        // その場合はEnsureProjectMatchAsync側で既にCenterError/Stateを設定済み。
        if (!await EnsureProjectMatchAsync(project).ConfigureAwait(true)) return;
        project = ProjectPane.SelectedItem?.Project; // 切替が起きていれば反映する。
        if (project is null) return; // 理論上は到達しない（切替後も必ずどこかのプロジェクトが選択されている）。

        // 4.8/7章: 未保存編集があれば保存を促してから続行する（破棄不可。MainViewModel.Apply.cs）。
        if (!await ConfirmTargetsSavedAsync(project.Root).ConfigureAwait(true)) return;

        State = CenterPaneState.Loading;
        var guard = new PathGuard(project.Root, new PathGuardOptions
        {
            AllowedExtensions = _settings.Safety.AllowedExtensions,
            MaxFileSizeMB = _settings.Safety.MaxFileSizeMB,
            MaxFilesPerRevision = _settings.Safety.MaxFilesPerRevision,
        });
        var context = new ApplyContext
        {
            ProjectId = project.Id,
            ProjectRoot = project.Root,
            Revision = project.NextRevision,
            Settings = _settings,
            Guard = guard,
        };

        var dryRun = await _applyEngine.DryRunAsync(_currentPatch, context).ConfigureAwait(true);
        if (!dryRun.IsSuccess)
        {
            CenterError = dryRun.Errors.FirstOrDefault();
            State = CenterPaneState.Error;
            return;
        }

        _lastContext = context;
        _dryRun = dryRun.Value;
        ReplaceBlocks(dryRun.Value.Plans);
        OnPropertyChanged(nameof(StatusSummaryText));
        OnPropertyChanged(nameof(TargetSummaryText));
    }

    // ApplyAsync/UndoLastAsync（適用の本体・Ctrl+Z）はMainViewModel.Apply.csへ分割している
    // （1ファイル400行上限のため。6.5適用後フックのonFailure分岐からApplyAsyncを呼ぶ都合上、
    // 同じ「適用」テーマのApply.csへまとめた）。

    private void DiscardCurrentPatch()
    {
        _currentPatch = null;
        _dryRun = null;
        _lastContext = null;
        CenterError = null;
        ReplaceBlocks(Array.Empty<BlockPlan>());
        OnPropertyChanged(nameof(StatusSummaryText));
        OnPropertyChanged(nameof(TargetSummaryText));
    }

    private void ReplaceBlocks(IReadOnlyList<BlockPlan> plans)
    {
        Blocks.Clear();
        foreach (var plan in plans)
        {
            Blocks.Add(new BlockItemViewModel(plan));
        }
        State = Blocks.Count == 0 ? CenterPaneState.Empty : CenterPaneState.Content;
        SelectedBlock = Blocks.FirstOrDefault();
    }
}
