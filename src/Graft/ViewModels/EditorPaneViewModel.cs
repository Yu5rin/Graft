using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows.Input;
using Graft.Core;
using Graft.Editor;
using Graft.Infra;
using Graft.Platform;

namespace Graft.ViewModels;

/// <summary>
/// エディタ領域全体（4章）のViewModel。ドキュメントタブの管理は<see cref="EditorTabManager"/>へ
/// 委譲し、本クラスはこれに差分タブ（9.2・4.8）を合成した一覧・アクティブタブ・フォントサイズ・
/// ステータスバー表示（9.2）の窓口を担う。差分タブは<see cref="EditorTabManager"/>が持つ
/// 保存確認等のドキュメント前提のロジックへ一切渡さず、本クラスが直接開閉する
/// （<see cref="EditorTabViewModel.Session"/>は差分タブでは利用できないため）。
/// 15章 editor設定は<see cref="Settings.Editor"/>（<see cref="EditorSettings"/>）から読み取る。
/// タブ右クリックメニュー用コマンドは<c>EditorPaneViewModel.TabActions.cs</c>で定義する。
/// </summary>
public sealed partial class EditorPaneViewModel : ObservableObject
{
    // 機能改善（Ctrl+マウスホイールでの文字サイズ変更）: ホイール操作でのクランプ範囲は8〜32。
    // settings.json自体（SettingsStoreのeditor.fontSize検証）は6〜72とより広い範囲を許容するが
    // （設定画面のJSON直接編集タブ等からの手動設定を尊重するため）、ホイール操作という
    // 連続入力についてはDiffViewModel.CodeFontSizeと同じ範囲に揃え、極端な値へ暴走しないようにする。
    private const double MinFontSize = 8;
    private const double MaxFontSize = 32;

    private readonly EditorTabManager _manager;
    // 機能改善: UpdateSettings経由で設定画面・他のCtrl+マウスホイール操作からのフォントサイズ
    // 変更を反映できるよう、DiffViewModelと同じくreadonlyにしない（課題1のコメント参照）。
    private Settings _settings;
    private readonly ObservableCollection<EditorTabViewModel> _tabs = new();
    private EditorTabViewModel? _diffTab;
    // 修正1: 履歴差分タブ（1個だけ使い回す。ShowHistoryDiffTab/CloseHistoryDiffTab参照）。
    private EditorTabViewModel? _historyDiffTab;
    // 不具合3対応: 差分タブへ切り替える直前にアクティブだったタブ。差分タブを閉じたときに
    // 「元のファイルのタブが開いていなければ直前のタブへ戻る」ためのフォールバック先として使う
    // （ResolveReturnTab参照）。
    private EditorTabViewModel? _tabBeforeDiff;
    private EditorTabViewModel? _activeTab;
    private double _fontSize;
    private bool _wordWrap;
    private bool _showWhitespace;

    public EditorPaneViewModel(Settings settings, IDialogService dialogs, Graft.Platform.IUiServices ui)
    {
        Ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _manager = new EditorTabManager(dialogs);
        _manager.Tabs.CollectionChanged += OnManagerTabsChanged;

        _fontSize = Math.Clamp(_settings.Editor.FontSize, MinFontSize, MaxFontSize);
        _wordWrap = _settings.Editor.WordWrap;
        _showWhitespace = _settings.Editor.ShowWhitespace;

        ToggleWordWrapCommand = new RelayCommand(() => WordWrap = !WordWrap);
        ToggleShowWhitespaceCommand = new RelayCommand(() => ShowWhitespace = !ShowWhitespace);
        InitializeTabActionCommands(); // タブ見出し右クリックメニュー（TabActions.cs）。
    }

    /// <summary>開いているタブの一覧（ドキュメント＋差分タブ、9.2）。</summary>
    public ObservableCollection<EditorTabViewModel> Tabs => _tabs;

