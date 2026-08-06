using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using Graft.Core;
using Graft.Platform;

namespace Graft.ViewModels;

/// <summary>リビジョン履歴ペインの表示状態（仕様書8.8）。</summary>
public enum HistoryPaneState
{
    Loading,
    Empty,
    Error,
    Content,
}

/// <summary>
/// リビジョン履歴の1行。<c>git log</c> 相当の情報密度（仕様書7.2）で表示するための
/// 表示用プロパティを持つ。
/// </summary>
public sealed class RevisionRowViewModel
{
    public RevisionRowViewModel(RevisionSummary revision)
    {
        Revision = revision ?? throw new ArgumentNullException(nameof(revision));
    }

    public RevisionSummary Revision { get; }

    public string RevisionLabel => $"r{Revision.Manifest.Revision}";

    public string AppliedAtText => Revision.Manifest.AppliedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm");

    public string TypeText => string.IsNullOrWhiteSpace(Revision.Manifest.Type) ? "-" : Revision.Manifest.Type!;

    public string SummaryText => string.IsNullOrWhiteSpace(Revision.Manifest.Summary) ? "（要約なし）" : Revision.Manifest.Summary!;

    public string StatsText => $"{Revision.Manifest.Stats.Files} files   +{Revision.Manifest.Stats.Added} -{Revision.Manifest.Stats.Removed}";

    /// <summary>復元可能かどうか（仕様書13.1）。バックアップ実体が失われている場合は false。</summary>
    public bool CanRestore => Revision.IsRestorable;

    public string RestorabilityText => Revision.IsRestorable ? string.Empty : "復元不可（バックアップの実体が見つかりません）";

    /// <summary>色のみに依存しないための読み上げ用テキスト（8.14）。</summary>
    public string AutomationName
    {
        get
        {
            var basic = $"{RevisionLabel}、{AppliedAtText}、{TypeText}、{SummaryText}、{StatsText}";
            return CanRestore ? basic : $"{basic}、復元不可";
        }
    }
}

/// <summary>
/// 左ペイン下段「リビジョン履歴」の状態管理。仕様書7.1〜7.3。
/// summary の全文検索・type 絞り込み・日付範囲での絞り込み（7.2）と、選択行の復元（7.3）を担う。
/// </summary>
public sealed class HistoryPaneViewModel : ObservableObject
{
    private readonly RevisionStore _revisionStore;
    private readonly RevisionRestorer _restorer;
    private readonly IDialogService _dialogs;

    private string? _projectId;
    private string? _projectRoot;
    private IReadOnlyList<RevisionSummary> _allRevisions = Array.Empty<RevisionSummary>();

    private HistoryPaneState _state = HistoryPaneState.Empty;
    private GraftIssue? _error;
    private RevisionRowViewModel? _selectedItem;
    private string _keyword = string.Empty;
    private string? _typeFilter;
    private DateTimeOffset? _dateFrom;
    private DateTimeOffset? _dateTo;

    public HistoryPaneViewModel(RevisionStore revisionStore, RevisionRestorer restorer, IDialogService dialogs)
    {
        _revisionStore = revisionStore ?? throw new ArgumentNullException(nameof(revisionStore));
        _restorer = restorer ?? throw new ArgumentNullException(nameof(restorer));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        RestoreCommand = new AsyncRelayCommand(RestoreSelectedAsync, () => SelectedItem is { CanRestore: true });
    }

    public ObservableCollection<RevisionRowViewModel> Items { get; } = new();

    /// <summary>type 絞り込みの選択肢。仕様書4.2のtype一覧。</summary>
    public IReadOnlyList<string> AvailableTypes { get; } = new[] { "feat", "fix", "refactor", "docs", "test", "chore" };

    public HistoryPaneState State
    {
        get => _state;
        private set => SetProperty(ref _state, value);
    }

