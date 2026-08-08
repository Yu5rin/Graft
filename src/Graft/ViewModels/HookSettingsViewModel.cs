using System.Collections.ObjectModel;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;

namespace Graft.ViewModels;

/// <summary>
/// 仕様書6.5・3.1の適用後フック管理を担う。設定画面の「適用後フック」タブが使う。
/// プロジェクトごとにフック（名前・コマンド・失敗時挙動）を追加・編集・削除し、
/// projects.json へ保存する（<see cref="ProjectStore"/> の既存の保存経路を使う）。
/// </summary>
public sealed class HookSettingsViewModel : ObservableObject
{
    private readonly ProjectStore _projectStore;
    private readonly IDialogService _dialogService;

    private Project? _selectedProject;
    private HookEntry? _selectedHook;
    private string? _statusMessage;

    public HookSettingsViewModel(ProjectStore projectStore, IDialogService dialogService)
    {
        _projectStore = projectStore ?? throw new ArgumentNullException(nameof(projectStore));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));

        Projects = new ObservableCollection<Project>();
        Hooks = new ObservableCollection<HookEntry>();

        AddCommand = new AsyncRelayCommand(AddAsync, () => _selectedProject is not null);
        DeleteCommand = new AsyncRelayCommand(DeleteAsync, () => _selectedHook is not null);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => _selectedProject is not null);
    }

    public ObservableCollection<Project> Projects { get; }

    public ObservableCollection<HookEntry> Hooks { get; }

    public AsyncRelayCommand AddCommand { get; }
    public AsyncRelayCommand DeleteCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }

    /// <summary>失敗時挙動の選択肢。内部値は<see cref="HookFailureAction"/>の定数、表示のみ日本語。</summary>
    public IReadOnlyList<HookFailureOption> OnFailureOptions { get; } = new[]
    {
        new HookFailureOption("記録のみ", HookFailureAction.Ignore),
        new HookFailureOption("警告表示", HookFailureAction.Warn),
        new HookFailureOption("ロールバックを提案", HookFailureAction.OfferRollback),
        new HookFailureOption("自動ロールバック", HookFailureAction.AutoRollback),
    };

    public Project? SelectedProject
    {
        get => _selectedProject;
        set => SetProperty(ref _selectedProject, value, OnSelectedProjectChanged);
    }

    public HookEntry? SelectedHook
    {
        get => _selectedHook;
        set => SetProperty(ref _selectedHook, value, RaiseCommandStates);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>プロジェクト一覧を読み込む。設定画面表示時に一度呼び出す。</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var loaded = await _projectStore.LoadAsync(ct).ConfigureAwait(true);
        Projects.Clear();
        foreach (var project in ProjectStore.Sort(loaded.Value))
        {
            Projects.Add(project);
        }

        SelectedProject = Projects.FirstOrDefault();
    }

    private void OnSelectedProjectChanged()
    {
        Hooks.Clear();
        if (_selectedProject is not null)
        {
            foreach (var hook in _selectedProject.PostApplyHooks)
            {
                Hooks.Add(new HookEntry(hook));
            }
        }
        SelectedHook = Hooks.FirstOrDefault();
        RaiseCommandStates();
    }

    private Task AddAsync()
    {
        if (_selectedProject is null) return Task.CompletedTask;

        var entry = new HookEntry(new PostApplyHook { Name = "新しいフック", Command = string.Empty, OnFailure = HookFailureAction.Warn });
        Hooks.Add(entry);
        SelectedHook = entry;
        StatusMessage = "フックを追加しました。保存を押すと確定します。";
        return Task.CompletedTask;
    }

    private async Task DeleteAsync()
    {
        if (_selectedHook is not { } entry) return;
        var confirmed = await _dialogService
            .ConfirmAsync("フックの削除", $"フック「{entry.Name}」を削除します。よろしいですか？")
            .ConfigureAwait(true);
        if (!confirmed) return;

        Hooks.Remove(entry);
        SelectedHook = Hooks.FirstOrDefault();
        await SaveAsync().ConfigureAwait(true);
    }

    /// <summary>選択中プロジェクトのフック一覧をprojects.jsonへ保存する。</summary>
    private async Task SaveAsync()
    {
        if (_selectedProject is null) return;

        var loaded = await _projectStore.LoadAsync().ConfigureAwait(true);
        var projects = loaded.Value.ToList();
        var index = projects.FindIndex(p => p.Id == _selectedProject.Id);
        if (index < 0) return;

        var updatedHooks = Hooks.Select(h => h.ToSource()).ToList();
        projects[index] = projects[index] with { PostApplyHooks = updatedHooks };
        await _projectStore.SaveAsync(projects).ConfigureAwait(true);

        _selectedProject = projects[index];
        var selectedIndex = Projects.ToList().FindIndex(p => p.Id == _selectedProject.Id);
        if (selectedIndex >= 0) Projects[selectedIndex] = _selectedProject;
        OnPropertyChanged(nameof(SelectedProject));

        StatusMessage = $"「{_selectedProject.DisplayName}」の適用後フックを保存しました。";
    }

    private void RaiseCommandStates()
    {
        AddCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
        SaveCommand.RaiseCanExecuteChanged();
    }
}

/// <summary>失敗時挙動の選択肢1件（表示ラベルと<see cref="HookFailureAction"/>の値の組）。</summary>
public sealed record HookFailureOption(string Label, string Value);

/// <summary>フック1件分の編集用ラッパー。</summary>
public sealed class HookEntry : ObservableObject
{
    private string _name;
    private string _command;
    private string _onFailure;

    public HookEntry(PostApplyHook source)
    {
        _name = source.Name;
        _command = source.Command;
        _onFailure = source.OnFailure;
    }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string Command
    {
        get => _command;
        set => SetProperty(ref _command, value);
    }

    public string OnFailure
    {
        get => _onFailure;
        set => SetProperty(ref _onFailure, value);
    }

    public PostApplyHook ToSource() => new() { Name = _name, Command = _command, OnFailure = _onFailure };
}
