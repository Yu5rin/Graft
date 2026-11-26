using System.Collections.ObjectModel;
using System.Windows.Input;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Features;
using Graft.Platform;

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
public sealed class ProjectListItemViewModel : ObservableObject
{
    private VbaBadgeState _vbaBadge = VbaBadgeState.Hidden;

    public ProjectListItemViewModel(Project project, int? shortcutNumber)
    {
        Project = project ?? throw new ArgumentNullException(nameof(project));
        ShortcutNumber = shortcutNumber;
    }

    /// <summary>元のプロジェクト定義。</summary>
    public Project Project { get; }

    /// <summary>上位9件に割り当てる数字キーショートカット。それ以外は null。</summary>
    public int? ShortcutNumber { get; }

    /// <summary>
    /// 表示用に正規化した名前（不具合2対応）。空・改行混じりの異常な名前でも一覧・
    /// ドロップダウンの見た目が崩れないよう <see cref="Project.DisplayName"/> をそのまま使う。
    /// </summary>
    public string DisplayName => Project.DisplayName;

    public bool IsPinned => Project.Pinned;

    /// <summary>未接続プロジェクトはグレー表示にする（仕様書3.2）。表示側はこの値でスタイルを切り替える。</summary>
    public bool IsDisconnected => Project.IsDisconnected;

    public string TagsText => Project.Tags.Count == 0 ? string.Empty : string.Join(" / ", Project.Tags);

    public string ShortcutText => ShortcutNumber is int n ? n.ToString() : string.Empty;

    /// <summary>
    /// 「VBA」バッジの内容（仕様書_VBA連携 7.6）。VBA プロジェクトでない行、またはこの環境で機能が使えないときは
    /// <see cref="VbaBadgeState.Hidden"/>。ブックの開閉と件数は非同期に調べて後から入るので、変更通知を持つ。
    /// </summary>
    public VbaBadgeState VbaBadge
    {
        get => _vbaBadge;
        set
        {
            if (!SetProperty(ref _vbaBadge, value ?? VbaBadgeState.Hidden)) return;
            OnPropertyChanged(nameof(IsVbaBadgeVisible));
            OnPropertyChanged(nameof(VbaBadgeText));
            OnPropertyChanged(nameof(IsVbaBadgeGray));
            OnPropertyChanged(nameof(VbaStatusText));
            OnPropertyChanged(nameof(HasVbaStatus));
            OnPropertyChanged(nameof(VbaTooltip));
            OnPropertyChanged(nameof(AutomationName));
        }
    }

    /// <summary>バッジを出すか。</summary>
    public bool IsVbaBadgeVisible => _vbaBadge.IsVisible;

    /// <summary>バッジの文言（「VBA」「VBA 連携オフ」）。</summary>
    public string VbaBadgeText => _vbaBadge.Text;

    /// <summary>グレー表示か（ブックが開いていない、または機能がオフ）。</summary>
    public bool IsVbaBadgeGray => _vbaBadge.IsGray;

    /// <summary>名前の下に出す補足（「未反映 N・削除保留 N」）。</summary>
    public string VbaStatusText => _vbaBadge.StatusText;

    /// <summary>補足を出すか。</summary>
    public bool HasVbaStatus => _vbaBadge.StatusText.Length > 0;

    /// <summary>バッジのツールチップ。</summary>
    public string VbaTooltip => _vbaBadge.Tooltip;

