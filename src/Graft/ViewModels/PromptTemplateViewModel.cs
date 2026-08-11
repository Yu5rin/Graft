using System.Collections.ObjectModel;
using Graft.Core;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;

namespace Graft.ViewModels;

/// <summary>
/// 仕様書4.8のプロンプトテンプレート管理を担う。テンプレートの追加・編集・削除、
/// プロジェクトごとの既定テンプレート紐づけ、テンプレート名の右に表示する推定トークン数
/// （<see cref="TokenEstimator"/> と <see cref="PromptTemplateRenderer"/> を使用）、
/// 初回用／継続用トグル（4.8.1）でのコピー操作をまとめて提供する。
/// </summary>
public sealed class PromptTemplateViewModel : ObservableObject
{
    private readonly PromptTemplateStore _templateStore;
    private readonly ProjectStore _projectStore;
    private readonly ContextCollector _collector;
    private readonly PromptTemplateRenderer _renderer;
    private readonly IDialogService _dialogService;
    private readonly IUiServices _ui;

    private Settings _settings;
    private Project? _selectedProject;
    private TemplateEntry? _selectedTemplate;
    private bool _isContinuationMode;
    private bool _isBusy;
    private string? _statusMessage;
    private GraftIssue? _errorIssue;

    public PromptTemplateViewModel(
        AppPaths appPaths, ProjectStore projectStore, IDialogService dialogService, Settings initialSettings, IUiServices ui)
    {
        ArgumentNullException.ThrowIfNull(appPaths);
        _projectStore = projectStore ?? throw new ArgumentNullException(nameof(projectStore));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _settings = initialSettings ?? throw new ArgumentNullException(nameof(initialSettings));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _templateStore = new PromptTemplateStore(appPaths);
        _collector = new ContextCollector(appPaths);
        _renderer = new PromptTemplateRenderer(_collector);

        Templates = new ObservableCollection<TemplateEntry>();
        Projects = new ObservableCollection<Project>();

        AddCommand = new AsyncRelayCommand(AddAsync, context: "テンプレートの追加");
        DeleteCommand = new AsyncRelayCommand(DeleteAsync, () => _selectedTemplate is { IsBuiltIn: false }, context: "テンプレートの削除");
        SaveEditCommand = new AsyncRelayCommand(SaveEditAsync, () => _selectedTemplate is { IsBuiltIn: false }, context: "テンプレートの保存");
        CopyCommand = new AsyncRelayCommand(CopyAsync, () => _selectedProject is not null, context: "テンプレートのコピー");
        SetAsProjectDefaultCommand = new AsyncRelayCommand(
            SetAsProjectDefaultAsync, () => _selectedProject is not null && _selectedTemplate is not null,
            context: "プロジェクト既定テンプレートの設定");
    }

    public ObservableCollection<TemplateEntry> Templates { get; }

    public ObservableCollection<Project> Projects { get; }

    public AsyncRelayCommand AddCommand { get; }
    public AsyncRelayCommand DeleteCommand { get; }
    public AsyncRelayCommand SaveEditCommand { get; }
    public AsyncRelayCommand CopyCommand { get; }
    public AsyncRelayCommand SetAsProjectDefaultCommand { get; }

    public Project? SelectedProject
    {
        get => _selectedProject;
        set => SetProperty(ref _selectedProject, value, OnSelectedProjectChanged);
    }

    public TemplateEntry? SelectedTemplate
    {
        get => _selectedTemplate;
        set => SetProperty(ref _selectedTemplate, value, RaiseCommandStates);
    }

    /// <summary>4.8.1: コピーボタンの初回用／継続用トグル。既定テンプレート2件の間を切り替える。</summary>
    public bool IsContinuationMode
    {
        get => _isContinuationMode;
        set => SetProperty(ref _isContinuationMode, value, OnContinuationModeChanged);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>8.8: 読み込み・保存失敗時のエラー（コード＋対処方法）。</summary>
    public GraftIssue? ErrorIssue
    {
        get => _errorIssue;
        private set => SetProperty(ref _errorIssue, value);
    }

    /// <summary>設定画面の他タブでSettingsが更新された際、トークン概算の比率を最新化するために呼ぶ。</summary>
    public async Task UpdateSettingsAsync(Settings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        await RecomputeEstimatesAsync().ConfigureAwait(true);
    }

    /// <summary>テンプレート一覧とプロジェクト一覧を読み込む。設定画面表示時に一度呼び出す。</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var loaded = await _templateStore.LoadAsync(ct).ConfigureAwait(true);
        ErrorIssue = loaded.Errors.FirstOrDefault();
        Templates.Clear();
        foreach (var template in loaded.Value)
        {
            Templates.Add(new TemplateEntry(template));
        }

        var projects = await _projectStore.LoadAsync(ct).ConfigureAwait(true);
        Projects.Clear();
        foreach (var project in ProjectStore.Sort(projects.Value))
        {
            Projects.Add(project);
        }

        SelectedProject = Projects.FirstOrDefault();
        SelectedTemplate = Templates.FirstOrDefault();
        await RecomputeEstimatesAsync().ConfigureAwait(true);
    }

    private void OnSelectedProjectChanged()
    {
        if (_selectedProject is not null)
        {
            IsContinuationMode = _templateStore.ShouldUseContinuation(_selectedProject.Id, DateTimeOffset.Now);
        }
        RaiseCommandStates();
        _ = RecomputeEstimatesAsync();
    }

    private void OnContinuationModeChanged()
    {
        if (_selectedTemplate is null) return;
        if (_selectedTemplate.Id is not ("builtin-full" or "builtin-continuation")) return;
        var targetId = _isContinuationMode ? "builtin-continuation" : "builtin-full";
        var target = Templates.FirstOrDefault(e => e.Id == targetId);
        if (target is not null)
        {
            _selectedTemplate = target;
            OnPropertyChanged(nameof(SelectedTemplate));
        }
    }

