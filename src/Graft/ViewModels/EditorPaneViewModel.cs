using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows.Input;
using Graft.Core;
using Graft.Editor;
using Graft.Infra;
using Graft.Views;

namespace Graft.ViewModels;

/// <summary>
/// エディタ領域全体（4章）のViewModel。ドキュメントタブの管理は<see cref="EditorTabManager"/>へ
/// 委譲し、本クラスはこれに差分タブ（9.2・4.8）を合成した一覧・アクティブタブ・フォントサイズ・
/// ステータスバー表示（9.2）の窓口を担う。差分タブは<see cref="EditorTabManager"/>が持つ
/// 保存確認等のドキュメント前提のロジックへ一切渡さず、本クラスが直接開閉する
/// （<see cref="EditorTabViewModel.Session"/>は差分タブでは利用できないため）。
/// 15章 editor設定は<see cref="Settings.Editor"/>（<see cref="EditorSettings"/>）から読み取る。
/// </summary>
public sealed class EditorPaneViewModel : ObservableObject
{
    // SettingsStoreの検証範囲（editor.fontSize: 6〜72）と合わせる。Ctrl+マウスホイールでの
    // 変更時にもこの範囲を超えないようにする。
    private const double MinFontSize = 6;
    private const double MaxFontSize = 72;

    private readonly EditorTabManager _manager;
    private readonly Settings _settings;
    private readonly ObservableCollection<EditorTabViewModel> _tabs = new();
    private EditorTabViewModel? _diffTab;
    private EditorTabViewModel? _activeTab;
    private double _fontSize;
    private bool _wordWrap;
    private bool _showWhitespace;

    public EditorPaneViewModel(Settings settings, DialogService dialogs)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _manager = new EditorTabManager(dialogs);
        _manager.Tabs.CollectionChanged += OnManagerTabsChanged;

        _fontSize = Math.Clamp(_settings.Editor.FontSize, MinFontSize, MaxFontSize);
        _wordWrap = _settings.Editor.WordWrap;
        _showWhitespace = _settings.Editor.ShowWhitespace;

        ToggleWordWrapCommand = new RelayCommand(() => WordWrap = !WordWrap);
        ToggleShowWhitespaceCommand = new RelayCommand(() => ShowWhitespace = !ShowWhitespace);
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

    /// <summary>エディタのフォントサイズ（Ctrl+マウスホイールで変更、プロジェクトごとに記憶）。</summary>
    public double FontSize
    {
        get => _fontSize;
        set => SetProperty(ref _fontSize, Math.Clamp(value, MinFontSize, MaxFontSize));
    }

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

    /// <summary>シンタックスハイライトが有効か（8.6/9.2章、settings.jsonの既存キー）。</summary>
    public bool SyntaxEnabled => _settings.Syntax.Enabled;

    public ICommand ToggleWordWrapCommand { get; }
    public ICommand ToggleShowWhitespaceCommand { get; }

    /// <summary>現在のプロジェクトルートの絶対パス。未選択時はnull（4.7 Gitガターの対象設定に使う）。</summary>
    public string? ProjectRoot { get; private set; }

    /// <summary>いずれかのタブが保存された（4.7 Gitガターの更新契機）。</summary>
    public event EventHandler<EditorTabViewModel>? TabSaved;

    /// <summary>プロジェクト切替。開いていたタブは呼び出し側が閉じてから設定する。</summary>
    public void SetProject(string? projectRoot)
    {
        ProjectRoot = projectRoot;
        _manager.SetProjectRoot(projectRoot);
        ActiveTab = null;
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

        ActiveTab = _diffTab;
    }

    /// <summary>選択ブロックが無くなった場合等に、確認なしで差分タブを閉じる（9.2）。</summary>
    public void CloseDiffTabIfOpen()
    {
        if (_diffTab is { } tab) CloseDiffTab(tab);
    }

    /// <summary>未保存なら確認ダイアログを出す。falseはユーザーがキャンセルしたことを表す。</summary>
    public async Task<bool> CloseTabAsync(EditorTabViewModel tab)
    {
        if (tab.Kind == EditorTabKind.Diff)
        {
            CloseDiffTab(tab);
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

    private IEnumerable<EditorTabViewModel> DocumentTabs => _tabs.Where(t => t.Kind == EditorTabKind.Document);

    private void CloseDiffTab(EditorTabViewModel tab)
    {
        if (!ReferenceEquals(tab, _diffTab)) return;

        var wasActive = ReferenceEquals(tab, ActiveTab);
        _tabs.Remove(tab);
        _diffTab = null;
        tab.PropertyChanged -= OnTabPropertyChanged;
        tab.DetachEvents();
        if (wasActive) ActiveTab = Tabs.Count > 0 ? Tabs[0] : null;
    }

    /// <summary>
    /// <see cref="EditorTabManager.Tabs"/>（ドキュメントタブのみ）の増減を、差分タブと合成した
    /// <see cref="Tabs"/>（View束縛対象）へ反映する。ドキュメントタブは常に差分タブより前に並べる。
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
            case NotifyCollectionChangedAction.Reset:
                foreach (var tab in _tabs.Where(t => t.Kind == EditorTabKind.Document).ToList()) _tabs.Remove(tab);
                foreach (var tab in _manager.Tabs) InsertDocumentTab(tab);
                break;
        }
    }

    private void InsertDocumentTab(EditorTabViewModel tab)
    {
        var insertIndex = _diffTab is not null ? _tabs.IndexOf(_diffTab) : _tabs.Count;
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