    /// <summary>色のみに依存しないための読み上げ用テキスト（8.14）。</summary>
    public string AutomationName
    {
        get
        {
            var parts = new List<string> { DisplayName };
            if (IsPinned) parts.Add("ピン留め");
            if (IsDisconnected) parts.Add("未接続");
            if (_vbaBadge.IsVisible)
            {
                parts.Add(_vbaBadge.Text);
                if (_vbaBadge.IsGray && _vbaBadge.Text == "VBA") parts.Add(VbaBadgeLogic.WorkbookClosedTip);
                if (_vbaBadge.StatusText.Length > 0) parts.Add(_vbaBadge.StatusText);
            }
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
public sealed partial class ProjectPaneViewModel : ObservableObject
{
    private readonly ProjectStore _store;
    private readonly IDialogService _dialogs;
    private ProjectPaneState _state = ProjectPaneState.Loading;
    private GraftIssue? _error;
    private ProjectListItemViewModel? _selectedItem;

    public ProjectPaneViewModel(ProjectStore store, IDialogService dialogs)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        AddProjectCommand = new AsyncRelayCommand(AddProjectViaDialogAsync, context: "プロジェクトの追加");

        // プロジェクトペイン改善: 削除・ピン留め・表示名変更・タグ編集・場所の変更。
        // すべて「右クリックメニューの1項目としても、ボタンとしても呼べる」ようにするため、
        // HistoryPaneのMenuItem群と同じ設計（CommandParameterを使わずSelectedItemを直接見る）に
        // 揃える。右クリックで選択行を切り替える処理はView側（ProjectPane.axaml.cs）が担う。
        DeleteProjectCommand = new AsyncRelayCommand(DeleteSelectedProjectAsync, () => SelectedItem is not null && !IsSelectedVbaProjectBusy(), context: "プロジェクトの削除");
        ClearHistoryCommand = new AsyncRelayCommand(ClearSelectedHistoryAsync, () => SelectedItem is not null && ClearHistoryHandler is not null, context: "履歴の削除");
        TogglePinCommand = new AsyncRelayCommand(ToggleSelectedPinAsync, () => SelectedItem is not null, context: "ピン留めの切り替え");
        RenameProjectCommand = new AsyncRelayCommand(RenameSelectedProjectAsync, () => SelectedItem is not null, context: "プロジェクト名の変更");
        EditTagsCommand = new AsyncRelayCommand(EditSelectedTagsAsync, () => SelectedItem is not null, context: "タグの編集");
        RelocateProjectCommand = new AsyncRelayCommand(RelocateSelectedProjectAsync, () => SelectedItem is not null && !IsSelectedVbaProjectBusy(), context: "プロジェクトの場所の変更");
        InitializeVbaCommands(); // Excel VBA 連携（ProjectPaneViewModel.Vba.cs）。

        // 右クリックメニューの「パスをコピー」「エクスプローラーで表示」。クリップボードと通知の窓口は
        // 履歴ペイン（HistoryPaneViewModel.ClipboardWriter／Notify）と同じ作りで MainViewModel が渡す。
        // コピーは場所が見つからないプロジェクトでも有効（パスの文字列は残っているので、探す手がかりに
        // なる）。表示は、フォルダの実体が無いと開けないため無効にする（履歴の「バックアップフォルダを開く」と同じ）。
        CopyPathCommand = new RelayCommand(
            CopySelectedPath,
            () => SelectedItem is not null && ClipboardWriter is not null);
        RevealCommand = new RelayCommand(
            () => { if (SelectedItem is { } item) PlatformServices.Current.FileManager.Reveal(item.Project.Root); },
            () => SelectedItem is { } item && !item.IsDisconnected && Directory.Exists(item.Project.Root));
    }

    /// <summary>クリップボードへ書く窓口（MainViewModel が IUiServices のクリップボードを渡す）。</summary>
    public Action<string>? ClipboardWriter { get; set; }

    /// <summary>数秒で消える通知を出す窓口（MainViewModel が StatusNoticeViewModel を渡す）。コピーしたことを伝える。</summary>
    public Action<string>? Notify { get; set; }

    /// <summary>右クリックメニューの「パスをコピー」。選択中のプロジェクトのフォルダのパスをコピーする。</summary>
    public ICommand CopyPathCommand { get; }

    /// <summary>右クリックメニューの「エクスプローラーで表示／ファイルマネージャーで表示」。フォルダが無ければ無効。</summary>
    public ICommand RevealCommand { get; }

    private void CopySelectedPath()
    {
        if (SelectedItem is not { } item || ClipboardWriter is null) return;
        ClipboardWriter(item.Project.Root);
        Notify?.Invoke(CopyNoticeText.ProjectPathCopied);
    }

    /// <summary>コマンドバー・空状態から呼ぶ「フォルダ選択で登録」（仕様書3.2）。</summary>
    public ICommand AddProjectCommand { get; }

    /// <summary>ヘッダーの削除ボタン・右クリックメニューの両方から呼ぶプロジェクトの削除。</summary>
    public ICommand DeleteProjectCommand { get; }

    /// <summary>右クリックメニュー・コマンドパレットの「履歴だけを削除…」（プロジェクトの登録は残す）。</summary>
    public ICommand ClearHistoryCommand { get; }

    /// <summary>右クリックメニューの「ピン留めする／解除する」。</summary>
    public ICommand TogglePinCommand { get; }

    /// <summary>右クリックメニューの「表示名の変更」。</summary>
    public ICommand RenameProjectCommand { get; }

    /// <summary>右クリックメニューの「タグの編集」。</summary>
    public ICommand EditTagsCommand { get; }

    /// <summary>右クリックメニューの「場所を変更」（行方不明プロジェクトの再結び付け）。</summary>
    public ICommand RelocateProjectCommand { get; }

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
            var previous = _selectedItem;
            // Excel へ Push している最中は別のプロジェクトへ切り替えない（仕様書_VBA連携 7.3-4-2）。一覧の選択は
            // 双方向に束縛されているので、変更通知を投げ直して、見かけの選択も元へ戻す。
            if (IsSwitchBlocked?.Invoke() == true && previous is not null && value is not null && !ReferenceEquals(previous, value))
            {
                OnPropertyChanged(nameof(SelectedItem));
                return;
            }

            if (!SetProperty(ref _selectedItem, value)) return;
            RaiseVbaSelectionChanged(); // 右クリックメニューの VBA 項目の表示とチェック状態（ProjectPaneViewModel.Vba.cs）。
            if (value is null) return;

            // 切り替えの前に確認が要る（開いているタブの未保存の確認）場合は、確認が済んでから
            // ProjectSelectedを発火する。以前は選択が変わった時点でイベントを発火し、購読側
            // （ShellViewModel）が未保存の確認で「キャンセル」されても、結果を見ずに切り替えが
            // 進んでいた（C4）。最初の選択（previousがnull）には閉じるタブが無いので素通りする。
            if (SwitchGuardAsync is null || previous is null)
            {
                ProjectSelected?.Invoke(this, value.Project);
                return;
            }

            _pendingSwitch = RaiseSelectedAfterGuardAsync(previous, value);
        }
    }

