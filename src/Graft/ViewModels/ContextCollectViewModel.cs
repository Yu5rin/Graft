using System.Collections.ObjectModel;
using System.Windows;
using Graft.Core;
using Graft.Features;
using Graft.Infra;

namespace Graft.ViewModels;

/// <summary>
/// 仕様書10章のコンテキスト収集UIを担う。収集モードの選択、ファイルのチェック選択、
/// 除外規則の確認、出力前の概算トークン数表示（10.4）と上限超過時の警告、
/// クリップボードへのコピーまでを行う。
/// </summary>
public sealed class ContextCollectViewModel : ObservableObject
{
    private readonly ContextCollector _collector;
    private readonly RevisionStore _revisionStore;
    private readonly ProjectStore _projectStore;
    private Project _project;
    private readonly Settings _settings;

    private ContextMode _selectedMode = ContextMode.TreeAndSelected;
    private RevisionOption? _selectedRevision;
    private ContextFileNodeViewModel? _selectedFile;
    private int _estimatedTokens;
    private bool _exceedsWarnThreshold;
    private bool _isScanning;
    private bool _isEmpty;
    private GraftIssue? _errorIssue;
    private string? _statusMessage;
    private string _newExcludePattern = string.Empty;

    public ContextCollectViewModel(AppPaths appPaths, ProjectStore projectStore, Project project, Settings settings)
    {
        ArgumentNullException.ThrowIfNull(appPaths);
        _projectStore = projectStore ?? throw new ArgumentNullException(nameof(projectStore));
        _project = project ?? throw new ArgumentNullException(nameof(project));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _collector = new ContextCollector(appPaths);
        _revisionStore = new RevisionStore(appPaths);

        Modes = new ObservableCollection<ModeOption>
        {
            new("ツリーのみ", ContextMode.TreeOnly),
            new("選択ファイル", ContextMode.SelectedFiles),
            new("ツリー＋選択", ContextMode.TreeAndSelected),
            new("差分のみ", ContextMode.ChangedSince),
        };
        Revisions = new ObservableCollection<RevisionOption>();
        Files = new ObservableCollection<ContextFileNodeViewModel>();
        ExtraExcludes = new ObservableCollection<string>(project.Overrides.Excludes);

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        PreviewCommand = new AsyncRelayCommand(PreviewAsync, () => !_isScanning);
        CopyCommand = new AsyncRelayCommand(CopyAsync, () => !_isScanning);
        ToggleSelectedCommand = new RelayCommand(ToggleSelected, () => _selectedFile is { IsDirectory: false, IsExcluded: false });
        AddExcludeCommand = new AsyncRelayCommand(AddExcludeAsync, () => !string.IsNullOrWhiteSpace(_newExcludePattern));
        RemoveExcludeCommand = new RelayCommand<string>(pattern => _ = RemoveExcludeAsync(pattern));
    }

    public Project Project => _project;

    public ObservableCollection<ModeOption> Modes { get; }
    public ObservableCollection<RevisionOption> Revisions { get; }
    public ObservableCollection<ContextFileNodeViewModel> Files { get; }

    /// <summary>10.2: 既定除外・.gitignore に加え、プロジェクト単位で追加した除外パターン。</summary>
    public ObservableCollection<string> ExtraExcludes { get; }

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand PreviewCommand { get; }
    public AsyncRelayCommand CopyCommand { get; }
    public RelayCommand ToggleSelectedCommand { get; }
    public AsyncRelayCommand AddExcludeCommand { get; }
    public RelayCommand<string> RemoveExcludeCommand { get; }

    public ContextMode SelectedMode
    {
        get => _selectedMode;
        set => SetProperty(ref _selectedMode, value, OnModeChanged);
    }

    public RevisionOption? SelectedRevision
    {
        get => _selectedRevision;
        set => SetProperty(ref _selectedRevision, value);
    }

    public ContextFileNodeViewModel? SelectedFile
    {
        get => _selectedFile;
        set => SetProperty(ref _selectedFile, value, () => ToggleSelectedCommand.RaiseCanExecuteChanged());
    }

