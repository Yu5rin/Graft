using System.ComponentModel;
using System.IO;
using System.Windows.Input;
using Graft.Features;
using Graft.Views;

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
public sealed class ShellViewModel : ObservableObject
{
    private readonly DialogService _dialogs;
    private readonly Graft.Infra.Settings _settings;
    private readonly HashSet<LegacyKey> _notifiedLegacyKeys = new();

    private SideViewKind _selectedSideView = SideViewKind.Project;
    private bool _isSideViewCollapsed;
    private bool _isGraftPanelOpen;
    private string? _currentProjectId;

    public ShellViewModel(MainViewModel graft, EditorPaneViewModel editor, DialogService dialogs, Graft.Infra.Settings settings)
    {
        Graft = graft ?? throw new ArgumentNullException(nameof(graft));
        Editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        Explorer = new ExplorerViewModel(Editor, _dialogs, settings);
        Search = new SearchViewModel(new Graft.Features.CrossFileSearchEngine(), _dialogs);
        Search.JumpRequested += OnSearchJumpRequested;
        _settings = settings;

        Graft.PropertyChanged += OnGraftPropertyChanged;
        Graft.ProjectPane.ProjectSelected += OnProjectSelected;

        SelectSideViewCommand = new RelayCommand<SideViewKind>(SelectSideView);
        ToggleGraftPanelCommand = new RelayCommand(() => IsGraftPanelOpen = !IsGraftPanelOpen);
    }

    /// <summary>既存機能一式（プロジェクト・履歴・接ぎ木・キュー・プロンプト等）。</summary>
    public MainViewModel Graft { get; }

    /// <summary>エディタタブ領域（他担当実装、仕様書4章）。</summary>
    public EditorPaneViewModel Editor { get; }

    /// <summary>エクスプローラビュー（仕様書4.2。ツリー表示・操作・監視反映を担う）。</summary>
    public ExplorerViewModel Explorer { get; }

    /// <summary>ファイル横断検索ビュー（仕様書4.4）。</summary>
    public SearchViewModel Search { get; }

    /// <summary>現在表示中のサイドビュー。</summary>
    public SideViewKind SelectedSideView
    {
        get => _selectedSideView;
        private set => SetProperty(ref _selectedSideView, value);
    }

    /// <summary>サイドビューが折りたたまれているかどうか（同じアイコンの再クリックで切替）。</summary>
    public bool IsSideViewCollapsed
    {
        get => _isSideViewCollapsed;
        private set => SetProperty(ref _isSideViewCollapsed, value);
    }

    /// <summary>接ぎ木パネルが展開されているかどうか。通常時はfalse（折りたたみ）。</summary>
    public bool IsGraftPanelOpen
    {
        get => _isGraftPanelOpen;
        set => SetProperty(ref _isGraftPanelOpen, value);
    }

    /// <summary>サイドバーのアイコンクリック（CommandParameterに<see cref="SideViewKind"/>）。</summary>
    public ICommand SelectSideViewCommand { get; }

    /// <summary>Ctrl+J・接ぎ木パネルのヘッダーボタン。</summary>
    public ICommand ToggleGraftPanelCommand { get; }

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
    }

    /// <summary>9.2: パッチ解析・履歴diffの表示が始まったら接ぎ木パネルを自動展開する。</summary>
    private void OnGraftPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.State) && Graft.State != CenterPaneState.Empty)
        {
            IsGraftPanelOpen = true;
        }
    }

    /// <summary>
    /// 3.1/3.2: プロジェクトが切り替わったら、まず切替前のプロジェクトのタブ構成・
    /// エクスプローラの展開状態をlayout.jsonへ記憶させたうえで、開いていたエディタタブを
    /// 閉じてエディタ・エクスプローラの対象プロジェクトを切り替え、新しいプロジェクトの
    /// タブ構成・展開状態を復元する。保存確認等はEditorPaneViewModel側の責務。
    /// </summary>
    /// <summary>横断検索の結果クリックで、該当ファイルの該当行をエディタで開く（仕様書4.4）。</summary>
    private async void OnSearchJumpRequested(object? sender, (string FullPath, int Line) target)
        => await Editor.OpenFileAsync(target.FullPath, preview: true, line: target.Line).ConfigureAwait(true);

    private async void OnProjectSelected(object? sender, Project project)
    {
        if (_currentProjectId is { } previousId) CaptureProjectState(previousId);

        await Editor.CloseAllAsync().ConfigureAwait(true);
        Editor.SetProject(project.Root);
        await Explorer.SetProjectAsync(project).ConfigureAwait(true);
        Search.SetContext(project, _settings);
        await RestoreProjectStateAsync(project).ConfigureAwait(true);

        _currentProjectId = project.Id;
    }

    /// <summary>
    /// 3.2: 切替前プロジェクトの開いていたタブ（相対パス・アクティブタブ・カーソル位置）と
    /// エクスプローラの展開状態を、そのプロジェクトのProjectPaneLayoutへ書き戻す。
    /// 実際のlayout.jsonへの永続化はShellWindow側の既存の終了時保存処理が担う。
    /// </summary>
    private void CaptureProjectState(string projectId)
    {
        var layout = WindowLayoutStore.GetOrCreatePaneLayout(Graft.Layout, projectId);
        layout.OpenTabs = Editor.Tabs
            .Select(t => new OpenTabState { RelativePath = t.Session.RelativePath, CaretLine = t.CaretLine, CaretColumn = t.CaretColumn })
            .ToList();
        layout.ActiveTabPath = Editor.ActiveTab?.Session.RelativePath;
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
            var activeTab = Editor.Tabs.FirstOrDefault(t => t.Session.RelativePath == activePath);
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