    private async Task AddAsync()
    {
        var name = await _dialogService.PromptAsync("テンプレートの追加", "テンプレート名を入力してください。").ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name)) return;

        var template = new PromptTemplate { Id = $"custom-{Guid.NewGuid():N}", Name = name, Body = string.Empty };
        var entry = new TemplateEntry(template);
        Templates.Add(entry);
        await PersistCustomTemplatesAsync().ConfigureAwait(true);
        SelectedTemplate = entry;
        StatusMessage = "テンプレートを追加しました。";
    }

    private async Task DeleteAsync()
    {
        if (_selectedTemplate is not { IsBuiltIn: false } entry) return;
        var confirmed = await _dialogService
            .ConfirmAsync("テンプレートの削除", $"テンプレート「{entry.Name}」を削除します。よろしいですか？")
            .ConfigureAwait(true);
        if (!confirmed) return;

        Templates.Remove(entry);
        await PersistCustomTemplatesAsync().ConfigureAwait(true);
        SelectedTemplate = Templates.FirstOrDefault();
        StatusMessage = "テンプレートを削除しました。";
    }

    private async Task SaveEditAsync()
    {
        if (_selectedTemplate is not { IsBuiltIn: false } entry) return;
        entry.UpdateSource(entry.Source with { Name = entry.Name, Body = entry.Body });
        await PersistCustomTemplatesAsync().ConfigureAwait(true);
        await RecomputeEstimatesAsync().ConfigureAwait(true);
        StatusMessage = "テンプレートを保存しました。";
    }

    private async Task CopyAsync()
    {
        if (_selectedProject is null) return;
        var target = ResolveCopyTarget();
        if (target is null) return;

        var request = new ContextRequest { Project = _selectedProject, Settings = _settings, Mode = ContextMode.TreeAndSelected };
        var rendered = await _renderer.RenderAsync(target.Source, request, null).ConfigureAwait(true);
        if (!rendered.IsSuccess)
        {
            ErrorIssue = rendered.Errors.FirstOrDefault();
            return;
        }

        _ui.Clipboard.SetText(rendered.Value);
        _templateStore.RecordCopy(_selectedProject.Id, DateTimeOffset.Now);
        StatusMessage = $"「{target.Name}」をクリップボードへコピーしました。";
    }

    private async Task SetAsProjectDefaultAsync()
    {
        if (_selectedProject is null || _selectedTemplate is null) return;
        var loaded = await _projectStore.LoadAsync().ConfigureAwait(true);
        var projects = loaded.Value.ToList();
        var index = projects.FindIndex(p => p.Id == _selectedProject.Id);
        if (index < 0) return;

        projects[index] = projects[index] with { PromptTemplateId = _selectedTemplate.Id };
        await _projectStore.SaveAsync(projects).ConfigureAwait(true);
        StatusMessage = $"「{_selectedProject.DisplayName}」の既定テンプレートを「{_selectedTemplate.Name}」に設定しました。";
    }

    private TemplateEntry? ResolveCopyTarget()
    {
        if (_selectedTemplate is null)
        {
            return Templates.FirstOrDefault(e => e.Id == (_isContinuationMode ? "builtin-continuation" : "builtin-full"));
        }
        return _selectedTemplate;
    }

    private async Task RecomputeEstimatesAsync()
    {
        if (_selectedProject is null)
        {
            foreach (var entry in Templates)
            {
                entry.EstimatedTokens = TokenEstimator.Estimate(entry.Body, _settings.Context.TokenRatio);
            }
            return;
        }

        IsBusy = true;
        try
        {
            var request = new ContextRequest { Project = _selectedProject, Settings = _settings, Mode = ContextMode.TreeAndSelected };
            foreach (var entry in Templates)
            {
                var rendered = await _renderer.RenderAsync(entry.Source, request, null).ConfigureAwait(true);
                entry.EstimatedTokens = rendered.IsSuccess
                    ? TokenEstimator.Estimate(rendered.Value, _settings.Context.TokenRatio)
                    : TokenEstimator.Estimate(entry.Body, _settings.Context.TokenRatio);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task PersistCustomTemplatesAsync()
    {
        var all = Templates.Select(e => e.Source).ToList();
        await _templateStore.SaveAsync(all).ConfigureAwait(true);
    }

    private void RaiseCommandStates()
    {
        DeleteCommand.RaiseCanExecuteChanged();
        SaveEditCommand.RaiseCanExecuteChanged();
        CopyCommand.RaiseCanExecuteChanged();
        SetAsProjectDefaultCommand.RaiseCanExecuteChanged();
    }
}

/// <summary>テンプレート1件分の編集用ラッパー。名前の右に表示する推定トークン数を保持する。</summary>
public sealed class TemplateEntry : ObservableObject
{
    private string _name;
    private string _body;
    private int _estimatedTokens;

    public TemplateEntry(PromptTemplate source)
    {
        Source = source;
        _name = source.Name;
        _body = source.Body;
    }

    public PromptTemplate Source { get; private set; }

    public string Id => Source.Id;

    public bool IsBuiltIn => Source.IsBuiltIn;

    public bool IsContinuation => Source.IsContinuation;

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string Body
    {
        get => _body;
        set => SetProperty(ref _body, value);
    }

    public int EstimatedTokens
    {
        get => _estimatedTokens;
        set => SetProperty(ref _estimatedTokens, value);
    }

    public void UpdateSource(PromptTemplate updated)
    {
        Source = updated;
        _name = updated.Name;
        _body = updated.Body;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Body));
    }
}
