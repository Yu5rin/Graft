using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Input;
using Graft.Core;
using Graft.Editor;
using Graft.Infra;
using Graft.Views;

namespace Graft.ViewModels;

/// <summary>
/// エディタ領域全体（4章）のViewModel。実際のタブ管理は<see cref="EditorTabManager"/>へ委譲し、
/// 本クラスはアクティブタブ・フォントサイズ・ステータスバー表示（9.2）の窓口を担う。
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
    private EditorTabViewModel? _activeTab;
    private double _fontSize;
    private bool _wordWrap;
    private bool _showWhitespace;

    public EditorPaneViewModel(Settings settings, DialogService dialogs)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _manager = new EditorTabManager(dialogs);

        _fontSize = Math.Clamp(_settings.Editor.FontSize, MinFontSize, MaxFontSize);
        _wordWrap = _settings.Editor.WordWrap;
        _showWhitespace = _settings.Editor.ShowWhitespace;

        ToggleWordWrapCommand = new RelayCommand(() => WordWrap = !WordWrap);
        ToggleShowWhitespaceCommand = new RelayCommand(() => ShowWhitespace = !ShowWhitespace);
    }

    /// <summary>開いているタブの一覧。</summary>
    public ObservableCollection<EditorTabViewModel> Tabs => _manager.Tabs;

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
                _manager.Touch(value);
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

    /// <summary>シンタックスハイライトが有効か（8.6/9.2章、settings.jsonの既存キー）。</summary>
    public bool SyntaxEnabled => _settings.Syntax.Enabled;

    public ICommand ToggleWordWrapCommand { get; }
    public ICommand ToggleShowWhitespaceCommand { get; }

    /// <summary>プロジェクト切替。開いていたタブは呼び出し側が閉じてから設定する。</summary>
    public void SetProject(string? projectRoot)
    {
        _manager.SetProjectRoot(projectRoot);
        ActiveTab = null;
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

    /// <summary>未保存なら確認ダイアログを出す。falseはユーザーがキャンセルしたことを表す。</summary>
    public async Task<bool> CloseTabAsync(EditorTabViewModel tab)
    {
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
        if (closed) ActiveTab = null;
        return closed;
    }

    public Task<GraftResult<bool>> SaveActiveAsync()
        => ActiveTab is { } tab ? tab.Session.SaveAsync() : Task.FromResult(GraftResult<bool>.Ok(true));

    public async Task<GraftResult<bool>> SaveAllAsync()
    {
        var issues = new List<GraftIssue>();
        foreach (var tab in Tabs.Where(t => t.IsModified).ToList())
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

    /// <summary>Ctrl+Tabで切り替える先のタブ（View側から呼ばれる）。</summary>
    public EditorTabViewModel? PeekMruNeighbor() => _manager.NextByMru();

    // ---- ステータスバー表示（9.2） ----

    public string CaretText => ActiveTab is { } t ? $"行 {t.CaretLine}, 列 {t.CaretColumn}" : string.Empty;
    public string EncodingText => ActiveTab is { } t ? EncodingLabel(t.Session.Shape) : string.Empty;
    public string NewLineText => ActiveTab is { } t ? NewLineLabel(t.Session.Shape.NewLine) : string.Empty;
    public string LanguageText => ActiveTab is { } t ? LanguageLabel(t.Session.FileName) : string.Empty;

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
}