    /// <summary>現在アクティブなタブ。</summary>
    public EditorTabViewModel? ActiveTab
    {
        get => _activeTab;
        set
        {
            var previous = _activeTab;
            if (!SetProperty(ref _activeTab, value)) return;
            if (previous is not null) previous.PropertyChanged -= OnTabPropertyChanged;
            if (value is not null)
            {
                value.PropertyChanged += OnTabPropertyChanged;
                // 差分タブはCtrl+Tabの直近使用順管理の対象外（EditorTabManagerはドキュメント
                // タブ専用のため、差分タブの参照を内部状態へ持ち込まない）。
                if (value.Kind == EditorTabKind.Document) _manager.Touch(value);
            }

            RaiseStatusChanged();
        }
    }

    /// <summary>エディタのフォントサイズ（Ctrl+マウスホイールで変更、設定として永続化）。</summary>
    public double FontSize
    {
        get => _fontSize;
        set => SetProperty(ref _fontSize, Math.Clamp(value, MinFontSize, MaxFontSize));
    }

    /// <summary>
    /// 機能改善: View（EditorPane.axaml.cs）がCtrl+マウスホイールを検知したときに呼ぶ。
    /// <see cref="FontSize"/>を直接インクリメントする代わりにこのメソッドを経由させる理由は、
    /// 値の即時反映（ローカル表示）に加えて<see cref="FontSizeChangeCommitted"/>を発火し、
    /// 設定への永続化・差分表示側との同期をShellViewModel経由で行わせるため
    /// （<see cref="UpdateSettings"/>のコメント参照。設定側からの反映と自分自身が発火した
    /// 変更を区別する必要があるため、プロパティのsetter自体にイベント発火を持たせていない）。
    /// </summary>
    public void AdjustFontSize(double delta)
    {
        FontSize += delta;
        FontSizeChangeCommitted?.Invoke(this, FontSize);
    }

    /// <summary>
    /// 機能改善: Ctrl+マウスホイールでの変更をShellViewModelへ伝える通知。実際の設定への
    /// 永続化（デバウンス保存）と、差分表示側（DiffViewModel.CodeFontSize）との同期は
    /// ShellViewModelが仲介する（並行実装を避けるため、既存のSettingsViewModelのデバウンス保存を
    /// そのまま使う。ShellViewModel.EditorFontSizeChangeRequested参照）。
    /// </summary>
    public event EventHandler<double>? FontSizeChangeCommitted;

    /// <summary>折り返し表示。</summary>
    public bool WordWrap { get => _wordWrap; set => SetProperty(ref _wordWrap, value); }

    /// <summary>空白文字の可視化。</summary>
    public bool ShowWhitespace { get => _showWhitespace; set => SetProperty(ref _showWhitespace, value); }

    /// <summary>15章 editor設定の値。設定変更はアプリ再起動または設定画面側の反映に委ねる。</summary>
    public bool ShowLineNumbers => _settings.Editor.ShowLineNumbers;
    public bool HighlightCurrentLine => _settings.Editor.HighlightCurrentLine;
    public int TabSize => _settings.Editor.TabSize;
    public bool InsertSpaces => _settings.Editor.InsertSpaces;
    public bool DetectIndentEnabled => _settings.Editor.DetectIndent;
    public bool AutoClosingBrackets => _settings.Editor.AutoClosingBrackets;
    public bool Folding => _settings.Editor.Folding;
    public bool CompletionEnabled => _settings.Editor.Completion;

    /// <summary>行番号ガターにGitの変更状態を表示するか（4.7章）。</summary>
    public bool GitGutterEnabled => _settings.Editor.GitGutter;

    /// <summary>UIフレームワーク固有の機能。検索オーバーレイなどViewから参照する。</summary>
    public Graft.Platform.IUiServices Ui { get; }

    /// <summary>シンタックスハイライトが有効か（8.6/9.2章、settings.jsonの既存キー）。</summary>
    public bool SyntaxEnabled => _settings.Syntax.Enabled;

    public ICommand ToggleWordWrapCommand { get; }
    public ICommand ToggleShowWhitespaceCommand { get; }

    /// <summary>現在のプロジェクトルートの絶対パス。未選択時はnull（4.7 Gitガターの対象設定に使う）。</summary>
    public string? ProjectRoot { get; private set; }

    /// <summary>いずれかのタブが保存された（4.7 Gitガターの更新契機）。</summary>
    public event EventHandler<EditorTabViewModel>? TabSaved;