    private Task _pendingSwitch = Task.CompletedTask;

    /// <summary>
    /// true の間はプロジェクトの切り替えを受け付けない（Excel VBA 連携の Push 中。<c>MainViewModel</c> が配線する）。
    /// 未設定なら常に切り替えられる。
    /// </summary>
    public Func<bool>? IsSwitchBlocked { get; set; }

    /// <summary>
    /// 切り替えの確認。別のプロジェクトへ切り替える直前に呼ばれ、falseを返すと切り替えを中止して
    /// 選択を元のプロジェクトへ戻す。ShellViewModelが、開いているタブの未保存の確認
    /// （保存／保存しない／キャンセル）を結ぶ。未設定ならこれまでどおり確認なしで切り替わる。
    /// </summary>
    public Func<Project, Task<bool>>? SwitchGuardAsync { get; set; }

    /// <summary>
    /// 選択を変え、切り替えの確認と<see cref="ProjectSelected"/>の発火が終わるまで待つ。
    /// 確認が即座に終わる通常の場合は、プロパティ代入と同じく同期的に完了する。
    /// 自動判定で別のプロジェクトへ切り替える経路（MainViewModel.ProjectMatch.cs）が、
    /// 切り替えの完了を待ってから続きを進めるために使う。
    /// </summary>
    public async Task SelectAsync(ProjectListItemViewModel item)
    {
        SelectedItem = item;
        await _pendingSwitch.ConfigureAwait(true);
    }

