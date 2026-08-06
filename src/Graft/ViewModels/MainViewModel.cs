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
    private readonly SettingsStore _settingsStore;
    private readonly IDialogService _dialogs;
    private readonly IUiServices _ui;
    private readonly PatchParser _parser = new();
    private readonly Action _openSettingsRequested;

    private Settings _settings = new();
    private Patch? _currentPatch;
    private DryRunResult? _dryRun;
    private ApplyContext? _lastContext;
    private bool _dryRunFromQueue;

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
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        LayoutStore = layoutStore ?? throw new ArgumentNullException(nameof(layoutStore));
        _dialogs = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        ArgumentNullException.ThrowIfNull(patchQueue);
        _openSettingsRequested = openSettingsRequested ?? throw new ArgumentNullException(nameof(openSettingsRequested));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));

        ProjectPane = new ProjectPaneViewModel(projectStore, dialogService);
        History = new HistoryPaneViewModel(revisionStore, revisionRestorer, dialogService);
        // DiffViewModelは構築時にSettingsを固定するため、設定読み込み前は既定値で仮に構築し、
        // 読み込み後にWordWrap/ShowWhitespaceのみ反映し直す（InitializeAsync参照）。
        Diff = new DiffViewModel(new Settings(), _ui);
        Diff.PropertyChanged += OnDiffPropertyChanged;

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
        DiscardCommand = new RelayCommand(DiscardCurrentPatch);
        FocusSearchCommand = new RelayCommand(() => RequestFocusSearch?.Invoke(this, EventArgs.Empty));
        ShowHistoryCommand = new RelayCommand(() => RequestFocusHistory?.Invoke(this, EventArgs.Empty));
        AddCurrentPatchToQueueCommand = new AsyncRelayCommand(AddCurrentPatchToQueueAsync, () => _currentPatch is not null);
        OpenQueueCommand = new RelayCommand(() => RequestOpenQueue?.Invoke(this, EventArgs.Empty));
        CopyRecoveryPromptCommand = new AsyncRelayCommand(CopyRecoveryPromptAsync, () => Blocks.Any(b => !b.Plan.CanApply));

        InitializePrompt(projectStore); // 4.8.4（MainViewModel.Prompt.cs）。
    }

    public ProjectPaneViewModel ProjectPane { get; }
    public HistoryPaneViewModel History { get; }
    public DiffViewModel Diff { get; }
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

    /// <summary>ステータスバー表示。仕様書8.2「2件適用可 / 1件要確認」の書式。</summary>
    public string StatusSummaryText => _dryRun is null
        ? "解析結果はありません"
        : $"{_dryRun.ApplicableCount}件適用可 / {_dryRun.ConfirmationCount}件要確認";

    public string CurrentProjectName => ProjectPane.SelectedItem?.Name ?? "(プロジェクト未選択)";

    public ICommand PasteAndParseCommand { get; }
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

    /// <summary>起動直後の初期化。設定・レイアウト・プロジェクト一覧を読み込む。</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var settingsResult = await _settingsStore.LoadAsync(ct).ConfigureAwait(true);
        _settings = settingsResult.Value;
        Diff.WordWrap = _settings.Diff.WordWrap;
        Diff.ShowWhitespace = _settings.Diff.ShowWhitespace;

        Layout = await LayoutStore.LoadAsync(ct).ConfigureAwait(true);
        await ProjectPane.LoadAsync(ct).ConfigureAwait(true);
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
        await CheckInProgressAsync(project).ConfigureAwait(true);
        OnPropertyChanged(nameof(CurrentProjectName));
    }

    /// <summary>6.3: in_progressのまま残るリビジョンを検出し通知する（自動ロールバックは非対応）。</summary>
    private async Task CheckInProgressAsync(Project project)
    {
        var result = await _revisionStore.FindInProgressAsync(project.Id).ConfigureAwait(true);
        if (!result.IsSuccess || result.Value.Count == 0) return;

        var revisions = string.Join("、", result.Value.Select(r => $"r{r.Manifest.Revision}"));
        await _dialogs.ConfirmAsync("前回の適用が未完了です",
            $"{revisions} が完了しないまま終了した可能性があります。バックアップフォルダを確認してください。").ConfigureAwait(true);
    }

    private async void OnRevisionSelected(object? sender, RevisionRowViewModel? row)
    {
        if (row is null) { Diff.Clear(); return; }

        State = CenterPaneState.Loading;
        var plans = await History.BuildDiffPlansAsync(row, _settings.Diff.ContextLines).ConfigureAwait(true);
        _currentPatch = null;
        _dryRun = null;
        ReplaceBlocks(plans);
        OnPropertyChanged(nameof(StatusSummaryText));
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
        // IClipboardAccess.GetTextは取得できない場合にnullを返す契約のため、
        // 個別の例外保護は不要（クリップボードが他プロセスに占有されている場合もnullになる）。
        var text = _ui.Clipboard.GetText();
        if (string.IsNullOrWhiteSpace(text)) return;

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
    }

    private async Task ApplyAsync()
    {
        if (_dryRun is null || _lastContext is null) return;

        var updatedPlans = Blocks.Select(b => b.Plan with { IsSelected = b.IsSelected }).ToList();
        var updatedDryRun = _dryRun with { Plans = updatedPlans };

        if (_settings.RequireSummary && string.IsNullOrWhiteSpace(updatedDryRun.Patch.Meta.Summary))
        {
            var input = await _dialogs.PromptAsync("要約を入力", "このリビジョンの概要を入力してください。", null).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(input)) return;
            var patchWithSummary = updatedDryRun.Patch with { Meta = updatedDryRun.Patch.Meta with { Summary = input } };
            updatedDryRun = updatedDryRun with { Patch = patchWithSummary };
        }

        var confirmed = await _dialogs
            .ConfirmAsync("適用の確認", $"{updatedDryRun.ApplicableCount}件を適用します。よろしいですか？")
            .ConfigureAwait(true);
        if (!confirmed) return;

        State = CenterPaneState.Loading;
        var result = await _applyEngine.ApplyAsync(updatedDryRun, _lastContext).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            CenterError = result.Errors.FirstOrDefault();
            State = CenterPaneState.Error;
            return;
        }

        await NotifyFilesRewrittenAsync(_lastContext.ProjectRoot, result.Value).ConfigureAwait(true); // 4.8/7章: 再読込フック。
        await _dialogs.ShowMessageAsync("適用が完了しました", $"r{result.Value.Revision} として記録しました。").ConfigureAwait(true);
        FinalizeApplyFromQueueIfNeeded(); // 4.10: キュー結合適用時はキューを空にする（MainViewModel.Queue.cs）。
        DiscardCurrentPatch();
        await ProjectPane.LoadAsync().ConfigureAwait(true);
        var project = ProjectPane.SelectedItem?.Project;
        if (project is not null) await History.LoadAsync(project.Id, project.Root).ConfigureAwait(true);
    }

    private async Task UndoLastAsync()
    {
        var undone = await History.UndoLatestAsync().ConfigureAwait(true);
        if (!undone) await _dialogs.ShowMessageAsync("取り消せません", "取り消し可能な直前のリビジョンがありません。").ConfigureAwait(true);
    }

    private void DiscardCurrentPatch()
    {
        _currentPatch = null;
        _dryRun = null;
        _lastContext = null;
        CenterError = null;
        ReplaceBlocks(Array.Empty<BlockPlan>());
        OnPropertyChanged(nameof(StatusSummaryText));
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