    /// <summary>
    /// 修正1: 履歴差分タブが閉じられた（×・Ctrl+W・タブ全閉じ等いずれの経路でも）ことの通知。
    /// ShellViewModelがこれを受けて履歴側の選択（History.SelectedItem）を解除し、
    /// 「タブは無いのに履歴側は選択済みのまま」という状態の矛盾を防ぐ。
    /// </summary>
    public event EventHandler? HistoryDiffTabClosed;

    /// <summary>
    /// 機能改善: 設定画面での変更、または他のCtrl+マウスホイール操作（差分表示側）で
    /// 確定したフォントサイズを、実行中のエディタへその場で反映する
    /// （ShellViewModel.EditorFontSizeChangeRequested → StartupCoordinator →
    /// SettingsViewModel経由の保存 → MainViewModel.UpdateSettings→ShellViewModel、という
    /// 経路の末端）。<see cref="FontSizeChangeCommitted"/>は発火しない
    /// （ここでの反映は「既に確定済みの値を映すだけ」であり、これを再度確定通知として
    /// 送り返すと無意味な保存要求が循環してしまうため）。
    /// </summary>
    public void UpdateSettings(Settings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        FontSize = _settings.Editor.FontSize;
    }

    /// <summary>プロジェクト切替。開いていたタブは呼び出し側が閉じてから設定する。</summary>
    public void SetProject(string? projectRoot)
    {
        ProjectRoot = projectRoot;
        _manager.SetProjectRoot(projectRoot);
        ActiveTab = null;
        _tabBeforeDiff = null;
        OnPropertyChanged(nameof(ProjectRoot));
    }

    public async Task<GraftResult<EditorTabViewModel>> OpenFileAsync(
        string fullPath, bool preview = false, int? line = null, CancellationToken ct = default)
    {
        var result = await _manager.OpenAsync(fullPath, preview, ct).ConfigureAwait(true);
        if (!result.IsSuccess) return result;

        ApplyDetectedIndent(result.Value);
        if (line is int l) result.Value.CaretLine = l;
        ActiveTab = result.Value;
        return result;
    }

    /// <summary>
    /// 9.2/4.8: ブロック選択に連動して差分タブを開く。既に差分タブが開いていれば、その内容
    /// （<paramref name="diff"/>は呼び出し側で使い回される単一のDiffViewModel）を再利用して
    /// アクティブ化するだけで、新しいタブは増やさない。
    /// </summary>
    public void ShowDiffTab(DiffViewModel diff)
    {
        ArgumentNullException.ThrowIfNull(diff);
        if (_diffTab is null)
        {
            _diffTab = new EditorTabViewModel(diff, tab => { CloseDiffTab(tab); return Task.CompletedTask; });
            _tabs.Add(_diffTab);
        }

        // 不具合3対応: 差分タブへ切り替える直前にアクティブだったタブを覚えておく
        // （ResolveReturnTab参照）。既に差分タブを表示中の状態から別ブロックを選び直した場合
        // （ブロック一覧の選択変更のたびにここを通る）は上書きしない。
        if (!ReferenceEquals(ActiveTab, _diffTab))
        {
            _tabBeforeDiff = ActiveTab;
        }

        ActiveTab = _diffTab;
    }

    /// <summary>選択ブロックが無くなった場合等に、確認なしで差分タブを閉じる（9.2）。</summary>
    public void CloseDiffTabIfOpen()
    {
        if (_diffTab is { } tab) CloseDiffTab(tab);
    }

    /// <summary>
    /// 修正1: 履歴のリビジョン選択に連動して履歴差分タブを開く。既に開いていれば
    /// アクティブ化するだけで新しいタブは増やさない（<paramref name="historyDiff"/>は
    /// 呼び出し側で使い回される単一インスタンスのため、内容の差し替え自体はそちら側で完結する。
    /// HistoryDiffViewModel.Load参照）。
    /// </summary>
    public void ShowHistoryDiffTab(HistoryDiffViewModel historyDiff)
    {
        ArgumentNullException.ThrowIfNull(historyDiff);
        if (_historyDiffTab is null)
        {
            _historyDiffTab = new EditorTabViewModel(historyDiff, tab => { CloseHistoryDiffTab(tab); return Task.CompletedTask; });
            _tabs.Add(_historyDiffTab);
        }

        ActiveTab = _historyDiffTab;
    }

