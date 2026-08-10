using System.ComponentModel;
using System.IO;
using System.Windows.Input;
using Graft.Core;
using Graft.Features;
using Graft.Platform;

namespace Graft.ViewModels;

/// <summary>サイドビューの種別（仕様書9.2）。エクスプローラ・検索はフェーズE2/E3で実体を持つ。</summary>
public enum SideViewKind
{
    Explorer,
    Project,
    History,
    Search,
}

/// <summary>
/// 接ぎ木パネルの配置（利用者からの改善要望: コードの下だけでなく右にも置けるようにする）。
/// 既定は<see cref="Bottom"/>（従来どおりコードの下、2列＋下段）。<see cref="Right"/>は
/// サイドバー｜エディタ｜接ぎ木パネルの3列になる。切替はGraftPanel.axamlのヘッダーボタン
/// （ShellViewModel.ToggleGraftPanelPlacementCommand）で行う。
/// </summary>
public enum GraftPanelPlacementKind
{
    Bottom,
    Right,
}

/// <summary>
/// 旧キー（v1.5のショートカット）の種別。附録A「キーマップ移行」の一度きり通知に使う
/// （9.5: 素のCtrl+V・Ctrl+Z・Ctrl+H・素の1〜9）。
/// </summary>
public enum LegacyKey
{
    PasteCtrlV,
    UndoCtrlZ,
    HistoryCtrlH,
    ProjectDigit,
}

