using System.Collections.ObjectModel;
using Graft.Core;
using Graft.Core.PromptFocus;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;

namespace Graft.ViewModels;

/// <summary>
/// 設定の「依頼の観点」タブ（仕様書_ブックの情報と依頼の観点 6.6）。<see cref="PromptTemplateViewModel"/> と同じ作りで、
/// 一覧・追加・削除・保存を持ち、テンプレートの画面にはない「複製」を足している。
///
/// 【組み込みは編集も削除もできない】組み込みの観点（<see cref="PromptFocus.IsBuiltIn"/>）は、
/// 版を上げたときにコード側の最新の文へ差し替わる前提で <c>focus.json</c> に書かない。
/// 手を加えたいときは「複製」で自作の観点（<c>custom-&lt;Guid&gt;</c>）に写してから編集する。
/// 編集欄の入力が先に変わっても、保存（<see cref="SaveEditCommand"/>）を押すまで <c>focus.json</c> は変わらない。
/// </summary>
public sealed class PromptFocusSettingsViewModel : ObservableObject
{
    private readonly PromptFocusStore _store;
    private readonly IDialogService _dialogService;

    private FocusEntry? _selectedFocus;
    private string? _statusMessage;
    private GraftIssue? _errorIssue;

    public PromptFocusSettingsViewModel(AppPaths appPaths, IDialogService dialogService)
    {
        ArgumentNullException.ThrowIfNull(appPaths);
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _store = new PromptFocusStore(appPaths);

        Focuses = new ObservableCollection<FocusEntry>();
        AddCommand = new AsyncRelayCommand(AddAsync, context: "依頼の観点の追加");
        DuplicateCommand = new AsyncRelayCommand(DuplicateAsync, () => _selectedFocus is not null, context: "依頼の観点の複製");
        DeleteCommand = new AsyncRelayCommand(DeleteAsync, () => _selectedFocus is { IsBuiltIn: false }, context: "依頼の観点の削除");
        SaveEditCommand = new AsyncRelayCommand(SaveEditAsync, () => _selectedFocus is { IsBuiltIn: false }, context: "依頼の観点の保存");
    }

    /// <summary>観点の一覧（組み込み17件＋自作。自作は追加順で組み込みの後ろ）。</summary>
    public ObservableCollection<FocusEntry> Focuses { get; }

    /// <summary>見出しの選択肢（<see cref="PromptFocusGroups.All"/>）。</summary>
    public IReadOnlyList<string> GroupChoices => PromptFocusGroups.All;

    public AsyncRelayCommand AddCommand { get; }
    public AsyncRelayCommand DuplicateCommand { get; }
    public AsyncRelayCommand DeleteCommand { get; }
    public AsyncRelayCommand SaveEditCommand { get; }

    public FocusEntry? SelectedFocus
    {
        get => _selectedFocus;
        set => SetProperty(ref _selectedFocus, value, RaiseCommandStates);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>読み込み・保存失敗時のエラー（コード＋対処方法）。</summary>
    public GraftIssue? ErrorIssue
    {
        get => _errorIssue;
        private set => SetProperty(ref _errorIssue, value);
    }

    /// <summary>設定画面の表示時に一度呼ぶ。観点の一覧を読み込む。</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var loaded = await _store.LoadAsync(ct).ConfigureAwait(true);
        ErrorIssue = loaded.Errors.FirstOrDefault();
        Focuses.Clear();
        foreach (var focus in loaded.Value) Focuses.Add(new FocusEntry(focus));
        SelectedFocus = Focuses.FirstOrDefault();
    }

