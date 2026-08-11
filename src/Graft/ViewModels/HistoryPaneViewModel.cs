using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using Graft.Core;
using Graft.Features;
using Graft.Infra;
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
    private readonly Func<DateTimeOffset> _now;

    /// <summary>
    /// 細かいユーザビリティ改善3: <paramref name="now"/>は「現在時刻」を注入可能にするための
    /// 引数（既定はDateTimeOffset.Now）。<see cref="RelativeAppliedAtText"/>は呼ぶたびに
    /// <paramref name="now"/>と<see cref="Revision"/>のAppliedAtの差から再計算するため、
    /// 壁時計を直接参照せずテストできる（HistoryPaneViewModel._nowと同じ理由・同じ流儀。
    /// RelativeTimeFormatterTests.cs参照）。
    /// </summary>
    public RevisionRowViewModel(RevisionSummary revision, Func<DateTimeOffset>? now = null)
    {
        Revision = revision ?? throw new ArgumentNullException(nameof(revision));
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public RevisionSummary Revision { get; }

    public string RevisionLabel => $"r{Revision.Manifest.Revision}";

    public string AppliedAtText => Revision.Manifest.AppliedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm");

    /// <summary>
    /// 細かいユーザビリティ改善3: 「3分前」「昨日」といった相対表現。一覧では読み取りやすさを
    /// 優先してこちらを主表示にし、正確な日時（<see cref="AppliedAtText"/>）はホバー
    /// （ToolTip.Tip、HistoryPane.axaml参照）で確認できるようにする。
    /// </summary>
    public string RelativeAppliedAtText => RelativeTimeFormatter.Format(Revision.Manifest.AppliedAt, _now());

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
    private readonly ProjectStore _projectStore;
    private readonly IDialogService _dialogs;
    private readonly Func<DateTimeOffset> _now;

    /// <summary>
    /// 「ここまで戻す」の成否・所要時間をlogs/へ記録する（直近でapplyのログを追加した前例
    /// （MainViewModel.Apply.cs）と同じ流儀）。StartupCoordinatorが生成後に設定する。
    /// </summary>
    public Logger? Logger { get; set; }

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
    private string? _fileFilterPath;

    public HistoryPaneViewModel(
        RevisionStore revisionStore, RevisionRestorer restorer, ProjectStore projectStore, IDialogService dialogs,
        Func<DateTimeOffset>? now = null)
    {
        _revisionStore = revisionStore ?? throw new ArgumentNullException(nameof(revisionStore));
        _restorer = restorer ?? throw new ArgumentNullException(nameof(restorer));
        _projectStore = projectStore ?? throw new ArgumentNullException(nameof(projectStore));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        // 現在時刻を注入可能にしているのは、期間プリセット（過去7日等）の計算をテストで
        // 日付をまたがずに固定できるようにするため。既定はローカル時刻の DateTimeOffset.Now。
        _now = now ?? (() => DateTimeOffset.Now);
        RestoreCommand = new AsyncRelayCommand(RestoreSelectedAsync, () => SelectedItem is { CanRestore: true }, context: "リビジョンの復元");
        // 「ここまで戻す」: 単発復元（RestoreCommand）とは別の操作として追加する（仕様）。
        // 取り消し対象が無い（最新リビジョンを選んでいる）場合は無効化する（8番目の要件）。
        RestoreThroughCommand = new AsyncRelayCommand(RestoreThroughSelectedAsync, HasRestoreThroughTarget, context: "ここまで戻す");
        // 修正2: 右クリックメニュー「差分を表示」。選択自体は変えず、同じ選択に対して
        // RevisionSelectedを明示的に再発火する（修正1の履歴差分タブを開き直す入口）。
        // 選択を変えたときは自動でタブが開く／更新されるが、タブを×で閉じた後に選択は
        // そのままという状況では選択の変化が起きないため、この明示的な入口が要る。
        ShowDiffCommand = new RelayCommand(
            () => { if (SelectedItem is { } item) RevisionSelected?.Invoke(this, item); },
            () => SelectedItem is not null);
        // ファイル単位の変更履歴（エクスプローラの右クリックメニュー「このファイルの変更履歴」）:
        // 絞り込みを解除して通常の全件表示へ戻すためのコマンド（バナーの「×」ボタン用）。
        ClearFileFilterCommand = new RelayCommand(() => FileFilterPath = null, () => IsFileFiltered);
        // E: 選択中リビジョンのback/フォルダをファイルマネージャで開く。フォルダの実体が
        // ディスク上に無ければ（history.jsonlのみ残っている等）無効化する。
        OpenBackupFolderCommand = new RelayCommand(
            () => { if (SelectedItem is { } item) PlatformServices.Current.FileManager.Reveal(item.Revision.FolderPath); },
            () => SelectedItem is not null && Directory.Exists(SelectedItem.Revision.FolderPath));
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
                ((AsyncRelayCommand)RestoreThroughCommand).RaiseCanExecuteChanged();
                ((RelayCommand)ShowDiffCommand).RaiseCanExecuteChanged();
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

    /// <summary>
    /// ファイル単位の変更履歴: 絞り込み対象のファイルの相対パス（'/'区切り、プロジェクトルート
    /// 基準。FileNodeViewModel.RelativePathと同じ形式）。nullは絞り込みなし（通常の履歴表示）。
    /// エクスプローラの右クリックメニュー「このファイルの変更履歴」から<see cref="ShowHistoryForFile"/>
    /// 経由でのみ設定する想定（setterはprivate）。
    /// </summary>
    public string? FileFilterPath
    {
        get => _fileFilterPath;
        private set
        {
            if (SetProperty(ref _fileFilterPath, value, ApplyFilter))
            {
                OnPropertyChanged(nameof(IsFileFiltered));
                OnPropertyChanged(nameof(FileFilterBannerText));
                OnPropertyChanged(nameof(EmptyStateMessage));
                ((RelayCommand)ClearFileFilterCommand).RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>ファイル単位の絞り込み中かどうか（履歴ビュー側でバナー表示の可否に使う）。</summary>
    public bool IsFileFiltered => FileFilterPath is not null;

    /// <summary>絞り込み中であることを明示するバナー文言。絞り込みなしのときは空文字。</summary>
    public string FileFilterBannerText
        => FileFilterPath is null ? string.Empty : $"「{FileFilterPath}」の変更履歴のみ表示中";

    /// <summary>
    /// 一覧が空のときの案内文言。ファイル単位で絞り込み中は「このファイルには履歴が無い」ことが
    /// わかる文言に、それ以外は従来どおりの案内にする。
    /// </summary>
    public string EmptyStateMessage
        => FileFilterPath is null
            ? "AIの出力を適用すると、ここに履歴が残ります"
            : $"「{FileFilterPath}」の変更履歴はありません";

    /// <summary>
    /// エクスプローラの右クリックメニュー「このファイルの変更履歴」から呼ぶ。対象ファイルの
    /// 相対パスで一覧を絞り込む。既存のキーワード・種別・期間の絞り込みと組み合わさって
    /// 意図せず0件になることを避けるため、これらは呼び出しのたびにリセットする
    /// （「このファイルの全履歴を見せる」という明確な操作のため、他の絞り込み条件を
    /// 引き継がない方が利用者の意図に合う）。
    /// </summary>
    public void ShowHistoryForFile(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        _keyword = string.Empty;
        _typeFilter = null;
        _dateFrom = null;
        _dateTo = null;
        _dateFromText = string.Empty;
        _dateToText = string.Empty;
        _datePreset = HistoryDatePreset.All;

        // FileFilterPathのsetterがApplyFilterを1回だけ走らせるため、上でフィールドを
        // 直接書き換えた分の変更通知はここでまとめて出す（各プロパティのsetter経由だと
        // ApplyFilterが複数回走ってしまう）。
        FileFilterPath = relativePath;
        OnPropertyChanged(nameof(Keyword));
        OnPropertyChanged(nameof(TypeFilter));
        OnPropertyChanged(nameof(SelectedTypeOption));
        OnPropertyChanged(nameof(DateFrom));
        OnPropertyChanged(nameof(DateTo));
        OnPropertyChanged(nameof(DateFromText));
        OnPropertyChanged(nameof(DateToText));
        OnPropertyChanged(nameof(DatePreset));
    }

    /// <summary>選択リビジョンが変わった（diffの再表示が必要になった）ことの通知。</summary>
    public event EventHandler<RevisionRowViewModel?>? RevisionSelected;

    /// <summary>復元が完了したことの通知（呼び出し側でブロック一覧・プロジェクト状態の更新に使う）。</summary>
    public event EventHandler? RevisionRestored;

    public ICommand RestoreCommand { get; }

    /// <summary>
    /// 「ここまで戻す」（まとめ戻し）。選択リビジョンより新しいリビジョンをすべて新しい順に
    /// 取り消し、選択リビジョンを適用した直後の状態を再現する。単発復元（RestoreCommand）とは
    /// 別の操作であり、既存の単発復元の挙動には影響しない。
    /// </summary>
    public ICommand RestoreThroughCommand { get; }

    /// <summary>
    /// 修正2: 履歴の右クリックメニュー「差分を表示」。選択を変えずに、修正1の履歴差分タブを
    /// 明示的に開き直す（選択済みリビジョンでタブだけを×で閉じた後の再表示手段）。
    /// </summary>
    public ICommand ShowDiffCommand { get; }

    /// <summary>
    /// ファイル単位の変更履歴の絞り込みを解除し、通常の全件表示へ戻す（バナーの「×」ボタン用）。
    /// </summary>
    public ICommand ClearFileFilterCommand { get; }

    /// <summary>
    /// E: 履歴の右クリックメニュー「バックアップフォルダを開く」。選択中のリビジョンの
    /// back/ フォルダ（<see cref="RevisionSummary.FolderPath"/>）をファイルマネージャで開く。
    /// 「本当に退避されているか」を自分の目で確かめられる安心材料のため、復元可否
    /// （<see cref="RevisionRowViewModel.CanRestore"/>）に関わらず、フォルダの実体が
    /// ディスク上に存在する限り開けるようにする（存在しない場合のみ無効化する）。
    /// </summary>
    public ICommand OpenBackupFolderCommand { get; }

    /// <summary>選択中のリビジョンより新しいリビジョンが1件でもあるか（＝取り消す対象があるか）。
    /// 最新リビジョンを選んでいるときは対象が無いため false（RestoreThroughCommandを無効化する）。
    /// フィルタで一覧が絞られていても、判定は常に全リビジョン（<see cref="_allRevisions"/>）基準で行う。</summary>
    private bool HasRestoreThroughTarget()
        => SelectedItem is not null && _allRevisions.Any(r => r.Manifest.Revision > SelectedItem.Revision.Manifest.Revision);

    /// <summary>指定プロジェクトのリビジョン一覧を読み込む。</summary>
    public async Task LoadAsync(string projectId, string projectRoot, CancellationToken ct = default)
    {
        if (_projectId != projectId)
        {
            // プロジェクトが切り替わった場合は、別プロジェクトの相対パスを引き継がないよう
            // ファイル単位の絞り込み（FileFilterPath）を解除する。同じプロジェクト内での
            // 再読み込み（復元後の一覧更新等）ではこの絞り込みを保ったままにしたいため、
            // ここではプロジェクトIDが変わったときだけリセットする。
            FileFilterPath = null;
        }

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
        FileFilterPath = null; // ファイル単位の絞り込みも解除する（次のプロジェクトへ引き継がないため）。
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
        if (!result.IsSuccess && result.HasIssue(ErrorCode.E301))
        {
            var force = await _dialogs
                .ConfirmAsync("適用後の変更を検出", BuildAppliedAfterChangeMessage(target.RevisionLabel, result.Issues, "復元すると"))
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

        // 不具合3対応: 単発復元（「このリビジョンを取り消す」）は新規リビジョンを記録しないが、
        // 実際にプロジェクトのファイルを書き換える＝そのプロジェクトを触った操作であるため、
        // 「最後に適用した日時」の更新対象に含める（判断理由はProjectModels.cs
        // Project.LastAppliedAtのコメント参照）。
        await MarkProjectAppliedAsync(ct).ConfigureAwait(true);
        RevisionRestored?.Invoke(this, EventArgs.Empty);
        await LoadAsync(_projectId, _projectRoot, ct).ConfigureAwait(true);
        return true;
    }

    /// <summary>
    /// 不具合3対応: <see cref="ProjectStore.MarkAppliedAsync"/>を呼ぶ薄いラッパー。呼び出し時点で
    /// <c>_projectId</c>は非nullであることを呼び出し元が保証する。失敗しても復元自体は既に
    /// 成功しているためエラー扱いにはせず、ログにのみ残す。
    /// </summary>
    private async Task MarkProjectAppliedAsync(CancellationToken ct)
    {
        var marked = await _projectStore.MarkAppliedAsync(_projectId!, DateTimeOffset.Now, ct).ConfigureAwait(true);
        if (!marked.IsSuccess)
        {
            Logger?.Warn("restore", "最終適用日時の更新に失敗しました（履歴の並び順に影響する可能性があります）");
        }
    }

    private Task RestoreThroughSelectedAsync()
        => SelectedItem is null ? Task.CompletedTask : RestoreThroughAsync(SelectedItem, CancellationToken.None).AsTask();

    /// <summary>
    /// 「ここまで戻す」の実体。事前確認（対象リビジョン数・影響ファイル・復元不可の検出）→
    /// 確認ダイアログ→リビジョン番号の消費→<see cref="RevisionRestorer.RestoreThroughAsync"/>の
    /// 順で進める。既存の<see cref="RestoreAsync"/>（単発復元）と同じくE301（適用後の変更検出）は
    /// forceで再試行できるようにする。
    /// </summary>
    private async ValueTask<bool> RestoreThroughAsync(RevisionRowViewModel target, CancellationToken ct)
    {
        if (_projectId is null || _projectRoot is null)
        {
            return false;
        }

        var preview = RevisionRestorer.BuildRestoreThroughPreview(_allRevisions, target.Revision.Manifest.Revision);
        if (preview.RevisionsToUndo.Count == 0)
        {
            await _dialogs
                .ShowMessageAsync("ここまで戻す", $"{target.RevisionLabel} は最新のリビジョンのため、取り消す対象がありません。")
                .ConfigureAwait(true);
            return false;
        }
        if (preview.NotRestorable.Count > 0)
        {
            var names = string.Join("、", preview.NotRestorable.Select(r => $"r{r.Manifest.Revision}"));
            await _dialogs
                .ShowMessageAsync(
                    "ここまで戻せません",
                    $"取り消し対象にバックアップの実体が失われているリビジョンが含まれるため中止しました（{names}）。" +
                    "順序を保ったまま取り消せないリビジョンを飛ばして続行すると内容が壊れるため、この操作は実行できません。")
                .ConfigureAwait(true);
            return false;
        }

        if (!await ConfirmRestoreThroughAsync(target, preview).ConfigureAwait(true))
        {
            return false;
        }

        var newRevision = await _projectStore.ConsumeNextRevisionAsync(_projectId, ct).ConfigureAwait(true);
        if (!newRevision.IsSuccess)
        {
            await _dialogs
                .ShowMessageAsync("ここまで戻せません", string.Join(Environment.NewLine, newRevision.Errors.Select(i => i.ToDisplayText())))
                .ConfigureAwait(true);
            return false;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = await _restorer
            .RestoreThroughAsync(
                _projectId, _projectRoot, target.Revision.Manifest.Revision, preview.RevisionsToUndo, newRevision.Value, force: false, ct)
            .ConfigureAwait(true);
        // 不具合対応: このE301再試行はnewRevision.Valueを変えずに同じ番号でforce実行し直すだけ
        // であり、この分岐に入った時点ではnextRevisionは既にnewRevision.Value+1のままで、
        // 番号は「まだ記録されていない状態」で保留されているだけである。ここで番号を返却
        // （＝nextRevisionをnewRevision.Valueへ戻す）してはいけない。返却してしまうと、
        // 直後のforce再試行が同じnewRevision.Valueを使ってバックアップフォルダを作る一方、
        // 万一この間に別の操作がConsumeNextRevisionAsyncを呼べば同じ番号を取得してしまい、
        // 同一リビジョン番号のフォルダが衝突する。
        if (!result.IsSuccess && result.HasIssue(ErrorCode.E301))
        {
            var newestLabel = $"r{preview.RevisionsToUndo[0].Manifest.Revision}";
            var force = await _dialogs
                .ConfirmAsync(
                    "適用後の変更を検出",
                    BuildAppliedAfterChangeMessage(newestLabel, result.Issues, "「ここまで戻す」を続行すると"))
                .ConfigureAwait(true);
            if (!force)
            {
                return false;
            }
            result = await _restorer
                .RestoreThroughAsync(
                    _projectId, _projectRoot, target.Revision.Manifest.Revision, preview.RevisionsToUndo, newRevision.Value, force: true, ct)
                .ConfigureAwait(true);
        }
        stopwatch.Stop();

        if (!result.IsSuccess)
        {
            // 6番目の要件: 途中で失敗しても成功したとは報告しない。RevisionRestorer側で
            // 「どこまで戻せたか」「次に何をすればよいか」を含めた日本語メッセージを組み立てて
            // 返すため、そのままダイアログへ出す。r{newRevision}としては記録済みのため、
            // 一覧を再読み込みして反映する（中途半端な状態であることが一覧からも分かるように）。
            //
            // 不具合対応: ここでnewRevision.Valueの番号を返却してはいけない。RevisionRestorer.
            // RestoreThroughAsyncは、失敗するタイミングによっては既にBackupManager.BeginAsyncで
            // r{newRevision}用のバックアップフォルダを作成済み（status: in_progress、6.3の中断復帰
            // 検出の対象）であったり、一部のファイルを実際に書き換えた中途半端な状態のまま
            // in_progressとしてhistory.jsonlへ記録済みだったりする（上記コメント「r{newRevision}
            // としては記録済みのため」の通り）。この場合にnextRevisionをnewRevision.Valueへ戻すと、
            // 次回の消費で同じ番号が再び使われ、既に存在するr{newRevision}のフォルダ・記録と
            // 衝突する。空リビジョン抑止（Entries.Count == 0）のときだけ、フォルダごと破棄済みで
            // 記録が一切残っていないことが保証されているため、その場合に限り返却してよい。
            Logger?.Error("restore-through", string.Join(" / ", result.Errors.Select(i => i.ToDisplayText())),
                revision: newRevision.Value, durationMs: stopwatch.ElapsedMilliseconds);
            await _dialogs
                .ShowMessageAsync("ここまで戻せませんでした", string.Join(Environment.NewLine, result.Errors.Select(i => i.ToDisplayText())))
                .ConfigureAwait(true);
            await LoadAsync(_projectId, _projectRoot, ct).ConfigureAwait(true);
            return false;
        }

        // 4番目の要件: 取り消した結果ファイルが1つも変わらなかった場合（既にその状態だった
        // 場合）、RevisionRestorer.RestoreThroughAsyncは空リビジョンを記録せずバックアップ
        // フォルダを破棄済み（result.Value.Entries.Count == 0がその目印）。履歴には何も
        // 増えないため、一覧の再読み込みやRevisionRestoredの発火は行わず、その旨だけを伝える。
        if (result.Value.Entries.Count == 0)
        {
            // 不具合対応: 空リビジョン抑止が働き記録しなかった場合、事前に
            // ConsumeNextRevisionAsyncで消費した番号（newRevision.Value）を返却し、履歴の欠番
            // （r7 → r9のように見える見た目）を防ぐ。ReleaseRevisionAsync自身がnextRevisionが
            // 既に他の操作で進んでいないかを安全確認するため、ここでは無条件に呼んでよい。
            // 返却に失敗（または安全条件を満たさず何もしなかった）しても、「ここまで戻す」自体は
            // 既に成功しているため利用者へはエラーにせず、ログにのみ残す（欠番が1つ残るだけで
            // データの整合性への実害は無い）。
            var released = await _projectStore.ReleaseRevisionAsync(_projectId, newRevision.Value, ct).ConfigureAwait(true);
            if (!released.IsSuccess || !released.Value)
            {
                Logger?.Warn("restore-through",
                    $"r{newRevision.Value}の番号を返却できませんでした（欠番として残ります）",
                    revision: newRevision.Value, durationMs: stopwatch.ElapsedMilliseconds);
            }

            Logger?.Info("restore-through",
                $"{target.RevisionLabel}まで戻す操作を行いましたが、ファイルは既にこの状態のため変更はありませんでした（リビジョンは記録していません）",
                durationMs: stopwatch.ElapsedMilliseconds);
            await _dialogs
                .ShowMessageAsync("変更はありませんでした",
                    $"ファイルは既に {target.RevisionLabel} を適用した直後の状態のため、変更はありませんでした。リビジョンは記録していません。")
                .ConfigureAwait(true);
            return true;
        }

        Logger?.Info("restore-through",
            $"{target.RevisionLabel}まで戻し、r{result.Value.Revision}として記録しました（{result.Value.Entries.Count}件）",
            revision: result.Value.Revision, durationMs: stopwatch.ElapsedMilliseconds);
        foreach (var issue in result.Issues.Where(i => i.Severity != Severity.Error))
        {
            Logger?.Warn("restore-through", issue.ToDisplayText(), revision: result.Value.Revision, targetPath: issue.Path);
        }

        // 不具合3対応: 「ここまで戻す」が成功し新規リビジョンとして記録された時点で
        // 「最後に適用した日時」を更新する（詳細はMarkProjectAppliedAsyncのコメント参照）。
        await MarkProjectAppliedAsync(ct).ConfigureAwait(true);
        RevisionRestored?.Invoke(this, EventArgs.Empty);
        await LoadAsync(_projectId, _projectRoot, ct).ConfigureAwait(true);
        await _dialogs
            .ShowMessageAsync("ここまで戻しました", $"{target.RevisionLabel} を適用した直後の状態まで戻し、r{result.Value.Revision} として記録しました。")
            .ConfigureAwait(true);
        return true;
    }

    /// <summary>
    /// E301（適用後の変更検出）で復元が止まったときの確認ダイアログの本文を組み立てる。
    /// 「何が起きているか」（<paramref name="revisionLabel"/>適用後に対象ファイルが手作業で
    /// 変更・削除されている）と、「続行すると何が失われるか」（その手直しが上書きされて消える）
    /// の両方を明示する。影響ファイルが多い場合は件数集約する（<see cref="ConfirmRestoreThroughAsync"/>
    /// と同じ方針）。
    /// </summary>
    private static string BuildAppliedAfterChangeMessage(string revisionLabel, IReadOnlyList<GraftIssue> issues, string continuation)
    {
        const int MaxFilesToList = 10;
        var paths = issues
            .Where(i => i.Code == ErrorCode.E301 && i.Path is not null)
            .Select(i => i.Path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var fileList = paths.Count <= MaxFilesToList
            ? string.Join("\n", paths)
            : string.Join("\n", paths.Take(MaxFilesToList)) + $"\n…ほか{paths.Count - MaxFilesToList}件";

        return $"{revisionLabel} を適用した後に、次の{paths.Count}件のファイルが手作業で変更されています（削除されたものを含みます）。\n\n" +
               $"{fileList}\n\n" +
               $"このまま{continuation}、これらの変更は失われます。続行しますか？";
    }

    /// <summary>
    /// 3番目の要件: 実行前に「何を・いくつ・どのファイルに対して行うか」を示す確認ダイアログ。
    /// 取り消し対象のリビジョン数と影響ファイル一覧（多ければ件数集約）を含める。
    /// </summary>
    private Task<bool> ConfirmRestoreThroughAsync(RevisionRowViewModel target, RestoreThroughPreview preview)
    {
        const int MaxFilesToList = 10;
        var revisionList = string.Join("、", preview.RevisionsToUndo.Select(r => $"r{r.Manifest.Revision}"));
        var fileList = preview.AffectedPaths.Count <= MaxFilesToList
            ? string.Join("\n", preview.AffectedPaths)
            : string.Join("\n", preview.AffectedPaths.Take(MaxFilesToList)) + $"\n…ほか{preview.AffectedPaths.Count - MaxFilesToList}件";

        var message =
            $"{target.RevisionLabel} を適用した直後の状態まで戻します。\n\n" +
            $"取り消すリビジョン（{preview.RevisionsToUndo.Count}件、新しい順）:\n{revisionList}\n\n" +
            $"影響を受けるファイル（{preview.AffectedPaths.Count}件）:\n{fileList}\n\n" +
            "この操作自体も新しいリビジョンとして記録されるため、後から「このリビジョンを取り消す」で元に戻せます。よろしいですか？";
        return _dialogs.ConfirmAsync("ここまで戻す確認", message);
    }

    /// <summary>
    /// 選択中リビジョンの各エントリについて diff 付き <see cref="BlockPlan"/> を組み立てる
    /// （仕様書7.2「行を選択するとそのリビジョンのdiffを右ペインに再表示する」）。
    /// バックアップ側の内容を変更前、プロジェクトルート側の現在の内容を変更後として扱う
    /// （適用後にさらに変更されている場合は現在の内容がそのまま表示される）。
    /// 復元・適用の対象ではないため、返す BlockPlan はすべて CanApply=false とする。
    /// <see cref="FileFilterPath"/>で絞り込み中は、そのファイルに該当するエントリだけを返す
    /// （ファイル単位の変更履歴で開く履歴差分タブに、無関係な他ファイルの差分を含めないため）。
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
            if (FileFilterPath is not null && !RevisionStore.EntryPathEquals(entry.Path, FileFilterPath))
            {
                continue;
            }

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
        var filtered = RevisionStore.Filter(_allRevisions, Keyword, TypeFilter, DateFrom, DateTo, FileFilterPath).ToList();
        var previouslySelected = _selectedItem?.Revision.Manifest.Revision;

        Items.Clear();
        foreach (var revision in filtered)
        {
            Items.Add(new RevisionRowViewModel(revision, _now));
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
