using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using Graft.Core;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;

namespace Graft.ViewModels;

/// <summary>
/// 仕様書10章のコンテキスト収集UIを担う。収集モードの選択、ファイルのチェック選択、除外規則の
/// 確認、出力前の概算トークン数表示（10.4）と上限超過時の警告、クリップボードへのコピーを行う。
/// </summary>
public sealed class ContextCollectViewModel : ObservableObject
{
    // 10.3出力形式のファイル見出し「# 相対パス  (ハッシュ)」を検出する正規表現（前提・ツリー見出しは末尾の(ハッシュ)が無く誤検出しない）。
    private static readonly Regex FileHeaderPattern = new(@"^# (?<path>.+?)  \((?<hash>[0-9a-fA-F]+)\)$", RegexOptions.Compiled);

    private readonly ContextCollector _collector;
    private readonly RevisionStore _revisionStore;
    private readonly ProjectStore _projectStore; private readonly IUiServices _ui;
    private Project _project; private readonly Settings _settings;

    private ContextMode _selectedMode = ContextMode.TreeAndSelected;
    private RevisionOption? _selectedRevision;
    private ContextFileNodeViewModel? _selectedFile;
    private int _estimatedTokens;
    private bool _exceedsWarnThreshold;
    private bool _isScanning;
    private bool _isEmpty;
    private GraftIssue? _errorIssue;
    private string? _statusMessage;
    private string _newExcludePattern = string.Empty;