    public bool ShowFileTree => _selectedMode is ContextMode.SelectedFiles or ContextMode.TreeAndSelected;

    public bool ShowRevisionPicker => _selectedMode == ContextMode.ChangedSince;

    public string NewExcludePattern
    {
        get => _newExcludePattern;
        set => SetProperty(ref _newExcludePattern, value, () => AddExcludeCommand.RaiseCanExecuteChanged());
    }

    /// <summary>10.4: 出力前の概算トークン数。</summary>
    public int EstimatedTokens
    {
        get => _estimatedTokens;
        private set => SetProperty(ref _estimatedTokens, value);
    }

    /// <summary>10.4: 上限超過フラグ。超過時はファイル選択の見直しを促す警告を表示する。</summary>
    public bool ExceedsWarnThreshold
    {
        get => _exceedsWarnThreshold;
        private set => SetProperty(ref _exceedsWarnThreshold, value);
    }

    public int TokenWarnThreshold => _settings.Context.TokenWarnThreshold;

    /// <summary>8.8: 読み込み中インジケータ。</summary>
    public bool IsScanning
    {
        get => _isScanning;
        private set => SetProperty(ref _isScanning, value);
    }

    /// <summary>8.8: 空状態。走査対象ファイルが1件もない場合。</summary>
    public bool IsEmpty
    {
        get => _isEmpty;
        private set => SetProperty(ref _isEmpty, value);
    }

    /// <summary>8.8: エラー状態（コード＋対処方法）。</summary>
    public GraftIssue? ErrorIssue
    {
        get => _errorIssue;
        private set => SetProperty(ref _errorIssue, value);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>初期表示時に一度呼び出し、ファイルツリーを走査する。</summary>
    public Task InitializeAsync(CancellationToken ct = default) => RefreshAsync(ct);

    private void OnModeChanged()
    {
        OnPropertyChanged(nameof(ShowFileTree));
        OnPropertyChanged(nameof(ShowRevisionPicker));
        if (_selectedMode == ContextMode.ChangedSince && Revisions.Count == 0)
        {
            _ = LoadRevisionsAsync();
        }
    }

    private async Task LoadRevisionsAsync()
    {
        var result = await _revisionStore.ListAsync(_project.Id).ConfigureAwait(true);
        Revisions.Clear();
        if (!result.IsSuccess) return;
        foreach (var summary in result.Value)
        {
            Revisions.Add(new RevisionOption(summary.Manifest.Revision, summary.Manifest.Summary ?? "(要約なし)"));
        }
        SelectedRevision = Revisions.FirstOrDefault();
    }

    private async Task RefreshAsync(CancellationToken ct = default)
    {
        IsScanning = true;
        ErrorIssue = null;
        try
        {
            var scan = await _collector.ScanAsync(_project, _settings, ct).ConfigureAwait(true);
            if (!scan.IsSuccess)
            {
                ErrorIssue = scan.Errors.FirstOrDefault();
                Files.Clear();
                IsEmpty = false;
                return;
            }

            Files.Clear();
            foreach (var node in scan.Value)
            {
                Files.Add(new ContextFileNodeViewModel(node));
            }
            IsEmpty = Files.Count == 0;
        }
        finally
        {
            IsScanning = false;
        }
    }

    private async Task PreviewAsync()
    {
        var result = await CollectAsync().ConfigureAwait(true);
        if (result is null) return;
        StatusMessage = ExceedsWarnThreshold
            ? $"推定トークン数 {EstimatedTokens} 件。上限（{TokenWarnThreshold} 件）を超えています。ファイル選択を見直してください。"
            : $"推定トークン数 {EstimatedTokens} 件。";
    }

    private async Task CopyAsync()
    {
        var result = await CollectAsync().ConfigureAwait(true);
        if (result is null) return;

        if (ExceedsWarnThreshold)
        {
            StatusMessage = $"推定トークン数 {EstimatedTokens} 件が上限（{TokenWarnThreshold} 件）を超えています。ファイル選択を見直してください。";
            return;
        }

        Clipboard.SetText(result.Text);
        StatusMessage = "クリップボードにコピーしました。";
    }

    private async Task<ContextResult?> CollectAsync()
    {
        var selectedPaths = Files.Where(f => f is { IsDirectory: false, IsExcluded: false, IsChecked: true })
            .Select(f => f.RelativePath).ToArray();

        var request = new ContextRequest
        {
            Project = _project,
            Settings = _settings,
            Mode = _selectedMode,
            SelectedPaths = selectedPaths,
            SinceRevision = _selectedRevision?.Revision,
        };

        var result = await _collector.CollectAsync(request).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            ErrorIssue = result.Errors.FirstOrDefault();
            return null;
        }

        ErrorIssue = null;
        EstimatedTokens = result.Value.EstimatedTokens;
        ExceedsWarnThreshold = result.Value.ExceedsWarnThreshold;
        return result.Value;
    }

