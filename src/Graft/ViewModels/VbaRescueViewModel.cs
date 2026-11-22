using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;

namespace Graft.ViewModels;

/// <summary>控えの一覧の1行。</summary>
public sealed class VbaRescueRowViewModel : ObservableObject
{
    public VbaRescueRowViewModel(VbaRescueItem item, bool showModuleHeader)
    {
        Item = item;
        ShowModuleHeader = showModuleHeader;
    }

    /// <summary>控え。</summary>
    public VbaRescueItem Item { get; }

    /// <summary>モジュールごとの先頭の行だけ、モジュール名の見出しを出す（一覧をモジュールごとにまとめて見せるため）。</summary>
    public bool ShowModuleHeader { get; }

    /// <summary>モジュール名。</summary>
    public string ModuleName => Item.Module;

    /// <summary>日時（秒まで）。</summary>
    public string TimeText => Item.At.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>種別（「反映前の自動控え」「上書き前」「戻す前のミラー」）。</summary>
    public string KindText => Item.KindLabel;

    /// <summary>行数とサイズ。</summary>
    public string DetailText => $"{Item.Lines} 行 / {FormatSize(Item.Size)}";

    /// <summary>読み上げ用の名前。</summary>
    public string AutomationName => $"{Item.Module}、{TimeText}、{KindText}、{DetailText}";

    internal static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.#} MB",
    };
}

/// <summary>
/// 「Excel 側の控えを見る」窓（<see cref="Views.VbaRescueWindow"/>）の ViewModel（仕様書_ブックの情報と依頼の観点 7.4・7.5）。
/// 左にモジュールごとの控えの一覧、右に<b>選んだ控えと現在のミラーの差分</b>（既存の <see cref="DiffViewModel"/>）。
/// ボタンは「ミラーへ戻す」「控えを削除」「保存先を開く」。
/// <para>
/// 【履歴とは混ぜない】 履歴ペインに何も増やさない（付録A-8）。戻した結果は <c>Logger</c> に <c>vba-rescue</c> で残る。
/// ファイルを見るだけなので、Excel が開いていなくても、<c>Vba.Enabled</c> が false でも開ける。戻したあとの
/// 「今すぐ Excel へ反映」だけが Excel に触れ、既存の Push（衝突ダイアログを含む）を使う。
/// </para>
/// </summary>
public sealed class VbaRescueViewModel : ObservableObject
{
    private readonly Project _project;
    private readonly VbaRescueService _service;
    private readonly int _contextLines;
    private VbaRescueRowViewModel? _selected;
    private string _statusText = string.Empty;
    private string _warningText = string.Empty;
    private string? _restoredRelativePath;
    private int _loadVersion;
    private string _diffCaptionText = "左: 控え / 右: 現在のミラー";

    public VbaRescueViewModel(Project project, VbaRescueService service, Settings settings, IUiServices ui)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
        _service = service ?? throw new ArgumentNullException(nameof(service));
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(ui);

        _contextLines = settings.Diff.ContextLines;
        Diff = new DiffViewModel(settings, ui)
        {
            WordWrap = settings.Diff.WordWrap,
            ShowWhitespace = settings.Diff.ShowWhitespace,
        };

        RestoreCommand = new AsyncRelayCommand(RestoreAsync, () => SelectedItem is not null, context: "控えをミラーへ戻す");
        DeleteCommand = new AsyncRelayCommand(DeleteAsync, () => SelectedItem is not null, context: "控えの削除");
        OpenFolderCommand = new RelayCommand(OpenFolder);
        PushNowCommand = new AsyncRelayCommand(PushNowAsync, () => HasRestored, context: "戻したモジュールの Excel への反映");
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>プロジェクトの表示名。</summary>
    public string ProjectName => _project.Name;

    /// <summary>見出し。</summary>
    public string HeadingText => $"Excel 側の控え（{_project.Name}）";

