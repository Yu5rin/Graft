using System.Collections.ObjectModel;
using System.Globalization;
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
/// 期間絞り込みのプリセット（仕様書7.2）。手入力のみでは絞り込みに手間がかかるため、
/// 「直近の適用を見たい」という大半の用途をワンクリックで満たせるようにする。
/// <see cref="Custom"/> は手入力で任意の範囲を指定している状態を表す表示専用の値で、
/// ドロップダウンで選んでも何も変化しない（現在の入力内容をそのまま保つ）。
/// </summary>
public enum HistoryDatePreset
{
    /// <summary>手入力など、他のいずれのプリセットにも一致しない状態（指定期間）。</summary>
    Custom,
    /// <summary>絞り込みなし。</summary>
    All,
    /// <summary>当日0時から。</summary>
    Today,
    /// <summary>当日を含む直近7日（6日前0時から）。</summary>
    Last7Days,
    /// <summary>当日を含む直近30日（29日前0時から）。</summary>
    Last30Days,
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

    // 9章: UIの文言は日本語で統一する。増減の +/- は記号として残す。
    public string StatsText => $"{Revision.Manifest.Stats.Files}ファイル   +{Revision.Manifest.Stats.Added} -{Revision.Manifest.Stats.Removed}";

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
    private readonly Func<DateTimeOffset> _now;

    private string? _projectId;
    private string? _projectRoot;
    private IReadOnlyList<RevisionSummary> _allRevisions = Array.Empty<RevisionSummary>();

    private HistoryPaneState _state = HistoryPaneState.Empty;
    private GraftIssue? _error;
    private RevisionRowViewModel? _selectedItem;
    private string _keyword = string.Empty;
    private string? _typeFilter;
    private string _dateFromText = string.Empty;
    private string _dateToText = string.Empty;
    private DateTimeOffset? _dateFrom;
    private DateTimeOffset? _dateTo;
    private HistoryDatePreset _datePreset = HistoryDatePreset.All;
    private bool _isApplyingPreset;

    public HistoryPaneViewModel(
        RevisionStore revisionStore, RevisionRestorer restorer, IDialogService dialogs, Func<DateTimeOffset>? now = null)
    {
        _revisionStore = revisionStore ?? throw new ArgumentNullException(nameof(revisionStore));
        _restorer = restorer ?? throw new ArgumentNullException(nameof(restorer));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        // 現在時刻を注入可能にしているのは、期間プリセット（過去7日等）の計算をテストで
        // 日付をまたがずに固定できるようにするため。既定はローカル時刻の DateTimeOffset.Now。
        _now = now ?? (() => DateTimeOffset.Now);
        RestoreCommand = new AsyncRelayCommand(RestoreSelectedAsync, () => SelectedItem is { CanRestore: true });
    }

    public ObservableCollection<RevisionRowViewModel> Items { get; } = new();

    /// <summary>
    /// 課題2-2: 種別絞り込みドロップダウンの先頭に出す「絞り込みなし」の選択肢。
    /// 未選択（TypeFilterがnull）のとき空欄のままだと何を選ぶドロップダウンか分からなかったため、
    /// 既定でこれを選択済みにする（<see cref="SelectedTypeOption"/>参照）。
    /// </summary>
    public const string AllTypesOption = "すべての種別";

    /// <summary>type 絞り込みの選択肢。仕様書4.2のtype一覧に<see cref="AllTypesOption"/>を先頭に足す。</summary>
    public IReadOnlyList<string> AvailableTypes { get; } =
        new[] { AllTypesOption, "feat", "fix", "refactor", "docs", "test", "chore" };

    /// <summary>
    /// 課題2-2: ComboBoxが直接バインドする表示用プロパティ。<see cref="TypeFilter"/>
    /// （null＝絞り込みなし）を、ドロップダウンが常に何か選択済みの状態を保てるよう
    /// <see cref="AllTypesOption"/>と相互変換する。実際の絞り込みは従来どおりTypeFilter
    /// （null許容）で行うため、絞り込みロジック側に影響はない。
    /// </summary>
    public string SelectedTypeOption
    {
        get => TypeFilter ?? AllTypesOption;
        set => TypeFilter = value == AllTypesOption ? null : value;
    }

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
        set
        {
            if (SetProperty(ref _typeFilter, value, ApplyFilter))
            {
                // 課題2-2: SelectedTypeOptionはTypeFilterの表示用ラッパーのため、
                // TypeFilter自体が（×ボタン等、SelectedTypeOption経由以外から）変わったときも
                // ドロップダウンの表示（AllTypesOption⇔実際の種別）を追従させる。
                OnPropertyChanged(nameof(SelectedTypeOption));
            }
        }
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