    private async Task RaiseSelectedAfterGuardAsync(ProjectListItemViewModel previous, ProjectListItemViewModel value)
    {
        var proceed = await SwitchGuardAsync!(value.Project).ConfigureAwait(true);
        if (!proceed)
        {
            // キャンセルされた。選択を元のプロジェクトへ戻し、切り替えは起こらなかったことにする
            // （ProjectSelectedは発火しない。解析結果の破棄などは切り替えが確定してから動く）。
            if (ReferenceEquals(_selectedItem, value))
            {
                if (Items.Contains(previous))
                {
                    _selectedItem = previous;
                    OnPropertyChanged(nameof(SelectedItem));
                    RaiseVbaSelectionChanged();
                    return;
                }

                // 【戻し先が一覧に無いときは戻さない】 元のプロジェクトが削除などで一覧から消えている（削除のあとの
                // 読み直しで選択が移るとき）。消えた項目へ選択を戻すと、一覧に無いプロジェクトが選択中のまま残る。
                // 戻さずに、新しい選択のまま切り替えを続ける（開いているタブの確認は、続く ProjectSelected 側の
                // CloseAllAsync でもう一度出る）。
            }
            else
            {
                return;
            }
        }

        // 確認の待ち中に、さらに別のプロジェクトへ選び直された場合は、新しい選択の処理に任せる。
        if (!ReferenceEquals(_selectedItem, value)) return;
        ProjectSelected?.Invoke(this, value.Project);
    }

    /// <summary>プロジェクトが選択された（切り替わった）ことの通知。</summary>
    public event EventHandler<Project>? ProjectSelected;

    /// <summary>
    /// プロジェクトペイン改善（利用者からの明示的な要望）: プロジェクト名のダブルクリック。
    /// 単クリック（<see cref="SelectedItem"/>経由の<see cref="ProjectSelected"/>）とは独立した
    /// 通知にし、単クリックの既存挙動（プロジェクト選択のみ）を変えないようにする。
    /// View（ProjectPane.axaml.cs）がダブルタップを検出して<see cref="NotifyActivated"/>を呼び、
    /// ShellViewModelがこれを購読してサイドビューをエクスプローラへ切り替える。
    /// </summary>
    public event EventHandler<Project>? ProjectActivated;

    /// <summary>
    /// プロジェクトペイン改善: 削除等で選択中のプロジェクトが無くなり、かつ他に選べる
    /// プロジェクトも残っていない（一覧が空になった）ことの通知。<see cref="SelectedItem"/>の
    /// setterはnullを渡してもProjectSelectedを発火しない仕様のため、Editor/Explorer等を
    /// 「プロジェクト未選択」の状態へ明示的に戻すための専用通知として別に持つ。
    /// </summary>
    public event EventHandler? SelectionCleared;

