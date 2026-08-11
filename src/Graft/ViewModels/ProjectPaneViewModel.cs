using System.Collections.ObjectModel;
using System.Windows.Input;
using Graft.Core;
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

    /// <summary>色のみに依存しないための読み上げ用テキスト（8.14）。</summary>
    public string AutomationName
    {
        get
        {
            var parts = new List<string> { DisplayName };
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
        DeleteProjectCommand = new AsyncRelayCommand(DeleteSelectedProjectAsync, () => SelectedItem is not null, context: "プロジェクトの削除");
        TogglePinCommand = new AsyncRelayCommand(ToggleSelectedPinAsync, () => SelectedItem is not null, context: "ピン留めの切り替え");
        RenameProjectCommand = new AsyncRelayCommand(RenameSelectedProjectAsync, () => SelectedItem is not null, context: "プロジェクト名の変更");
        EditTagsCommand = new AsyncRelayCommand(EditSelectedTagsAsync, () => SelectedItem is not null, context: "タグの編集");
        RelocateProjectCommand = new AsyncRelayCommand(RelocateSelectedProjectAsync, () => SelectedItem is not null, context: "プロジェクトの場所の変更");
    }

    /// <summary>コマンドバー・空状態から呼ぶ「フォルダ選択で登録」（仕様書3.2）。</summary>
    public ICommand AddProjectCommand { get; }

    /// <summary>ヘッダーの削除ボタン・右クリックメニューの両方から呼ぶプロジェクトの削除。</summary>
    public ICommand DeleteProjectCommand { get; }

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
            if (SetProperty(ref _selectedItem, value) && value is not null)
            {
                ProjectSelected?.Invoke(this, value.Project);
            }
        }
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
        await _store.RemoveAsync(projectId, deleteHistory, ct).ConfigureAwait(true);
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
        else if (previouslySelectedId is not null)
        {
            // プロジェクトペイン改善: 削除等で選択中のプロジェクトが無くなり、他に選べる
            // プロジェクトも残っていない場合。何もしないと_selectedItemが削除済みの
            // プロジェクトを指したままになるため、明示的に未選択へ戻す（初回起動時のように
            // 一度も選択したことが無い場合はprevious・IdがnullなためこのelseIfへは来ない＝
            // 既存の起動直後の空一覧の挙動は変えない）。
            _selectedItem = null;
            OnPropertyChanged(nameof(SelectedItem));
            SelectionCleared?.Invoke(this, EventArgs.Empty);
        }
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
        var result = await _store.RemoveAsync(project.Id, deleteHistory: deleteHistory).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            await ShowFailureAsync("プロジェクトを削除できませんでした", result.Issues).ConfigureAwait(true);
            return;
        }
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