    public ContextCollectViewModel(AppPaths appPaths, ProjectStore projectStore, Project project, Settings settings, IUiServices ui)
    {
        ArgumentNullException.ThrowIfNull(appPaths);
        _projectStore = projectStore ?? throw new ArgumentNullException(nameof(projectStore));
        _project = project ?? throw new ArgumentNullException(nameof(project));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _collector = new ContextCollector(appPaths);
        _revisionStore = new RevisionStore(appPaths);

        Modes = new ObservableCollection<ModeOption>
        {
            new("ツリーのみ", ContextMode.TreeOnly), new("選択ファイル", ContextMode.SelectedFiles),
            new("ツリー＋選択", ContextMode.TreeAndSelected), new("差分のみ", ContextMode.ChangedSince),
        };
        Revisions = new ObservableCollection<RevisionOption>();
        Files = new ObservableCollection<ContextFileNodeViewModel>();
        ExtraExcludes = new ObservableCollection<string>(project.Overrides.Excludes);
        PreviewLines = new ObservableCollection<PreviewLine>();

        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync());
        PreviewCommand = new AsyncRelayCommand(PreviewAsync, () => !_isScanning);
        CopyCommand = new AsyncRelayCommand(CopyAsync, () => !_isScanning);
        ToggleSelectedCommand = new RelayCommand(ToggleSelected, () => _selectedFile is { IsDirectory: false, IsExcluded: false });
        AddExcludeCommand = new AsyncRelayCommand(AddExcludeAsync, () => !string.IsNullOrWhiteSpace(_newExcludePattern));
        RemoveExcludeCommand = new RelayCommand<string>(pattern => _ = RemoveExcludeAsync(pattern));
    }

    public Project Project => _project;

    public ObservableCollection<ModeOption> Modes { get; }
    public ObservableCollection<RevisionOption> Revisions { get; }
    public ObservableCollection<ContextFileNodeViewModel> Files { get; }

    /// <summary>8.6: 出力プレビューの行（シンタックストークン付き）。プレビュー・コピー実行時に更新する。</summary>
    public ObservableCollection<PreviewLine> PreviewLines { get; }

    /// <summary>10.2: 既定除外・.gitignore に加え、プロジェクト単位で追加した除外パターン。</summary>
    public ObservableCollection<string> ExtraExcludes { get; }

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand PreviewCommand { get; }
    public AsyncRelayCommand CopyCommand { get; }
    public RelayCommand ToggleSelectedCommand { get; }
    public AsyncRelayCommand AddExcludeCommand { get; }
    public RelayCommand<string> RemoveExcludeCommand { get; }

    public ContextMode SelectedMode
    {
        get => _selectedMode;
        set => SetProperty(ref _selectedMode, value, OnModeChanged);
    }

    public RevisionOption? SelectedRevision
    {
        get => _selectedRevision;
        set => SetProperty(ref _selectedRevision, value);
    }

    public ContextFileNodeViewModel? SelectedFile
    {
        get => _selectedFile;
        set => SetProperty(ref _selectedFile, value, () => ToggleSelectedCommand.RaiseCanExecuteChanged());
    }

    public bool ShowFileTree => _selectedMode is ContextMode.SelectedFiles or ContextMode.TreeAndSelected;

    public bool ShowRevisionPicker => _selectedMode == ContextMode.ChangedSince;

    public string NewExcludePattern
    {
        get => _newExcludePattern;
        set => SetProperty(ref _newExcludePattern, value, () => AddExcludeCommand.RaiseCanExecuteChanged());
    }

    /// <summary>10.4: 出力前の概算トークン数。</summary>
    public int EstimatedTokens
    {
        get => _estimatedTokens;
        private set => SetProperty(ref _estimatedTokens, value);
    }

    /// <summary>10.4: 上限超過フラグ。超過時はファイル選択の見直しを促す警告を表示する。</summary>
    public bool ExceedsWarnThreshold
    {
        get => _exceedsWarnThreshold;
        private set => SetProperty(ref _exceedsWarnThreshold, value);
    }

    public int TokenWarnThreshold => _settings.Context.TokenWarnThreshold;

    /// <summary>8.8: 読み込み中インジケータ。</summary>
    public bool IsScanning
    {
        get => _isScanning;
        private set => SetProperty(ref _isScanning, value);
    }

    /// <summary>8.8: 空状態。走査対象ファイルが1件もない場合。</summary>
    public bool IsEmpty
    {
        get => _isEmpty;
        private set => SetProperty(ref _isEmpty, value);
    }

    /// <summary>8.8: エラー状態（コード＋対処方法）。</summary>
    public GraftIssue? ErrorIssue
    {
        get => _errorIssue;
        private set => SetProperty(ref _errorIssue, value);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>初期表示時に一度呼び出し、ファイルツリーを走査する。</summary>
    public Task InitializeAsync(CancellationToken ct = default) => RefreshAsync(ct);

    private void OnModeChanged()
    {
        OnPropertyChanged(nameof(ShowFileTree));
        OnPropertyChanged(nameof(ShowRevisionPicker));
        if (_selectedMode == ContextMode.ChangedSince && Revisions.Count == 0)
        {
            _ = LoadRevisionsAsync();
        }
    }

    private async Task LoadRevisionsAsync()
    {
        var result = await _revisionStore.ListAsync(_project.Id).ConfigureAwait(true);
        Revisions.Clear();
        if (!result.IsSuccess) return;
        foreach (var summary in result.Value)
        {
            Revisions.Add(new RevisionOption(summary.Manifest.Revision, summary.Manifest.Summary ?? "(要約なし)"));
        }
        SelectedRevision = Revisions.FirstOrDefault();
    }

    private async Task RefreshAsync(CancellationToken ct = default)
    {
        IsScanning = true;
        ErrorIssue = null;
        try
        {
            var scan = await _collector.ScanAsync(_project, _settings, ct).ConfigureAwait(true);
            if (!scan.IsSuccess)
            {
                ErrorIssue = scan.Errors.FirstOrDefault();
                Files.Clear();
                IsEmpty = false;
                return;
            }

            Files.Clear();
            foreach (var node in scan.Value)
            {
                Files.Add(new ContextFileNodeViewModel(node));
            }
            IsEmpty = Files.Count == 0;
        }
        finally
        {
            IsScanning = false;
        }
    }

    private async Task PreviewAsync()
    {
        var result = await CollectAsync().ConfigureAwait(true);
        if (result is null) return;
        StatusMessage = ExceedsWarnThreshold
            ? $"推定トークン数 {EstimatedTokens} 件。上限（{TokenWarnThreshold} 件）を超えています。ファイル選択を見直してください。"
            : $"推定トークン数 {EstimatedTokens} 件。";
    }

    private async Task CopyAsync()
    {
        var result = await CollectAsync().ConfigureAwait(true);
        if (result is null) return;

        if (ExceedsWarnThreshold)
        {
            StatusMessage = $"推定トークン数 {EstimatedTokens} 件が上限（{TokenWarnThreshold} 件）を超えています。ファイル選択を見直してください。";
            return;
        }

        _ui.Clipboard.SetText(result.Text);
        StatusMessage = "クリップボードにコピーしました。";
    }

    private async Task<ContextResult?> CollectAsync()
    {
        var selectedPaths = Files.Where(f => f is { IsDirectory: false, IsExcluded: false, IsChecked: true })
            .Select(f => f.RelativePath).ToArray();

        var request = new ContextRequest
        {
            Project = _project,
            Settings = _settings,
            Mode = _selectedMode,
            SelectedPaths = selectedPaths,
            SinceRevision = _selectedRevision?.Revision,
        };

        var result = await _collector.CollectAsync(request).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            ErrorIssue = result.Errors.FirstOrDefault();
            return null;
        }

        ErrorIssue = null;
        EstimatedTokens = result.Value.EstimatedTokens;
        ExceedsWarnThreshold = result.Value.ExceedsWarnThreshold;
        UpdatePreviewLines(result.Value.Text);
        return result.Value;
    }

    /// <summary>
    /// 8.6: 実際の出力テキストをファイル見出しで区切り、区間ごとに拡張子別の<see cref="SyntaxLexer"/>で
    /// 走査する。syntax.enabled=false・言語ルール無し・差分のみモード（対象解決が煩雑なため）は
    /// プレーン表示へフォールバックする（コピー結果自体には一切影響しない）。
    /// </summary>
    private void UpdatePreviewLines(string text)
    {
        PreviewLines.Clear();
        var rawLines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (!_settings.Syntax.Enabled || _selectedMode == ContextMode.ChangedSince)
        {
            foreach (var line in rawLines) PreviewLines.Add(new PreviewLine(line, Array.Empty<SyntaxToken>()));
            return;
        }

        string? extension = null;
        var buffer = new List<string>();
        foreach (var line in rawLines)
        {
            var match = FileHeaderPattern.Match(line);
            if (match.Success)
            {
                FlushPreviewSection(extension, buffer);
                extension = Path.GetExtension(match.Groups["path"].Value);
                PreviewLines.Add(new PreviewLine(line, Array.Empty<SyntaxToken>()));
            }
            else
            {
                buffer.Add(line);
            }
        }
        FlushPreviewSection(extension, buffer);
    }

    /// <summary>直前のファイル見出しから現在行までの区間（1ファイル分の本文）をトークン化して積む。</summary>
    private void FlushPreviewSection(string? extension, List<string> buffer)
    {
        if (buffer.Count == 0) return;

        var rule = extension is null ? null : SyntaxLexer.RuleForExtension(extension);
        if (rule is null)
        {
            foreach (var line in buffer) PreviewLines.Add(new PreviewLine(line, Array.Empty<SyntaxToken>()));
            buffer.Clear();
            return;
        }

        var lexer = new SyntaxLexer(rule);
        var scanned = lexer.Scan(buffer);
        for (var i = 0; i < buffer.Count; i++)
        {
            var tokens = scanned && !lexer.IsDisabled ? lexer.TokenizeLine(i, buffer[i]) : Array.Empty<SyntaxToken>();
            PreviewLines.Add(new PreviewLine(buffer[i], tokens));
        }
        buffer.Clear();
    }

    private void ToggleSelected()
    {
        if (_selectedFile is { IsDirectory: false, IsExcluded: false } file)
        {
            file.IsChecked = !file.IsChecked;
        }
    }

    private async Task AddExcludeAsync()
    {
        var pattern = _newExcludePattern.Trim();
        if (string.IsNullOrEmpty(pattern) || ExtraExcludes.Contains(pattern)) return;

        ExtraExcludes.Add(pattern);
        NewExcludePattern = string.Empty;
        await PersistOverridesAsync().ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
    }

    private async Task RemoveExcludeAsync(string? pattern)
    {
        if (pattern is null || !ExtraExcludes.Remove(pattern)) return;
        await PersistOverridesAsync().ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
    }

    private async Task PersistOverridesAsync()
    {
        var loaded = await _projectStore.LoadAsync().ConfigureAwait(true);
        var projects = loaded.Value.ToList();
        var index = projects.FindIndex(p => p.Id == _project.Id);
        if (index < 0) return;

        _project = _project with { Overrides = _project.Overrides with { Excludes = ExtraExcludes.ToArray() } };
        projects[index] = _project;
        await _projectStore.SaveAsync(projects).ConfigureAwait(true);
    }

    /// <summary>収集モードの選択肢1件。</summary>
    public sealed record ModeOption(string Label, ContextMode Mode);

    /// <summary>差分のみモード用のリビジョン選択肢1件。</summary>
    public sealed record RevisionOption(int Revision, string Summary)
    {
        public string DisplayText => $"r{Revision} — {Summary}";
    }

    /// <summary>8.6: 出力プレビューの1行。<see cref="Graft.Views.CodeLineControl"/> にそのまま束縛する。</summary>
    public sealed record PreviewLine(string Text, IReadOnlyList<SyntaxToken> Tokens);
}