    /// <summary>
    /// 絞り込みの開始日を「yyyy-MM-dd」の文字列として読み書きする（空文字は指定なし）。
    ///
    /// 日付選択コントロールを使わないのは、UIをすべて日本語で統一する方針（9章）に対して
    /// AvaloniaのDatePickerが英語の項目名（year/month/day）を表示し、差し替える手段が
    /// 無いため。加えて、サイドビューの幅（既定260px）に3項目分の枠が収まらない。
    /// 解釈できない入力は「指定なし」として扱い、入力の途中で一覧が消えないようにする。
    ///
    /// 手入力欄だけでは細かい範囲は指定できても、大半の用途である「直近の適用を見たい」
    /// という操作のたびに日付を打ち直すのは不親切なため、<see cref="DatePreset"/> で
    /// 「今日」「過去7日」「過去30日」等のプリセットを選ぶだけでこの欄へ反映できるようにする。
    /// 手入力欄そのものは、細かい範囲指定の手段として残す。
    /// </summary>
    public string DateFromText
    {
        get => FormatDate(_dateFrom);
        set
        {
            if (EqualityComparer<string>.Default.Equals(_dateFromText, value))
            {
                return;
            }
            _dateFromText = value;
            // DateFromの反映（DateFrom自身の通知とApplyFilterを含む）を先に済ませてから
            // DateFromText自身の変更通知を出す。逆順だと、このプロパティのgetterが参照する
            // _dateFromがまだ更新されておらず、バインド先（TextBox）へ古い値を読み直させて
            // しまう（プリセット選択でテキスト欄が更新されない不具合の原因になっていた）。
            DateFrom = ParseDate(value);
            OnPropertyChanged();
            SyncPresetWithManualInput();
        }
    }

    /// <inheritdoc cref="DateFromText"/>
    public string DateToText
    {
        get => FormatDate(_dateTo);
        set
        {
            if (EqualityComparer<string>.Default.Equals(_dateToText, value))
            {
                return;
            }
            _dateToText = value;
            DateTo = ParseDate(value);
            OnPropertyChanged();
            SyncPresetWithManualInput();
        }
    }

    /// <summary>期間絞り込みのプリセット選択肢（ドロップダウンの表示順）。</summary>
    public IReadOnlyList<HistoryDatePreset> AvailableDatePresets { get; } = new[]
    {
        HistoryDatePreset.All,
        HistoryDatePreset.Today,
        HistoryDatePreset.Last7Days,
        HistoryDatePreset.Last30Days,
        HistoryDatePreset.Custom,
    };

    /// <summary>
    /// 期間絞り込みのプリセット選択状態。選ぶと即座に <see cref="DateFromText"/>・
    /// <see cref="DateToText"/> へ反映する（<see cref="HistoryDatePreset.Custom"/> を除く）。
    /// 手入力欄を直接編集した場合は、選択中の表示をこの値経由で <see cref="HistoryDatePreset.Custom"/>
    /// （空欄なら<see cref="HistoryDatePreset.All"/>）へ切り替え、プリセット表示と手入力欄の
    /// 内容が矛盾しないようにする。
    /// </summary>
    public HistoryDatePreset DatePreset
    {
        get => _datePreset;
        set => SetProperty(ref _datePreset, value, () => OnDatePresetChanged(value));
    }

    private void OnDatePresetChanged(HistoryDatePreset preset)
    {
        if (_isApplyingPreset || preset == HistoryDatePreset.Custom)
        {
            return;
        }
        ApplyPresetRange(preset);
    }

    /// <summary>
    /// プリセットから開始日・終了日の入力欄を組み立てる。終了日は常に指定なし（＝現在まで）とする。
    /// 開始日は「その日の0時」を基準にし、<see cref="DateFromText"/> と同じ書式・解釈で反映する。
    /// </summary>
    private void ApplyPresetRange(HistoryDatePreset preset)
    {
        _isApplyingPreset = true;
        try
        {
            var todayStart = StartOfToday(_now());
            DateFromText = preset switch
            {
                HistoryDatePreset.Today => FormatDate(todayStart),
                // 「直近7日／30日」は当日を含むため、6日前／29日前の0時が起点になる。
                HistoryDatePreset.Last7Days => FormatDate(todayStart.AddDays(-6)),
                HistoryDatePreset.Last30Days => FormatDate(todayStart.AddDays(-29)),
                _ => string.Empty, // All（全期間）
            };
            DateToText = string.Empty;
        }
        finally
        {
            _isApplyingPreset = false;
        }
    }

    /// <summary>手入力欄が編集されたとき、プリセット表示を実際の内容に合わせて更新する。</summary>
    private void SyncPresetWithManualInput()
    {
        if (_isApplyingPreset)
        {
            return;
        }
        var isUnfiltered = string.IsNullOrEmpty(_dateFromText) && string.IsNullOrEmpty(_dateToText);
        DatePreset = isUnfiltered ? HistoryDatePreset.All : HistoryDatePreset.Custom;
    }

    private static DateTimeOffset StartOfToday(DateTimeOffset now) => new(now.Date, now.Offset);

    private static string FormatDate(DateTimeOffset? value)
        => value is null ? string.Empty : value.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateTimeOffset? ParseDate(string? text)
        => DateTimeOffset.TryParseExact(
            text?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;

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
