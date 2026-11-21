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
        // 機能2: 適用直後の「元に戻す」通知（MainViewModel.ApplyUndoNotice.cs）。
        // ExplorerViewModelの削除取り消し通知と同じくCreateTimerは反復タイマーのため、
        // 1回消したらStopして次の適用まで眠らせておく。
        _applyUndoNoticeTimer = ui.CreateTimer(ApplyUndoNoticeDuration, OnApplyUndoNoticeTimeout);
        Notice = new StatusNoticeViewModel(ui); // MainViewModel.DiscardUndo.cs。

        ProjectPane = new ProjectPaneViewModel(projectStore, dialogService);
        ProjectPane.ClipboardWriter = text => _ui.Clipboard.SetText(text);
        ProjectPane.Notify = text => Notice.Show(text); // 「パスをコピー」を数秒で消える通知で伝える。
        _vbaPaths = projectStore.Paths; // _projectStore はこの時点では未設定（MainViewModel.Prompt.cs で後から入る）ため、引数から取る。
        RebuildVbaSync(); // Excel VBA 連携の窓口（MainViewModel.Vba.cs）。ブリッジが差し替わるたびに作り直す。
        History = new HistoryPaneViewModel(revisionStore, revisionRestorer, projectStore, dialogService);
        History.ClipboardWriter = text => _ui.Clipboard.SetText(text);
        History.Notify = text => Notice.Show(text); // 取り消しが終わったことを数秒で消える通知で伝える。
        History.NotifyWithAction = (text, label, action) => Notice.Show(text, label, action); // 「ここまで戻しました」＋「元に戻す」。
        // DiffViewModelは構築時にSettingsを固定するため、設定読み込み前は既定値で仮に構築し、
        // 読み込み後にWordWrap/ShowWhitespaceのみ反映し直す（InitializeAsync参照）。
        Diff = new DiffViewModel(new Settings(), _ui);
        Diff.PropertyChanged += OnDiffPropertyChanged;
        Diff.CanShowInVbe = ProjectPane.CanShowInVbe; // 差分ビューの右クリック「VBE でこの行を開く」（仕様書_VBA連携 7.6）。
        Diff.ShowInVbeAsync = ProjectPane.ShowInVbeAsync;
        Diff.InlineEditAdopter = AdoptInlineEditAsync; // 8.7: 書き換えたSEARCH部を適用に含める（MainViewModel.InlineEditAdopt.cs）。
        // 修正1: 履歴差分タブ専用の表示状態。接ぎ木パネル（Diff/Blocks/State）とは完全に独立させ、
        // 履歴を閲覧しても接ぎ木パネルには一切触れないようにする（OnRevisionSelected参照）。
        HistoryDiff = new HistoryDiffViewModel(new Settings(), _ui);

        PatchQueue = patchQueue;
        Queue = new QueueViewModel(patchQueue, dialogService);
        // 積む・外す・空にするたびにキューを保存する（強制終了で積んだ内容を失わないため。C12）。
        // 一覧（ツールバーの「キュー（N）」の件数を含む）も同じ合図で読み直し、どの経路で
        // キューが変わっても表示が古い件数のまま残らないようにする。
        patchQueue.Changed += (_, _) =>
        {
            Queue.Refresh();
            _ = SafeHandler.RunAsync("パッチキューの保存", () => patchQueue.SaveAsync());
        };
        Queue.MergeRequested += async (_, _) => await MergeQueueAndLoadAsync().ConfigureAwait(true);

        ProjectPane.ProjectSelected += OnProjectSelected;
        // プロジェクトペイン改善: 削除等でプロジェクトが1件も無くなった場合も、コマンドバーの
        // 現在プロジェクト表示（CurrentProjectName）を「プロジェクト未選択」へ即時反映する。
        ProjectPane.SelectionCleared += (_, _) =>
        {
            DiscardCurrentPatch();
            OnPropertyChanged(nameof(CurrentProjectName));
        };
        History.RevisionSelected += OnRevisionSelected;
        History.RevisionRestored += OnRevisionRestored;
        // Excel VBA 連携（MainViewModel.VbaApply.cs）: 取り消し・復元の後の Push と、Push 中に受け付けない操作。
        History.AfterRestoreAsync = PushVbaAfterRestoreAsync;
        History.IsOperationBlocked = () => IsVbaPushing;
        ProjectPane.IsSwitchBlocked = () => IsVbaPushing;

        // Excel へ Push している最中は、新しいパッチの貼り付けを受け付けない（MainViewModel.VbaApply.cs）。
        PasteAndParseCommand = new AsyncRelayCommand(PasteAndParseAsync, () => !IsVbaPushing, context: "貼り付けと解析");
        PreviewCommand = new AsyncRelayCommand(RunDryRunAsync, () => _currentPatch is not null, context: "適用前プレビュー");
        // 適用済みのパッチ（E302）は、適用できるブロックがあっても押せない。押しても要約入力と
        // 確認の窓を通った後にApplyEngineの再判定で必ず止まる（しかも止まった時点で
        // リビジョン番号を1つ消費する）ため、プレビューの時点で閉じる（MainViewModel.AlreadyApplied.cs）。
        ApplyCommand = new AsyncRelayCommand(
            ApplyAsync, () => _dryRun is { ApplicableCount: > 0 } && _alreadyAppliedRevision is null, context: "パッチの適用");
        UndoCommand = new AsyncRelayCommand(UndoLastAsync, context: "適用の取り消し");
        OpenSettingsCommand = new RelayCommand(() => _openSettingsRequested());
        // 修正3: 接ぎ木パネルのヘッダーに露出させる「破棄」ボタン用。解析結果が無いときは無効化する
        // （PreviewCommand/ApplyCommandと同じ、_currentPatch/_dryRunを見るだけの簡易判定。
        // CanExecuteの再評価はCommandRequery.Invalidateがポインタ・キー操作のたびに全コマンドへ
        // 促す既存の仕組みに乗る）。
        // 一部だけ適用できた後は、失敗ブロックだけが_dryRunと共に残り_currentPatchはnullになる
        // （MainViewModel.PartialApply.cs）。その残りも「破棄」で消せるよう_dryRunも条件に含める。
        // 利用者の操作による破棄は数秒だけ「元に戻す」を出す（MainViewModel.DiscardUndo.cs）。
        // Escでは破棄しない（ShellWindow.Keyboard.cs）ので、ここへ来るのは「破棄」ボタンと
        // コマンドパレットだけ。
        DiscardCommand = new RelayCommand(DiscardByUser, () => _currentPatch is not null || _dryRun is not null);
        ShowHistoryCommand = new RelayCommand(() => RequestFocusHistory?.Invoke(this, EventArgs.Empty));
        AddCurrentPatchToQueueCommand = new AsyncRelayCommand(
            AddCurrentPatchToQueueAsync, () => _currentPatch is not null, context: "キューへの追加");
        OpenQueueCommand = new RelayCommand(() => RequestOpenQueue?.Invoke(this, EventArgs.Empty));
        CopyRecoveryPromptCommand = new AsyncRelayCommand(
            CopyRecoveryPromptAsync, () => Blocks.Any(b => !b.Plan.CanApply), context: "リカバリー用プロンプトのコピー");
        ParseFromFileCommand = new AsyncRelayCommand(PickAndParseFileAsync, context: "ファイルからの解析"); // 4.1（MainViewModel.FileParse.cs）。

        InitializePrompt(projectStore); // 4.8.4（MainViewModel.Prompt.cs）。
        WireDisabledReasons(); // 押せないボタンの理由（MainViewModel.DisabledReasons.cs）。
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

    /// <summary>
    /// 見た目の整備: 接ぎ木パネルの一覧に出す並び。失敗したブロックを先頭へ寄せ、残りは元の順を保つ。
    /// 以前は失敗したブロックが一覧の下に埋もれ、スクロールしないと見つからなかった。
    /// <see cref="Blocks"/>（パッチ本来の順序）は、適用の順序・復元・チェック状態の保存が添字で参照するため
    /// 並べ替えず、表示専用のこちらだけを並べ替える（中身は同じ <see cref="BlockItemViewModel"/> の参照）。
    /// </summary>
    public ObservableCollection<BlockItemViewModel> BlocksDisplay { get; } = new();

    public CenterPaneState State
    {
        get => _state;
        private set => SetProperty(ref _state, value);
    }

    public GraftIssue? CenterError
    {
        get => _centerError;
        // E710（AIの「このファイルも見せて」）のときだけ出す操作ボタンの文言と、その押せる・押せないが
        // この値から決まる。CanExecuteの再評価は通常ポインタ・キー入力のたびに行われる
        // （CommandRequery）が、エラーが出た瞬間に押せない見た目のまま残らないよう、ここでも促す。
        private set => SetProperty(ref _centerError, value, () =>
        {
            OnPropertyChanged(nameof(RequestedFilesActionText));
            CommandRequery.Invalidate();
        });
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
    public ICommand ShowHistoryCommand { get; }

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
    /// <summary>
    /// v1.0.7実機不具合対応（環境要約ログ）: 現在有効な設定への読み取り専用アクセス。
    /// <see cref="_settings"/>は適用処理中のみ反映を保留する以外は常に最新（<see cref="UpdateSettings"/>
    /// 参照）であり、<see cref="ShellViewModel"/>側の環境要約ログ（プロジェクト切替時、
    /// <see cref="Infra.EnvironmentSummaryLogger"/>参照）が「取り込み結果を左右する設定」の
    /// 現在値を読むために公開する。
    /// </summary>
    public Settings CurrentSettings => _settings;

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
        ApplyVbaSettings(_settings);

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

        // 適用の最中に加えて、Excel へ書いている最中も保留する（不具合 9。取り消し・復元の後の反映や手動の反映は
        // 適用ではない）。終わったところで FlushPendingSettingsIfIdle が反映する。
        if (_isApplyInProgress || IsVbaWriting)
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
        ApplyVbaSettings(settings); // Vba.ComTimeoutMs／CallTimeoutMs／Enabled を次の呼び出しから効かせる。
        // プロンプトコピーも設定の最新値（トークン概算比率・「選んだファイルも付ける」）を取り込む。
        // 設定画面でオン・オフを切り替えたとき、開いているドロップダウンのチェックにも反映するため。
        PromptCopy?.ApplySettings(settings);

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
        if (IsVbaPushing) return; // キー操作の経路は CanExecute を見ないことがあるので、ここでも塞ぐ。
        // IClipboardAccess.GetTextAsyncは取得できない場合にnullを返す契約のため、
        // 個別の例外保護は不要（クリップボードが他プロセスに占有されている場合もnullになる）。
        var text = await _ui.Clipboard.GetTextAsync().ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(text))
        {
            // 黙って戻ると「解析を押しても何も起きない」という状態になり、
            // 利用者は原因を知る手がかりを得られない。理由を中央ペインへ出す。
            // 対処方法（再度コピーしてやり直す）はErrorCatalogのRemedyが一元的に持つため、
            // ここでは状況の報告だけにとどめ、同じ指示の重複表示を避ける（利用者からの指摘対応）。
            CenterError = GraftIssue.Of(
                ErrorCode.E708,
                text is null
                    ? "クリップボードの内容を取得できませんでした。他のアプリがクリップボードを保持したままの可能性があります。"
                    : "クリップボードが空です。");
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
        if (IsVbaPushing) return; // ファイルの取り込み・ドロップの経路も、Push 中は受け付けない。
        // 点検指摘A-6: 解析そのものの間が無表示だった。従来 State=Loading が立つのは
        // RunDryRunAsync のドライラン直前で、その手前にある _parser.Parse（全文の字句解析。
        // 大きなパッチほど時間がかかり、しかも完全に同期処理）の間は何も出ていなかった。
        // ここで待機表示を出しつつ、解析自体もスレッドプールへ逃がしてUIスレッドを空ける
        // （PatchParserは状態を持たない純粋な解析器のため、別スレッドで呼んで差し支えない）。
        var generation = BeginAnalysis();
        var stateBeforeParse = State;
        State = CenterPaneState.Loading;
        var parsed = await Task.Run(() => _parser.Parse(text)).ConfigureAwait(true);
        // 解析の待ち中に、さらに新しい解析が始まった場合は、古い結果で上書きしない（C15）。
        if (generation != _analysisGeneration) return;
        if (!parsed.IsSuccess)
        {
            CenterError = parsed.Errors.FirstOrDefault();
            State = CenterPaneState.Error;
            return;
        }

        // 解析が終わったら、いったん元の表示へ戻す。この先の HandleTruncatedPatchAsync・
        // RunDryRunAsync は途中で確認ダイアログ（プロジェクト自動判定・未保存の保存確認）を
        // 挟むことがあり、その間まで「読み込み中」を出し続けると、利用者が判断材料にしたい
        // 中央ペインの内容が消えたままになる。ドライラン本体の待機表示は
        // RunDryRunAsync が自分で立て直す。
        State = stateBeforeParse;

        // 4.10: パッチが途中で切れている場合は直接適用フローへ乗せず、キューへ積んで続きを依頼する。
        if (parsed.Value.IsTruncated)
        {
            await HandleTruncatedPatchAsync(parsed.Value).ConfigureAwait(true);
            return;
        }

        _currentPatch = parsed.Value;
        _dryRunFromQueue = false;
        // 新しいパッチ。前のパッチで選んだ「ファイルだけ適用する」・上書きの承認は引き継がない（Excel VBA 連携）。
        ResetVbaApplyChoices();
        await RunDryRunCoreAsync(generation).ConfigureAwait(true);
    }

    // C15: 解析・ドライランの「最新の要求」を見分けるための世代番号。解析やドライランは途中で
    // 非同期に待つ（構文解析・自動判定の確認ダイアログ・未保存の保存確認・ドライラン本体）。
    // その間に別の解析を始めた、解析結果を破棄した、別のプロジェクトへ切り替えた、のいずれかが
    // 起きても、待っていた古い要求が結果を反映してしまい、新しい画面を古い内容で上書きしていた。
    private int _analysisGeneration;

    private int BeginAnalysis() => ++_analysisGeneration;

    /// <summary>「プレビュー」ボタン（PreviewCommand）など、新しいドライランを始める入口。</summary>
    private Task RunDryRunAsync() => RunDryRunCoreAsync(BeginAnalysis());

    private async Task RunDryRunCoreAsync(int generation)
    {
        if (_currentPatch is null) return;
        var patch = _currentPatch;

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

        // Excel VBA 連携: 「ファイルだけ適用する」の選択は、その解析（と続く適用）だけのもの。再解析・キューの結合など、
        // 新しい解析を始めるたびに下ろす（不具合 8）。取り込みの前に下ろすのは、取り込めずに「ファイルだけ」を選び直した
        // 場合に、その選択がこの解析に効くようにするため。上書きの承認（_vbaApprovedOverwrites）は、適用の直前の再検査の
        // 再解析をまたいで保つ必要があるので、ここでは消さない。
        _vbaFileOnly = false;

        // Excel VBA 連携: 対象のモジュールに限って、Excel 側の変更を解析の前に取り込む（MainViewModel.VbaApply.cs）。
        // 衝突で「中止」・接続できず「中止」なら解析しない。VBA プロジェクトでなければ何もせず true。
        if (!await PullVbaBeforeDryRunAsync(project, patch).ConfigureAwait(true)) return;

        // 保存確認の待ち中に、破棄・別の解析・プロジェクトの切り替えがあれば、この要求は古い（C15）。
        if (generation != _analysisGeneration || !ReferenceEquals(_currentPatch, patch)
            || ProjectPane.SelectedItem?.Project.Id != project.Id)
        {
            return;
        }

        State = CenterPaneState.Loading;
        // 履歴番号の見込み。ProjectPaneが持つprojectは読み込み時のスナップショットで、その後に
        // 別の操作（コンテキスト収集画面の保存など）がprojects.jsonのnextRevisionを動かしていても
        // 追従していない。実機ではこれが戻った番号のままドライランに渡り、ログ・プレビューに
        // 出る番号と実際に使う番号が食い違った。ここで最新のnextRevisionと実体の最大番号+1を
        // 見て揃える（実際の払い出しは適用の直前にReserveRevisionNumberAsyncがやり直す）。
        var expectedRevision = await RevisionNumbering
            .PeekNextAsync(_projectStore, _revisionStore, project.Id, project.NextRevision)
            .ConfigureAwait(true);
        var guard = new PathGuard(project.Root, new PathGuardOptions
        {
            // VBA プロジェクトのミラーは .bas/.cls/.frm なので、実効の許可に加える（仕様書_VBA連携 4.1）。
            // 加えないと、適用が E202（許可されていない拡張子）で止まる。全体の設定は変えない。
            AllowedExtensions = Core.Vba.VbaExtensionPolicy.Effective(_settings.Safety.AllowedExtensions, project),
            MaxFileSizeMB = _settings.Safety.MaxFileSizeMB,
            MaxFilesPerRevision = _settings.Safety.MaxFilesPerRevision,
        });
        var context = new ApplyContext
        {
            ProjectId = project.Id,
            ProjectRoot = project.Root,
            Revision = expectedRevision,
            Settings = _settings,
            Guard = guard,
        };

        var dryRun = await _applyEngine.DryRunAsync(patch, context).ConfigureAwait(true);

        // 結果を反映する前に、「これが最新の要求か」「解析の対象が変わっていないか」を確かめ、
        // 違えば捨てる（C15）。ドライラン中にプロジェクトを切り替えると、以前は切り替え先の
        // 画面へ元のプロジェクトのドライラン結果（と、その文脈で適用してしまう_lastContext）が
        // 入っていた。破棄・別の解析の開始も同様に、待っていた古い結果は反映しない。
        if (generation != _analysisGeneration
            || !ReferenceEquals(_currentPatch, patch)
            || ProjectPane.SelectedItem?.Project.Id != project.Id)
        {
            return;
        }

        if (!dryRun.IsSuccess)
        {
            CenterError = dryRun.Errors.FirstOrDefault();
            State = CenterPaneState.Error;
            return;
        }

        _lastContext = context;
        _dryRun = dryRun.Value;
        // 適用済みのパッチ（E302）はここ＝プレビューの時点で利用者に見せる（MainViewModel.AlreadyApplied.cs）。
        SetAlreadyAppliedRevision(dryRun.Value.AlreadyAppliedRevision);
        LogDryRunFileProbes(dryRun.Value, context); // 依頼4対応: MainViewModel.DryRunDiagnostics.cs参照
        ReplaceBlocks(dryRun.Value.Plans);
        OnPropertyChanged(nameof(StatusSummaryText));
        OnPropertyChanged(nameof(StatusSummaryMainText));
        OnPropertyChanged(nameof(StatusSummaryFailedText));
        OnPropertyChanged(nameof(HasFailedBlocks));
        OnPropertyChanged(nameof(TargetSummaryText));

        // Excel VBA 連携: 適用できない変更（DELETE／RENAME の E814、CP932 で表せない文字の E811）を、適用を押す前に案内する。
        await NotifyVbaGuardAfterAnalysisAsync(project).ConfigureAwait(true);
    }

    // ApplyAsync/UndoLastAsync（適用の本体・Ctrl+Z）はMainViewModel.Apply.csへ分割している
    // （1ファイル400行上限のため。6.5適用後フックのonFailure分岐からApplyAsyncを呼ぶ都合上、
    // 同じ「適用」テーマのApply.csへまとめた）。

    private void DiscardCurrentPatch()
    {
        // 解析結果を捨てたら、そのパッチ向けの VBA の選択（ファイルだけ適用する・上書きの承認）も捨てる（不具合 8）。
        // ただし適用の最中は、適用が済んだ直後にここへ来て、そのあとの Excel への自動反映がこの選択を読むので、
        // 適用の終わり（ApplyAsync の finally）まで残す。
        if (!_isApplyInProgress) ResetVbaApplyChoices();
        _currentPatch = null;
        _dryRun = null;
        _lastContext = null;
        CenterError = null;
        SetAlreadyAppliedRevision(null);
        ReplaceBlocks(Array.Empty<BlockPlan>());
        OnPropertyChanged(nameof(StatusSummaryText));
        OnPropertyChanged(nameof(StatusSummaryMainText));
        OnPropertyChanged(nameof(StatusSummaryFailedText));
        OnPropertyChanged(nameof(HasFailedBlocks));
        OnPropertyChanged(nameof(TargetSummaryText));
    }

    private void ReplaceBlocks(IReadOnlyList<BlockPlan> plans)
    {
        // 不具合5対応: StatusSummaryTextが「いま実際にチェックが入っている件数」を都度
        // 数え直す形に変わったため（MainViewModel.Display.cs参照）、各行のチェック操作
        // （Space・チェックボックス・右クリックメニュー、いずれもBlockItemViewModel.IsSelectedの
        // setterを通る）のたびに再評価させる必要がある。入れ替え前の行の購読は必ず解除する
        // （でないと古いBlockItemViewModelが以後も参照され続け、メモリリークかつ二重発火の
        // 原因になる）。
        foreach (var old in Blocks) old.PropertyChanged -= OnAnyBlockPropertyChanged;
        Blocks.Clear();
        foreach (var plan in plans)
        {
            var item = new BlockItemViewModel(plan);
            item.PropertyChanged += OnAnyBlockPropertyChanged;
            Blocks.Add(item);
        }
        BlocksDisplay.Clear();
        foreach (var item in Blocks.Where(b => b.IsError).Concat(Blocks.Where(b => !b.IsError)))
        {
            BlocksDisplay.Add(item);
        }
        State = Blocks.Count == 0 ? CenterPaneState.Empty : CenterPaneState.Content;
        SelectedBlock = Blocks.FirstOrDefault();
    }

    /// <summary>
    /// 不具合5対応: ブロック一覧のどの行でチェックが変わっても、ステータスバーの
    /// 「N件を適用（M件は対象外）」（StatusSummaryText）を再評価させるための購読先。
    /// SelectedBlockだけを見ているOnSelectedBlockPropertyChanged（diff側との同期用）とは
    /// 別に、全行を対象に持つ必要があるため独立させている。
    /// </summary>
    private void OnAnyBlockPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(BlockItemViewModel.IsSelected)) return;
        OnPropertyChanged(nameof(StatusSummaryText));
        OnPropertyChanged(nameof(StatusSummaryMainText));
        OnPropertyChanged(nameof(StatusSummaryFailedText));
    }
}
