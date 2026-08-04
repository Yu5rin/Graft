using System.Collections.ObjectModel;
using System.Windows.Input;
using Graft.Core;
using Graft.Features;
using Graft.Views;

namespace Graft.ViewModels;

/// <summary>プロジェクト一覧ペインの表示状態（仕様書8.8）。</summary>
public enum ProjectPaneState
{
    Loading,
    Empty,
    Error,
    Content,
}

/// <summary>
/// プロジェクト一覧の1行。ピン留め・未接続表示・数字キーショートカット（仕様書3.2）に
/// 必要な表示用プロパティを持つ。
/// </summary>
public sealed class ProjectListItemViewModel
{
    public ProjectListItemViewModel(Project project, int? shortcutNumber)
    {
        Project = project ?? throw new ArgumentNullException(nameof(project));
        ShortcutNumber = shortcutNumber;
    }

    /// <summary>元のプロジェクト定義。</summary>
    public Project Project { get; }

    /// <summary>上位9件に割り当てる数字キーショートカット。それ以外は null。</summary>
    public int? ShortcutNumber { get; }

    public string Name => Project.Name;

    public bool IsPinned => Project.Pinned;

    /// <summary>未接続プロジェクトはグレー表示にする（仕様書3.2）。表示側はこの値でスタイルを切り替える。</summary>
    public bool IsDisconnected => Project.IsDisconnected;

    public string TagsText => Project.Tags.Count == 0 ? string.Empty : string.Join(" / ", Project.Tags);

    public string ShortcutText => ShortcutNumber is int n ? n.ToString() : string.Empty;

    /// <summary>色のみに依存しないための読み上げ用テキスト（8.14）。</summary>
    public string AutomationName
    {
        get
        {
            var parts = new List<string> { Name };
            if (IsPinned) parts.Add("ピン留め");
            if (IsDisconnected) parts.Add("未接続");
            if (ShortcutNumber is int n) parts.Add($"ショートカット{n}");
            return string.Join("、", parts);
        }
    }
}

/// <summary>
/// 左ペイン上段「プロジェクト一覧」の状態管理。仕様書3.1〜3.2。
/// ピン留め優先→最終使用日時降順（<see cref="ProjectStore.Sort"/>）で並べ、
/// 上位9件に数字キーショートカットを割り当てる。空・読み込み中・エラーの3状態を持つ（8.8）。
/// </summary>
public sealed class ProjectPaneViewModel : ObservableObject
{
    private readonly ProjectStore _store;
    private readonly DialogService _dialogs;
    private ProjectPaneState _state = ProjectPaneState.Loading;
    private GraftIssue? _error;
    private ProjectListItemViewModel? _selectedItem;

    public ProjectPaneViewModel(ProjectStore store, DialogService dialogs)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        AddProjectCommand = new AsyncRelayCommand(AddProjectViaDialogAsync);
    }

    /// <summary>コマンドバー・空状態から呼ぶ「フォルダ選択で登録」（仕様書3.2）。</summary>
    public ICommand AddProjectCommand { get; }

    public ObservableCollection<ProjectListItemViewModel> Items { get; } = new();

    public ProjectPaneState State
    {
        get => _state;
        private set => SetProperty(ref _state, value);
    }

    /// <summary>projects.json 読み込み失敗時の問題（8.8のエラー状態表示に使う）。</summary>
    public GraftIssue? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    /// <summary>選択中のプロジェクト。変更すると <see cref="ProjectSelected"/> を発火する。</summary>
    public ProjectListItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (SetProperty(ref _selectedItem, value) && value is not null)
            {
                ProjectSelected?.Invoke(this, value.Project);
            }
        }
    }

    /// <summary>プロジェクトが選択された（切り替わった）ことの通知。</summary>
    public event EventHandler<Project>? ProjectSelected;

    /// <summary>projects.json を読み込み、検証・並べ替えを行って一覧を更新する。</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        State = ProjectPaneState.Loading;
        var loaded = await _store.LoadAsync(ct).ConfigureAwait(true);
        if (!loaded.IsSuccess)
        {
            Error = loaded.Errors.FirstOrDefault();
            State = ProjectPaneState.Error;
            return;
        }

        var validated = await _store.ValidateAsync(loaded.Value, ct).ConfigureAwait(true);
        ApplyItems(validated.Value);
    }

    /// <summary>フォルダを新規登録し、一覧を再読み込みする（D&D・フォルダ選択の両経路から呼ぶ）。</summary>
    public async Task<GraftResult<Project>> RegisterFolderAsync(string folderPath, CancellationToken ct = default)
    {
        var result = await _store.RegisterAsync(folderPath, name: null, ct).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            await LoadAsync(ct).ConfigureAwait(true);
            SelectedItem = Items.FirstOrDefault(i => i.Project.Id == result.Value.Id);
        }
        return result;
    }

    private async Task AddProjectViaDialogAsync()
    {
        var folder = _dialogs.PickFolder("プロジェクトフォルダを選択");
        if (string.IsNullOrEmpty(folder))
        {
            return;
        }
        await RegisterFolderAsync(folder).ConfigureAwait(true);
    }

    /// <summary>数字キー（1〜9）によるプロジェクト選択（仕様書3.2・8.10）。</summary>
    public bool SelectByShortcut(int number)
    {
        var item = Items.FirstOrDefault(i => i.ShortcutNumber == number);
        if (item is null)
        {
            return false;
        }
        SelectedItem = item;
        return true;
    }

    private void ApplyItems(IReadOnlyList<Project> projects)
    {
        var sorted = ProjectStore.Sort(projects);
        var previouslySelectedId = _selectedItem?.Project.Id;

        Items.Clear();
        var shortcut = 1;
        foreach (var project in sorted)
        {
            Items.Add(new ProjectListItemViewModel(project, shortcut <= 9 ? shortcut : null));
            shortcut++;
        }

        State = Items.Count == 0 ? ProjectPaneState.Empty : ProjectPaneState.Content;

        var restored = previouslySelectedId is null ? null : Items.FirstOrDefault(i => i.Project.Id == previouslySelectedId);
        if (restored is not null)
        {
            _selectedItem = restored;
            OnPropertyChanged(nameof(SelectedItem));
        }
        else if (Items.Count > 0)
        {
            SelectedItem = Items[0];
        }
    }
}