    /// <summary>修正1: 履歴の選択解除等で、確認なしに履歴差分タブを閉じる。</summary>
    public void CloseHistoryDiffTabIfOpen()
    {
        if (_historyDiffTab is { } tab) CloseHistoryDiffTab(tab);
    }

    /// <summary>未保存なら確認ダイアログを出す。falseはユーザーがキャンセルしたことを表す。</summary>
    public async Task<bool> CloseTabAsync(EditorTabViewModel tab)
    {
        if (tab.Kind == EditorTabKind.Diff)
        {
            CloseDiffTab(tab);
            return true;
        }
        if (tab.Kind == EditorTabKind.HistoryDiff)
        {
            CloseHistoryDiffTab(tab);
            return true;
        }

        var wasActive = ReferenceEquals(tab, ActiveTab);
        var closed = await _manager.CloseAsync(tab).ConfigureAwait(true);
        if (!closed) return false;

        tab.PropertyChanged -= OnTabPropertyChanged;
        if (wasActive) ActiveTab = Tabs.Count > 0 ? Tabs[0] : null;
        return true;
    }

    public async Task<bool> CloseAllAsync()
    {
        var closed = await _manager.CloseAllAsync().ConfigureAwait(true);
        if (!closed) return false;

        if (_diffTab is { } tab) CloseDiffTab(tab);
        if (_historyDiffTab is { } historyTab) CloseHistoryDiffTab(historyTab);
        ActiveTab = null;
        return true;
    }

    /// <summary>差分タブでは保存対象が無いため何もしない（4.8: 保存確認の対象外）。</summary>
    public async Task<GraftResult<bool>> SaveActiveAsync()
    {
        if (ActiveTab is not { Kind: EditorTabKind.Document } tab)
        {
            return GraftResult<bool>.Ok(true);
        }

        var result = await tab.Session.SaveAsync().ConfigureAwait(true);
        if (result.IsSuccess) TabSaved?.Invoke(this, tab);
        return result;
    }

    public async Task<GraftResult<bool>> SaveAllAsync()
    {
        var issues = new List<GraftIssue>();
        foreach (var tab in DocumentTabs.Where(t => t.IsModified).ToList())
        {
            var result = await tab.Session.SaveAsync().ConfigureAwait(true);
            if (result.IsSuccess) TabSaved?.Invoke(this, tab);
            issues.AddRange(result.Issues);
        }

        return issues.Any(i => i.Severity == Severity.Error)
            ? GraftResult<bool>.Fail(issues)
            : GraftResult<bool>.Ok(true, issues);
    }

    /// <summary>
    /// 4.8 適用前チェック: 指定した絶対パスのうち、未保存の変更がある開いているタブのみを保存する。
    /// <see cref="FindUnsaved"/>で対象を確認したうえで呼び出す想定。
    /// </summary>
    public async Task<GraftResult<bool>> SaveFilesAsync(IReadOnlyList<string> fullPaths)
    {
        var issues = new List<GraftIssue>();
        foreach (var tab in DocumentTabs.Where(t => t.IsModified && fullPaths.Any(p => PathsEqual(p, t.Session.FullPath))).ToList())
        {
            var result = await tab.Session.SaveAsync().ConfigureAwait(true);
            issues.AddRange(result.Issues);
        }

        return issues.Any(i => i.Severity == Severity.Error)
            ? GraftResult<bool>.Fail(issues)
            : GraftResult<bool>.Ok(true, issues);
    }

    /// <summary>7章の適用後に、書き換わったファイルのタブを再読込する（4.8）。</summary>
    public Task ReloadIfOpenAsync(IEnumerable<string> fullPaths) => _manager.ReloadIfOpenAsync(fullPaths);

    /// <summary>適用前チェック（4.8）。未保存の変更があるファイルの絶対パスを返す。</summary>
    public IReadOnlyList<string> FindUnsaved(IEnumerable<string> fullPaths) => _manager.FindUnsaved(fullPaths);

    /// <summary>Ctrl+Tabで切り替える先のタブ（View側から呼ばれる）。差分タブは対象外。</summary>
    public EditorTabViewModel? PeekMruNeighbor() => _manager.NextByMru();