    /// <summary>View側のダブルタップ検出から呼ぶ。<see cref="ProjectActivated"/>参照。</summary>
    public void NotifyActivated(Project project) => ProjectActivated?.Invoke(this, project);

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
        var folder = await _dialogs.PickFolderAsync("プロジェクトフォルダを選択").ConfigureAwait(true);
        if (string.IsNullOrEmpty(folder))
        {
            return;
        }
        await RegisterFolderAsync(folder).ConfigureAwait(true);
    }

    /// <summary>
    /// 画面上のチュートリアル（コーチマーク。<c>Graft.Views.ShellWindow</c>のShellWindow.
    /// Tutorial.cs参照）専用: 確認ダイアログを介さず、指定した1件だけを一覧・履歴ごと削除する。
    /// <see cref="DeleteSelectedProjectAsync"/>と異なり<see cref="SelectedItem"/>に依存せず、
    /// 履歴を残すかどうかの3択ダイアログも出さない（チュートリアルが生成したサンプルは、
    /// 終了時に常に履歴も含めて後片付けする方針のため）。
    /// </summary>
    public async Task RemoveWithoutConfirmationAsync(string projectId, bool deleteHistory, CancellationToken ct = default)
    {
        var removed = await _store.RemoveAsync(projectId, deleteHistory, ct).ConfigureAwait(true);
        if (removed.IsSuccess) ProjectRemoved?.Invoke(this, projectId);
        await LoadAsync(ct).ConfigureAwait(true);
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
            // バッジは、前回調べた結果（ブックの開閉・件数）があればそれで先に描く。作り直しのたびに消えて
            // ちらつかないようにするため。件数の調べ直しは下の RequestVbaBadgeRefresh が非同期で行う。
            Items.Add(new ProjectListItemViewModel(project, shortcut <= 9 ? shortcut : null) { VbaBadge = BadgeFor(project) });
            shortcut++;
        }

        State = Items.Count == 0 ? ProjectPaneState.Empty : ProjectPaneState.Content;

        var restored = previouslySelectedId is null ? null : Items.FirstOrDefault(i => i.Project.Id == previouslySelectedId);
        if (restored is not null)
        {
            _selectedItem = restored;
            OnPropertyChanged(nameof(SelectedItem));
            RaiseVbaSelectionChanged();
        }
        else if (Items.Count > 0)
        {
            SelectedItem = Items[0];
        }
        else if (previouslySelectedId is not null)
        {
            // プロジェクトペイン改善: 削除等で選択中のプロジェクトが無くなり、他に選べる
            // プロジェクトも残っていない場合。何もしないと_selectedItemが削除済みの
            // プロジェクトを指したままになるため、明示的に未選択へ戻す（初回起動時のように
            // 一度も選択したことが無い場合はprevious・IdがnullなためこのelseIfへは来ない＝
            // 既存の起動直後の空一覧の挙動は変えない）。
            _selectedItem = null;
            OnPropertyChanged(nameof(SelectedItem));
            RaiseVbaSelectionChanged();
            SelectionCleared?.Invoke(this, EventArgs.Empty);
        }

        // VBA プロジェクトの未反映・削除保留の件数を、Excel に接続せずに数え直す（開閉は調べない）。
        RequestVbaBadgeRefresh(checkWorkbookOpen: false);
    }

    // ------------------------------------------------------------------
    // プロジェクトペイン改善: 削除・ピン留め・表示名変更・タグ編集・場所の変更。
    // いずれも「右クリックメニューが事前に選択行を切り替えてからコマンドを実行する」
    // （HistoryPane.axaml.csと同じ設計）ため、SelectedItemを直接見るだけでよい。
    // ------------------------------------------------------------------

    /// <summary>
    /// プロジェクトの削除（利用者からの明示的な要望）。削除するのは projects.json の
    /// エントリだけで、実際のプロジェクトフォルダには一切触れない（<see cref="ProjectStore.RemoveAsync"/>
    /// 参照）。バックアップ履歴（back/&lt;projectId&gt;/）を残すかどうかを3択ダイアログで選ばせる。
    /// </summary>
    private async Task DeleteSelectedProjectAsync()
    {
        var item = SelectedItem;
        if (item is null) return;
        var project = item.Project;
        if (RejectWhileVbaBusy()) return;

        // 不具合2点検（実機報告の横断チェック）: 以前は「履歴も削除する」（不可逆）をyesLabelに
        // 渡しており、AvaloniaDialogService.ConfirmThreeWayAsyncの既定ボタン（IsDefault、Enterで
        // 実行される）が破壊的な選択肢になってしまっていた。既定ボタンには非破壊的な「履歴は残す」を
        // 渡し、Enterキーの誤操作で履歴が復元不能にならないようにする（yesLabel/noLabelの意味が
        // 入れ替わるため、下のdeleteHistoryの導出も反転させている）。
        var choice = await _dialogs.ConfirmThreeWayAsync(
            "プロジェクトを削除",
            $"プロジェクト「{project.DisplayName}」をGraftの一覧から削除します。\n\n" +
            $"・削除されるのはGraftの登録情報だけです。フォルダそのもの（{project.Root}）や、中のファイルは一切削除されません。\n" +
            "・これまでの変更履歴（バックアップ）をどうするか選んでください。「履歴も削除する」を選ぶと、以後は復元できなくなります。「履歴は残す」を選んだ場合、同じフォルダを後でもう一度登録すると、履歴も自動的に復活します。",
            "履歴は残す",
            "履歴も削除する").ConfigureAwait(true);
        if (choice is null) return; // キャンセル

        var deleteHistory = !choice.Value; // 「履歴は残す」(choice==true)ならdeleteHistory=false。

        // Excel VBA 連携（仕様書_VBA連携 4.4）: VBA プロジェクトでは、履歴に加えてミラーと同期状態の扱いも確認する。
        // 既定のボタン（Enter）は非破壊的な「登録だけ外す」。Excel 側のモジュールには何もしない。
        var vbaOptions = VbaRemoveOptions.KeepAll;
        if (project.Vba is not null)
        {
            var inDataDirectory = _store.IsUnderMirrorsDirectory(project.Root);
            var vbaChoice = await _dialogs.ConfirmThreeWayAsync(
                "ミラーと同期状態の扱い",
                inDataDirectory
                    ? $"このプロジェクトのミラーフォルダ（{project.Root}）と同期状態は、Graft のデータ保存先にあります。\n\n" +
                      "・「登録だけ外す」を選ぶと、ミラーと同期状態は残ります。同じブックを後で追加し直すと、同じフォルダで三方比較のまま続けられます。\n" +
                      "・「ミラーと同期状態も削除する」を選ぶと、ミラーフォルダはごみ箱へ送られ、同期状態は削除されます。Graft にだけ残っている未反映の編集も、ごみ箱から戻さない限り失われます。\n" +
                      "Excel のブックの中身には、どちらも何もしません。"
                    : $"このプロジェクトのミラーフォルダ（{project.Root}）はあなたが選んだ場所にあるため、フォルダ自体は削除しません。\n\n" +
                      "・「同期状態は残す」を選ぶと、同期状態（Excel との差分を見分ける記録）が残ります。同じブックを追加し直すと続けられます。\n" +
                      "・「同期状態も削除する」を選ぶと、その記録が削除されます。\n" +
                      "Excel のブックの中身には、どちらも何もしません。",
                inDataDirectory ? "登録だけ外す" : "同期状態は残す",
                inDataDirectory ? "ミラーと同期状態も削除する" : "同期状態も削除する").ConfigureAwait(true);
            if (vbaChoice is null) return; // キャンセル。何も削除しない。
            if (vbaChoice == false) vbaOptions = new VbaRemoveOptions(DeleteSyncState: true, DeleteMirror: inDataDirectory);
        }

        // 【削除の前に、開いているタブを閉じる確認を済ませる】 削除してから一覧を読み直すと、選択が別のプロジェクトへ
        // 移るときに切り替えの確認（SwitchGuardAsync。開いているタブの未保存の確認）が走る。そこで「キャンセル」されると、
        // 選択が「戻し先」＝いま消したプロジェクトへ戻され、消えたはずの項目が選択中のまま残っていた
        // （RaiseSelectedAfterGuardAsync）。確認は削除より前に済ませ、キャンセルなら削除自体を中止する。
        // 確認の順は「削除してよいか」→「未保存を保存するか」。削除をやめた利用者に、先に保存の確認を出して
        // タブを閉じさせてしまわないため。
        if (SwitchGuardAsync is { } guard && !await guard(project).ConfigureAwait(true)) return;

        GraftResult<bool> result;
        try
        {
            result = await _store.RemoveAsync(project.Id, deleteHistory, vbaOptions, PlatformServices.Current.Trash).ConfigureAwait(true);
        }
        catch
        {
            // 登録の削除（projects.json の書き換え）は、履歴・ミラーの後始末より先に済んでいる。後始末で想定外の例外が出ても、
            // 一覧だけは実際の登録に合わせて読み直す（読み直さないと、登録は消えたのに一覧には残って見える）。例外は呼び出し元
            // （コマンド）の共通の知らせへ回す。
            await LoadAsync().ConfigureAwait(true);
            throw;
        }

        if (!result.IsSuccess)
        {
            await ShowFailureAsync("プロジェクトを削除できませんでした", result.Issues).ConfigureAwait(true);
            return;
        }

        // 消した Id の画面の記憶（タブ構成・ペイン幅）を捨てるよう知らせる。一覧の読み直しより前に出す
        // （読み直しで別のプロジェクトへ選択が移るとき、消した Id の状態を書き戻させないため）。
        ProjectRemoved?.Invoke(this, project.Id);

        if (result.Issues.Count > 0)
        {
            // 登録の削除自体は成功しつつ、履歴フォルダの削除だけ失敗した場合（権限等）。
            await _dialogs.ShowMessageAsync(
                "履歴フォルダの後始末で問題が発生しました",
                string.Join(Environment.NewLine, result.Issues.Select(i => i.ToDisplayText()))).ConfigureAwait(true);
        }

        await LoadAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// プロジェクトを一覧から削除した（登録を外した）ことの通知。引数は消した Id。
    /// <c>ShellViewModel</c> が購読し、その Id のタブ構成・ペイン幅（layout.json の projectPaneWidths）を捨てる。
    /// 捨てないと、同じ場所を登録し直したときに（Id は Root から決まるので同じ Id になる）前のタブが開き直る。
    /// </summary>
    public event EventHandler<string>? ProjectRemoved;

    /// <summary>
    /// 選択中のプロジェクトの履歴だけを削除する右クリックメニューの「履歴だけを削除…」とコマンドパレットの入口。
    /// 実処理は適用・復元の状態を知っている <c>MainViewModel</c>（MainViewModel.ClearHistory.cs）が持ち、
    /// ここでは選択中の1件を渡すだけにする。未設定のあいだ（単体のテストなど）はコマンドを無効にする。
    /// </summary>
    public Func<Project, Task>? ClearHistoryHandler { get; set; }

    private Task ClearSelectedHistoryAsync()
        => SelectedItem is { } item && ClearHistoryHandler is { } handler ? handler(item.Project) : Task.CompletedTask;

    /// <summary>
    /// ピン留めの切替（仕様書3.2の並べ替え・上位9件ショートカットに連動する）。要望対応:
    /// オンにした瞬間の時刻を<see cref="Project.PinnedAt"/>へ記録し（<see cref="ProjectStore.Sort"/>
    /// がピン留め済み同士を「ピン留めした順」に並べるための基準になる）、オフにするとnullへ戻す
    /// （再度ピン留めすると新しい日時が入り、ピン留め済みグループの最後尾に来る）。
    /// </summary>
    private async Task ToggleSelectedPinAsync()
    {
        var item = SelectedItem;
        if (item is null) return;

        var result = await _store.UpdateAsync(item.Project.Id, p => p.Pinned
            ? p with { Pinned = false, PinnedAt = null }
            : p with { Pinned = true, PinnedAt = DateTimeOffset.Now }).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            await ShowFailureAsync("ピン留めを切り替えられませんでした", result.Issues).ConfigureAwait(true);
            return;
        }
        await LoadAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// 表示名の変更。空文字にした場合は<see cref="Project.DisplayName"/>の既定
    /// （<see cref="ProjectNameFormatter.Normalize"/>によるフォルダ名由来の名前）へ自動的に戻る
    /// （Nameを空のまま保存すればNormalizeが表示のたびにフォルダ名へ差し替えるため、
    /// ここで特別扱いする必要はない）。
    /// </summary>
    private async Task RenameSelectedProjectAsync()
    {
        var item = SelectedItem;
        if (item is null) return;
        var project = item.Project;

        var input = await _dialogs.PromptAsync(
            "表示名の変更",
            "新しい表示名を入力してください。空のままOKを押すと、フォルダ名から自動生成される既定の名前に戻ります。",
            project.Name).ConfigureAwait(true);
        if (input is null) return; // キャンセル

        var result = await _store.UpdateAsync(project.Id, p => p with { Name = input.Trim() }).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            await ShowFailureAsync("表示名を変更できませんでした", result.Issues).ConfigureAwait(true);
            return;
        }
        await LoadAsync().ConfigureAwait(true);
    }

    /// <summary>タグの編集。カンマ区切りの文字列で受け取り、前後の空白を落として空要素を除く。</summary>
    private async Task EditSelectedTagsAsync()
    {
        var item = SelectedItem;
        if (item is null) return;
        var project = item.Project;

        var current = string.Join(", ", project.Tags);
        var input = await _dialogs.PromptAsync(
            "タグの編集",
            "カンマ区切りでタグを入力してください（例: web, backend）。空のままOKを押すとタグなしになります。",
            current).ConfigureAwait(true);
        if (input is null) return; // キャンセル

        var tags = input
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
        var result = await _store.UpdateAsync(project.Id, p => p with { Tags = tags }).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            await ShowFailureAsync("タグを更新できませんでした", result.Issues).ConfigureAwait(true);
            return;
        }
        await LoadAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// 行方不明（未接続）プロジェクトの場所を再指定する。<see cref="ProjectStore.RelocateAsync"/>が
    /// Idの再計算・履歴フォルダの移動まで面倒を見るため、ここではフォルダを選ばせて渡すだけ。
    /// </summary>
    private async Task RelocateSelectedProjectAsync()
    {
        var item = SelectedItem;
        if (item is null) return;
        var project = item.Project;
        if (RejectWhileVbaBusy()) return;

        var folder = await _dialogs.PickFolderAsync("プロジェクトの新しい場所を選択").ConfigureAwait(true);
        if (string.IsNullOrEmpty(folder)) return;

        var result = await _store.RelocateAsync(project.Id, folder).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            await ShowFailureAsync("場所を変更できませんでした", result.Issues).ConfigureAwait(true);
            return;
        }
        if (result.Issues.Count > 0)
        {
            await _dialogs.ShowMessageAsync(
                "履歴フォルダの移動で問題が発生しました",
                string.Join(Environment.NewLine, result.Issues.Select(i => i.ToDisplayText()))).ConfigureAwait(true);
        }

        // RelocateAsyncはRootの変更に伴いIdも変わりうるため、RegisterFolderAsyncと同様に
        // 再読み込み後に新しいIdで選択し直す（ApplyItemsの選択維持ロジックは旧Idでしか
        // 探せないため、ここで明示的に選び直さないと別の項目が選ばれてしまう）。
        await LoadAsync().ConfigureAwait(true);
        SelectedItem = Items.FirstOrDefault(i => i.Project.Id == result.Value.Id);
    }

    private Task ShowFailureAsync(string title, IEnumerable<GraftIssue> issues)
        => _dialogs.ShowMessageAsync(title, string.Join(Environment.NewLine, issues.Select(i => i.ToDisplayText())));
}