/// <summary>
/// 新シェルレイアウト（サイドバー＋エディタ中央＋接ぎ木パネル下部、仕様書9.2）を統括する
/// ViewModel。既存の <see cref="MainViewModel"/> の全機能をそのまま内包し（<see cref="Graft"/>）、
/// これにエディタペイン（<see cref="Editor"/>）とサイドビュー・接ぎ木パネルの開閉状態を
/// 追加する。依存はすべてコンストラクタ引数で受け取り、生成は起動処理担当（StartupCoordinator）
/// が手動で行う（附録A.3・DIコンテナ禁止）。
/// </summary>
public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly IDialogService _dialogs;
    private readonly Graft.Infra.Settings _settings;
    private readonly IUiServices _ui;
    private readonly HashSet<LegacyKey> _notifiedLegacyKeys = new();

    private SideViewKind _selectedSideView = SideViewKind.Project;
    private bool _isSideViewCollapsed;
    private bool _isGraftPanelOpen;
    private GraftPanelPlacementKind _graftPanelPlacement = GraftPanelPlacementKind.Bottom;
    private string? _currentProjectId;

    public ShellViewModel(
        Graft.Infra.AppPaths appPaths, MainViewModel graft, EditorPaneViewModel editor, IDialogService dialogs,
        Graft.Infra.Settings settings, IUiServices ui)
    {
        Graft = graft ?? throw new ArgumentNullException(nameof(graft));
        Editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        Explorer = new ExplorerViewModel(appPaths, Editor, _dialogs, settings, ui);
        Search = new SearchViewModel(new Graft.Features.CrossFileSearchEngine(), _dialogs);
        Search.JumpRequested += OnSearchJumpRequested;
        QuickOpen = new QuickOpenViewModel();
        QuickOpen.FileOpenRequested += OnQuickOpenFileRequested;
        _settings = settings;

        Graft.PropertyChanged += OnGraftPropertyChanged;
        Graft.ProjectPane.ProjectSelected += OnProjectSelected;
        // プロジェクトペイン改善（要望2）: プロジェクト名のダブルクリックでサイドビューを
        // エクスプローラへ切り替える（折りたたまれていれば展開も行う。SelectSideView参照）。
        Graft.ProjectPane.ProjectActivated += (_, _) => SelectSideView(SideViewKind.Explorer);
        // プロジェクトペイン改善（要望1）: 削除等でプロジェクトが1件も無くなった場合、
        // エディタ・エクスプローラ・検索・クイックオープンを「プロジェクト未選択」の状態へ戻す。
        Graft.ProjectPane.SelectionCleared += OnProjectSelectionCleared;
        Graft.Diff.JumpRequested += OnDiffJumpRequested; // 4.8: diff表示の行をダブルクリックしたときのジャンプ。
        Graft.HistoryDiff.JumpRequested += OnDiffJumpRequested; // 修正1: 履歴差分タブでも同じジャンプ処理を再利用する。
        Graft.HistoryDiffChanged += OnHistoryDiffChanged; // 修正1: 履歴差分タブの開閉。
        // 機能改善: エディタ本文・差分表示（通常＋履歴）いずれかでのCtrl+マウスホイールに
        // よるフォントサイズ確定を1つのイベントへ集約し、StartupCoordinatorへ伝える
        // （そこから常駐のSettingsViewModel経由で永続化・全画面への同期を行う）。
        Editor.FontSizeChangeCommitted += (_, size) => EditorFontSizeChangeRequested?.Invoke(this, size);
        Graft.Diff.FontSizeChangeCommitted += (_, size) => EditorFontSizeChangeRequested?.Invoke(this, size);
        Graft.HistoryDiff.FontSizeChangeCommitted += (_, size) => EditorFontSizeChangeRequested?.Invoke(this, size);
        Editor.HistoryDiffTabClosed += OnHistoryDiffTabClosed; // 修正1: タブの×で閉じたら履歴側の選択も解除する。
        // ファイル単位の変更履歴: エクスプローラの右クリック「このファイルの変更履歴」を、
        // 履歴ペイン（Graft.History）の絞り込みと連動させる。ExplorerViewModelはHistoryPane
        // ViewModelを知らないため、両方を知るこのクラスが橋渡しする（ProjectActivated等と同じ構造）。
        Explorer.ShowFileHistoryRequested += OnShowFileHistoryRequested;
        Graft.BeforeApplyAsync = EnsureTargetsSavedAsync; // 4.8: ドライラン開始前の未保存確認。
        Graft.AfterApplyAsync = files => Editor.ReloadIfOpenAsync(files); // 4.8: 適用後の自動再読込。
        WireStatusBarWarningSources(); // ShellViewModel.StatusBarWarning.cs参照。

        SelectSideViewCommand = new RelayCommand<SideViewKind>(SelectSideView);
        ToggleGraftPanelCommand = new RelayCommand(() => IsGraftPanelOpen = !IsGraftPanelOpen);
        ToggleGraftPanelPlacementCommand = new RelayCommand(() => GraftPanelPlacement =
            GraftPanelPlacement == GraftPanelPlacementKind.Bottom ? GraftPanelPlacementKind.Right : GraftPanelPlacementKind.Bottom);
        OpenBlockInEditorCommand = new RelayCommand<BlockItemViewModel>(block => OpenBlockInEditor(block));
        ToggleQuickOpenCommand = new RelayCommand(() => _ = ToggleQuickOpenAsync());
        OpenShortcutsCommand = new RelayCommand(() => RequestOpenShortcuts?.Invoke(this, EventArgs.Empty));
        AnalyzeClipboardPatchCommand = new RelayCommand(AnalyzeClipboardPatch); // ShellViewModel.ClipboardWatch.cs参照。
    }

    /// <summary>UIフレームワーク固有の機能。ウィンドウ位置の復元などでViewから参照する。</summary>
    public IUiServices Ui => _ui;

    /// <summary>保持している破棄が必要な資源（ファイル監視）を解放する。</summary>
    public void Dispose() => Explorer.Dispose();

    /// <summary>既存機能一式（プロジェクト・履歴・接ぎ木・キュー・プロンプト等）。</summary>
    public MainViewModel Graft { get; }

    /// <summary>エディタタブ領域（他担当実装、仕様書4章）。</summary>
    public EditorPaneViewModel Editor { get; }

    /// <summary>エクスプローラビュー（仕様書4.2。ツリー表示・操作・監視反映を担う）。</summary>
    public ExplorerViewModel Explorer { get; }

    /// <summary>ファイル横断検索ビュー（仕様書4.4）。</summary>
    public SearchViewModel Search { get; }

    /// <summary>クイックオープン（Ctrl+P、ファイル名あいまい検索）。</summary>
    public QuickOpenViewModel QuickOpen { get; }

    /// <summary>現在表示中のサイドビュー。</summary>
    public SideViewKind SelectedSideView
    {
        get => _selectedSideView;
        private set => SetProperty(ref _selectedSideView, value, NotifyActiveSideViewChanged);
    }

    /// <summary>サイドビューが折りたたまれているかどうか（同じアイコンの再クリックで切替）。</summary>
    public bool IsSideViewCollapsed
    {
        get => _isSideViewCollapsed;
        private set => SetProperty(ref _isSideViewCollapsed, value, NotifyActiveSideViewChanged);
    }

    /// <summary>
    /// サイドバーのアイコンを選択状態（背景を強調）にするかどうか（9.2）。折りたたみ中は
    /// どのアイコンも選択状態にしない。
    /// 「表示中のビューであること」と「折りたたまれていないこと」の2条件の組み合わせのため、
    /// UI側で条件を組み立てるとWPFのMultiDataTriggerのようなUIフレームワーク固有の
    /// 仕組みに頼ることになる。ViewModel側で解決してboolとして公開する。
    /// </summary>
    public bool IsExplorerActive => IsSideViewActive(SideViewKind.Explorer);

    /// <inheritdoc cref="IsExplorerActive"/>
    public bool IsProjectActive => IsSideViewActive(SideViewKind.Project);

    /// <inheritdoc cref="IsExplorerActive"/>
    public bool IsHistoryActive => IsSideViewActive(SideViewKind.History);

    /// <inheritdoc cref="IsExplorerActive"/>
    public bool IsSearchActive => IsSideViewActive(SideViewKind.Search);

    /// <summary>接ぎ木パネルが展開されているかどうか。通常時はfalse（折りたたみ）。</summary>
    public bool IsGraftPanelOpen
    {
        get => _isGraftPanelOpen;
        set => SetProperty(ref _isGraftPanelOpen, value);
    }

    /// <summary>
    /// 接ぎ木パネルの配置（下／右）。既定は<see cref="GraftPanelPlacementKind.Bottom"/>。
    /// 実際のGrid行・列の付け替えはShellWindow.axaml.csが担う（Viewは1インスタンスのまま
    /// Grid.Row/Grid.Columnだけ動かすため、このプロパティ自体はUIフレームワークを知らない）。
    /// </summary>
    public GraftPanelPlacementKind GraftPanelPlacement
    {
        get => _graftPanelPlacement;
        set => SetProperty(ref _graftPanelPlacement, value, NotifyGraftPanelPlacementChanged);
    }

    /// <summary>
    /// GraftPanel.axamlのヘッダーアイコン出し分け用。「右配置かどうか」をbool一つで持たせ、
    /// XAML側でMultiDataTrigger相当の分岐を組まずに済むようにする（IsExplorerActive等と同じ考え方）。
    /// </summary>
    public bool IsGraftPanelPlacementRight => GraftPanelPlacement == GraftPanelPlacementKind.Right;

    private void NotifyGraftPanelPlacementChanged() => OnPropertyChanged(nameof(IsGraftPanelPlacementRight));

    /// <summary>
    /// ProjectPaneLayout.GraftPanelPlacement（文字列）をenumへ変換する。後方互換のため、
    /// 未知の値・null（新キーの無い既存layout.jsonを読んだ場合を含む）は既定の下配置として扱う。
    /// </summary>
    public static GraftPanelPlacementKind ParseGraftPanelPlacement(string? value)
        => value == "right" ? GraftPanelPlacementKind.Right : GraftPanelPlacementKind.Bottom;

    /// <summary><see cref="ParseGraftPanelPlacement"/>の逆変換。layout.jsonへ書き戻す文字列を返す。</summary>
    public static string ToGraftPanelPlacementValue(GraftPanelPlacementKind placement)
        => placement == GraftPanelPlacementKind.Right ? "right" : "bottom";

    /// <summary>サイドバーのアイコンクリック（CommandParameterに<see cref="SideViewKind"/>）。</summary>
    public ICommand SelectSideViewCommand { get; }

    /// <summary>Ctrl+J・接ぎ木パネルのヘッダーボタン。</summary>
    public ICommand ToggleGraftPanelCommand { get; }

    /// <summary>接ぎ木パネルのヘッダーの配置切替ボタン。下配置と右配置を1クリックで切り替える。</summary>
    public ICommand ToggleGraftPanelPlacementCommand { get; }

    /// <summary>4.8: ブロック一覧の「エディタで開く」。マッチ位置をエディタで開く。</summary>
    public ICommand OpenBlockInEditorCommand { get; }

    /// <summary>Ctrl+P。クイックオープンオーバーレイの開閉（トグル）。</summary>
    public ICommand ToggleQuickOpenCommand { get; }

    /// <summary>
    /// Ctrl+/・ツールバーの「?」ボタン。キーボードショートカット一覧ウィンドウを開く。
    /// テキスト入力欄・エディタにフォーカスがある間はCtrl+/がエディタの行コメント切り替えに
    /// 使われるため（ShellWindow.Keyboard.cs）、そちらを優先しここは反応しない。
    /// </summary>
    public ICommand OpenShortcutsCommand { get; }

    /// <summary>
    /// 4.4: 検索ビューを表示したとき（サイドバーの虫眼鏡アイコン・Ctrl+Shift+Fのいずれも
    /// <see cref="SelectSideView"/>を経由するため、ここで一括して発火する）、検索テキストボックスへ
    /// フォーカスするようViewへ要求する。
    /// </summary>
    public event EventHandler? RequestFocusSearchView;

    /// <summary>ショートカット一覧ウィンドウを開くタイミングの通知。View側（ShellWindow）が購読する。</summary>
    public event EventHandler? RequestOpenShortcuts;

    /// <summary>
    /// 機能改善: エディタ本文・差分表示（通常＋履歴）のいずれかでCtrl+マウスホイールにより
    /// フォントサイズが確定した（ドラッグ中の連続変化ではなく、1回のホイール操作の結果）ことの
    /// 通知。StartupCoordinatorが購読し、常駐のSettingsViewModel経由で設定への永続化
    /// （デバウンス保存）を行う。
    /// </summary>
    public event EventHandler<double>? EditorFontSizeChangeRequested;

    /// <summary>
    /// 9.2: サイドバーのアイコンをクリックしたときの挙動。既に表示中のビューを
    /// 再クリックした場合はサイドビューを折りたたむ。それ以外は該当ビューへ切り替えて展開する。
    /// </summary>
    public void SelectSideView(SideViewKind kind)
    {
        if (!IsSideViewCollapsed && SelectedSideView == kind)
        {
            IsSideViewCollapsed = true;
            return;
        }
        SelectedSideView = kind;
        IsSideViewCollapsed = false;
        if (kind == SideViewKind.Search) RequestFocusSearchView?.Invoke(this, EventArgs.Empty);
    }

    private bool IsSideViewActive(SideViewKind kind) => !IsSideViewCollapsed && SelectedSideView == kind;

    private void NotifyActiveSideViewChanged()
    {
        OnPropertyChanged(nameof(IsExplorerActive));
        OnPropertyChanged(nameof(IsProjectActive));
        OnPropertyChanged(nameof(IsHistoryActive));
        OnPropertyChanged(nameof(IsSearchActive));
    }

    /// <summary>
    /// 9.2: パッチ解析・履歴diffの表示が始まったら接ぎ木パネルを自動展開する。
    /// 9.2/4.8: ブロック選択の変化に連動して、選択ブロックの差分をエディタ領域のタブとして
    /// 開閉する（MainViewModelはDiffのLoad/Clearのみを行い、タブ化自体はここで配線する）。
    /// </summary>
    private void OnGraftPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.State) && Graft.State != CenterPaneState.Empty)
        {
            IsGraftPanelOpen = true;
        }
        else if (e.PropertyName == nameof(MainViewModel.SelectedBlock))
        {
            if (Graft.SelectedBlock is not null) Editor.ShowDiffTab(Graft.Diff);
            else Editor.CloseDiffTabIfOpen();
        }
        else if (e.PropertyName == nameof(MainViewModel.IsDataDirectoryReadOnly))
        {
            // ShellViewModel.StatusBarWarning.cs参照。書き込み不可警告もステータスバーの
            // 統合警告表示の対象のため、変化をここから中継する。
            NotifyStatusBarWarningChanged();
        }
    }

    /// <summary>
    /// 修正1: <see cref="MainViewModel.HistoryDiff"/>の内容が変わるたびに、履歴差分タブを
    /// 開く／内容を更新する（HasFilesがtrue）か、閉じる（false。選択解除・タブの×で閉じた
    /// 直後の再クリア等）。HistoryDiffは選択のたびに同じインスタンスを使い回すため、この
    /// イベント経由でしか「今開くべきか」を知る手段が無い（OnGraftPropertyChangedの
    /// SelectedBlock分岐と同じ考え方）。
    /// </summary>
    private void OnHistoryDiffChanged(object? sender, EventArgs e)
    {
        if (Graft.HistoryDiff.HasFiles) Editor.ShowHistoryDiffTab(Graft.HistoryDiff);
        else Editor.CloseHistoryDiffTabIfOpen();
    }

    /// <summary>
    /// 修正1: 履歴差分タブがタブの×・Ctrl+W等で閉じられたら、履歴側の選択も解除する
    /// （「タブは無いのに履歴一覧では選択済みのまま」という状態の矛盾を防ぐ）。
    /// 既に選択解除済み（HistoryDiff.Clear経由でこのタブが閉じられた場合）はSelectedItemの
    /// setterが変化なしとして何もしないため、ここから無限にイベントが往復することは無い。
    /// </summary>
    private void OnHistoryDiffTabClosed(object? sender, EventArgs e) => Graft.History.SelectedItem = null;

    /// <summary>
    /// ファイル単位の変更履歴: エクスプローラの右クリックメニュー「このファイルの変更履歴」。
    /// 履歴ペインをそのファイルへ絞り込んだうえで、履歴ビューを開き一覧へフォーカスする
    /// （フォーカス移動自体はShellWindow.OnRequestFocusHistoryが担うView側の責務のため、
    /// ここではGraft.ShowHistoryCommand経由でその入口だけを呼ぶ）。
    ///
    /// Graft.ShowHistoryCommandをそのまま呼ばないのは、SelectSideView（サイドバーのアイコンを
    /// 再クリックすると折りたたむ、9.2のトグル仕様）に巻き込まれるとの実機確認による不具合修正:
    /// 既に履歴ビューを開いた状態でこのメニューを別のファイルへ実行すると「同じビューへの
    /// 再選択」と見なされ、絞り込みが更新される代わりにサイドビューごと折りたたまれてしまう。
    /// 既に履歴ビューが表示中（IsHistoryActive）ならこの呼び出しをスキップし、絞り込みの反映
    /// だけにとどめることで、意図せぬ折りたたみを避ける。
    /// </summary>
    private void OnShowFileHistoryRequested(object? sender, string relativePath)
    {
        Graft.History.ShowHistoryForFile(relativePath);
        if (!IsHistoryActive) Graft.ShowHistoryCommand.Execute(null);
    }

    /// <summary>4.8: diff表示の行をダブルクリックしたときのジャンプ。変更後の行番号を優先する。</summary>
    private async void OnDiffJumpRequested(object? sender, (string RelativePath, int Line) target)
        => await SafeHandler.RunAsync("差分からのジャンプ", async () =>
        {
            var root = Graft.ProjectPane.SelectedItem?.Project.Root;
            if (root is null) return;
            var fullPath = Path.Combine(root, target.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            await Editor.OpenFileAsync(fullPath, preview: true, line: target.Line).ConfigureAwait(true);
        }).ConfigureAwait(true);

    /// <summary>4.8: ブロック一覧の「エディタで開く」。マッチ位置（無ければ先頭）をエディタで開く。</summary>
    private async void OpenBlockInEditor(BlockItemViewModel? block)
        => await SafeHandler.RunAsync("ブロックをエディタで開く", async () =>
        {
            var root = Graft.ProjectPane.SelectedItem?.Project.Root;
            if (block is null || root is null) return;

            var fullPath = Path.Combine(root, block.Plan.Path.Replace('/', Path.DirectorySeparatorChar));
            var line = FirstChangedLine(block.Plan.Diff);
            await Editor.OpenFileAsync(fullPath, preview: true, line: line).ConfigureAwait(true);
        }).ConfigureAwait(true);

    // 変更後の行番号を優先し、無ければ変更前を使う（4.8のdiffジャンプと同じ考え方）。
    private static int? FirstChangedLine(DiffModel? diff)
    {
        if (diff is null) return null;
        foreach (var line in diff.Hunks.SelectMany(h => h.Lines))
        {
            if (line.Kind == DiffLineKind.Omitted) continue;
            if (line.NewLine is int n) return n;
            if (line.OldLine is int o) return o;
        }
        return null;
    }

    /// <summary>
    /// 4.8 適用前チェック: ドライラン開始時に対象ファイルの未保存編集を確認し、保存してから
    /// 続行する（破棄しての続行は不可）。ユーザーが保存を拒んだ場合はfalseを返し中止させる。
    /// MainViewModel.BeforeApplyAsyncへ結ぶ。
    /// </summary>
    private async Task<bool> EnsureTargetsSavedAsync(IReadOnlyList<string> fullPaths)
    {
        var unsaved = Editor.FindUnsaved(fullPaths);
        if (unsaved.Count == 0) return true;

        var names = string.Join("、", unsaved.Select(Path.GetFileName));
        var proceed = await _dialogs.ConfirmAsync(
            "未保存の変更を保存します",
            $"適用対象のファイル（{names}）に未保存の変更があります。保存してから適用を続行します。よろしいですか？")
            .ConfigureAwait(true);
        if (!proceed) return false;

        var saved = await Editor.SaveFilesAsync(unsaved).ConfigureAwait(true);
        if (!saved.IsSuccess)
        {
            await _dialogs.ShowMessageAsync("保存に失敗しました", "ファイルの保存に失敗したため、適用を中止します。").ConfigureAwait(true);
            return false;
        }
        return true;
    }

    /// <summary>
    /// 3.1/3.2: プロジェクトが切り替わったら、まず切替前のプロジェクトのタブ構成・
    /// エクスプローラの展開状態をlayout.jsonへ記憶させたうえで、開いていたエディタタブを
    /// 閉じてエディタ・エクスプローラの対象プロジェクトを切り替え、新しいプロジェクトの
    /// タブ構成・展開状態を復元する。保存確認等はEditorPaneViewModel側の責務。
    /// </summary>
    /// <summary>
    /// 3章: アプリ終了時に呼び出し、現在選択中のプロジェクトのタブ構成・アクティブタブ・
    /// エクスプローラの展開状態をProjectPaneLayoutへ取り込む。プロジェクト未選択
    /// （_currentProjectIdがnull）の場合は何もしない。実際のlayout.jsonへの永続化は
    /// 呼び出し元（ShellWindow.OnClosing）のSaveLayoutAsyncが担う。
    /// </summary>
    public void CaptureCurrentProjectState()
    {
        if (_currentProjectId is { } projectId) CaptureProjectState(projectId);
    }

    /// <summary>横断検索の結果クリックで、該当ファイルの該当行をエディタで開く（仕様書4.4）。</summary>
    private async void OnSearchJumpRequested(object? sender, (string FullPath, int Line) target)
        => await SafeHandler.RunAsync("検索結果からのジャンプ", () =>
            Editor.OpenFileAsync(target.FullPath, preview: true, line: target.Line)).ConfigureAwait(true);

    /// <summary>クイックオープンでの確定（Enter・マウスクリック）。プレビュータブとして開く。</summary>
    private async void OnQuickOpenFileRequested(object? sender, string fullPath)
        => await SafeHandler.RunAsync("クイックオープンからのファイルを開く", () =>
            Editor.OpenFileAsync(fullPath, preview: true)).ConfigureAwait(true);

    /// <summary>Ctrl+P。プロジェクト未選択時は何もしない（QuickOpenViewModel.ToggleAsyncが判定する）。</summary>
    private async Task ToggleQuickOpenAsync() => await QuickOpen.ToggleAsync().ConfigureAwait(true);

    private async void OnProjectSelected(object? sender, Project project)
        => await SafeHandler.RunAsync("プロジェクトの切り替え", async () =>
        {
            if (_currentProjectId is { } previousId) CaptureProjectState(previousId);

            await Editor.CloseAllAsync().ConfigureAwait(true);
            Editor.SetProject(project.Root);
            await Explorer.SetProjectAsync(project).ConfigureAwait(true);
            Search.SetContext(project, _settings);
            QuickOpen.SetContext(project, _settings);
            await RestoreProjectStateAsync(project).ConfigureAwait(true);

            _currentProjectId = project.Id;
        }).ConfigureAwait(true);

    /// <summary>
    /// プロジェクトペイン改善（要望1）: プロジェクトの削除等で一覧が空になり、選択できる
    /// プロジェクトが1件も無くなったときの後始末。<see cref="OnProjectSelected"/>と対になる
    /// 経路で、開いていたタブ・エクスプローラ・検索・クイックオープンを、一度もプロジェクトを
    /// 選んだことが無い起動直後と同じ「プロジェクト未選択」の状態へ戻す（Editor.SetProject・
    /// Explorer.SetProjectAsync・Search/QuickOpen.SetContextはいずれもnullを受け付ける設計のため、
    /// OnProjectSelectedとほぼ同じ形で書ける）。ファイル自体は削除していないため、既に開いていた
    /// タブが指すファイルは実在するが、「どのプロジェクトの一部か」を示す文脈が失われるため
    /// 一貫性のため閉じる。
    /// </summary>
    private async void OnProjectSelectionCleared(object? sender, EventArgs e)
        => await SafeHandler.RunAsync("プロジェクト削除後のクリア", async () =>
        {
            if (_currentProjectId is { } previousId) CaptureProjectState(previousId);

            await Editor.CloseAllAsync().ConfigureAwait(true);
            Editor.SetProject(null);
            await Explorer.SetProjectAsync(null).ConfigureAwait(true);
            Search.SetContext(null, _settings);
            QuickOpen.SetContext(null, _settings);

            _currentProjectId = null;
        }).ConfigureAwait(true);

    /// <summary>
    /// 3.2: 切替前プロジェクトの開いていたタブ（相対パス・アクティブタブ・カーソル位置）と
    /// エクスプローラの展開状態を、そのプロジェクトのProjectPaneLayoutへ書き戻す。
    /// 実際のlayout.jsonへの永続化はShellWindow側の既存の終了時保存処理が担う。
    /// </summary>
    private void CaptureProjectState(string projectId)
    {
        var layout = WindowLayoutStore.GetOrCreatePaneLayout(Graft.Layout, projectId);
        // 差分タブ（9.2/4.8）はSessionを持たないため記憶対象から除く。
        layout.OpenTabs = Editor.Tabs
            .Where(t => t.Kind == EditorTabKind.Document)
            .Select(t => new OpenTabState { RelativePath = t.Session.RelativePath, CaretLine = t.CaretLine, CaretColumn = t.CaretColumn })
            .ToList();
        layout.ActiveTabPath = Editor.ActiveTab is { Kind: EditorTabKind.Document } active ? active.Session.RelativePath : null;
        layout.ExpandedFolders = Explorer.GetExpandedFolderPaths().ToList();
    }

    /// <summary>
    /// 3.2: 新しく選択されたプロジェクトのProjectPaneLayoutから、タブ構成・アクティブタブ・
    /// エクスプローラの展開状態を復元する。復元時に存在しなくなったファイルは黙って読み飛ばす。
    /// </summary>
    private async Task RestoreProjectStateAsync(Project project)
    {
        var layout = WindowLayoutStore.GetOrCreatePaneLayout(Graft.Layout, project.Id);
        foreach (var tab in layout.OpenTabs)
        {
            var fullPath = Path.Combine(project.Root, tab.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath)) continue;
            var opened = await Editor.OpenFileAsync(fullPath, preview: false, line: tab.CaretLine).ConfigureAwait(true);
            if (opened.IsSuccess) opened.Value.CaretColumn = tab.CaretColumn;
        }

        if (layout.ActiveTabPath is { } activePath)
        {
            var activeTab = Editor.Tabs.FirstOrDefault(t => t.Kind == EditorTabKind.Document && t.Session.RelativePath == activePath);
            if (activeTab is not null) Editor.ActiveTab = activeTab;
        }

        foreach (var folder in layout.ExpandedFolders) await Explorer.ExpandPathAsync(folder).ConfigureAwait(true);
    }

    /// <summary>
    /// 附録A「キーマップ移行」: 旧キーがエディタ外で押された場合、初回のみ変更内容を通知する。
    /// 通知済みフラグはメモリ保持のみ（プロセス再起動で再び通知される）。
    /// </summary>
    public void NotifyLegacyKey(LegacyKey key)
    {
        if (!_notifiedLegacyKeys.Add(key))
        {
            return;
        }
        _ = _dialogs.ShowMessageAsync("このキーは変更されました", LegacyKeyMessage(key));
    }

    private static string LegacyKeyMessage(LegacyKey key) => key switch
    {
        LegacyKey.PasteCtrlV =>
            "パッチの取り込み解析は Ctrl+Shift+V に変更されました。Ctrl+V はエディタでの通常の貼り付けに使えます。",
        LegacyKey.UndoCtrlZ =>
            "直前リビジョンの取り消しは Ctrl+Alt+Z に変更されました。Ctrl+Z はエディタのアンドゥに使えます。",
        LegacyKey.HistoryCtrlH =>
            "履歴ビューを開く操作は Ctrl+Shift+H に変更されました。Ctrl+H はエディタの置換に使えます。",
        LegacyKey.ProjectDigit =>
            "プロジェクト切替は Ctrl+Alt+1〜9 に変更されました。数字キー単独は通常の入力に使えます。",
        _ => "このキーの割り当てが変更されました。",
    };
}
