using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using Graft.Core;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;
using Graft.Themes;

namespace Graft.ViewModels;

/// <summary>
/// 14章の設定画面を担う。settings.json の全項目編集、json直接編集タブ、
/// 不正値のフォールバック通知（<see cref="SettingsStore"/> が返す Issue の表示）、
/// エクスポート／インポート、テーマの即時反映（<see cref="Graft.Themes.ThemeManager"/> 経由）を行う。
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly SettingsStore _settingsStore; private readonly IDialogService _dialogService;
    private Settings _settings = new();

    private string _selectedTheme = "system";
    private string _selectedApplyMode = "allOrNothing";
    private bool _showPreview; private bool _requireSummary;
    private string _hotkey = string.Empty;
    private string _selectedLogLevel = "info";
    private bool _clipboardWatchEnabled;
    private string _selectedClipboardAction = "notify";
    private string _maxRevisionsText = "0";
    private string _maxTotalMbText = "0";
    private bool _useRecycleBin;
    private string _similarityThresholdText = "0";
    private bool _allowSimilarityMatch;
    private string _rangeWarningLinesText = "0";
    private string _newFileEncoding = "utf-8";
    private bool _newFileBom;
    private bool _syntaxEnabled;
    private bool _showLineNumbers;
    private string _contextLinesText = "0";
    private bool _wordWrap;
    private bool _showWhitespace;
    private string _allowedExtensionsText = string.Empty;
    private string _maxFileSizeMbText = "0";
    private string _maxFilesPerRevisionText = "0";
    private bool _respectGitignore;
    private string _tokenRatioText = "0";
    private string _tokenWarnThresholdText = "0";
    private string _hooksTimeoutSecText = "0";
    private bool _autoCommit;
    private string _editorFontSizeText = "13";
    private bool _editorWordWrap;
    private bool _editorShowWhitespace;
    private bool _editorShowLineNumbers = true;
    private bool _editorHighlightCurrentLine = true;
    private string _editorTabSizeText = "4";
    private bool _editorInsertSpaces = true;
    private bool _editorDetectIndent = true;
    private bool _editorAutoClosingBrackets = true;
    private bool _editorFolding = true;
    private bool _editorCompletion = true; private bool _editorGitGutter = true;
    private bool _exportSettingsOnly = true;
    private string _jsonText = string.Empty;
    private string? _jsonParseError;
    private bool _isBusy;
    private string? _statusMessage;

    // 課題2・3で追加した「閉じたときの動作」「PC起動時に自動で起動する」は即時反映方式
    // （SetEditableProperty/ScheduleSave/CommitAndSaveAsync）で扱う。既存項目は並行して
    // 別担当者が全体を即時反映方式へ移行中のため、ここでは新規追加分のみをこの方式にし、
    // 既存の保存ボタン方式（SaveAsync等）には手を入れない。
    private string _closeBehavior = "exit";
    private bool _launchAtStartup;
    private CancellationTokenSource? _saveDebounceCts;
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(300);
    private readonly Action<Settings>? _onLiveSettingsChanged;

    public SettingsViewModel(
        AppPaths appPaths, IDialogService dialogService, IUiServices ui, Action<Settings>? onLiveSettingsChanged = null)
    {
        ArgumentNullException.ThrowIfNull(appPaths);
        ArgumentNullException.ThrowIfNull(ui);
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _onLiveSettingsChanged = onLiveSettingsChanged;
        _settingsStore = new SettingsStore(appPaths);
        var projectStore = new ProjectStore(appPaths);

        Templates = new PromptTemplateViewModel(appPaths, projectStore, dialogService, _settings, ui);
        TokenStats = new TokenStatisticsViewModel(appPaths, projectStore);
        Hooks = new HookSettingsViewModel(projectStore, dialogService);
        ValidationIssues = new ObservableCollection<GraftIssue>();

        SaveCommand = new AsyncRelayCommand(SaveAsync);
        SaveJsonCommand = new AsyncRelayCommand(SaveJsonAsync);
        ExportCommand = new AsyncRelayCommand(ExportAsync);
        ImportCommand = new AsyncRelayCommand(ImportAsync);
    }

    public PromptTemplateViewModel Templates { get; }
    public TokenStatisticsViewModel TokenStats { get; }
    public HookSettingsViewModel Hooks { get; }
    public ObservableCollection<GraftIssue> ValidationIssues { get; }

    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand SaveJsonCommand { get; }
    public AsyncRelayCommand ExportCommand { get; }
    public AsyncRelayCommand ImportCommand { get; }

    public IReadOnlyList<ChoiceOption> ThemeOptions { get; } = new[]
    {
        new ChoiceOption("ダーク", "dark"), new ChoiceOption("ライト", "light"), new ChoiceOption("システム追従", "system"),
    };

    public IReadOnlyList<ChoiceOption> ApplyModeOptions { get; } = new[]
    {
        new ChoiceOption("全件適用（All or Nothing）", "allOrNothing"), new ChoiceOption("部分適用可", "partial"),
    };

    public IReadOnlyList<ChoiceOption> LogLevelOptions { get; } = new[]
    {
        new ChoiceOption("trace", "trace"), new ChoiceOption("debug", "debug"), new ChoiceOption("info", "info"),
        new ChoiceOption("warn", "warn"), new ChoiceOption("error", "error"),
    };

    public IReadOnlyList<ChoiceOption> ClipboardActionOptions { get; } = new[]
    {
        new ChoiceOption("トレイ通知のみ", "notify"), new ChoiceOption("非アクティブ表示", "passive"), new ChoiceOption("アクティブ表示", "active"),
    };

    /// <summary>
    /// 課題2: トレイが実際に機能しない環境（Linuxでの未対応デスクトップ環境等）では
    /// 「タスクトレイに常駐する」を選択肢そのものから外す（仕様書2.3の縮退）。
    /// ShellWindow.OnClosing側でも実際の対応可否を二重に確認しているため、万一ここが
    /// 誤ってtrueを返しても、実際にトレイが使えない環境なら閉じると終了する。
    /// </summary>
    public bool IsTraySupported { get; } = PlatformServices.Current.Tray.IsSupported;

    /// <summary>トレイが使えない場合に画面へ表示する理由（利用可能なら null）。</summary>
    public string? TrayUnsupportedReason => PlatformServices.Current.Tray.UnsupportedReason;

    public IReadOnlyList<ChoiceOption> CloseBehaviorOptions => IsTraySupported
        ? new[] { new ChoiceOption("終了する", "exit"), new ChoiceOption("タスクトレイに常駐する", "tray") }
        : new[] { new ChoiceOption("終了する", "exit") };

    /// <summary>課題3: この環境で自動起動に対応しているか。非対応ならチェックボックスを無効化する。</summary>
    public bool IsAutoStartSupported { get; } = PlatformServices.Current.AutoStart.IsSupported;

    /// <summary>自動起動が使えない場合に画面へ表示する理由（利用可能なら null）。</summary>
    public string? AutoStartUnsupportedReason => PlatformServices.Current.AutoStart.UnsupportedReason;

    /// <summary>テーマ。設定変更と同時に <see cref="ThemeManager"/> 経由で即時反映する（再起動不要）。</summary>
    public string SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (!SetProperty(ref _selectedTheme, value)) return;
            ThemeManager.SetTheme(ParseTheme(value));
        }
    }

    public string SelectedApplyMode { get => _selectedApplyMode; set => SetProperty(ref _selectedApplyMode, value); }
    public bool ShowPreview { get => _showPreview; set => SetProperty(ref _showPreview, value); }
    public bool RequireSummary { get => _requireSummary; set => SetProperty(ref _requireSummary, value); }
    public string Hotkey { get => _hotkey; set => SetProperty(ref _hotkey, value); }
    public string SelectedLogLevel { get => _selectedLogLevel; set => SetProperty(ref _selectedLogLevel, value); }
    public bool ClipboardWatchEnabled { get => _clipboardWatchEnabled; set => SetProperty(ref _clipboardWatchEnabled, value); }
    public string SelectedClipboardAction { get => _selectedClipboardAction; set => SetProperty(ref _selectedClipboardAction, value); }
    public string MaxRevisionsText { get => _maxRevisionsText; set => SetProperty(ref _maxRevisionsText, value); }
    public string MaxTotalMBText { get => _maxTotalMbText; set => SetProperty(ref _maxTotalMbText, value); }
    public bool UseRecycleBin { get => _useRecycleBin; set => SetProperty(ref _useRecycleBin, value); }
    public string SimilarityThresholdText { get => _similarityThresholdText; set => SetProperty(ref _similarityThresholdText, value); }
    public bool AllowSimilarityMatch { get => _allowSimilarityMatch; set => SetProperty(ref _allowSimilarityMatch, value); }
    public string RangeWarningLinesText { get => _rangeWarningLinesText; set => SetProperty(ref _rangeWarningLinesText, value); }
    public string NewFileEncoding { get => _newFileEncoding; set => SetProperty(ref _newFileEncoding, value); }
    public bool NewFileBom { get => _newFileBom; set => SetProperty(ref _newFileBom, value); }
    public bool SyntaxEnabled { get => _syntaxEnabled; set => SetProperty(ref _syntaxEnabled, value); }
    public bool ShowLineNumbers { get => _showLineNumbers; set => SetProperty(ref _showLineNumbers, value); }
    public string ContextLinesText { get => _contextLinesText; set => SetProperty(ref _contextLinesText, value); }
    public bool WordWrap { get => _wordWrap; set => SetProperty(ref _wordWrap, value); }
    public bool ShowWhitespace { get => _showWhitespace; set => SetProperty(ref _showWhitespace, value); }
    public string AllowedExtensionsText { get => _allowedExtensionsText; set => SetProperty(ref _allowedExtensionsText, value); }
    public string MaxFileSizeMBText { get => _maxFileSizeMbText; set => SetProperty(ref _maxFileSizeMbText, value); }
    public string MaxFilesPerRevisionText { get => _maxFilesPerRevisionText; set => SetProperty(ref _maxFilesPerRevisionText, value); }
    public bool RespectGitignore { get => _respectGitignore; set => SetProperty(ref _respectGitignore, value); }
    public string TokenRatioText { get => _tokenRatioText; set => SetProperty(ref _tokenRatioText, value); }
    public string TokenWarnThresholdText { get => _tokenWarnThresholdText; set => SetProperty(ref _tokenWarnThresholdText, value); }
    public string HooksTimeoutSecText { get => _hooksTimeoutSecText; set => SetProperty(ref _hooksTimeoutSecText, value); }
    public bool AutoCommit { get => _autoCommit; set => SetProperty(ref _autoCommit, value); }

    /// <summary>
    /// 課題2: ウィンドウを×で閉じたときの動作（"exit" / "tray"）。ドロップダウンのため、
    /// 変更した瞬間に即時反映する（<see cref="SetEditableProperty{T}"/>）。
    /// </summary>
    public string CloseBehavior { get => _closeBehavior; set => SetEditableProperty(ref _closeBehavior, value); }

    /// <summary>
    /// 課題3: PC起動時に自動で起動するか。チェックボックスのため、変更した瞬間に即時反映する。
    /// 実際のスタートアップフォルダへの登録・解除は<see cref="CommitAndSaveAsync"/>で行う。
    /// </summary>
    public bool LaunchAtStartup { get => _launchAtStartup; set => SetEditableProperty(ref _launchAtStartup, value); }

    /// <summary>15章・4章 エディタ設定（12項目）。設定画面の「エディタ」タブが編集する。</summary>
    public string EditorFontSizeText { get => _editorFontSizeText; set => SetProperty(ref _editorFontSizeText, value); }
    public bool EditorWordWrap { get => _editorWordWrap; set => SetProperty(ref _editorWordWrap, value); }
    public bool EditorShowWhitespace { get => _editorShowWhitespace; set => SetProperty(ref _editorShowWhitespace, value); }
    public bool EditorShowLineNumbers { get => _editorShowLineNumbers; set => SetProperty(ref _editorShowLineNumbers, value); }
    public bool EditorHighlightCurrentLine { get => _editorHighlightCurrentLine; set => SetProperty(ref _editorHighlightCurrentLine, value); }
    public string EditorTabSizeText { get => _editorTabSizeText; set => SetProperty(ref _editorTabSizeText, value); }
    public bool EditorInsertSpaces { get => _editorInsertSpaces; set => SetProperty(ref _editorInsertSpaces, value); }
    public bool EditorDetectIndent { get => _editorDetectIndent; set => SetProperty(ref _editorDetectIndent, value); }
    public bool EditorAutoClosingBrackets { get => _editorAutoClosingBrackets; set => SetProperty(ref _editorAutoClosingBrackets, value); }
    public bool EditorFolding { get => _editorFolding; set => SetProperty(ref _editorFolding, value); }
    public bool EditorCompletion { get => _editorCompletion; set => SetProperty(ref _editorCompletion, value); }
    public bool EditorGitGutter { get => _editorGitGutter; set => SetProperty(ref _editorGitGutter, value); }

    /// <summary>エクスポート／インポートの範囲。trueなら設定のみ（プロジェクト定義を除外）。</summary>
    public bool ExportSettingsOnly { get => _exportSettingsOnly; set => SetProperty(ref _exportSettingsOnly, value); }

    /// <summary>settings.json のjson直接編集タブの内容。</summary>
    public string JsonText { get => _jsonText; set => SetProperty(ref _jsonText, value); }

    /// <summary>8.8: json直接編集タブの構文エラー表示。エラーが無ければ null。</summary>
    public string? JsonParseError { get => _jsonParseError; private set => SetProperty(ref _jsonParseError, value); }

    /// <summary>8.8: 読み込み中インジケータ（200ms未満で完了する処理では表示しない）。</summary>
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }

    public string? StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }

    /// <summary>設定画面の表示時に一度呼び出す。settings.json・テンプレート・トークン統計を読み込む。</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await LoadAsync(ct).ConfigureAwait(true);
        await Templates.InitializeAsync(ct).ConfigureAwait(true);
        await TokenStats.LoadProjectsAsync(ct).ConfigureAwait(true);
        await Hooks.InitializeAsync(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// 「閉じる」ボタン・Escapeキー・ウィンドウの×（Closing）のすべてから共通で呼び出す
    /// クローズ処理。バグ2の対応: <see cref="SelectedTheme"/> は変更と同時に
    /// <see cref="ThemeManager.SetTheme"/> で即時プレビュー反映されるが、保存せずに閉じた場合は
    /// そのプレビューを取り消し、最後に保存された状態へ戻す。あわせて未保存の変更がある場合は
    /// 「保存する／破棄して閉じる／キャンセル」を確認する（3択の意味は
    /// <see cref="IDialogService.ConfirmThreeWayAsync"/>のyes/no/nullに対応）。
    /// </summary>
    /// <returns>ウィンドウを閉じてよいならtrue、ユーザーがキャンセルしたならfalse。</returns>
    public async Task<bool> RequestCloseAsync()
    {
        if (!HasUnsavedChanges()) return true;

        var choice = await _dialogService.ConfirmThreeWayAsync(
            "未保存の変更があります",
            "設定に保存されていない変更があります。保存せずに閉じますか？",
            "保存する", "破棄して閉じる").ConfigureAwait(true);

        switch (choice)
        {
            case true:
                await SaveAsync().ConfigureAwait(true);
                return true;
            case false:
                // テーマ等の即時プレビューを、最後に保存された_settingsの内容へ戻す。
                // SelectedThemeのsetterはThemeManager.SetTheme経由でウィンドウの見た目自体を
                // 変えるため、フィールドを入れ直すだけでプレビューが取り消される。
                PopulateEditableFields(_settings);
                return true;
            default:
                return false; // キャンセル：閉じずに編集を続けさせる
        }
    }

    /// <summary>
    /// 読み込み直後（または直近の保存直後）の設定と、現在の入力欄から組み立てた設定を比較し、
    /// 未保存の変更があるかどうかを判定する。<see cref="Settings"/>はrecordだが
    /// <see cref="SafetySettings.AllowedExtensions"/>のList&lt;string&gt;など既定の構造的等価性が
    /// 効かないフィールドを含むため、record同士の==ではなく、JsonTextと同じ手段
    /// （JSONへ直列化した文字列）で比較する。
    /// </summary>
    private bool HasUnsavedChanges()
    {
        var current = JsonSerializer.Serialize(BuildSettingsFromFields(), JsonFileStore.DefaultOptions);
        var saved = JsonSerializer.Serialize(_settings, JsonFileStore.DefaultOptions);
        return current != saved;
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        await RunBusyAsync(async () =>
        {
            var result = await _settingsStore.LoadAsync(ct).ConfigureAwait(true);
            await ApplyLoadedResultAsync(result).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private async Task SaveAsync()
    {
        await RunBusyAsync(async () =>
        {
            var built = BuildSettingsFromFields();
            await _settingsStore.SaveAsync(built).ConfigureAwait(true);
            var result = await _settingsStore.LoadAsync().ConfigureAwait(true);
            await ApplyLoadedResultAsync(result).ConfigureAwait(true);
        }).ConfigureAwait(true);
        StatusMessage = "設定を保存しました。";
    }

    private async Task SaveJsonAsync()
    {
        Settings? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<Settings>(_jsonText, JsonFileStore.DefaultOptions);
        }
        catch (JsonException ex)
        {
            JsonParseError = $"JSONを解析できませんでした: {ex.Message}";
            return;
        }

        if (parsed is null)
        {
            JsonParseError = "JSONを解析できませんでした。";
            return;
        }

        JsonParseError = null;
        await RunBusyAsync(async () =>
        {
            await _settingsStore.SaveAsync(parsed).ConfigureAwait(true);
            var result = await _settingsStore.LoadAsync().ConfigureAwait(true);
            await ApplyLoadedResultAsync(result).ConfigureAwait(true);
        }).ConfigureAwait(true);
        StatusMessage = "設定を保存しました。";
    }

    private async Task ExportAsync()
    {
        var folder = await _dialogService.PickFolderAsync("エクスポート先フォルダを選択してください").ConfigureAwait(true);
        if (folder is null) return;

        var scope = _exportSettingsOnly ? SettingsExportScope.SettingsOnly : SettingsExportScope.IncludeProjects;
        var result = await _settingsStore.ExportAsync(folder, scope).ConfigureAwait(true);
        var message = result.IsSuccess
            ? $"{result.Value.Count}件のファイルを書き出しました。"
            : "エクスポートに失敗しました。" + string.Join("\n", result.Issues.Select(i => i.ToDisplayText()));
        await _dialogService.ShowMessageAsync("エクスポート", message).ConfigureAwait(true);
    }

    private async Task ImportAsync()
    {
        var folder = await _dialogService.PickFolderAsync("インポート元フォルダを選択してください").ConfigureAwait(true);
        if (folder is null) return;

        var confirmed = await _dialogService
            .ConfirmAsync("インポートの確認", "現在の設定を選択したフォルダの内容で上書きします。よろしいですか？")
            .ConfigureAwait(true);
        if (!confirmed) return;

        var scope = _exportSettingsOnly ? SettingsExportScope.SettingsOnly : SettingsExportScope.IncludeProjects;
        var result = await _settingsStore.ImportAsync(folder, scope).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            await ApplyLoadedResultAsync(result).ConfigureAwait(true);
        }

        var message = result.IsSuccess ? "設定を取り込みました。" : "インポートに失敗しました。";
        await _dialogService.ShowMessageAsync("インポート", message).ConfigureAwait(true);
    }

    private async Task ApplyLoadedResultAsync(GraftResult<Settings> result)
    {
        _settings = result.Value;
        PopulateEditableFields(_settings);
        JsonText = JsonSerializer.Serialize(_settings, JsonFileStore.DefaultOptions);
        ValidationIssues.Clear();
        foreach (var issue in result.Issues)
        {
            ValidationIssues.Add(issue);
        }
        await Templates.UpdateSettingsAsync(_settings).ConfigureAwait(true);
    }

    private void PopulateEditableFields(Settings s)
    {
        SelectedTheme = s.Theme; SelectedApplyMode = s.ApplyMode; ShowPreview = s.ShowPreview;
        RequireSummary = s.RequireSummary; Hotkey = s.Hotkey; SelectedLogLevel = s.LogLevel;
        ClipboardWatchEnabled = s.ClipboardWatch.Enabled; SelectedClipboardAction = s.ClipboardWatch.Action;
        MaxRevisionsText = s.Backup.MaxRevisions.ToString(CultureInfo.InvariantCulture);
        MaxTotalMBText = s.Backup.MaxTotalMB.ToString(CultureInfo.InvariantCulture); UseRecycleBin = s.Backup.UseRecycleBin;
        SimilarityThresholdText = s.Matching.SimilarityThreshold.ToString(CultureInfo.InvariantCulture);
        AllowSimilarityMatch = s.Matching.AllowSimilarityMatch;
        RangeWarningLinesText = s.Matching.RangeWarningLines.ToString(CultureInfo.InvariantCulture);
        NewFileEncoding = s.Encoding.NewFileEncoding; NewFileBom = s.Encoding.NewFileBom;
        SyntaxEnabled = s.Syntax.Enabled; ShowLineNumbers = s.Syntax.ShowLineNumbers;
        ContextLinesText = s.Diff.ContextLines.ToString(CultureInfo.InvariantCulture);
        WordWrap = s.Diff.WordWrap; ShowWhitespace = s.Diff.ShowWhitespace;
        AllowedExtensionsText = string.Join(", ", s.Safety.AllowedExtensions);
        MaxFileSizeMBText = s.Safety.MaxFileSizeMB.ToString(CultureInfo.InvariantCulture);
        MaxFilesPerRevisionText = s.Safety.MaxFilesPerRevision.ToString(CultureInfo.InvariantCulture);
        RespectGitignore = s.Context.RespectGitignore;
        TokenRatioText = s.Context.TokenRatio.ToString(CultureInfo.InvariantCulture);
        TokenWarnThresholdText = s.Context.TokenWarnThreshold.ToString(CultureInfo.InvariantCulture);
        HooksTimeoutSecText = s.Hooks.TimeoutSec.ToString(CultureInfo.InvariantCulture);
        AutoCommit = s.Git.AutoCommit;
        // 課題2・3の追加分は即時反映プロパティ（SetEditableProperty）のため、公開セッター
        // 経由で代入すると読み込み直後に不要な保存とスタートアップフォルダへの再登録が
        // 走ってしまう。SetProperty（ScheduleSaveを伴わない版）で直接フィールドへ反映する。
        SetProperty(ref _closeBehavior, s.CloseBehavior, nameof(CloseBehavior));
        SetProperty(ref _launchAtStartup, s.LaunchAtStartup, nameof(LaunchAtStartup));
        PopulateEditorFields(s.Editor);
    }

    private void PopulateEditorFields(EditorSettings e)
    {
        EditorFontSizeText = e.FontSize.ToString(CultureInfo.InvariantCulture);
        EditorWordWrap = e.WordWrap; EditorShowWhitespace = e.ShowWhitespace;
        EditorShowLineNumbers = e.ShowLineNumbers; EditorHighlightCurrentLine = e.HighlightCurrentLine;
        EditorTabSizeText = e.TabSize.ToString(CultureInfo.InvariantCulture);
        EditorInsertSpaces = e.InsertSpaces; EditorDetectIndent = e.DetectIndent;
        EditorAutoClosingBrackets = e.AutoClosingBrackets; EditorFolding = e.Folding;
        EditorCompletion = e.Completion; EditorGitGutter = e.GitGutter;
    }

    private Settings BuildSettingsFromFields() => new()
    {
        Theme = _selectedTheme,
        ApplyMode = _selectedApplyMode,
        ShowPreview = _showPreview,
        RequireSummary = _requireSummary,
        Hotkey = _hotkey,
        LogLevel = _selectedLogLevel,
        CloseBehavior = _closeBehavior,
        LaunchAtStartup = _launchAtStartup,
        ClipboardWatch = new ClipboardWatchSettings { Enabled = _clipboardWatchEnabled, Action = _selectedClipboardAction },
        Backup = new BackupSettings { MaxRevisions = ParseInt(_maxRevisionsText), MaxTotalMB = ParseInt(_maxTotalMbText), UseRecycleBin = _useRecycleBin },
        Matching = new MatchingSettings
        {
            SimilarityThreshold = ParseDouble(_similarityThresholdText),
            AllowSimilarityMatch = _allowSimilarityMatch,
            RangeWarningLines = ParseInt(_rangeWarningLinesText),
        },
        Encoding = new EncodingSettings { NewFileEncoding = _newFileEncoding, NewFileBom = _newFileBom },
        Syntax = new SyntaxSettings { Enabled = _syntaxEnabled, ShowLineNumbers = _showLineNumbers },
        Diff = new DiffSettings { ContextLines = ParseInt(_contextLinesText), WordWrap = _wordWrap, ShowWhitespace = _showWhitespace },
        Safety = new SafetySettings
        {
            AllowedExtensions = ParseExtensions(_allowedExtensionsText),
            MaxFileSizeMB = ParseInt(_maxFileSizeMbText),
            MaxFilesPerRevision = ParseInt(_maxFilesPerRevisionText),
        },
        Context = new ContextSettings
        {
            RespectGitignore = _respectGitignore,
            TokenRatio = ParseDouble(_tokenRatioText),
            TokenWarnThreshold = ParseInt(_tokenWarnThresholdText),
        },
        Hooks = new HookSettings { TimeoutSec = ParseInt(_hooksTimeoutSecText) },
        Git = new GitSettings { AutoCommit = _autoCommit },
        Editor = new EditorSettings
        {
            FontSize = ParseDouble(_editorFontSizeText), WordWrap = _editorWordWrap,
            ShowWhitespace = _editorShowWhitespace, ShowLineNumbers = _editorShowLineNumbers,
            HighlightCurrentLine = _editorHighlightCurrentLine, TabSize = ParseInt(_editorTabSizeText),
            InsertSpaces = _editorInsertSpaces, DetectIndent = _editorDetectIndent,
            AutoClosingBrackets = _editorAutoClosingBrackets, Folding = _editorFolding,
            Completion = _editorCompletion, GitGutter = _editorGitGutter,
        },
    };

    /// <summary>200ms以上かかる処理でのみ <see cref="IsBusy"/> を立てる（8.8章）。</summary>
    private async Task RunBusyAsync(Func<Task> action)
    {
        var operation = action();
        if (await Task.WhenAny(operation, Task.Delay(200)).ConfigureAwait(true) != operation)
        {
            IsBusy = true;
        }
        await operation.ConfigureAwait(true);
        IsBusy = false;
    }

    // ------------------------------------------------------------------
    // 課題2・3: 即時反映方式（新規追加分のみ）。
    // チェックボックス・ドロップダウンは変更した瞬間にフィールドとUIへ反映し
    // （SetEditableProperty）、300msデバウンス後に実際の保存・副作用（自動起動の
    // 登録／解除）を行う（ScheduleSave→CommitAndSaveAsync）。短時間に連続で
    // 変更されても、最後の値だけが保存される。
    // ------------------------------------------------------------------

    /// <summary>値を即座にフィールド・UIへ反映したうえで、保存をデバウンスする。</summary>
    private void SetEditableProperty<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
        => SetProperty(ref field, value, ScheduleSave, propertyName);

    /// <summary>
    /// 直前の保存待ちを打ち切り、300ms後に<see cref="CommitAndSaveAsync"/>を実行し直す。
    /// </summary>
    private void ScheduleSave()
    {
        _saveDebounceCts?.Cancel();
        var cts = new CancellationTokenSource();
        _saveDebounceCts = cts;
        _ = CommitAndSaveAsync(cts.Token);
    }

    /// <summary>
    /// デバウンス後に実際へ保存する。課題2・3で追加した項目のみを、直近に読み込み／
    /// 保存済みの <see cref="_settings"/> へ重ねて保存する（既存項目の未保存編集を
    /// 巻き込まない）。LaunchAtStartupが変化した場合は、実際のスタートアップフォルダへの
    /// 登録・解除も行い、失敗時は利用者へ通知する（黙って失敗させない）。
    /// </summary>
    private async Task CommitAndSaveAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(SaveDebounce, ct).ConfigureAwait(true);
        }
        catch (TaskCanceledException)
        {
            return; // 新しい変更で上書きされた。
        }
        if (ct.IsCancellationRequested) return;

        var previousLaunchAtStartup = _settings.LaunchAtStartup;
        var updated = _settings with { CloseBehavior = _closeBehavior, LaunchAtStartup = _launchAtStartup };

        await _settingsStore.SaveAsync(updated, ct).ConfigureAwait(true);
        if (ct.IsCancellationRequested) return;

        _settings = updated;
        _onLiveSettingsChanged?.Invoke(updated);

        if (_launchAtStartup != previousLaunchAtStartup)
        {
            await ApplyAutoStartAsync(_launchAtStartup).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// 実際のスタートアップフォルダへの登録・解除を行う（課題3）。ファイルI/Oは
    /// スレッドプールへ逃がし、UIスレッドをブロックしない。失敗時はチェックボックスを
    /// 元の状態へ戻したうえで理由をダイアログで伝える（黙って失敗させない）。
    /// </summary>
    private async Task ApplyAutoStartAsync(bool enable)
    {
        var platform = PlatformServices.Current.AutoStart;
        var result = await Task.Run(() => enable ? platform.Enable() : platform.Disable()).ConfigureAwait(true);
        if (result.Success) return;

        // 実際には登録・解除できていないので、チェックボックスの表示も元へ戻す。
        // このsetterも即時反映プロパティのため、この巻き戻し自体も改めて保存される
        // （settings.json側もLaunchAtStartupの実状態と一貫する）。
        LaunchAtStartup = !enable;
        await _dialogService.ShowMessageAsync("自動起動",
            (enable ? "自動起動の登録に失敗しました。" : "自動起動の解除に失敗しました。") + Environment.NewLine + result.ErrorMessage)
            .ConfigureAwait(true);
    }

    // 対応表は ThemeManager 側に集約する（起動時の反映と同じ規則を使うため）。
    private static AppTheme ParseTheme(string value) => ThemeManager.ParseTheme(value);

    // 解析に失敗した場合はあえてどの許容範囲にも収まらない値を返し、SettingsStore側の
    // 検証（NormalizeMin/NormalizeRange）による既定値フォールバックと通知に一本化する。
    private static int ParseInt(string text) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : int.MinValue;

    private static double ParseDouble(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.MinValue;

    private static List<string> ParseExtensions(string text) => text
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToList();

    /// <summary>選択肢1件（表示ラベルと設定値の組）。</summary>
    public sealed record ChoiceOption(string Label, string Value);
}