    /// <summary>
    /// 機能改善（タブのドラッグ並べ替え）: <paramref name="tab"/>をドキュメントタブの並びの中で
    /// <paramref name="targetIndex"/>（0起点、ドラッグ開始前の並び順での挿入先）へ移動する。
    /// ドキュメントタブのみが対象で、差分タブ・履歴差分タブは常に末尾に固定のため対象外
    /// （<see cref="InsertDocumentTab"/>の不変条件を崩さないため、呼ばれても無視する）。
    /// <paramref name="targetIndex"/>はドキュメントタブだけを数えた範囲（<see cref="Tabs"/>全体
    /// ではなく<see cref="EditorTabManager.Tabs"/>と同じ基準）で指定する。
    ///
    /// 実際の並び替えは<see cref="EditorTabManager.MoveTab"/>（MRU順は変更しない）に委譲し、
    /// その結果は<see cref="OnManagerTabsChanged"/>のMoveハンドラ経由で<see cref="Tabs"/>
    /// （View束縛対象）へも反映される。並び順自体は<see cref="Tabs"/>を素直に読むだけの
    /// 既存のプロジェクト状態保存（ShellViewModel.CaptureProjectState→layout.json）に
    /// そのまま乗るため、再起動後もドラッグした並び順が保たれる。
    /// </summary>
    public void ReorderTab(EditorTabViewModel tab, int targetIndex)
    {
        if (tab.Kind != EditorTabKind.Document) return;
        _manager.MoveTab(tab, targetIndex);
    }

    /// <summary>
    /// 外部変更検知（4.6）。エクスプローラ側の<c>FileWatchService</c>がディスク上の変更を
    /// 検知した際にこのメソッドを呼ぶ。未保存の変更が無ければ黙って再読込し、あれば
    /// <see cref="EditorTabViewModel.HasExternalConflict"/>を立てて非モーダルの通知バーを出す
    /// （E702）。該当タブが開いていなければ何もしない。
    /// </summary>
    public async Task NotifyExternalChangeAsync(string fullPath)
    {
        var tab = DocumentTabs.FirstOrDefault(t => PathsEqual(t.Session.FullPath, fullPath));
        if (tab is null) return;

        if (!tab.Session.IsModified)
        {
            await tab.Session.ReloadAsync().ConfigureAwait(true);
            return;
        }

        tab.HasExternalConflict = true;
    }

    /// <summary>エクスプローラでのリネーム・移動に追従し、開いているタブのパス表示を更新する。</summary>
    public void NotifyRenamed(string oldFullPath, string newFullPath)
    {
        var wasActive = ActiveTab is { Kind: EditorTabKind.Document } active && PathsEqual(active.Session.FullPath, oldFullPath);
        _manager.NotifyRenamed(oldFullPath, newFullPath);
        if (wasActive) RaiseStatusChanged(); // 拡張子変更でLanguageText等が変わりうるため
    }

    /// <summary>エクスプローラでの削除に追従し、開いているタブがあれば確認なしで閉じる。</summary>
    public async Task NotifyDeletedAsync(string fullPath)
    {
        var tab = DocumentTabs.FirstOrDefault(t => PathsEqual(t.Session.FullPath, fullPath));
        if (tab is null) return;

        var wasActive = ReferenceEquals(tab, ActiveTab);
        await _manager.NotifyDeletedAsync(fullPath).ConfigureAwait(true);
        tab.PropertyChanged -= OnTabPropertyChanged;
        if (wasActive) ActiveTab = Tabs.Count > 0 ? Tabs[0] : null;
    }

    // ---- ステータスバー表示（9.2）。差分タブがアクティブな間は対象外（E5でステータスバー本体を実装）。 ----

    public string CaretText => ActiveTab is { Kind: EditorTabKind.Document } t ? $"行 {t.CaretLine}, 列 {t.CaretColumn}" : string.Empty;
    public string EncodingText => ActiveTab is { Kind: EditorTabKind.Document } t ? EncodingLabel(t.Session.Shape) : string.Empty;
    public string NewLineText => ActiveTab is { Kind: EditorTabKind.Document } t ? NewLineLabel(t.Session.Shape.NewLine) : string.Empty;
    public string LanguageText => ActiveTab is { Kind: EditorTabKind.Document } t ? LanguageLabel(t.Session.FileName) : string.Empty;

