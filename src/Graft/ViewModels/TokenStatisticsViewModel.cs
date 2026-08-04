using System.Collections.ObjectModel;
using Graft.Core;
using Graft.Features;
using Graft.Infra;

namespace Graft.ViewModels;

/// <summary>
/// 仕様書12章のトークン統計を表示する。プロジェクトを選ぶと、そのプロジェクトの
/// 全リビジョンを <see cref="RevisionStore"/> から読み込み、<see cref="TokenStatistics.ByPeriod"/>
/// で期間別（日/週/月）に集計する。空・読み込み中・エラーの3状態（8.8章）を持つ。
/// </summary>
public sealed class TokenStatisticsViewModel : ObservableObject
{
    private readonly RevisionStore _revisionStore;
    private readonly ProjectStore _projectStore;

    private Project? _selectedProject;
    private string _granularity = TokenStatistics.Granularity.Day;
    private bool _isBusy;
    private bool _isEmpty;
    private GraftIssue? _errorIssue;

    public TokenStatisticsViewModel(AppPaths appPaths, ProjectStore projectStore)
    {
        ArgumentNullException.ThrowIfNull(appPaths);
        _projectStore = projectStore ?? throw new ArgumentNullException(nameof(projectStore));
        _revisionStore = new RevisionStore(appPaths);

        Projects = new ObservableCollection<Project>();
        Buckets = new ObservableCollection<TokenStatistics.Bucket>();
        Granularities = new ObservableCollection<GranularityOption>
        {
            new("日別", TokenStatistics.Granularity.Day),
            new("週別", TokenStatistics.Granularity.Week),
            new("月別", TokenStatistics.Granularity.Month),
        };

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
    }

    /// <summary>選択可能なプロジェクト一覧（先頭に「すべて」を含まない。全体集計は別途 <see cref="LoadAllAsync"/> を使う）。</summary>
    public ObservableCollection<Project> Projects { get; }

    /// <summary>期間別集計の選択肢。</summary>
    public ObservableCollection<GranularityOption> Granularities { get; }

    /// <summary>期間別集計結果。</summary>
    public ObservableCollection<TokenStatistics.Bucket> Buckets { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public Project? SelectedProject
    {
        get => _selectedProject;
        set => SetProperty(ref _selectedProject, value, () => _ = RefreshAsync());
    }

    public string Granularity
    {
        get => _granularity;
        set => SetProperty(ref _granularity, value, () => _ = RefreshAsync());
    }

    /// <summary>8.8: 読み込み中インジケータ（200ms未満での完了時は呼び出し側で表示を抑制する）。</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    /// <summary>8.8: 空状態。選択プロジェクトにリビジョンが1件もない場合。</summary>
    public bool IsEmpty
    {
        get => _isEmpty;
        private set => SetProperty(ref _isEmpty, value);
    }

    /// <summary>8.8: エラー状態。該当なしなら null。</summary>
    public GraftIssue? ErrorIssue
    {
        get => _errorIssue;
        private set => SetProperty(ref _errorIssue, value);
    }

    public int TotalTokens => Buckets.Sum(b => b.Tokens);

    public int TotalSavedTokens => Buckets.Sum(b => b.SavedTokens);

    public int TotalRevisions => Buckets.Sum(b => b.Revisions);

    /// <summary>プロジェクト一覧を読み込む。設定画面表示時に一度呼び出す想定。</summary>
    public async Task LoadProjectsAsync(CancellationToken ct = default)
    {
        var loaded = await _projectStore.LoadAsync(ct).ConfigureAwait(true);
        Projects.Clear();
        foreach (var project in ProjectStore.Sort(loaded.Value))
        {
            Projects.Add(project);
        }

        if (SelectedProject is null && Projects.Count > 0)
        {
            SelectedProject = Projects[0];
        }
    }

    private async Task RefreshAsync()
    {
        if (_selectedProject is null)
        {
            Buckets.Clear();
            IsEmpty = true;
            ErrorIssue = null;
            OnPropertyChanged(nameof(TotalTokens));
            OnPropertyChanged(nameof(TotalSavedTokens));
            OnPropertyChanged(nameof(TotalRevisions));
            return;
        }

        var busyGate = Task.Delay(200);
        var loadTask = _revisionStore.ListAsync(_selectedProject.Id);
        if (await Task.WhenAny(busyGate, loadTask).ConfigureAwait(true) == busyGate && !loadTask.IsCompleted)
        {
            IsBusy = true;
        }

        var result = await loadTask.ConfigureAwait(true);
        IsBusy = false;

        if (!result.IsSuccess)
        {
            ErrorIssue = result.Errors.FirstOrDefault();
            Buckets.Clear();
            IsEmpty = false;
            return;
        }

        ErrorIssue = null;
        var buckets = TokenStatistics.ByPeriod(result.Value, _granularity);
        Buckets.Clear();
        foreach (var bucket in buckets)
        {
            Buckets.Add(bucket);
        }

        IsEmpty = Buckets.Count == 0;
        OnPropertyChanged(nameof(TotalTokens));
        OnPropertyChanged(nameof(TotalSavedTokens));
        OnPropertyChanged(nameof(TotalRevisions));
    }

    /// <summary>期間別集計の選択肢1件（表示名とgranularity値の組）。</summary>
    public sealed record GranularityOption(string Label, string Value);
}