    public GraftIssue? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    public RevisionRowViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (SetProperty(ref _selectedItem, value))
            {
                RevisionSelected?.Invoke(this, value);
                ((AsyncRelayCommand)RestoreCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public string Keyword
    {
        get => _keyword;
        set => SetProperty(ref _keyword, value, ApplyFilter);
    }

    public string? TypeFilter
    {
        get => _typeFilter;
        set => SetProperty(ref _typeFilter, value, ApplyFilter);
    }

    public DateTimeOffset? DateFrom
    {
        get => _dateFrom;
        set => SetProperty(ref _dateFrom, value, ApplyFilter);
    }

    public DateTimeOffset? DateTo
    {
        get => _dateTo;
        set => SetProperty(ref _dateTo, value, ApplyFilter);
    }

    /// <summary>選択リビジョンが変わった（diffの再表示が必要になった）ことの通知。</summary>
    public event EventHandler<RevisionRowViewModel?>? RevisionSelected;

    /// <summary>復元が完了したことの通知（呼び出し側でブロック一覧・プロジェクト状態の更新に使う）。</summary>
    public event EventHandler? RevisionRestored;

    public ICommand RestoreCommand { get; }

    /// <summary>指定プロジェクトのリビジョン一覧を読み込む。</summary>
    public async Task LoadAsync(string projectId, string projectRoot, CancellationToken ct = default)
    {
        _projectId = projectId;
        _projectRoot = projectRoot;
        State = HistoryPaneState.Loading;

        var result = await _revisionStore.ListAsync(projectId, ct).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            Error = result.Errors.FirstOrDefault();
            State = HistoryPaneState.Error;
            return;
        }

        _allRevisions = result.Value;
        ApplyFilter();
    }

    /// <summary>プロジェクト未選択状態へ戻す。</summary>
    public void Clear()
    {
        _projectId = null;
        _projectRoot = null;
        _allRevisions = Array.Empty<RevisionSummary>();
        Items.Clear();
        SelectedItem = null;
        State = HistoryPaneState.Empty;
    }

    /// <summary>Ctrl+Z（直前リビジョンの取り消し）用。最新リビジョンを復元する。</summary>
    public async Task<bool> UndoLatestAsync(CancellationToken ct = default)
    {
        var latest = Items.FirstOrDefault();
        if (latest is null || !latest.CanRestore)
        {
            return false;
        }
        return await RestoreAsync(latest, ct).ConfigureAwait(true);
    }

    private Task RestoreSelectedAsync()
        => SelectedItem is null ? Task.CompletedTask : RestoreAsync(SelectedItem, CancellationToken.None).AsTask();

    private async ValueTask<bool> RestoreAsync(RevisionRowViewModel target, CancellationToken ct)
    {
        if (_projectId is null || _projectRoot is null)
        {
            return false;
        }

        var confirmed = await _dialogs
            .ConfirmAsync("復元の確認", $"{target.RevisionLabel} 直前の状態へ復元します。よろしいですか？")
            .ConfigureAwait(true);
        if (!confirmed)
        {
            return false;
        }

        var result = await _restorer.RestoreAsync(_projectId, _projectRoot, target.Revision, force: false, ct).ConfigureAwait(true);
        if (!result.IsSuccess && result.Errors.Any(i => i.Code == ErrorCode.E301))
        {
            var force = await _dialogs
                .ConfirmAsync("適用後の変更を検出", "復元対象のファイルが適用後にさらに変更されています。上書きして復元しますか？")
                .ConfigureAwait(true);
            if (!force)
            {
                return false;
            }
            result = await _restorer.RestoreAsync(_projectId, _projectRoot, target.Revision, force: true, ct).ConfigureAwait(true);
        }

        if (!result.IsSuccess)
        {
            await _dialogs
                .ConfirmAsync("復元に失敗しました", string.Join(Environment.NewLine, result.Errors.Select(i => i.ToDisplayText())))
                .ConfigureAwait(true);
            return false;
        }

        RevisionRestored?.Invoke(this, EventArgs.Empty);
        await LoadAsync(_projectId, _projectRoot, ct).ConfigureAwait(true);
        return true;
    }

    /// <summary>
    /// 選択中リビジョンの各エントリについて diff 付き <see cref="BlockPlan"/> を組み立てる
    /// （仕様書7.2「行を選択するとそのリビジョンのdiffを右ペインに再表示する」）。
    /// バックアップ側の内容を変更前、プロジェクトルート側の現在の内容を変更後として扱う
    /// （適用後にさらに変更されている場合は現在の内容がそのまま表示される）。
    /// 復元・適用の対象ではないため、返す BlockPlan はすべて CanApply=false とする。
    /// </summary>
    public async Task<IReadOnlyList<BlockPlan>> BuildDiffPlansAsync(
        RevisionRowViewModel row, int contextLines, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (_projectRoot is null)
        {
            return Array.Empty<BlockPlan>();
        }

        var plans = new List<BlockPlan>();
        foreach (var entry in row.Revision.Manifest.Entries)
        {
            var before = await ReadBackupTextAsync(row.Revision, entry, ct).ConfigureAwait(true);
            var after = await ReadCurrentTextAsync(_projectRoot, entry, ct).ConfigureAwait(true);
            var diff = Core.DiffBuilder.Build(entry.Path, before, after, contextLines);
            plans.Add(new BlockPlan
            {
                Block = new FullContentBlock { Path = entry.Path, Content = after ?? before ?? string.Empty },
                Path = entry.Path,
                Operation = entry.Operation,
                Stage = MatchStage.None,
                CanApply = false,
                NeedsConfirmation = false,
                IsSelected = false,
                BeforeText = before,
                AfterText = after,
                Diff = diff,
                Description = entry.Desc,
                Added = diff.Added,
                Removed = diff.Removed,
            });
        }
        return plans;
    }

    private static async Task<string?> ReadBackupTextAsync(RevisionSummary revision, RevisionEntry entry, CancellationToken ct)
    {
        if (entry.Operation is EntryOperation.Create or EntryOperation.Mkdir)
        {
            return null;
        }

        var relative = entry.Operation == EntryOperation.Rename ? entry.RenamedFrom : entry.Path;
        if (string.IsNullOrEmpty(relative))
        {
            return null;
        }

        var full = Path.Combine(revision.FolderPath, relative.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(full))
        {
            return null;
        }

        var read = await FileTextIO.ReadAsync(full, ct).ConfigureAwait(true);
        return read.IsSuccess ? read.Value.Text : null;
    }

    private static async Task<string?> ReadCurrentTextAsync(string projectRoot, RevisionEntry entry, CancellationToken ct)
    {
        if (entry.Operation == EntryOperation.Delete)
        {
            return null;
        }

        var full = Path.Combine(projectRoot, entry.Path.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(full))
        {
            return null;
        }

        var read = await FileTextIO.ReadAsync(full, ct).ConfigureAwait(true);
        return read.IsSuccess ? read.Value.Text : null;
    }

    private void ApplyFilter()
    {
        var filtered = RevisionStore.Filter(_allRevisions, Keyword, TypeFilter, DateFrom, DateTo).ToList();
        var previouslySelected = _selectedItem?.Revision.Manifest.Revision;

        Items.Clear();
        foreach (var revision in filtered)
        {
            Items.Add(new RevisionRowViewModel(revision));
        }

        State = _allRevisions.Count == 0 ? HistoryPaneState.Empty
            : Items.Count == 0 ? HistoryPaneState.Empty
            : HistoryPaneState.Content;

        var restored = previouslySelected is int rev ? Items.FirstOrDefault(i => i.Revision.Manifest.Revision == rev) : null;
        if (!ReferenceEquals(restored, _selectedItem))
        {
            SelectedItem = restored;
        }
    }
}