    private void ToggleSelected()
    {
        if (_selectedFile is { IsDirectory: false, IsExcluded: false } file)
        {
            file.IsChecked = !file.IsChecked;
        }
    }

    private async Task AddExcludeAsync()
    {
        var pattern = _newExcludePattern.Trim();
        if (string.IsNullOrEmpty(pattern) || ExtraExcludes.Contains(pattern)) return;

        ExtraExcludes.Add(pattern);
        NewExcludePattern = string.Empty;
        await PersistOverridesAsync().ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
    }

    private async Task RemoveExcludeAsync(string? pattern)
    {
        if (pattern is null || !ExtraExcludes.Remove(pattern)) return;
        await PersistOverridesAsync().ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
    }

    private async Task PersistOverridesAsync()
    {
        var loaded = await _projectStore.LoadAsync().ConfigureAwait(true);
        var projects = loaded.Value.ToList();
        var index = projects.FindIndex(p => p.Id == _project.Id);
        if (index < 0) return;

        _project = _project with { Overrides = _project.Overrides with { Excludes = ExtraExcludes.ToArray() } };
        projects[index] = _project;
        await _projectStore.SaveAsync(projects).ConfigureAwait(true);
    }

    /// <summary>収集モードの選択肢1件。</summary>
    public sealed record ModeOption(string Label, ContextMode Mode);

    /// <summary>差分のみモード用のリビジョン選択肢1件。</summary>
    public sealed record RevisionOption(int Revision, string Summary)
    {
        public string DisplayText => $"r{Revision} — {Summary}";
    }
}

/// <summary>ファイルツリー1行分の選択状態を保持する。</summary>
public sealed class ContextFileNodeViewModel : ObservableObject
{
    private bool _isChecked;

    public ContextFileNodeViewModel(ContextFileNode node)
    {
        RelativePath = node.RelativePath;
        IsDirectory = node.IsDirectory;
        IsExcluded = node.IsExcluded;
        ExcludeReason = node.ExcludeReason;
        IndentLevel = node.RelativePath.Count(c => c == '/');
        var nameStart = node.RelativePath.LastIndexOf('/') + 1;
        DisplayName = node.RelativePath[nameStart..];
    }

    public string RelativePath { get; }
    public bool IsDirectory { get; }
    public bool IsExcluded { get; }
    public string? ExcludeReason { get; }
    public int IndentLevel { get; }
    public string DisplayName { get; }

    public bool IsChecked
    {
        get => _isChecked;
        set => SetProperty(ref _isChecked, value);
    }

    /// <summary>8.14: スクリーンリーダー向けの読み上げ文言。種別・除外理由を含める。</summary>
    public string AutomationLabel
    {
        get
        {
            if (IsDirectory) return $"フォルダ {DisplayName}";
            if (IsExcluded) return $"除外 {DisplayName}（{ExcludeReason}）";
            return $"ファイル {DisplayName}";
        }
    }
}