    /// <summary>見出しの補足。</summary>
    public string GuideText =>
        "「Excel へ反映」で上書きされる前の Excel 側の本文が、毎回ここに残ります。「ミラーへ戻す」は、選んだ控えの内容を" +
        "Graft のファイル（ミラー）へ書きます。Excel にはまだ書かず、履歴にも記録されません。";

    /// <summary>差分の見方（ミラーにファイルが無い・控えと同じ、のときは、その旨を添える）。</summary>
    public string DiffCaptionText
    {
        get => _diffCaptionText;
        private set => SetProperty(ref _diffCaptionText, value);
    }

    /// <summary>控えの一覧（モジュールごとにまとめ、モジュール内は新しい順）。</summary>
    public ObservableCollection<VbaRescueRowViewModel> Items { get; } = new();

    /// <summary>選んでいる控えと現在のミラーの差分。</summary>
    public DiffViewModel Diff { get; }

    /// <summary>控えが 1 つも無い。</summary>
    public bool IsEmpty => Items.Count == 0;

    /// <summary>一覧が空のときの案内。</summary>
    public string EmptyText => "控えはまだありません。「Excel へ反映」で Excel 側が上書きされるたびに、ここへ残ります。";

    public VbaRescueRowViewModel? SelectedItem
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            CommandRequery.Invalidate();
            _ = LoadDiffSafelyAsync();
        }
    }

    /// <summary>操作の結果の一文（「戻しました。…」など）。</summary>
    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (SetProperty(ref _statusText, value)) OnPropertyChanged(nameof(HasStatus));
        }
    }

    /// <summary>結果の一文があるか。</summary>
    public bool HasStatus => _statusText.Length > 0;

    /// <summary>警告（従来の控えだけで容量の上限を超えている、など）。</summary>
    public string WarningText
    {
        get => _warningText;
        private set
        {
            if (SetProperty(ref _warningText, value)) OnPropertyChanged(nameof(HasWarning));
        }
    }

    /// <summary>警告があるか。</summary>
    public bool HasWarning => _warningText.Length > 0;

    /// <summary>直前に戻したモジュールがあり、「今すぐ Excel へ反映」を出せるか。</summary>
    public bool HasRestored => _restoredRelativePath is not null;

    /// <summary>「ミラーへ戻す」。</summary>
    public ICommand RestoreCommand { get; }

    /// <summary>「控えを削除」。</summary>
    public ICommand DeleteCommand { get; }

    /// <summary>「保存先を開く」。</summary>
    public ICommand OpenFolderCommand { get; }

    /// <summary>「今すぐ Excel へ反映」（戻した直後だけ表示）。</summary>
    public ICommand PushNowCommand { get; }

    /// <summary>「閉じる」。</summary>
    public ICommand CloseCommand { get; }

    /// <summary>窓を閉じてよいことの通知。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>
    /// 選択肢つきの確認（<c>VbaChoiceWindow</c>）を出して、選ばれたボタンの識別子を返す。窓の側が配線する。
    /// 未設定のときは、既定のボタン（<see cref="VbaChoiceRequest.Default"/>）として進む。
    /// </summary>
    public Func<VbaChoiceRequest, Task<string>>? ChooseAsync { get; set; }

    /// <summary>
    /// 戻したモジュール 1 件を Excel へ反映する（プロジェクトペインの手動の反映と同じ流れ。衝突ダイアログを含む）。
    /// 引数はミラーの相対パス。未設定なら「今すぐ Excel へ反映」のボタンは出さない。
    /// </summary>
    public Func<string, Task>? PushModuleAsync { get; set; }

    /// <summary>適用または Excel への書き込みの最中か。この間は、ミラーを書き換える「戻す」を受け付けない。</summary>
    public Func<bool>? IsBusy { get; set; }

    /// <summary>控えのフォルダを開く。既定は OS のファイルマネージャ。テストで差し替える。</summary>
    public Action<string> RevealFolder { get; set; } = static path => PlatformServices.Current.FileManager.Reveal(path);

    /// <summary>「今すぐ Excel へ反映」のボタンを出すか（戻した直後で、反映の入口が配線されているとき）。</summary>
    public bool ShowPushNow => HasRestored && PushModuleAsync is not null;

    // ------------------------------------------------------------------
    // 読み込み
    // ------------------------------------------------------------------

    /// <summary>
    /// 窓を開いたとき: 掃除してから一覧を読む（仕様書 7.3-5。起動時や定期の掃除はしない）。掃除したものがあれば伝える。
    /// </summary>
    public async Task InitializeAsync()
    {
        var pruned = await ReloadAsync(prune: true, select: null).ConfigureAwait(true);
        if (pruned > 0) StatusText = $"古い控えを {pruned} 件、自動で整理しました。";
    }

    private async Task<int> ReloadAsync(bool prune, string? select)
    {
        var listing = await _service.ListAsync(_project, prune).ConfigureAwait(true);
        var previous = select ?? SelectedItem?.Item.FileName;

        Items.Clear();
        string? lastModule = null;
        foreach (var item in listing.Items.OrderBy(i => i.Module, StringComparer.OrdinalIgnoreCase)
                     .ThenByDescending(i => i.Name.At).ThenByDescending(i => i.Name.Sequence)
                     .ThenByDescending(i => i.FileName, StringComparer.Ordinal))
        {
            Items.Add(new VbaRescueRowViewModel(item, !string.Equals(lastModule, item.Module, StringComparison.OrdinalIgnoreCase)));
            lastModule = item.Module;
        }

        OnPropertyChanged(nameof(IsEmpty));
        WarningText = listing.OverLimit
            ? $"控えの合計が {VbaRescueRowViewModel.FormatSize(listing.TotalBytes)} で、設定の上限を超えています。" +
              "従来の控え（衝突で上書きする前など）は自動では消さないため、不要なものは「控えを削除」で消してください。"
            : string.Empty;

        // 選択を保つ（無ければ先頭）。選択が変わらなくても、差分は読み直す。
        var target = Items.FirstOrDefault(i => i.Item.FileName == previous) ?? Items.FirstOrDefault();
        if (ReferenceEquals(target, _selected))
        {
            await LoadDiffSafelyAsync().ConfigureAwait(true);
        }
        else
        {
            SelectedItem = target;
            await LoadDiffSafelyAsync().ConfigureAwait(true);
        }

        CommandRequery.Invalidate();
        return listing.Pruned;
    }

    /// <summary>差分を読み込む（途中で選択が変わったら、古い結果は捨てる）。</summary>
    public async Task LoadDiffAsync()
    {
        var version = Interlocked.Increment(ref _loadVersion);
        var row = _selected;
        if (row is null)
        {
            Diff.Clear();
            return;
        }

        var rescue = await _service.ReadTextAsync(row.Item).ConfigureAwait(true);
        var (relative, mirror) = await _service.ReadMirrorTextAsync(_project, row.Item.Module).ConfigureAwait(true);
        if (version != _loadVersion || !ReferenceEquals(row, _selected)) return;

        var path = relative ?? row.Item.Module;
        var caption = mirror is null
            ? "左: 控え / 右: 現在のミラー（ミラーにファイルがありません。戻すと新しく作られます）"
            : VbaTextNormalizer.Hash(mirror) == VbaTextNormalizer.Hash(rescue)
                ? "左: 控え / 右: 現在のミラー（ミラーは、この控えと同じ内容です）"
                : "左: 控え / 右: 現在のミラー";
        DiffCaptionText = caption;
        Diff.Load(VbaConflictDiff.BuildPlan(path, rescue, mirror ?? string.Empty, caption, _contextLines));
    }

    private async Task LoadDiffSafelyAsync()
    {
        try
        {
            await LoadDiffAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diff.Clear();
            StatusText = $"控えを読めませんでした: {ExceptionMessages.Describe(ex)}";
        }
    }

    // ------------------------------------------------------------------
    // 操作
    // ------------------------------------------------------------------

    /// <summary>
    /// 「ミラーへ戻す」。確認のあと <see cref="VbaRescueService.RestoreAsync"/>。ミラーを書き換えるので、適用または
    /// Excel への書き込みの最中は受け付けない。
    /// </summary>
    public async Task RestoreAsync()
    {
        if (SelectedItem is not { } row) return;
        if (IsBusy?.Invoke() == true)
        {
            StatusText = "適用または Excel への反映の最中のため、終わってからやり直してください。";
            return;
        }

        var item = row.Item;
        var choice = await ChooseOptionAsync(new VbaChoiceRequest(
            "ミラーへ戻しますか",
            $"「{item.Module}」を、{row.TimeText} の控え（{item.KindLabel}）の内容に戻します。" + Environment.NewLine + Environment.NewLine +
            "Graft のファイル（ミラー）が控えの内容に置き換わります。いまのミラーの内容は「戻す前のミラー」として控えに残ります。" +
            "Excel にはまだ書かず、履歴にも記録されません。",
            new[]
            {
                new VbaChoiceOption("restore", "ミラーへ戻す", IsDefault: true),
                new VbaChoiceOption("cancel", "やめる"),
            })).ConfigureAwait(true);
        if (choice != "restore") return;

        var outcome = await _service.RestoreAsync(_project, item).ConfigureAwait(true);
        switch (outcome.Status)
        {
            case VbaRescueRestoreStatus.Restored:
                _restoredRelativePath = outcome.RelativePath;
                OnPropertyChanged(nameof(HasRestored));
                OnPropertyChanged(nameof(ShowPushNow));
                StatusText = outcome.Message;
                break;
            case VbaRescueRestoreStatus.Failed:
                StatusText = outcome.Issues.Count > 0
                    ? $"{outcome.Message}{Environment.NewLine}{string.Join(Environment.NewLine, outcome.Issues.Select(i => $"{i.ToDisplayText()} 対処: {i.Remedy}"))}"
                    : outcome.Message;
                break;
            default:
                StatusText = outcome.Message;
                break;
        }

        await ReloadAsync(prune: false, select: item.FileName).ConfigureAwait(true);
        CommandRequery.Invalidate();
    }

    /// <summary>「控えを削除」。控えを消すだけ（ごみ箱には送らない）。</summary>
    public async Task DeleteAsync()
    {
        if (SelectedItem is not { } row) return;

        var choice = await ChooseOptionAsync(new VbaChoiceRequest(
            "控えを削除しますか",
            $"「{row.ModuleName}」の {row.TimeText} の控え（{row.KindText}）を削除します。元には戻せません。",
            new[]
            {
                new VbaChoiceOption("delete", "控えを削除"),
                new VbaChoiceOption("cancel", "やめる", IsDefault: true),
            })).ConfigureAwait(true);
        if (choice != "delete") return;

        var (deleted, failure) = _service.Delete(row.Item);
        StatusText = deleted ? "控えを削除しました。" : $"控えを削除できませんでした: {failure}";
        await ReloadAsync(prune: false, select: null).ConfigureAwait(true);
    }

    private void OpenFolder()
    {
        var directory = _service.DirectoryOf(_project);
        try
        {
            Directory.CreateDirectory(directory);
            RevealFolder(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"控えのフォルダを開けませんでした: {ExceptionMessages.Describe(ex)}";
        }
    }

    private async Task PushNowAsync()
    {
        if (_restoredRelativePath is not { } relative || PushModuleAsync is null) return;
        await PushModuleAsync(relative).ConfigureAwait(true);
        _restoredRelativePath = null;
        OnPropertyChanged(nameof(HasRestored));
        OnPropertyChanged(nameof(ShowPushNow));
        CommandRequery.Invalidate();
        await ReloadAsync(prune: false, select: null).ConfigureAwait(true);
    }

    private async Task<string> ChooseOptionAsync(VbaChoiceRequest request)
    {
        if (ChooseAsync is null) return request.Default.Id;
        var id = await ChooseAsync(request).ConfigureAwait(true);
        return request.Options.Any(o => o.Id == id) ? id : request.Default.Id;
    }
}