/// <summary>ファイルツリー1行分の選択状態を保持する。</summary>
public sealed class ContextFileNodeViewModel : ObservableObject
{
    private bool _isChecked;

    public ContextFileNodeViewModel(ContextFileNode node)
    {
        RelativePath = node.RelativePath;
        IsDirectory = node.IsDirectory;
        IsExcluded = node.IsExcluded;
        ExcludeReason = node.ExcludeReason;
        IndentLevel = node.RelativePath.Count(c => c == '/');
        var nameStart = node.RelativePath.LastIndexOf('/') + 1;
        DisplayName = node.RelativePath[nameStart..];
    }

    public string RelativePath { get; }
    public bool IsDirectory { get; }
    public bool IsExcluded { get; }
    public string? ExcludeReason { get; }
    public int IndentLevel { get; }
    public string DisplayName { get; }

    public bool IsChecked
    {
        get => _isChecked;
        set => SetProperty(ref _isChecked, value);
    }

    /// <summary>8.14: スクリーンリーダー向けの読み上げ文言。種別・除外理由を含める。</summary>
    public string AutomationLabel => IsDirectory ? $"フォルダ {DisplayName}"
        : IsExcluded ? $"除外 {DisplayName}（{ExcludeReason}）"
        : $"ファイル {DisplayName}";
}