    private async Task AddAsync()
    {
        var name = await _dialogService.PromptAsync("依頼の観点の追加", "観点の名前を入力してください。").ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name)) return;

        name = name.Trim();
        var focus = new PromptFocus
        {
            Id = NewCustomId(),
            Group = PromptFocusGroups.Mine,
            Name = name,
            Body = PromptFocusStore.EnsureHeading(name, string.Empty),
        };
        var entry = new FocusEntry(focus);
        Focuses.Add(entry);
        await PersistCustomAsync().ConfigureAwait(true);
        SelectedFocus = entry;
        StatusMessage = "観点を追加しました。指示文を書いて「保存」を押してください。";
    }

    /// <summary>
    /// 選んでいる観点（組み込みでも自作でも）を、自作の観点として写す。写したものは「（複製）」を名前に足し、
    /// 見出し・ツールチップ・指示文・VBA追記・VBA専用をそのまま引き継ぐ。組み込みの指示文を土台に
    /// 自分の言い回しへ直したいときの入口（テンプレートの画面にはない操作）。
    /// </summary>
    private async Task DuplicateAsync()
    {
        if (_selectedFocus is null) return;
        var source = _selectedFocus;
        var name = source.Name + "（複製）";
        var focus = new PromptFocus
        {
            Id = NewCustomId(),
            Group = source.Group,
            Name = name,
            Tooltip = source.Tooltip,
            Body = source.Body,
            VbaAddendum = string.IsNullOrWhiteSpace(source.VbaAddendum) ? null : source.VbaAddendum,
            VbaOnly = source.VbaOnly,
            IsBuiltIn = false,
        };
        var entry = new FocusEntry(focus);
        Focuses.Add(entry);
        await PersistCustomAsync().ConfigureAwait(true);
        SelectedFocus = entry;
        StatusMessage = $"「{source.Name}」を複製しました。";
    }

    private async Task DeleteAsync()
    {
        if (_selectedFocus is not { IsBuiltIn: false } entry) return;
        var confirmed = await _dialogService
            .ConfirmAsync("依頼の観点の削除", $"観点「{entry.Name}」を削除します。よろしいですか？")
            .ConfigureAwait(true);
        if (!confirmed) return;

        Focuses.Remove(entry);
        await PersistCustomAsync().ConfigureAwait(true);
        SelectedFocus = Focuses.FirstOrDefault();
        StatusMessage = "観点を削除しました。";
    }

    private async Task SaveEditAsync()
    {
        if (_selectedFocus is not { IsBuiltIn: false } entry) return;

        var name = entry.Name.Trim();
        if (name.Length == 0)
        {
            StatusMessage = "観点の名前を入力してください。";
            return;
        }

        // 「VBA」の見出しを選べるのは、VBA専用の観点だけ（仕様書6.6）。VBAでないプロジェクトにも出る観点が
        // 「VBA」の見出しに並ぶと、見出しの意味（VBA プロジェクトのときだけ出す）と食い違うため。
        if (entry.Group == PromptFocusGroups.Vba && !entry.VbaOnly)
        {
            StatusMessage = "見出し「VBA」は、VBA 専用の観点でだけ選べます。「VBA プロジェクトのときだけ出す」にチェックを入れてください。";
            return;
        }

        entry.UpdateSource(entry.Source with
        {
            Name = name,
            Group = entry.Group,
            Tooltip = entry.Tooltip.Trim(),
            // 先頭が「## 」でなければ「## 名前」の行を補う。依頼文の中で観点ごとの見出しがそろうようにするため。
            Body = PromptFocusStore.EnsureHeading(name, entry.Body),
            VbaAddendum = string.IsNullOrWhiteSpace(entry.VbaAddendum) ? null : entry.VbaAddendum.TrimEnd(),
            VbaOnly = entry.VbaOnly,
        });
        await PersistCustomAsync().ConfigureAwait(true);
        StatusMessage = "観点を保存しました。";
    }

    private async Task PersistCustomAsync()
    {
        var all = Focuses.Select(e => e.Source).ToList();
        await _store.SaveAsync(all).ConfigureAwait(true);
    }

    private static string NewCustomId() => $"custom-{Guid.NewGuid():N}";

    private void RaiseCommandStates()
    {
        DuplicateCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
        SaveEditCommand.RaiseCanExecuteChanged();
    }
}

/// <summary>観点1件分の編集用ラッパー。保存を押すまで <see cref="Source"/>（保存される値）は変わらない。</summary>
public sealed class FocusEntry : ObservableObject
{
    private string _name;
    private string _group;
    private string _tooltip;
    private string _body;
    private string _vbaAddendum;
    private bool _vbaOnly;

    public FocusEntry(PromptFocus source)
    {
        Source = source;
        _name = source.Name;
        _group = source.Group;
        _tooltip = source.Tooltip;
        _body = source.Body;
        _vbaAddendum = source.VbaAddendum ?? string.Empty;
        _vbaOnly = source.VbaOnly;
    }

    public PromptFocus Source { get; private set; }

    public string Id => Source.Id;

    public bool IsBuiltIn => Source.IsBuiltIn;

    /// <summary>編集できるか（組み込み以外）。編集欄の読み取り専用の切り替えに使う。</summary>
    public bool IsEditable => !Source.IsBuiltIn;

    public string Name { get => _name; set => SetProperty(ref _name, value); }

    public string Group { get => _group; set => SetProperty(ref _group, value); }

    public string Tooltip { get => _tooltip; set => SetProperty(ref _tooltip, value); }

    public string Body
    {
        get => _body;
        set => SetProperty(ref _body, value, () => OnPropertyChanged(nameof(HasVariableWarning)));
    }

    public string VbaAddendum
    {
        get => _vbaAddendum;
        set => SetProperty(ref _vbaAddendum, value, () => OnPropertyChanged(nameof(HasVariableWarning)));
    }

    public bool VbaOnly { get => _vbaOnly; set => SetProperty(ref _vbaOnly, value); }

    /// <summary>
    /// 指示文に <c>{{</c> が含まれているか。観点の文では変数（<c>{{files}}</c> など）は展開されないので、
    /// 書いても意図どおりにならないことを編集欄の近くで知らせる（仕様書6.6。展開しない理由は
    /// <c>PromptTemplateRenderer</c> の再展開の防止）。
    /// </summary>
    public bool HasVariableWarning
        => _body.Contains("{{", StringComparison.Ordinal) || _vbaAddendum.Contains("{{", StringComparison.Ordinal);

    /// <summary>一覧の右に添える見出し名（「自分用」など）。</summary>
    public string GroupLabel => Source.IsBuiltIn ? $"{Source.Group}・組み込み" : Source.Group;

    public void UpdateSource(PromptFocus updated)
    {
        Source = updated;
        _name = updated.Name;
        _group = updated.Group;
        _tooltip = updated.Tooltip;
        _body = updated.Body;
        _vbaAddendum = updated.VbaAddendum ?? string.Empty;
        _vbaOnly = updated.VbaOnly;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Group));
        OnPropertyChanged(nameof(Tooltip));
        OnPropertyChanged(nameof(Body));
        OnPropertyChanged(nameof(VbaAddendum));
        OnPropertyChanged(nameof(VbaOnly));
        OnPropertyChanged(nameof(GroupLabel));
        OnPropertyChanged(nameof(HasVariableWarning));
    }
}
