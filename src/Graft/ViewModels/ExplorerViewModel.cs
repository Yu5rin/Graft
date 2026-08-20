using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows.Input;
using Graft.Core;
using Graft.Editor;
using Graft.Features;
using Graft.Platform;

namespace Graft.ViewModels;

/// <summary>
/// エクスプローラビュー（仕様書4.2）のViewModel。ツリーの遅延読み込み・除外表示トグル・
/// ファイル操作（新規・リネーム・削除・パスコピー・エクスプローラで表示）・
/// <see cref="FileWatchService"/>と連携した自動更新／外部変更検知（4.6）を担う。
/// 除外判定・ファイルI/Oの実処理は<see cref="FileTreeService"/>（WPF非依存）へ委譲する。
/// 課題2: 削除の取り消し（Ctrl+Z）は<see cref="DeleteUndoStore"/>へ委譲し、このクラスは
/// 退避・復元のタイミング（削除前後）とステータスバー通知・フォーカス要求の橋渡しに徹する。
/// </summary>
public sealed class ExplorerViewModel : ObservableObject, IDisposable
{
    private readonly EditorPaneViewModel _editor;
    private readonly IDialogService _dialogs;
    private readonly IUiServices _ui;
    private readonly FileTreeService _treeService;
    private readonly FileWatchService _fileWatch = new();
    private readonly DeleteUndoStore _undoStore;
    private readonly IUiTimer _deleteNoticeTimer;

    // 細かいユーザビリティ改善4: エクスプローラのファイル名絞り込み。
    private readonly ExplorerFilterService _filterService = new();
    private readonly IUiTimer _filterDebounceTimer;

    // 依頼「エクスプローラへ既存のファイルを取り込む手段」。実コピーはFileImportServiceへ委譲する
    // （WPFに依存しない・テストプロジェクトから直接テストできる、FileTreeServiceと同じ方針）。
    private readonly FileImportService _importService = new();
    private CancellationTokenSource? _importCts;

    // 課題2: 削除直後のステータスバー案内（「Ctrl+Zで元に戻せます」）を数秒で自動的に消す。
    // 消えても取り消しスタック自体は残るため、Ctrl+Zや右クリックメニューはそのまま使える。
    private static readonly TimeSpan DeleteNoticeDuration = TimeSpan.FromSeconds(6);

    // 細かいユーザビリティ改善4: 入力のたびにディスクを再走査すると大きなプロジェクトで
    // 固まるため300msデバウンスする（SearchOverlayViewModelの150ms・DebounceMsと同じ考え方だが、
    // こちらはメモリ上の正規表現走査ではなくディスクI/Oを伴う分、やや長めに取った）。
    private const int FilterDebounceMs = 300;

    private Project? _project;
    private GitignoreFilter _filter = GitignoreFilter.Empty;
    private PathGuardOptions _guardOptions = PathGuardOptions.Default;
    private bool _showExcludedFiles;
    private bool _isLoading;
    private FileNodeViewModel? _selectedNode;
    private bool _hasDeleteUndoNotice;
    private string _deleteUndoNoticeText = string.Empty;
    private string _filterText = string.Empty;
    private bool _filterHasNoMatches;
    private bool _filterTruncated;
    private IReadOnlyList<string>? _expandedPathsBeforeFilter;
    private CancellationTokenSource? _filterCts;
    private bool _isImporting;
    private string _importProgressText = string.Empty;

    /// <summary>
    /// 絞り込み中の一致パス集合（一致ファイル自身＋その祖先フォルダ）。nullは絞り込み無し。
    /// <see cref="ApplyFilterToLevel"/>がこれを見て、ツリーに載せるノード自体を絞り込む
    /// （見た目だけの非表示にしない理由は同メソッドのコメント参照）。
    /// </summary>
    private HashSet<string>? _activeFilterVisiblePaths;

    /// <summary>
    /// プロジェクトルート直下を最後に実列挙したときの、絞り込みに関わらない全ノード（ディスク順）。
    /// <see cref="FileNodeViewModel.AllChildrenCache"/>のルート版。
    /// </summary>
    private List<FileNodeViewModel>? _rootChildrenCache;