    /// <summary>
    /// 課題3（再設計）: アクティブなタブに極端に長い行（20,000文字超）があるか
    /// （ステータスバー通知用）。名前は据え置くが、実際の挙動はファイル全体の無効化ではなく
    /// 「その行だけ構文強調・括弧の言語認識をキャップする」に変わっている
    /// （ShellViewModel.StatusBarWarning.csの文言・EditorPane.axaml.cs参照）。
    /// </summary>
    public bool ActiveTabHasLongLineWarning => ActiveTab is { Kind: EditorTabKind.Document } t && t.Session.HasExtremelyLongLine;

    private IEnumerable<EditorTabViewModel> DocumentTabs => _tabs.Where(t => t.Kind == EditorTabKind.Document);

    private void CloseDiffTab(EditorTabViewModel tab)
    {
        if (!ReferenceEquals(tab, _diffTab)) return;

        var wasActive = ReferenceEquals(tab, ActiveTab);
        var returnTo = wasActive ? ResolveReturnTab(tab) : null;
        _tabs.Remove(tab);
        _diffTab = null;
        _tabBeforeDiff = null;
        tab.PropertyChanged -= OnTabPropertyChanged;
        tab.DetachEvents();
        if (wasActive) ActiveTab = returnTo;
    }

    /// <summary>
    /// 修正1: 履歴差分タブを閉じる。通常の差分タブ（ResolveReturnTabで元ファイルへ戻る等）と
    /// 異なり「戻るべき元のタブ」という概念が無いため、フォールバック先は単純に先頭のタブとする。
    /// 閉じた後は<see cref="HistoryDiffTabClosed"/>で履歴側の選択解除をShellViewModelへ委ねる。
    /// </summary>
    private void CloseHistoryDiffTab(EditorTabViewModel tab)
    {
        if (!ReferenceEquals(tab, _historyDiffTab)) return;

        var wasActive = ReferenceEquals(tab, ActiveTab);
        _tabs.Remove(tab);
        _historyDiffTab = null;
        tab.PropertyChanged -= OnTabPropertyChanged;
        tab.DetachEvents();
        if (wasActive) ActiveTab = Tabs.Count > 0 ? Tabs[0] : null;

        HistoryDiffTabClosed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 不具合3対応: 差分タブを閉じたときの戻り先を決める。マッチ失敗時の画面から
    /// コード編集へ戻る手段が見当たらないという指摘への対応（差分タブ自体を閉じる導線は
    /// EditorTabViewModel.CloseCommand・タブの閉じるボタン・Ctrl+Wに加えて用意する）。
    /// 優先順位: 1. 差分の元になったファイルのタブが開いていればそこへ、
    /// 2. 差分タブを開く直前にアクティブだったタブへ、3. それも無ければ先頭のタブへ。
    /// </summary>
    private EditorTabViewModel? ResolveReturnTab(EditorTabViewModel closedDiffTab)
    {
        var originalFullPath = ResolveDiffFullPath(closedDiffTab);
        if (originalFullPath is not null)
        {
            var original = DocumentTabs.FirstOrDefault(t => PathsEqual(t.Session.FullPath, originalFullPath));
            if (original is not null) return original;
        }

        if (_tabBeforeDiff is not null && !ReferenceEquals(_tabBeforeDiff, closedDiffTab) && _tabs.Contains(_tabBeforeDiff))
        {
            return _tabBeforeDiff;
        }

        return Tabs.Count > 0 ? Tabs[0] : null;
    }

    private string? ResolveDiffFullPath(EditorTabViewModel diffTab)
    {
        if (diffTab.Diff?.FilePath is not { } relativePath || ProjectRoot is null) return null;
        return Path.Combine(ProjectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// <see cref="EditorTabManager.Tabs"/>（ドキュメントタブのみ）の増減・並べ替えを、差分タブと
    /// 合成した<see cref="Tabs"/>（View束縛対象）へ反映する。ドキュメントタブは常に差分タブより
    /// 前に並べる。
    /// </summary>
    private void OnManagerTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is not null:
                foreach (EditorTabViewModel tab in e.NewItems) InsertDocumentTab(tab);
                break;
            case NotifyCollectionChangedAction.Remove when e.OldItems is not null:
                foreach (EditorTabViewModel tab in e.OldItems) _tabs.Remove(tab);
                break;
            case NotifyCollectionChangedAction.Move:
                // 機能改善（タブのドラッグ並べ替え）: _manager.Tabsはドキュメントタブのみを
                // 持ち、_tabs側でもドキュメントタブは常に先頭から連続して並ぶ（InsertDocumentTab
                // の不変条件）ため、_manager.Tabs上のインデックスはそのまま_tabs上の
                // インデックスとしても有効。差分系タブの位置には触れないため、そのまま
                // ObservableCollection.Moveへ委譲できる。
                _tabs.Move(e.OldStartingIndex, e.NewStartingIndex);
                break;
            case NotifyCollectionChangedAction.Reset:
                foreach (var tab in _tabs.Where(t => t.Kind == EditorTabKind.Document).ToList()) _tabs.Remove(tab);
                foreach (var tab in _manager.Tabs) InsertDocumentTab(tab);
                break;
        }
    }

    /// <summary>
    /// ドキュメントタブは常に差分系タブ（通常の差分タブ・履歴差分タブ）より前に並べる。
    /// 修正1で履歴差分タブが増えたことにより、通常の差分タブと同時に開いている場合もあるため、
    /// 両者のうち一覧内で最も手前（インデックスが小さい）の位置の直前へ挿入する。
    /// </summary>
    private void InsertDocumentTab(EditorTabViewModel tab)
    {
        var specialTabs = new[] { _diffTab, _historyDiffTab };
        var insertIndex = specialTabs.Where(t => t is not null).Select(t => _tabs.IndexOf(t!))
            .DefaultIfEmpty(_tabs.Count).Min();
        _tabs.Insert(Math.Max(0, insertIndex), tab);
    }

    private void ApplyDetectedIndent(EditorTabViewModel tab)
    {
        if (!DetectIndentEnabled)
        {
            tab.IndentUseTabs = !InsertSpaces;
            tab.IndentWidth = TabSize;
            return;
        }

        var (useTabs, width) = tab.Session.DetectIndent(TabSize);
        tab.IndentUseTabs = useTabs;
        tab.IndentWidth = width;
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not EditorTabViewModel tab) return;

        // 4.3: プレビュータブへの編集は固定タブへの昇格を意味する。
        if (e.PropertyName == nameof(EditorTabViewModel.IsModified) && tab is { IsPreview: true, IsModified: true })
        {
            tab.IsPreview = false;
        }

        if (e.PropertyName is nameof(EditorTabViewModel.CaretLine) or nameof(EditorTabViewModel.CaretColumn))
        {
            OnPropertyChanged(nameof(CaretText));
        }
    }

