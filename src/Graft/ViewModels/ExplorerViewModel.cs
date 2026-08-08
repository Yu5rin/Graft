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
/// </summary>
public sealed class ExplorerViewModel : ObservableObject, IDisposable
{
    private readonly EditorPaneViewModel _editor;
    private readonly IDialogService _dialogs;
    private readonly IUiServices _ui;
    private readonly FileTreeService _treeService = new();
    private readonly FileWatchService _fileWatch = new();

    private Project? _project;
    private GitignoreFilter _filter = GitignoreFilter.Empty;
    private PathGuardOptions _guardOptions = PathGuardOptions.Default;
    private bool _showExcludedFiles;
    private bool _isLoading;
    private FileNodeViewModel? _selectedNode;

    public ExplorerViewModel(EditorPaneViewModel editor, IDialogService dialogs, Graft.Infra.Settings settings, IUiServices ui)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _guardOptions = FileTreeService.BuildGuardOptions(settings);
        _fileWatch.DirectoriesChanged += OnDirectoriesChanged;
        _fileWatch.FileContentChanged += OnFileContentChanged;
        AttachTabWatchers();

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => _project is not null);
        OpenCommand = new RelayCommand<FileNodeViewModel>(ExecuteOpen);
        NewFileCommand = new RelayCommand<FileNodeViewModel>(node => _ = NewFileAsync(node ?? SelectedNode), _ => _project is not null);
        NewFolderCommand = new RelayCommand<FileNodeViewModel>(node => _ = NewFolderAsync(node ?? SelectedNode), _ => _project is not null);
        RenameCommand = new RelayCommand<FileNodeViewModel>(node => _ = RenameNodeAsync(node ?? SelectedNode));
        DeleteCommand = new RelayCommand<FileNodeViewModel>(node => _ = DeleteNodeAsync(node ?? SelectedNode));
        CopyPathCommand = new RelayCommand<FileNodeViewModel>(node => CopyPath(node ?? SelectedNode));
        RevealCommand = new RelayCommand<FileNodeViewModel>(node => RevealInExplorer(node ?? SelectedNode));
    }

    /// <summary>ツリーの最上位（プロジェクトルート直下）のノード一覧。</summary>
    public ObservableCollection<FileNodeViewModel> RootNodes { get; } = new();

    /// <summary>現在のプロジェクト。未選択時はnull（仕様書9.2の空状態表示に使う）。</summary>
    public Project? Project => _project;

    /// <summary>プロジェクトが選択されているかどうか（空状態表示の判定用）。</summary>
    public bool HasProject => _project is not null;

    /// <summary>ツリーを読み込み中かどうか（9.2の不確定プログレスバー表示用）。</summary>
    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }

    /// <summary>除外ファイルを表示するトグル（仕様書4.2）。表示可否はViewの変換のみで切り替わるため再読込は不要。</summary>
    public bool ShowExcludedFiles { get => _showExcludedFiles; set => SetProperty(ref _showExcludedFiles, value); }

    /// <summary>右クリック・キー操作の対象になる、現在選択中のノード。</summary>
    public FileNodeViewModel? SelectedNode { get => _selectedNode; set => SetProperty(ref _selectedNode, value); }

    public ICommand RefreshCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand NewFileCommand { get; }
    public ICommand NewFolderCommand { get; }
    public ICommand RenameCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand CopyPathCommand { get; }
    public ICommand RevealCommand { get; }

    /// <summary>ファイル監視を停止して解放する。アプリ終了時にShellViewModelから呼ばれる。</summary>
    public void Dispose() => _fileWatch.Dispose();

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

    /// <summary>指定ディレクトリ（nullはプロジェクトルート）の子要素を実体と突き合わせて更新する。</summary>
    private async Task ReconcileDirectoryAsync(FileNodeViewModel? dirNode, CancellationToken ct)
    {
        if (_project is null) return;
        var relativeDir = dirNode?.RelativePath ?? string.Empty;
        var listed = await _treeService.ListChildrenAsync(_project, relativeDir, _filter, ct).ConfigureAwait(true);
        if (!listed.IsSuccess) return; // 監視イベントで頻発するため、消失等は静かに諦める

        var target = dirNode?.Children ?? RootNodes;
        var existing = target.Where(n => !n.IsPlaceholder).ToDictionary(n => n.RelativePath);
        var merged = new List<FileNodeViewModel>(listed.Value.Count);
        foreach (var entry in listed.Value)
        {
            if (existing.TryGetValue(entry.RelativePath, out var node))
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

        target.Clear();
        foreach (var node in merged) target.Add(node);
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
        var name = await _dialogs.PromptAsync("新規ファイル", "ファイル名を入力してください。", null).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name)) return;

        var created = await _treeService.CreateFileAsync(_project, relativeDir, name, _guardOptions).ConfigureAwait(true);
        if (!created.IsSuccess) { await ShowFailureAsync("ファイルを作成できませんでした", created.Issues).ConfigureAwait(true); return; }

        var fullPath = ToFullPath(created.Value);
        _fileWatch.SuppressPath(fullPath);
        await ReconcileDirectoryAsync(dirNode, CancellationToken.None).ConfigureAwait(true);
        await OpenFileNodeAsync(fullPath).ConfigureAwait(true);
    }

    private async Task NewFolderAsync(FileNodeViewModel? contextNode)
    {
        if (_project is null) return;
        var (dirNode, relativeDir) = ResolveTargetDirectory(contextNode);
        var name = await _dialogs.PromptAsync("新規フォルダ", "フォルダ名を入力してください。", null).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name)) return;

        var created = await _treeService.CreateFolderAsync(_project, relativeDir, name, _guardOptions).ConfigureAwait(true);
        if (!created.IsSuccess) { await ShowFailureAsync("フォルダを作成できませんでした", created.Issues).ConfigureAwait(true); return; }

        _fileWatch.SuppressPath(ToFullPath(created.Value));
        await ReconcileDirectoryAsync(dirNode, CancellationToken.None).ConfigureAwait(true);
    }

    private async Task RenameNodeAsync(FileNodeViewModel? node)
    {
        if (_project is null || node is null || node.IsPlaceholder) return;
        var name = await _dialogs.PromptAsync("名前の変更", "新しい名前を入力してください。", node.Name).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name) || name == node.Name) return;

        var renamed = await _treeService.RenameAsync(_project, node.RelativePath, name, node.IsDirectory, _guardOptions).ConfigureAwait(true);
        if (!renamed.IsSuccess) { await ShowFailureAsync("名前を変更できませんでした", renamed.Issues).ConfigureAwait(true); return; }

        _fileWatch.SuppressPath(node.FullPath);
        var newFullPath = ToFullPath(renamed.Value);
        _fileWatch.SuppressPath(newFullPath);

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

        _fileWatch.SuppressPath(node.FullPath);
        var deleted = await _treeService.DeleteAsync(_project, node.RelativePath, node.IsDirectory, _guardOptions).ConfigureAwait(true);
        if (!deleted.IsSuccess) { await ShowFailureAsync("削除できませんでした", deleted.Issues).ConfigureAwait(true); return; }

        // 削除されたファイルのタブは開いたままにできないため閉じる（4.2・4.3）。
        if (!node.IsDirectory) await _editor.NotifyDeletedAsync(node.FullPath).ConfigureAwait(true);
        await ReconcileDirectoryAsync(node.Parent, CancellationToken.None).ConfigureAwait(true);
        if (ReferenceEquals(SelectedNode, node)) SelectedNode = null;
    }

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