    public ExplorerViewModel(
        Graft.Infra.AppPaths appPaths, EditorPaneViewModel editor, IDialogService dialogs, Graft.Infra.Settings settings, IUiServices ui)
    {
        ArgumentNullException.ThrowIfNull(appPaths);
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        // 10件目の不具合修正: ごみ箱への削除をITrashService経由に揃える。
        _treeService = new FileTreeService(PlatformServices.Current.Trash);
        _undoStore = new DeleteUndoStore(appPaths);
        // CreateTimerは反復タイマー（デバウンス用）のため、1回消したらStopして次の削除まで
        // 眠らせておく（Restartで毎回数え直す、SearchOverlayViewModel等と同じ使い方）。
        _deleteNoticeTimer = ui.CreateTimer(DeleteNoticeDuration, OnDeleteNoticeTimeout);
        _filterDebounceTimer = ui.CreateTimer(TimeSpan.FromMilliseconds(FilterDebounceMs), OnFilterDebounceTick);
        _guardOptions = FileTreeService.BuildGuardOptions(settings);
        _fileWatch.DirectoriesChanged += OnDirectoriesChanged;
        _fileWatch.FileContentChanged += OnFileContentChanged;
        AttachTabWatchers();

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => _project is not null, context: "ファイル一覧の更新");
        OpenCommand = new RelayCommand<FileNodeViewModel>(ExecuteOpen);
        NewFileCommand = new RelayCommand<FileNodeViewModel>(node => _ = NewFileAsync(node ?? SelectedNode), _ => _project is not null);
        NewFolderCommand = new RelayCommand<FileNodeViewModel>(node => _ = NewFolderAsync(node ?? SelectedNode), _ => _project is not null);
        // 依頼「エクスプローラへ既存のファイルを取り込む手段」2: ツールバー・右クリックメニュー
        // 共通のコマンド（ドラッグ＆ドロップはExplorerView.axaml.csがExplorerViewModel.ImportPathsAsyncを
        // 直接呼ぶ）。取り込み中の多重実行を避けるため、IsImportingの間はCanExecuteをfalseにする。
        AddFileCommand = new RelayCommand<FileNodeViewModel>(
            node => _ = AddFilesAsync(node ?? SelectedNode), _ => _project is not null && !_isImporting);
        CancelImportCommand = new RelayCommand(() => _importCts?.Cancel(), () => _isImporting);
        RenameCommand = new RelayCommand<FileNodeViewModel>(node => _ = RenameNodeAsync(node ?? SelectedNode));
        DeleteCommand = new RelayCommand<FileNodeViewModel>(node => _ = DeleteNodeAsync(node ?? SelectedNode));
        CopyPathCommand = new RelayCommand<FileNodeViewModel>(node => CopyPath(node ?? SelectedNode));
        RevealCommand = new RelayCommand<FileNodeViewModel>(node => RevealInExplorer(node ?? SelectedNode));
        UndoDeleteCommand = new AsyncRelayCommand(UndoDeleteAsync, () => _undoStore.CanUndo, context: "削除の取り消し");
        // ファイル単位の変更履歴: フォルダには適用できないため、対象がファイルのときだけ有効化する。
        ShowFileHistoryCommand = new RelayCommand<FileNodeViewModel>(
            node => RequestShowFileHistory(node ?? SelectedNode),
            node => (node ?? SelectedNode) is { IsDirectory: false, IsPlaceholder: false });
        // 細かいユーザビリティ改善4: 絞り込みボックスの「×」。
        ClearFilterCommand = new RelayCommand(() => FilterText = string.Empty, () => FilterText.Length > 0);
    }

    /// <summary>ツリーの最上位（プロジェクトルート直下）のノード一覧。</summary>
    public ObservableCollection<FileNodeViewModel> RootNodes { get; } = new();

    /// <summary>現在のプロジェクト。未選択時はnull（仕様書9.2の空状態表示に使う）。</summary>
    public Project? Project => _project;

    /// <summary>プロジェクトが選択されているかどうか（空状態表示の判定用）。</summary>
    public bool HasProject => _project is not null;

    /// <summary>ツリーを読み込み中かどうか（9.2の不確定プログレスバー表示用）。</summary>
    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }

    /// <summary>
    /// 取り込み（ドラッグ＆ドロップ・「ファイルを追加」）でファイルをコピー中かどうか。
    /// 実コピーはUIスレッドを塞がない（<see cref="FileImportService.ImportAsync"/>参照）ため、
    /// このフラグはあくまで進捗バー・中止ボタンの表示可否のためのもの。
    /// </summary>
    public bool IsImporting { get => _isImporting; private set => SetProperty(ref _isImporting, value); }

    /// <summary>取り込み中の進捗文言（例:「3/120件をコピー中... (path/to/file)」）。</summary>
    public string ImportProgressText { get => _importProgressText; private set => SetProperty(ref _importProgressText, value); }

    /// <summary>
    /// 除外ファイルを表示するトグル（仕様書4.2）。表示可否はViewの変換のみで切り替わるため再読込は不要。
    /// 細かいユーザビリティ改善4: 絞り込み中に切り替えた場合、除外ファイル配下も検索対象に
    /// 含めるかどうかが変わるため、絞り込み結果を作り直す（ExplorerFilterService.FindMatchesAsync
    /// のincludeExcluded引数参照）。
    /// </summary>
    public bool ShowExcludedFiles
    {
        get => _showExcludedFiles;
        set
        {
            if (!SetProperty(ref _showExcludedFiles, value)) return;
            if (HasFilterText) ScheduleFilterRecompute();
        }
    }

    /// <summary>
    /// 細かいユーザビリティ改善4: ファイル名絞り込みの入力文字列。設定のたびに
    /// <see cref="FilterDebounceMs"/>だけデバウンスしてから再検索する（<see cref="ScheduleFilterRecompute"/>）。
    /// Ctrl+Pのクイックオープン（「開く」ための一覧）とは別物で、こちらはツリー上の表示を
    /// 絞り込むためのもの。
    /// </summary>
    public string FilterText
    {
        get => _filterText;
        set
        {
            if (!SetProperty(ref _filterText, value, OnFilterTextChanged)) return;
        }
    }

    private void OnFilterTextChanged()
    {
        OnPropertyChanged(nameof(HasFilterText));
        ((RelayCommand)ClearFilterCommand).RaiseCanExecuteChanged();
        ScheduleFilterRecompute();
    }

    /// <summary>絞り込み中かどうか（「×」ボタンの表示・自動テストの判定に使う）。</summary>
    public bool HasFilterText => _filterText.Length > 0;

    /// <summary>
    /// 絞り込み中で、かつ1件も一致しなかったかどうか（「一致するファイルがありません」表示用）。
    /// 検索が完了するまではfalseのまま（検索中に一瞬「0件」を出して点滅させないため）。
    /// </summary>
    public bool FilterHasNoMatches { get => _filterHasNoMatches; private set => SetProperty(ref _filterHasNoMatches, value); }

    /// <summary>
    /// 一致件数が<see cref="ExplorerFilterService.MaxMatches"/>に達し、途中で打ち切られたかどうか。
    /// 「一部のみ表示しています」の注記に使う。
    /// </summary>
    public bool FilterTruncated { get => _filterTruncated; private set => SetProperty(ref _filterTruncated, value); }

    /// <summary>右クリック・キー操作の対象になる、現在選択中のノード。</summary>
    public FileNodeViewModel? SelectedNode { get => _selectedNode; set => SetProperty(ref _selectedNode, value); }

    /// <summary>
    /// 課題2: 削除直後、数秒間だけステータスバーに「Ctrl+Zで元に戻せます」を出すかどうか。
    /// 消えても<see cref="UndoDeleteCommand"/>自体は（取り消せる削除がある限り）実行できる。
    /// </summary>
    public bool HasDeleteUndoNotice { get => _hasDeleteUndoNotice; private set => SetProperty(ref _hasDeleteUndoNotice, value); }

    /// <summary>削除の取り消し案内の文言。</summary>
    public string DeleteUndoNoticeText { get => _deleteUndoNoticeText; private set => SetProperty(ref _deleteUndoNoticeText, value); }

    public ICommand RefreshCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand NewFileCommand { get; }
    public ICommand NewFolderCommand { get; }

    /// <summary>
    /// 依頼「エクスプローラへ既存のファイルを取り込む手段」2: ツールバーの「ファイルを追加」・
    /// 右クリックメニューの「ここにファイルを追加...」共通のコマンド。ファイル選択ダイアログ
    /// （複数選択、<see cref="IDialogService.PickFilesAsync"/>）で選んだ既存ファイルを、
    /// パラメータのノード（フォルダならそのフォルダ、ファイルならその親、nullなら
    /// <see cref="SelectedNode"/>にフォールバック）へコピーで取り込む。
    /// </summary>
    public ICommand AddFileCommand { get; }

    /// <summary>取り込み中の進捗表示に添える中止ボタン。<see cref="IsImporting"/>の間だけ実行できる。</summary>
    public ICommand CancelImportCommand { get; }

    public ICommand RenameCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand CopyPathCommand { get; }
    public ICommand RevealCommand { get; }

    /// <summary>細かいユーザビリティ改善4: ファイル名絞り込みボックスの「×」（<see cref="FilterText"/>を空にする）。</summary>
    public ICommand ClearFilterCommand { get; }

    /// <summary>
    /// 課題2: 直近の削除を元に戻す（Ctrl+Z、右クリックメニュー、ステータスバー通知のクリック）。
    /// エクスプローラにフォーカスがあるときだけ届くようにする配線はShellWindow.Keyboard.cs・
    /// ExplorerView.axaml（UserControl.KeyBindings）側の責務で、ここでは純粋にコマンドとして
    /// 公開するだけに留める（エディタのCtrl+Z＝テキスト取り消しとは衝突しない）。
    /// </summary>
    public ICommand UndoDeleteCommand { get; }

    /// <summary>
    /// ファイル単位の変更履歴: エクスプローラの右クリックメニュー「このファイルの変更履歴」。
    /// 実際の絞り込み・表示は履歴ペイン（<see cref="HistoryPaneViewModel"/>）の責務のため、
    /// このクラスはHistoryPaneViewModelを知らない（コンストラクタ引数にも無い）。
    /// 代わりに<see cref="ShowFileHistoryRequested"/>で対象ファイルの相対パスだけを通知し、
    /// 実際の橋渡しはShellViewModel（ExplorerとGraft.Historyの両方を知っている）に委ねる
    /// （ProjectPane.ProjectActivated等、他の画面間連携と同じ構造）。
    /// </summary>
    public ICommand ShowFileHistoryCommand { get; }

    /// <summary>
    /// <see cref="ShowFileHistoryCommand"/>が実行されたことの通知。引数は対象ファイルの
    /// プロジェクトルート基準の相対パス（'/'区切り）。
    /// </summary>
    public event EventHandler<string>? ShowFileHistoryRequested;

    /// <summary>
    /// 課題2: ツリーの再構築でフォーカスが失われた直後にViewへ再フォーカスを促す
    /// （削除・削除の取り消しの双方で発火する）。ExplorerView.axaml.csがこのイベントを購読し、
    /// TreeView自身へフォーカスを戻す（TreeView自体はItemsControl由来でFocusable="True"を
    /// 明示している）。
    /// </summary>
    public event EventHandler? FocusRequested;

    /// <summary>
    /// ファイル監視・削除取り消しの案内タイマーを停止して解放する。アプリ終了時にShellViewModelから
    /// 呼ばれる。取り消し用の退避（back/trash/）自体の後始末は<see cref="DeleteUndoStore.Cleanup"/>
    /// （StartupCoordinator.DisposeAsync経由でこのDisposeから呼ぶ）。
    /// </summary>
    public void Dispose()
    {
        _fileWatch.Dispose();
        _deleteNoticeTimer.Dispose();
        _filterDebounceTimer.Dispose();
        _filterCts?.Cancel();
        // セッション内のみ保持する方針（DeleteUndoStoreのクラスコメント参照）のため、
        // アプリ終了時に退避コピーを必ず空にする。
        _undoStore.Cleanup();
    }

    /// <summary>
    /// ファイル監視の開始結果（成功・失敗いずれも）の通知先を差し替えるフック（不具合4対応）。
    /// 既定（null）では失敗時にこのクラス自身が即座にダイアログを出すが、起動直後の
    /// 自動プロジェクト選択のときだけ、StartupCoordinatorがここへ集約用のコールバックを
    /// 差し込む。失敗時は<see cref="GraftIssue"/>を、成功時はnullを渡して必ず1回呼ぶ
    /// （成功・失敗どちらであっても「起動時の初回監視開始が完了した」こと自体を
    /// StartupCoordinator側が待ち合わせに使うため）。
    /// <para>
    /// 単に「失敗したらハンドラへ」ではなく成功時も含めて必ず通知する設計にしたのは、
    /// 実機検証で「背景の起動時検証（RunStartupValidationAsync）が、この監視開始より先に
    /// 完了してレポートを確定・表示してしまい、監視失敗の警告が一切表示されないまま
    /// 消えてしまう」レースを実際に踏んだため。StartupCoordinator側はプロジェクトが
    /// 1件以上あるとき、この通知（またはタイムアウト）を待ってからレポートを確定することで、
    /// 完了の順序に依らず必ず集約できるようにする（StartupCoordinator.Validation.cs参照）。
    /// </para>
    /// </summary>
    public Action<GraftIssue?>? WatchStartCompletedHandler { get; set; }

    /// <summary>プロジェクト切替時にShellViewModelから呼ばれる。ツリーの再構築と監視の再開始を行う。</summary>
    public async Task SetProjectAsync(Project? project, CancellationToken ct = default)
    {
        _fileWatch.Stop();
        _project = project;
        SelectedNode = null;
        RootNodes.Clear();
        OnPropertyChanged(nameof(Project));
        OnPropertyChanged(nameof(HasProject));
        // 細かいユーザビリティ改善4: プロジェクトを切り替えたら絞り込みも一旦解除する
        // （RootNodesを丸ごと作り直すため、切替前の絞り込み・退避した展開状態は意味を失う）。
        _filterDebounceTimer.Stop();
        _filterCts?.Cancel();
        _expandedPathsBeforeFilter = null;
        _activeFilterVisiblePaths = null;
        _rootChildrenCache = null;
        _filterText = string.Empty;
        OnPropertyChanged(nameof(FilterText));
        OnPropertyChanged(nameof(HasFilterText));
        FilterHasNoMatches = false;
        FilterTruncated = false;
        if (project is null) return;

        IsLoading = true;
        try
        {
            _filter = await FileTreeService.BuildFilterAsync(project, ct).ConfigureAwait(true);
            await ReconcileDirectoryAsync(null, ct).ConfigureAwait(true);

            var started = _fileWatch.Start(project.Root);
            var failureIssue = started.IsSuccess ? null : started.Issues.FirstOrDefault();
            if (WatchStartCompletedHandler is not null)
            {
                WatchStartCompletedHandler(failureIssue);
            }
            else if (failureIssue is not null)
            {
                await ShowFailureAsync("ファイル監視を開始できませんでした", started.Issues).ConfigureAwait(true);
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>指定した相対パスの祖先ディレクトリを順番に展開する（3.2 プロジェクトごとの展開状態復元用）。</summary>
    public async Task ExpandPathAsync(string relativeDirectoryPath)
    {
        if (string.IsNullOrEmpty(relativeDirectoryPath)) return;
        var segments = relativeDirectoryPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = string.Empty;
        IEnumerable<FileNodeViewModel> level = RootNodes;
        foreach (var segment in segments)
        {
            current = current.Length == 0 ? segment : $"{current}/{segment}";
            var node = level.FirstOrDefault(n => n.IsDirectory && n.RelativePath == current);
            if (node is null) return;
            node.IsExpanded = true;
            await WaitForLoadAsync(node).ConfigureAwait(true);
            level = node.Children;
        }
    }

    private static async Task WaitForLoadAsync(FileNodeViewModel node)
    {
        for (var i = 0; i < 50 && !node.IsLoaded; i++) await Task.Delay(10).ConfigureAwait(true);
    }

    /// <summary>現在展開中のフォルダの相対パス一覧を返す（3.2 プロジェクトごとの展開状態保存用）。</summary>
    public IReadOnlyList<string> GetExpandedFolderPaths()
    {
        var result = new List<string>();
        CollectExpanded(RootNodes, result);
        return result;
    }

    private static void CollectExpanded(IEnumerable<FileNodeViewModel> nodes, List<string> result)
    {
        foreach (var node in nodes.Where(n => n.IsDirectory && n.IsExpanded))
        {
            result.Add(node.RelativePath);
            CollectExpanded(node.Children, result);
        }
    }

    // ------------------------------------------------------------------
    // 細かいユーザビリティ改善4: ファイル名絞り込み。
    //
    // 遅延読み込みのツリー（未展開のフォルダは子を持たない）とは独立に、プロジェクト全体を
    // ExplorerFilterService でディスクから直接走査して一致ファイルを探す。見つかった一致の
    // 祖先フォルダだけを ExpandPathAsync で自動展開し、一致しないノードは
    // ツリー（Children/RootNodes）に載せないことで絞り込む（詳しくはApplyFilterToLevel参照。
    // 当初はIsVisibleの見た目だけの非表示にしていたが、実機Xvfb環境で描画が更新されずに
    // 残る不具合があったため、ノード自体を絞り込む方式に変更した）。
    // ------------------------------------------------------------------

    /// <summary>
    /// 入力の変化を受けて呼ばれる。絞り込みを解除した場合は即座に元へ戻し、それ以外は
    /// デバウンス（<see cref="FilterDebounceMs"/>）してから<see cref="ApplyFilterAsync"/>を呼ぶ。
    /// </summary>
    private void ScheduleFilterRecompute()
    {
        if (_project is null) return;

        if (!HasFilterText)
        {
            _filterDebounceTimer.Stop();
            _filterCts?.Cancel();
            _activeFilterVisiblePaths = null;
            FilterHasNoMatches = false;
            FilterTruncated = false;
            // ディスクを再走査せず、既知の全項目キャッシュ（AllChildrenCache/_rootChildrenCache）から
            // 絞り込み無しの状態を復元する。
            ApplyFilterToLoadedTree();
            // 絞り込みを始める前の展開状態へ戻す（要件: 「絞り込みを消したら元の展開状態へ戻る」）。
            if (_expandedPathsBeforeFilter is { } saved)
            {
                _expandedPathsBeforeFilter = null;
                CollapseNotIn(RootNodes, new HashSet<string>(saved, StringComparer.Ordinal));
            }
            return;
        }

        // 絞り込みを開始した瞬間（空→非空への変化）にだけ、現在の展開状態を退避する。
        // 以降、文字を1つ足す・消すたびに呼ばれても、退避済みなら上書きしない。
        _expandedPathsBeforeFilter ??= GetExpandedFolderPaths();
        _filterDebounceTimer.Restart();
    }

    private void OnFilterDebounceTick()
    {
        _filterDebounceTimer.Stop();
        _ = SafeHandler.RunAsync("ファイルの絞り込み", ApplyFilterAsync);
    }

    private async Task ApplyFilterAsync()
    {
        if (_project is null) return;
        var query = _filterText;
        if (query.Length == 0) return; // ScheduleFilterRecomputeが既に処理済み。

        _filterCts?.Cancel();
        var cts = new CancellationTokenSource();
        _filterCts = cts;

        ExplorerFilterResult result;
        try
        {
            result = await _filterService.FindMatchesAsync(_project, _filter, query, ShowExcludedFiles, cts.Token)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return; // 新しい入力（または絞り込み解除・プロジェクト切替）に置き換えられた。
        }

        // 検索が完了するまでの間に、より新しい検索へ差し替えられていたら結果を捨てる
        // （入力し続けている間に古い結果で表示を上書きしないため）。
        if (!ReferenceEquals(_filterCts, cts) || _filterText != query) return;

        var visiblePaths = BuildVisiblePathSet(result.MatchedRelativePaths);
        _activeFilterVisiblePaths = visiblePaths;

        // ExpandPathAsyncは「未展開だった経路」の初回列挙（ReconcileDirectoryAsync）をトリガする
        // ためのものであり、既に読み込み済みのフォルダの子には効かない（FileNodeViewModel.IsExpanded
        // のsetterは初回のみExpandRequestedを発火するため）。そのため、ここで先に「既にロード済みの
        // 全フォルダ」へディスクを再走査せず絞り込みを適用しておく。新規に展開される経路は
        // ReconcileDirectoryAsync自身が都度絞り込みを適用する（同メソッド参照）。
        ApplyFilterToLoadedTree();

        // 一致したファイルの祖先フォルダを自動的に展開する（重複を除いた一意なフォルダ単位で）。
        var dirsToExpand = result.MatchedRelativePaths
            .Select(GetParentRelativePath)
            .Where(d => d.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        foreach (var dir in dirsToExpand)
        {
            await ExpandPathAsync(dir).ConfigureAwait(true);
        }

        if (!ReferenceEquals(_filterCts, cts) || _filterText != query) return; // 展開中に差し替えられた場合の再ガード。

        FilterHasNoMatches = result.MatchedRelativePaths.Count == 0;
        FilterTruncated = result.Truncated;
    }

    private static HashSet<string> BuildVisiblePathSet(IReadOnlyList<string> matchedRelativePaths)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in matchedRelativePaths)
        {
            set.Add(path);
            for (var dir = GetParentRelativePath(path); dir.Length > 0; dir = GetParentRelativePath(dir))
            {
                set.Add(dir);
            }
        }

        return set;
    }

    private static string GetParentRelativePath(string relativePath)
    {
        var idx = relativePath.LastIndexOf('/');
        return idx < 0 ? string.Empty : relativePath[..idx];
    }

    /// <summary>
    /// ディスクを再走査せず、既知の全項目キャッシュ（<see cref="_rootChildrenCache"/>・
    /// <see cref="FileNodeViewModel.AllChildrenCache"/>）から現在の絞り込み条件
    /// （<see cref="_activeFilterVisiblePaths"/>）を再適用する。絞り込み文字列の変更・解除の
    /// たびに呼ぶ（新規に展開されるフォルダの初回列挙自体はReconcileDirectoryAsyncが担当する）。
    /// </summary>
    private void ApplyFilterToLoadedTree()
    {
        if (_rootChildrenCache is { } rootFull) ApplyFilterToLevel(RootNodes, rootFull);
    }

    /// <summary>
    /// 細かいユーザビリティ改善4の要（不具合の直接の修正箇所）: <paramref name="fullList"/>
    /// （このフォルダの絞り込みに関わらない全子ノード）から、現在の絞り込み条件に一致する
    /// ものだけを<paramref name="target"/>（実際にツリーへバインドされているコレクション。
    /// Children/RootNodes）へ反映する。
    /// <para>
    /// 経緯（実機Xvfb環境で発見した不具合）: 当初は全ノードを常にChildrenへ入れたうえで、
    /// 一致しないものだけFileNodeViewModel.IsVisible（IsExcluded・ShowExcludedFilesと同じ
    /// MultiBinding経由）をfalseにして見た目だけ隠す設計だった。しかし展開済みフォルダの
    /// 直下で、既に実体化済みの兄弟ノードのうち一部だけをIsVisible=falseへ切り替えても、
    /// 実機（Xvfb）では描画が更新されず残ってしまう事例が確認された（ヘッドレス自動テストは
    /// ウィンドウを実際に描画しないため、この症状は再現しなかった）。VirtualizingStackPanel
    /// 配下での、既に実体化済みの項目に対するIsVisible変更が、ヒットテストは正しく無効化される
    /// （クリックしても選択されない）一方で再描画までは反映されない、という描画パス固有の
    /// 問題と見られる。この問題を回避するため、絞り込みは「見た目を隠す」のではなく
    /// 「そもそもツリーへ載せない」データレベルの絞り込みに変更した。
    /// </para>
    /// <para>
    /// ノードインスタンス自体は<see cref="ReconcileDirectoryAsync"/>側で既存のものを使い回す
    /// （<c>fullList</c>を都度作り直すのではなく、同一パスの既存インスタンスを更新して積む）ため、
    /// 一時的にツリーから外れても展開状態・読み込み済みフラグ・孫ノードは保持される。絞り込みを
    /// 解除・変更したときに再列挙せず即座に元へ戻せるのはこのため。
    /// </para>
    /// </summary>
    private void ApplyFilterToLevel(ObservableCollection<FileNodeViewModel> target, List<FileNodeViewModel> fullList)
    {
        var visible = _activeFilterVisiblePaths is { } visiblePaths
            ? fullList.Where(n => visiblePaths.Contains(n.RelativePath)).ToList()
            : fullList;

        if (!target.SequenceEqual(visible))
        {
            target.Clear();
            foreach (var node in visible) target.Add(node);
        }

        // 現在ロード済みの子フォルダについてのみ再帰する（未ロードのフォルダはプレースホルダのみで
        // AllChildrenCacheを持たないため対象外。展開されたときにReconcileDirectoryAsyncが
        // 現在の絞り込みを反映して初回列挙する）。target（絞り込み後）ではなくfullList（絞り込みに
        // 関わらない全項目）を辿るのは、現在は非表示のフォルダの中身も、後で絞り込みが変わった
        // ときに正しい状態へ戻せるようにするため。
        foreach (var dir in fullList.Where(n => n.IsDirectory && n.IsLoaded))
        {
            if (dir.AllChildrenCache is { } dirFull) ApplyFilterToLevel(dir.Children, dirFull);
        }
    }

    /// <summary>絞り込み中に自動展開されたフォルダのうち、元々（絞り込み開始前に）開いていなかったものを閉じ直す。</summary>
    private static void CollapseNotIn(IEnumerable<FileNodeViewModel> nodes, HashSet<string> savedPaths)
    {
        foreach (var node in nodes.Where(n => n.IsDirectory))
        {
            if (node.IsExpanded && !savedPaths.Contains(node.RelativePath)) node.IsExpanded = false;
            CollapseNotIn(node.Children, savedPaths);
        }
    }

    private async Task RefreshAsync()
    {
        if (_project is null) return;
        IsLoading = true;
        try
        {
            _filter = await FileTreeService.BuildFilterAsync(_project, CancellationToken.None).ConfigureAwait(true);
            await ReconcileRecursivelyAsync(null).ConfigureAwait(true);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task ReconcileRecursivelyAsync(FileNodeViewModel? dirNode)
    {
        await ReconcileDirectoryAsync(dirNode, CancellationToken.None).ConfigureAwait(true);
        var loadedChildren = (dirNode?.Children ?? RootNodes).Where(n => n.IsDirectory && n.IsLoaded).ToList();
        foreach (var child in loadedChildren) await ReconcileRecursivelyAsync(child).ConfigureAwait(true);
    }

    /// <summary>
    /// 指定ディレクトリ（nullはプロジェクトルート）の子要素を実体と突き合わせて更新する。
    /// 細かいユーザビリティ改善4: 既存インスタンスの検索元を、現在ツリーへ表示中の
    /// <c>target</c>（絞り込みで一部が除外されている可能性がある）ではなく、絞り込みに
    /// 関わらない直近の全項目キャッシュ（<see cref="FileNodeViewModel.AllChildrenCache"/>・
    /// <see cref="_rootChildrenCache"/>）から探す。これにより、絞り込みで一時的にツリーから
    /// 除外されていたノードも同一インスタンスを使い回せ、展開状態等を失わない
    /// （<see cref="ApplyFilterToLevel"/>のコメント参照）。
    /// </summary>
    private async Task ReconcileDirectoryAsync(FileNodeViewModel? dirNode, CancellationToken ct)
    {
        if (_project is null) return;
        var relativeDir = dirNode?.RelativePath ?? string.Empty;
        var listed = await _treeService.ListChildrenAsync(_project, relativeDir, _filter, ct).ConfigureAwait(true);
        if (!listed.IsSuccess) return; // 監視イベントで頻発するため、消失等は静かに諦める

        var target = dirNode?.Children ?? RootNodes;
        var previousFull = dirNode is null ? _rootChildrenCache : dirNode.AllChildrenCache;
        var previousByPath = previousFull is null
            ? new Dictionary<string, FileNodeViewModel>()
            : previousFull.ToDictionary(n => n.RelativePath);

        var merged = new List<FileNodeViewModel>(listed.Value.Count);
        foreach (var entry in listed.Value)
        {
            if (previousByPath.TryGetValue(entry.RelativePath, out var node))
            {
                node.UpdateEntry(entry);
            }
            else
            {
                node = new FileNodeViewModel(entry, dirNode);
                node.ExpandRequested += OnNodeExpandRequested;
            }
            merged.Add(node);
        }

        if (dirNode is null) _rootChildrenCache = merged; else dirNode.AllChildrenCache = merged;

        ApplyFilterToLevel(target, merged);
    }

    private async void OnNodeExpandRequested(object? sender, EventArgs e)
        => await SafeHandler.RunAsync("フォルダの展開", async () =>
        {
            if (sender is FileNodeViewModel node) await ReconcileDirectoryAsync(node, CancellationToken.None).ConfigureAwait(true);
        }).ConfigureAwait(true);

    /// <summary>FileWatchServiceからのデバウンス済み通知。変更のあったディレクトリだけを更新する（4.2）。</summary>
    private async void OnDirectoriesChanged(object? sender, IReadOnlyList<string> dirs)
        => await SafeHandler.RunAsync("ファイル一覧の更新", async () =>
        {
            foreach (var dir in dirs)
            {
                if (dir.Length == 0)
                {
                    await ReconcileDirectoryAsync(null, CancellationToken.None).ConfigureAwait(true);
                    continue;
                }
                var node = FindLoadedNode(RootNodes, dir);
                if (node is not null) await ReconcileDirectoryAsync(node, CancellationToken.None).ConfigureAwait(true);
            }
        }).ConfigureAwait(true);

    /// <summary>
    /// 外部変更検知（仕様書4.6）。判断はエディタ側へ委ねる。
    /// 未保存編集が無ければ黙って再読込され、あればエディタ上に非モーダルの
    /// 「ディスク上で変更されています」バーが表示される（E702）。
    /// </summary>
    private async void OnFileContentChanged(object? sender, string fullPath)
        => await SafeHandler.RunAsync("外部変更の反映",
            () => _editor.NotifyExternalChangeAsync(fullPath)).ConfigureAwait(true);

    private static FileNodeViewModel? FindLoadedNode(IEnumerable<FileNodeViewModel> nodes, string relativePath)
    {
        foreach (var node in nodes)
        {
            if (!node.IsDirectory) continue;
            if (node.RelativePath == relativePath) return node;
            if (node.IsLoaded && relativePath.StartsWith(node.RelativePath + "/", StringComparison.Ordinal))
            {
                var found = FindLoadedNode(node.Children, relativePath);
                if (found is not null) return found;
            }
        }
        return null;
    }

    private void ExecuteOpen(FileNodeViewModel? node)
    {
        if (node is null || node.IsPlaceholder) return;
        if (node.IsDirectory) { node.IsExpanded = !node.IsExpanded; return; }
        _ = OpenFileNodeAsync(node.FullPath);
    }

    private async Task OpenFileNodeAsync(string fullPath)
    {
        var opened = await _editor.OpenFileAsync(fullPath).ConfigureAwait(true);
        if (!opened.IsSuccess) await ShowFailureAsync("ファイルを開けませんでした", opened.Issues).ConfigureAwait(true);
    }

    private static (FileNodeViewModel? DirNode, string RelativeDir) ResolveTargetDirectory(FileNodeViewModel? contextNode)
        => contextNode is null
            ? (null, string.Empty)
            : contextNode.IsDirectory
                ? (contextNode, contextNode.RelativePath)
                : (contextNode.Parent, contextNode.Parent?.RelativePath ?? string.Empty);

    private async Task NewFileAsync(FileNodeViewModel? contextNode)
    {
        if (_project is null) return;
        var (dirNode, relativeDir) = ResolveTargetDirectory(contextNode);
        var name = await _dialogs
            .PromptAsync("新規ファイル", "ファイル名を入力してください（拡張子を含めてください。例: memo.md。拡張子なしのファイル名も作成できます）。", null)
            .ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name)) return;

        // CI環境固有のごく低確率な失敗調査で発見したレース対策: 抑制登録は必ず実書き込みより
        // 前に行う（SuppressPath/SuppressDirectoryのクラスコメント・RevealCreatedNodeAsyncの
        // クラスコメント参照）。CreateFileAsync（内部でFileTextIO.WriteAsyncが一時ファイル
        // 書き込み→リネームする）は実ディスクI/Oを伴うため、書き込み後にSuppressPathを呼ぶ
        // 従来の順序だと、「作成完了からSuppressPath呼び出しまでの間」にFileSystemWatcherが
        // 自分自身の書き込みを検知してしまう窓ができる。通常は数マイクロ秒で無視できる窓だが、
        // CPU競合下（低速なCIランナー・高負荷時）ではディスパッチャの再開が数百ms遅れることが
        // あり、その間にウォッチャー起点の別経路のReconcileDirectoryAsyncが先に走ってRootNodes/
        // Childrenへ反映してしまう。すると「作成した項目をツリー上で選択状態にする」処理
        // （RevealCreatedNodeAsync）がまだ走っていないのに、項目自体はツリーに現れてしまい、
        // 利用者から見ると「作成はされたのに選択されない」という不具合になる
        // （このクラスの回帰テストNewFileRevealTestsが検出）。書き込み前に確定できる想定パスへ
        // 先に抑制を掛けておけば、ウォッチャーがいつ気付いてもこの窓自体が存在しない。
        // 一時ファイル名（乱数）はSuppressPathで個別に予測できないため、対象ディレクトリごと
        // SuppressDirectoryで併せて抑制する（同メソッドのコメント参照）。
        var prospectiveFullPath = ToFullPath(CombineRelative(relativeDir, name));
        _fileWatch.SuppressDirectory(dirNode is null ? _project.Root : dirNode.FullPath);
        _fileWatch.SuppressPath(prospectiveFullPath);

        var created = await _treeService.CreateFileAsync(_project, relativeDir, name, _guardOptions).ConfigureAwait(true);
        if (!created.IsSuccess) { await ShowFailureAsync("ファイルを作成できませんでした", created.Issues).ConfigureAwait(true); return; }

        var fullPath = ToFullPath(created.Value);
        await RevealCreatedNodeAsync(dirNode, created.Value).ConfigureAwait(true);
        await OpenFileNodeAsync(fullPath).ConfigureAwait(true);
    }

    private async Task NewFolderAsync(FileNodeViewModel? contextNode)
    {
        if (_project is null) return;
        var (dirNode, relativeDir) = ResolveTargetDirectory(contextNode);
        var name = await _dialogs.PromptAsync("新規フォルダ", "フォルダ名を入力してください。", null).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name)) return;

        // NewFileAsyncと同じレース対策（同メソッドのコメント参照）: 抑制は実際にディレクトリを
        // 作成するより前に登録する。
        _fileWatch.SuppressDirectory(dirNode is null ? _project.Root : dirNode.FullPath);
        _fileWatch.SuppressPath(ToFullPath(CombineRelative(relativeDir, name)));

        var created = await _treeService.CreateFolderAsync(_project, relativeDir, name, _guardOptions).ConfigureAwait(true);
        if (!created.IsSuccess) { await ShowFailureAsync("フォルダを作成できませんでした", created.Issues).ConfigureAwait(true); return; }

        await RevealCreatedNodeAsync(dirNode, created.Value).ConfigureAwait(true);
        RequestFocus();
    }

    /// <summary>
    /// 不具合2対応: 新規作成したファイル・フォルダをツリー上で実際に確認できるようにする。
    /// 従来はReconcileDirectoryAsyncで内部データを更新するだけで、作成先の親フォルダが
    /// 折りたたまれている（未展開・未読込）場合はツリーの見た目に反映されず、
    /// 「作成しても何も表示されない」ように見えていた（真の原因）。
    /// ここで親フォルダを自動展開し、作成した項目をツリー上で選択状態にする
    /// （TreeView.AutoScrollToSelectedItemの既定値がtrueのため、選択に追従してスクロールもされる）。
    /// </summary>
    private async Task RevealCreatedNodeAsync(FileNodeViewModel? dirNode, string createdRelativePath)
    {
        await ReconcileDirectoryAsync(dirNode, CancellationToken.None).ConfigureAwait(true);
        // ReconcileDirectoryAsyncで既に実列挙を終えているため、MarkExpandedはIsExpanded/IsLoadedの
        // 状態だけを追従させる（ExpandRequestedを再度発火させて二重列挙しないようにするため、
        // IsExpanded=trueへの単純な代入ではなくこちらを使う）。
        dirNode?.MarkExpanded();

        var target = dirNode?.Children ?? RootNodes;
        var created = target.FirstOrDefault(n => n.RelativePath == createdRelativePath);
        if (created is not null) SelectedNode = created;
    }

    // ------------------------------------------------------------------
    // 依頼「エクスプローラへ既存のファイルを取り込む手段」。利用者の指摘
    // 「ファイルをボタンやドラッグ＆ドロップでエクスプローラーに追加できないのですね」への対応。
    //
    // 【常にコピー】 元の場所からファイルが消えると事故になるため、Windowsの「同一ドライブ内は
    // 移動」という慣習にはあえて従わず、常にコピーする（FileImportServiceのクラスコメント参照）。
    //
    // 【衝突（同名の項目が既にある）の扱い】 黙って上書きしないこと、が要件のため、
    // 3択の確認ダイアログ（上書き／別名で保存／中止）を都度確認する。「別名で保存」では
    // 新規ファイル作成と同じPromptAsyncで名前を編集させ、無効な名前・再度の衝突はループして
    // 確認し直す。この確認は「ドロップされたトップレベルの項目」単位（＝1回のドラッグ＆ドロップや
    // ファイル選択で選んだ各ファイル・フォルダ）で行い、フォルダの中身1件ごとには行わない
    // （数百件のフォルダをドロップするたびに数百回の確認が要求されるのは非現実的なため。
    // トップレベルで「上書き」を選んだ場合、フォルダの中身はマージ＝同名ファイルは黙って
    // 上書きされる。これは「そのフォルダごと置き換える」という利用者の意思決定に含まれると
    // 判断した）。
    //
    // 【安全機構の適用範囲】 プロジェクト外への書き込み・シンボリックリンク経由の脱出は
    // PathGuard.ResolveImportTarget（_guardOptions越し）で従来どおり防ぐ。拡張子ホワイトリスト・
    // ファイルサイズ上限は取り込みには適用しない（FileImportServiceのクラスコメント参照）。
    //
    // 【UIスレッドを塞がない・進捗・中止】 実コピーはFileImportService.ImportAsync内部の
    // Task.Runでスレッドプールへ逃がす。進捗はSystem.Progress<T>で（構築したスレッド＝UIスレッドの
    // SynchronizationContext経由で自動的にマーシャリングされるため）安全にUIへ反映できる。
    // 中止はCancellationTokenSourceを介し、ImportProgressText横の「中止」ボタン
    // （ExplorerView.axaml）から呼べる。
    // ------------------------------------------------------------------

    /// <summary>
    /// ツールバー「ファイルを追加」・右クリックメニュー「ここにファイルを追加...」の実体。
    /// 複数ファイルを選べるダイアログ（対応していない実装ではProject選択にフォールバック、
    /// <see cref="IDialogService.PickFilesAsync"/>参照）で選んだ既存ファイルを取り込む。
    /// </summary>
    private async Task AddFilesAsync(FileNodeViewModel? contextNode)
    {
        if (_project is null) return;
        var picked = await _dialogs.PickFilesAsync("追加するファイルを選択").ConfigureAwait(true);
        if (picked is null || picked.Count == 0) return;

        await ImportPathsAsync(contextNode, picked).ConfigureAwait(true);
    }

    /// <summary>
    /// 取り込みの共通入口。ドラッグ＆ドロップ（ExplorerView.axaml.cs）・「ファイルを追加」
    /// ボタン・右クリックメニューの3経路から呼ばれる。<paramref name="contextNode"/>は
    /// 落とされた／右クリックされたノード（<see cref="ResolveTargetDirectory"/>と同じ規約:
    /// フォルダならそのフォルダ、ファイルならその親、nullならプロジェクトルート）。
    /// </summary>
    public async Task ImportPathsAsync(FileNodeViewModel? contextNode, IReadOnlyList<string> sourcePaths)
    {
        if (_project is null || sourcePaths.Count == 0 || IsImporting) return;

        var (dirNode, relativeDir) = ResolveTargetDirectory(contextNode);
        var (plan, skippedCount, preflightIssues) = await BuildImportPlanAsync(relativeDir, sourcePaths).ConfigureAwait(true);

        if (plan.Count == 0)
        {
            // 全件が「中止」で見送られた場合は静かに終える（利用者が能動的にやめただけのため）。
            // パスの検証自体に失敗した項目があれば伝える。
            if (preflightIssues.Count > 0) await ShowFailureAsync("取り込めませんでした", preflightIssues).ConfigureAwait(true);
            return;
        }

        _importCts = new CancellationTokenSource();
        SetImporting(true);
        ImportProgressText = plan.Count == 1 ? "取り込み中..." : $"0/{plan.Count}件を取り込み中...";

        // NewFileAsyncと同じレース対策（同メソッドのコメント参照）: 取り込み先ディレクトリの
        // 抑制は実コピーより前に登録しておく。個別の最終パス（衝突時の別名づけ込み）は
        // コピー後の実行結果（outcomes）でしか確定しないため、このディレクトリ単位の抑制は
        // 「コピー中の一時ファイル・書き込みイベントに巻き込まれたディレクトリ再列挙が
        // Revealより先に走ってしまう」窓を塞ぐための保険であり、下のoutcomeごとの
        // SuppressPath（確定パスへの抑制）と役割を分担する。
        _fileWatch.SuppressDirectory(dirNode is null ? _project.Root : dirNode.FullPath);

        IReadOnlyList<FileImportItemOutcome> outcomes;
        try
        {
            // System.Progress<T>はコンストラクタ時点（＝ここ、UIスレッド）のSynchronizationContextを
            // 捕まえ、Report呼び出し（FileImportService内部のTask.Run＝スレッドプール上）を
            // 自動的にそのコンテキストへポストする。手動でDispatcher.Postする必要は無い。
            var progress = new Progress<FileImportProgress>(p =>
                ImportProgressText = plan.Count == 1
                    ? "取り込み中..."
                    : $"{p.CompletedFiles}/{p.TotalFiles}件をコピー中... ({p.CurrentRelativePath})");
            outcomes = await _importService.ImportAsync(plan, progress, _importCts.Token).ConfigureAwait(true);
        }
        finally
        {
            _importCts.Dispose();
            _importCts = null;
            SetImporting(false);
            ImportProgressText = string.Empty;
        }

        // FileWatchServiceとの二重反映防止（NewFileAsync/NewFolderAsyncと同じ作法）。
        // 大量ファイルの取り込みでは監視イベント自体はデバウンスで自然にまとまるが、
        // 自分で書いたトップレベルの項目だけは明示的に抑制しておく。
        foreach (var outcome in outcomes.Where(o => o.IsSuccess))
        {
            _fileWatch.SuppressPath(outcome.Item.DestinationFullPath);
        }

        await ReconcileDirectoryAsync(dirNode, CancellationToken.None).ConfigureAwait(true);
        dirNode?.MarkExpanded();

        // 複数取り込んだ場合、TreeView.SelectedItemは単一選択のため最後に成功した項目を選択する
        // （新規ファイル・新規フォルダも1件ずつしか選択状態にできない仕様のため、この単一選択の
        // 制約自体は既存の挙動と同じ）。
        var lastSuccess = outcomes.LastOrDefault(o => o.IsSuccess);
        if (lastSuccess is not null)
        {
            var target = dirNode?.Children ?? RootNodes;
            var createdNode = target.FirstOrDefault(n => n.RelativePath == lastSuccess.Item.DestinationRelativePath);
            if (createdNode is not null) SelectedNode = createdNode;
        }

        await ReportImportResultAsync(outcomes, skippedCount, preflightIssues).ConfigureAwait(true);
    }

    private void SetImporting(bool value)
    {
        IsImporting = value;
        ((RelayCommand)CancelImportCommand).RaiseCanExecuteChanged();
        ((RelayCommand<FileNodeViewModel>)AddFileCommand).RaiseCanExecuteChanged();
    }

    /// <summary>
    /// ドロップ・選択された各トップレベル項目について、配置先パスの検証（PathGuard）と
    /// 同名衝突の確認（UIダイアログ）を行い、実コピー用の計画（<see cref="FileImportPlanItem"/>）を
    /// 組み立てる。ダイアログを伴うためUIスレッドで逐次実行する（実コピー本体は
    /// <see cref="ImportPathsAsync"/>側でTask.Runへ逃がす）。
    /// </summary>
    private async Task<(List<FileImportPlanItem> Plan, int SkippedCount, List<GraftIssue> PreflightIssues)> BuildImportPlanAsync(
        string relativeDir, IReadOnlyList<string> sourcePaths)
    {
        var plan = new List<FileImportPlanItem>();
        var preflightIssues = new List<GraftIssue>();
        var skippedCount = 0;

        foreach (var rawSource in sourcePaths)
        {
            if (string.IsNullOrWhiteSpace(rawSource)) continue;
            var source = rawSource.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var isDirectory = Directory.Exists(source);
            var isFile = !isDirectory && File.Exists(source);
            if (!isDirectory && !isFile) continue; // ドラッグ中に消えた等。静かに無視する。

            var name = Path.GetFileName(source);
            if (string.IsNullOrEmpty(name)) continue;

            var overwrite = false;
            while (true)
            {
                var resolved = FileImportService.ResolveDestination(_project!, relativeDir, name, _guardOptions);
                if (!resolved.IsSuccess)
                {
                    preflightIssues.AddRange(resolved.Issues);
                    break;
                }

                if (!FileImportService.DestinationExists(resolved.Value))
                {
                    plan.Add(new FileImportPlanItem
                    {
                        SourceFullPath = source,
                        DestinationFullPath = resolved.Value,
                        DestinationRelativePath = CombineRelative(relativeDir, name),
                        IsDirectory = isDirectory,
                        Overwrite = overwrite,
                    });
                    break;
                }

                // 黙って上書きしないこと（依頼の必須要件）。上書き／別名で保存／中止の3択で確認する。
                // 不具合2点検（実機報告の横断チェック）: 「上書き」は取り込み先の既存ファイルを
                // バックアップ無しでFile.Copy(overwrite: true)により完全に置き換える不可逆な操作
                // （FileImportService参照）。AvaloniaDialogService.ConfirmThreeWayAsyncの既定ボタン
                // （IsDefault、Enterで実行される）にこの破壊的な選択肢を渡さないよう、非破壊的な
                // 「別名で保存」をyesLabel（既定）に、「上書き」をnoLabelに渡す
                // （下のdecision==true/falseの分岐もこの入れ替えに合わせて反転させている）。
                var kind = isDirectory ? "フォルダ" : "ファイル";
                var decision = await _dialogs.ConfirmThreeWayAsync(
                    "同名の項目があります",
                    $"取り込み先に同名の{kind}「{name}」が既にあります。上書きしますか？",
                    "別名で保存", "上書き").ConfigureAwait(true);

                if (decision == false)
                {
                    overwrite = true;
                    plan.Add(new FileImportPlanItem
                    {
                        SourceFullPath = source,
                        DestinationFullPath = resolved.Value,
                        DestinationRelativePath = CombineRelative(relativeDir, name),
                        IsDirectory = isDirectory,
                        Overwrite = true,
                    });
                    break;
                }

                if (decision == true)
                {
                    var suggestion = SuggestUniqueName(relativeDir, name, isDirectory);
                    var newName = await _dialogs.PromptAsync(
                            "別名で保存", $"「{name}」という名前は既に使われています。新しい名前を入力してください。", suggestion)
                        .ConfigureAwait(true);
                    if (string.IsNullOrWhiteSpace(newName))
                    {
                        skippedCount++;
                        break;
                    }
                    name = newName;
                    continue; // 新しい名前で衝突を再チェックする。
                }

                // decision == null: キャンセル（この項目のみ中止し、他の項目は続行する）。
                skippedCount++;
                break;
            }
        }

        return (plan, skippedCount, preflightIssues);
    }

    /// <summary>「別名で保存」の初期候補（"name (2).ext"方式）を、衝突しなくなるまで採番する。</summary>
    private string SuggestUniqueName(string relativeDir, string name, bool isDirectory)
    {
        var ext = isDirectory ? string.Empty : Path.GetExtension(name);
        var stem = isDirectory ? name : Path.GetFileNameWithoutExtension(name);
        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{stem} ({i}){ext}";
            var resolved = FileImportService.ResolveDestination(_project!, relativeDir, candidate, _guardOptions);
            if (resolved.IsSuccess && !FileImportService.DestinationExists(resolved.Value)) return candidate;
        }
        // 極端に多い衝突が続いた場合の保険。
        return $"{stem} ({Guid.NewGuid():N}){ext}";
    }

    /// <summary>
    /// 取り込み結果を利用者へ伝える。全件成功（スキップのみ含む）なら静かに終える。1件でも
    /// 失敗・中止があれば、成功件数と合わせて「何が成功し何が失敗したか」を伝える
    /// （依頼の必須要件: 部分的な失敗の内訳を伝えること）。
    /// </summary>
    private Task ReportImportResultAsync(
        IReadOnlyList<FileImportItemOutcome> outcomes, int skippedCount, IReadOnlyList<GraftIssue> preflightIssues)
    {
        var failures = outcomes.Where(o => !o.IsSuccess && !o.WasCancelled).ToList();
        var cancelledCount = outcomes.Count(o => o.WasCancelled);
        var successCount = outcomes.Count(o => o.IsSuccess);

        var messages = new List<string>();
        messages.AddRange(preflightIssues.Select(i => i.ToDisplayText()));
        messages.AddRange(failures.Select(f => $"{f.Item.DestinationRelativePath}: {f.Issue?.ToDisplayText() ?? "取り込みに失敗しました"}"));
        if (cancelledCount > 0) messages.Add($"取り込みを中止したため、{cancelledCount}件は取り込まれていません。");

        // 同名衝突で利用者自身が個別に「中止」を選んだ項目は、その場（3択ダイアログ）で
        // 既に本人の判断を経ているため、他に問題が無ければあらためて通知しない。他の失敗・
        // 中止と合わせて表示する場合のみ、内訳として一言添える。
        if (skippedCount > 0 && messages.Count > 0)
        {
            messages.Add($"同名のため取り込みを見送った項目: {skippedCount}件");
        }

        if (messages.Count == 0) return Task.CompletedTask; // 全件成功(同名スキップのみを含む)。

        var title = successCount > 0
            ? $"一部のファイルを取り込めませんでした（成功 {successCount}件）"
            : "ファイルを取り込めませんでした";
        return _dialogs.ShowMessageAsync(title, string.Join(Environment.NewLine, messages));
    }

    private static string CombineRelative(string relativeDir, string name)
        => string.IsNullOrEmpty(relativeDir) ? name : $"{relativeDir.TrimEnd('/')}/{name}";

    private async Task RenameNodeAsync(FileNodeViewModel? node)
    {
        if (_project is null || node is null || node.IsPlaceholder) return;
        var name = await _dialogs.PromptAsync("名前の変更", "新しい名前を入力してください。", node.Name).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name) || name == node.Name) return;

        // NewFileAsyncと同じレース対策（同メソッドのコメント参照）: 新しいパスの抑制は
        // 実際のリネームより前に登録する（FileTreeService.RenameAsyncと同じ組み立て方で
        // 想定パスを事前計算する）。元のパスは消える側なので抑制の意味は薄いが、
        // OSによってはリネームがDelete+Createとして観測されることもあるため念のため
        // こちらも先に抑制しておく。
        var newRelativePath = CombineRelative(GetParentRelativePath(node.RelativePath), name);
        var newFullPath = ToFullPath(newRelativePath);
        _fileWatch.SuppressDirectory(node.Parent is null ? _project.Root : node.Parent.FullPath);
        _fileWatch.SuppressPath(node.FullPath);
        _fileWatch.SuppressPath(newFullPath);

        var renamed = await _treeService.RenameAsync(_project, node.RelativePath, name, node.IsDirectory, _guardOptions).ConfigureAwait(true);
        if (!renamed.IsSuccess) { await ShowFailureAsync("名前を変更できませんでした", renamed.Issues).ConfigureAwait(true); return; }

        // 開いているタブを新しいパスへ追従させる（4.2・4.3）。
        if (!node.IsDirectory) _editor.NotifyRenamed(node.FullPath, newFullPath);
        await ReconcileDirectoryAsync(node.Parent, CancellationToken.None).ConfigureAwait(true);
    }

    private async Task DeleteNodeAsync(FileNodeViewModel? node)
    {
        if (_project is null || node is null || node.IsPlaceholder) return;
        var kind = node.IsDirectory ? "フォルダ" : "ファイル";
        var confirmed = await _dialogs
            .ConfirmAsync("削除の確認", $"{kind}「{node.Name}」をごみ箱へ移動します。よろしいですか？")
            .ConfigureAwait(true);
        if (!confirmed) return;

        // 課題2: 実際の削除（ごみ箱送り／完全削除）より前に、アプリ内Ctrl+Zで戻せるよう
        // 退避コピーを取っておく。退避自体に失敗しても（ディスク容量不足等）、OSのごみ箱という
        // 別の安全網があるため削除自体は続行する。取り消し通知は退避に成功した場合のみ出す。
        var staged = await _undoStore.StageAsync(node.FullPath, node.IsDirectory).ConfigureAwait(true);

        // NewFileAsyncと同じレース対策（同メソッドのコメント参照）: 抑制は実際の削除より前に
        // 登録する。
        _fileWatch.SuppressDirectory(node.Parent is null ? _project.Root : node.Parent.FullPath);
        _fileWatch.SuppressPath(node.FullPath);
        var deleted = await _treeService.DeleteAsync(_project, node.RelativePath, node.IsDirectory, _guardOptions).ConfigureAwait(true);
        if (!deleted.IsSuccess)
        {
            if (staged.IsSuccess) _undoStore.DiscardLast(); // 実際には削除されなかったので退避コピーだけ後始末する
            await ShowFailureAsync("削除できませんでした", deleted.Issues).ConfigureAwait(true);
            return;
        }

        // 削除されたファイルのタブは開いたままにできないため閉じる（4.2・4.3）。
        if (!node.IsDirectory) await _editor.NotifyDeletedAsync(node.FullPath).ConfigureAwait(true);
        await ReconcileDirectoryAsync(node.Parent, CancellationToken.None).ConfigureAwait(true);
        if (ReferenceEquals(SelectedNode, node)) SelectedNode = null;

        if (staged.IsSuccess)
        {
            DeleteUndoNoticeText = $"「{node.Name}」を削除しました。Ctrl+Zで元に戻せます";
            HasDeleteUndoNotice = true;
            _deleteNoticeTimer.Restart();

            // 課題2: ツリーの再構築（直前のReconcileDirectoryAsync）で選択中だった項目の
            // コンテナが作り直され、フォーカスを失っている。エクスプローラにフォーカスがある
            // 状態でのCtrl+Zが削除直後も引き続き届くよう、Viewへツリーへの再フォーカスを促す。
            RequestFocus();
        }
    }

    private void OnDeleteNoticeTimeout()
    {
        HasDeleteUndoNotice = false;
        _deleteNoticeTimer.Stop();
    }

    /// <summary>
    /// 課題2: 直近の削除を元に戻す。<see cref="UndoDeleteCommand"/>の実体。取り消せる削除が
    /// 無ければ何もしない（キー入力・クリックのたびに呼ばれても安全なようにするため）。
    /// </summary>
    private async Task UndoDeleteAsync()
    {
        if (!_undoStore.CanUndo) return;

        _deleteNoticeTimer.Stop();
        HasDeleteUndoNotice = false;

        var result = await _undoStore.UndoAsync().ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            await ShowFailureAsync("削除を元に戻せませんでした", result.Issues).ConfigureAwait(true);
            return;
        }

        var outcome = result.Value;
        if (outcome is null) return; // CanUndoの直後のため通常はここに来ない

        _fileWatch.SuppressPath(outcome.OriginalFullPath);
        // 復元先の親ディレクトリがツリー上でどこまで読み込まれているか（未展開の可能性がある）を
        // 逐一調べるよりも、常に全体をやり直す方が単純で確実（附録A: 並行実装を増やさない）。
        await RefreshAsync().ConfigureAwait(true);

        // 削除時と同じ理由（RefreshAsyncのツリー再構築でフォーカスを失う）で、連続でCtrl+Zを
        // 押しても2回目以降が引き続きエクスプローラへ届くよう、Viewへ再フォーカスを促す。
        RequestFocus();
    }

    private void RequestFocus() => FocusRequested?.Invoke(this, EventArgs.Empty);

    private void CopyPath(FileNodeViewModel? node)
    {
        // IClipboardAccess.SetTextは失敗しても例外を投げない契約のため、ここでの保護は不要。
        if (node is not null) _ui.Clipboard.SetText(node.FullPath);
    }

    /// <summary>
    /// 不具合2対応: 以前はここで <see cref="FileTreeService.RevealInFileExplorer"/>
    /// （Windows専用の別実装で、Linuxでは何もしない・フォルダのときに親フォルダが開く
    /// 不具合も未対応のまま）を直接呼んでいた。プラットフォーム差し替え可能な
    /// <see cref="IFileManagerLauncher"/>（Windows/Linuxの両方に対応し、対象がフォルダの
    /// ときはフォルダ自体を開く）へ揃える。
    /// </summary>
    private static void RevealInExplorer(FileNodeViewModel? node)
    {
        if (node is not null) PlatformServices.Current.FileManager.Reveal(node.FullPath);
    }

    /// <summary>ShowFileHistoryCommandの実体。対象がファイルであることはCanExecute側で保証済み。</summary>
    private void RequestShowFileHistory(FileNodeViewModel? node)
    {
        if (node is null || node.IsDirectory || node.IsPlaceholder) return;
        ShowFileHistoryRequested?.Invoke(this, node.RelativePath);
    }

    private string ToFullPath(string relativePath)
        => Path.Combine(_project!.Root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private Task ShowFailureAsync(string title, IEnumerable<GraftIssue> issues)
        => _dialogs.ShowMessageAsync(title, string.Join(Environment.NewLine, issues.Select(i => i.ToDisplayText())));

    // ---- 保存直後の自己検知ループ防止（仕様書4.6）: 自分のタブが保存されたパスを一時的に無視する ----

    private void AttachTabWatchers()
    {
        _editor.Tabs.CollectionChanged += OnTabsCollectionChanged;
        foreach (var tab in _editor.Tabs) tab.PropertyChanged += OnTabPropertyChanged;
    }

    private void OnTabsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (EditorTabViewModel tab in e.NewItems) tab.PropertyChanged += OnTabPropertyChanged;
        }
        if (e.OldItems is not null)
        {
            foreach (EditorTabViewModel tab in e.OldItems) tab.PropertyChanged -= OnTabPropertyChanged;
        }
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(EditorTabViewModel.IsModified)) return;
        if (sender is EditorTabViewModel { IsModified: false } tab) _fileWatch.SuppressPath(tab.Session.FullPath);
    }

    private static bool PathsEqual(string a, string b) => OperatingSystem.IsWindows()
        ? string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
        : string.Equals(a, b, StringComparison.Ordinal);
}