    private void RaiseStatusChanged()
    {
        OnPropertyChanged(nameof(CaretText));
        OnPropertyChanged(nameof(EncodingText));
        OnPropertyChanged(nameof(NewLineText));
        OnPropertyChanged(nameof(LanguageText));
        OnPropertyChanged(nameof(ActiveTabHasLongLineWarning));
    }

    private static string EncodingLabel(TextShape shape)
    {
        var name = shape.Encoding.CodePage switch
        {
            65001 => "UTF-8",
            1200 => "UTF-16 LE",
            1201 => "UTF-16 BE",
            932 => "Shift_JIS",
            _ => shape.Encoding.EncodingName,
        };
        return name == "UTF-8" ? (shape.HasBom ? "UTF-8 (BOMあり)" : "UTF-8 (BOMなし)") : name;
    }

    private static string NewLineLabel(string newLine) => newLine switch
    {
        "\r\n" => "CRLF",
        "\n" => "LF",
        "\r" => "CR",
        _ => "CRLF",
    };

    private static string LanguageLabel(string fileName)
        => SyntaxLexer.RuleForExtension(Path.GetExtension(fileName))?.Name ?? "プレーンテキスト";

    // Windowsではファイルパスの大文字小文字を区別しない（Editor/EditorTabManagerの判定と揃える）。
    private static bool PathsEqual(string a, string b) => OperatingSystem.IsWindows()
        ? string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
        : string.Equals(a, b, StringComparison.Ordinal);
}
