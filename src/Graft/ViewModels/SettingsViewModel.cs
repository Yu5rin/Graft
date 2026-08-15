using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Graft.Core;
using Graft.Editor;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;
using Graft.Themes;
using Graft.Views;

namespace Graft.ViewModels;

/// <summary>
/// 14章の設定画面を担う。settings.json の全項目編集、json直接編集タブ、
/// 不正値の検証結果通知（<see cref="SettingsStore"/> が返す Issue の表示）、
/// エクスポート／インポート、テーマの即時反映（<see cref="Graft.Themes.ThemeManager"/> 経由）を行う。
///
/// 【即時反映方式（保存ボタンの廃止）】
/// 以前は「保存」ボタンを押すまでどの設定も反映されない方式だったが、テーマだけは
/// setterから<see cref="Graft.Themes.ThemeManager.SetTheme"/>を呼んで即時プレビューしていた。
/// この不統一が「保存せずに閉じるとテーマだけプレビューのまま残り、次に開くと巻き戻る」
/// 不具合の真因だったため、全項目を即時反映方式へ揃えた。ただし種別によって
/// 「値が確定した」とみなすタイミングが異なるため、確定タイミングの違いはXAML側の
/// バインディングトリガーだけで表現し、ViewModel側は「setterへ値が届いた＝確定」という
/// 単純なルール1本にまとめている。
///   - CheckBox・ComboBox: バインディングの既定（PropertyChanged）のまま。選択・切替の
///     瞬間にsetterへ届くので、それがそのまま「変更した瞬間に反映」になる。
///   - TextBox（数値・テキスト入力）: XAML側で<c>UpdateSourceTrigger=LostFocus</c>を明示し、
///     フォーカスを外すまでsetterを呼ばせない。既定のPropertyChangedのままだと「100」を
///     「50」に打ち替える途中の「10」や空文字が確定してしまうため。Enterキーでも確定でき
///     るよう<see cref="Graft.Views.TextBoxCommitBehavior"/>を添付する。
///   - JSON直接編集タブ（<see cref="JsonText"/>）: 唯一の例外として明示保存のまま
///     （<see cref="SaveJsonCommand"/>）。テキスト全体で1つの値であり、フォーカスが
///     外れた時点のテキストが有効なJSONとは限らない（括弧を打ち終える前など）ため、
///     「フォーカスを外したら確定」という他の入力欄と同じ規則を適用できない。
///
/// setterへ値が届くたび<see cref="ScheduleSave"/>で短いデバウンス（300ms）を挟んで保存する。
/// ドロップダウンを連続で切り替えたときに毎回ディスクへ書き込むのを避けるためで、
/// デバウンス中に新しい変更が来たら古い方は打ち切って合流させる。保存直前に
/// <see cref="SettingsStore.ValidateOnly"/>で検証し、1件でも問題があれば保存自体を行わない
/// （不正な値を黙って既定値へ差し替えて保存する＝見えている値と保存された値が食い違う事故も、
/// 不正なまま保存することも避けたいため）。
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    /// <summary>設定変更から実際の保存までの合流待ち時間。</summary>
    private static readonly TimeSpan SaveDebounceInterval = TimeSpan.FromMilliseconds(300);

    private readonly SettingsStore _settingsStore; private readonly IDialogService _dialogService;
    private readonly AppPaths _appPaths;
    private readonly IFontCatalog _fontCatalog;
    private Settings _settings = new();

    // デバウンス中の保存予約。ウィンドウを閉じる直前にFlushPendingSaveAsyncで
    // 待たずに確定させるため、予約の有無と取り消し用のCancellationTokenSourceを保持する。
    private CancellationTokenSource? _saveDebounceCts;
    private bool _hasPendingSave;

    // PopulateEditableFields実行中はtrueにする。読み込み・インポート・既定値への復元は
    // 「保存済みの値をそのまま画面へ映すだけ」の操作であり、ここで各プロパティのsetterが
    // 反応してScheduleSaveを呼ぶと、読み込んだ直後に無意味な保存が走ってしまう
    // （既定値復元は専用のResetToDefaultsAsyncが明示的に保存するため、なおさら不要）。
    private bool _isApplyingLoadedSettings;

    private string _selectedTheme = "system";
    private string _selectedTooltipDetail = "standard";
    private string _selectedApplyMode = "allOrNothing";
    private bool _showPreview; private bool _requireSummary;
    private string _hotkey = string.Empty;
    private string _selectedLogLevel = "info";
    private bool _clipboardWatchEnabled;
    private string _selectedClipboardAction = "notify";
    private bool _clipboardAutoParse = true;
    private bool _clipboardActivateOnDetect = true;
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
    private bool _sideBySide = true;
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
    private string _selectedIndentGuideMode = "foldable";

    // 検討書「フォント設定」。""は「未指定＝アプリ既定のフォントを使う」を表す
    // （settings.jsonのnullと相互変換する。PopulateEditorFields/BuildSettingsFromFields参照）。
    private string _selectedFontFamily = string.Empty;
    private string _selectedMonospaceFontFamily = string.Empty;
    private bool _exportSettingsOnly = true;
    private string _jsonText = string.Empty;
    private string? _jsonParseError;
    private bool _isBusy;
    private string? _statusMessage;

    // 課題2・3で追加した「閉じたときの動作」「PC起動時に自動で起動する」。設定画面全体が
    // 即時反映方式（SetEditableProperty/ScheduleSave/CommitAndSaveAsync）へ移行済みのため、
    // 既存の仕組みへそのまま乗せる。自動起動の登録・解除はCommitAndSaveAsync内で行う
    // （ApplyAutoStartAsync参照）。
    private string _closeBehavior = "exit";
    private bool _launchAtStartup;

    // 不具合修正: 「最小化でタスクトレイへ格納する」（既定オフ）。上の2項目と同じ即時反映方式。
    // StartupCoordinator側はwindow.PropertyChangedのたびに_settings（インスタンスフィールド）を
    // 直接読むため、ここで即時反映すれば再起動なしで反映される（クロージャへ値を焼き付けない
    // 作法。StartupCoordinator.csのコメント参照）。
    private bool _minimizeToTray;
    private readonly Action<Settings>? _onLiveSettingsChanged;

    /// <param name="appPaths">現在のデータ保存先。</param>
    /// <param name="dialogService">確認・通知ダイアログ。</param>
    /// <param name="ui">クリップボード等のUI機能。</param>
    /// <param name="onLiveSettingsChanged">設定変更の即時反映コールバック。</param>
    /// <param name="exeDirectory">
    /// 機能3: 実行ファイルと同じ階層（データ保存先の切り替え用ポインタファイル
    /// <see cref="DataDirectoryPointer"/> を置く場所）。省略時は<see cref="AppContext.BaseDirectory"/>。
    /// 本番の起動経路（<see cref="Views.StartupCoordinator"/>）は必ず実際のexeフォルダを渡す。
    /// 省略可能にしているのは、この画面の他の機能（データ保存先の移行）を使わない既存のテストの
    /// 呼び出しをすべて書き換えずに済ませるためで、それらのテストがこの既定値を実際に
    /// 参照することは無い（データ保存先の移行操作を行わない限り読まれない）。
    /// </param>
    /// <param name="fontCatalog">
    /// 検討書「フォント設定」。OSインストール済みフォントの列挙元。省略時は
    /// <see cref="SystemFontCatalog"/>（実際のAvalonia FontManager経由の列挙）を使う。
    /// テストからフェイクへ差し替えられるようにするための引数
    /// （<see cref="IFontCatalog"/>のクラスドキュメント参照）。
    /// </param>
    public SettingsViewModel(
        AppPaths appPaths, IDialogService dialogService, IUiServices ui,
        Action<Settings>? onLiveSettingsChanged = null, string? exeDirectory = null,
        IFontCatalog? fontCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(appPaths);
        ArgumentNullException.ThrowIfNull(ui);
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _appPaths = appPaths;
        _exeDirectory = exeDirectory ?? AppContext.BaseDirectory;
        _onLiveSettingsChanged = onLiveSettingsChanged;
        // 実際の列挙・等幅判定（FontManager経由のグリフ計測）はIFontCatalog側でLazy化されており、
        // ここで生成してもコンストラクタの時点では実行されない。設定画面のフォント欄が実際に
        // 開かれてItemsSourceが評価されるまで計算が走らないため、アプリ起動時間には影響しない
        // （FontFamilyOptions/MonospaceFontFamilyOptionsのコメント参照）。
        _fontCatalog = fontCatalog ?? new SystemFontCatalog();
        _settingsStore = new SettingsStore(appPaths);
        var projectStore = new ProjectStore(appPaths);

        Templates = new PromptTemplateViewModel(appPaths, projectStore, dialogService, _settings, ui);
        TokenStats = new TokenStatisticsViewModel(appPaths, projectStore);
        Hooks = new HookSettingsViewModel(projectStore, dialogService);
        ValidationIssues = new ObservableCollection<GraftIssue>();

        // SettingsWindow.axamlの警告表示は IsVisible="{Binding ValidationIssues,
        // Converter=...HasItems}" という、コレクション「への参照」を対象にした値バインディングで
        // ある。この形のバインディングはAvaloniaのINotifyPropertyChanged経由の再評価に乗るため、
        // ValidationIssuesプロパティ自体が別のインスタンスへ差し変わる（PropertyChangedが飛ぶ）
        // ときにしか再評価されない。しかしClear()/Add()はコレクションの中身だけを書き換え、
        // プロパティの参照自体は変えない（INotifyCollectionChangedで通知するのみ）ため、
        // このままでは警告欄が最初の（空の）評価のまま固まってしまい、値を確定するたびに
        // 検証結果が変わっても画面に警告が一切表示されない（実機で確認済みの不具合）。
        // 即時反映方式では「不正な値は保存しない」ことを利用者に気づかせるのがこの欄の役目そのもの
        // なので、CollectionChangedのたびにValidationIssues自体のPropertyChangedを代わりに
        // 発火させ、バインディングを強制的に再評価させる。
        ValidationIssues.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ValidationIssues));

        SaveJsonCommand = new AsyncRelayCommand(SaveJsonAsync, context: "設定JSONの保存");
        ExportCommand = new AsyncRelayCommand(ExportAsync, context: "設定のエクスポート");
        ImportCommand = new AsyncRelayCommand(ImportAsync, context: "設定のインポート");
        ResetToDefaultsCommand = new AsyncRelayCommand(ResetToDefaultsAsync, context: "設定を既定に戻す");

        // 機能3・機能2: データ保存先の切り替え、ログフォルダを開く・最新のログを表示する。
        // 実処理はSettingsViewModel.DataDirectory.cs（分割ファイル）にまとめている。
        MigrateDataDirectoryCommand = new AsyncRelayCommand(
            MigrateDataDirectoryAsync, () => !IsDataDirectoryMigrationPending, context: "データ保存先の移行");
        OpenLogsFolderCommand = new AsyncRelayCommand(OpenLogsFolderAsync, context: "ログフォルダを開く");
        ShowLatestLogCommand = new AsyncRelayCommand(ShowLatestLogAsync, context: "最新のログの表示");
    }

    public PromptTemplateViewModel Templates { get; }
    public TokenStatisticsViewModel TokenStats { get; }
    public HookSettingsViewModel Hooks { get; }
    public ObservableCollection<GraftIssue> ValidationIssues { get; }

    public AsyncRelayCommand SaveJsonCommand { get; }
    public AsyncRelayCommand ExportCommand { get; }
    public AsyncRelayCommand ImportCommand { get; }

    /// <summary>
    /// 「既定に戻す」。触りすぎて分からなくなったときの逃げ道として用意する。即時反映方式では
    /// 「保存しない」という選択肢が無いため、代わりにこのボタンが唯一の後戻り手段になる。
    /// 対象は設定（settings.json）のみで、プロジェクト定義（projects.json）・適用後フック・
    /// プロンプトテンプレート・リビジョン履歴は対象外（別ファイルであり、意図せず消えると
    /// 実害が大きいため）。破壊的操作なので<see cref="ResetToDefaultsAsync"/>内で必ず確認する。
    /// </summary>
    public AsyncRelayCommand ResetToDefaultsCommand { get; }

    /// <summary>
    /// テーマプリセット9種＋システム追従（検討書「テーマプリセット9種」）。移植元は
    /// Pane（github.com/Yu5rin/pane）の9プリセット。既定ライト/既定ダークは既存の
    /// Dark/Light（v2.0のWPF版由来の配色）をそのまま指し、残り7つが今回追加した
    /// プリセット。idの綴りは<see cref="Graft.Themes.ThemeManager.ParseTheme"/>と
    /// 揃える（対応表を二重に持たない）。既存の"dark"/"light"/"system"という値の意味は
    /// 変えていないため、古いsettings.jsonを持つ利用者もそのまま動く。
    /// </summary>
    public IReadOnlyList<ChoiceOption> ThemeOptions { get; } = new[]
    {
        new ChoiceOption("既定ライト", "light"), new ChoiceOption("既定ダーク", "dark"),
        new ChoiceOption("セピア", "sepia"), new ChoiceOption("GitHub風", "github"),
        new ChoiceOption("Solarized Light", "solarized-light"), new ChoiceOption("Solarized Dark", "solarized-dark"),
        new ChoiceOption("Nord", "nord"), new ChoiceOption("Dracula", "dracula"), new ChoiceOption("Night", "night"),
        new ChoiceOption("システム追従", "system"),
    };

    /// <summary>
    /// 「操作の説明」（ツールチップ）の表示レベル選択肢。テーマのすぐ下に置く（利用者からの
    /// 要望）。「表示しない」「最低限（現在の値だけ）」「標準の説明（既定）」「くわしい説明」の
    /// 4段階（<see cref="HelpTip"/>）。「最低限」は検討書「ツールチップの4段階化」で追加した。
    /// </summary>
    public IReadOnlyList<ChoiceOption> TooltipDetailOptions { get; } = new[]
    {
        new ChoiceOption("表示しない", "off"), new ChoiceOption("最低限", "minimal"),
        new ChoiceOption("標準の説明", "standard"), new ChoiceOption("くわしい説明", "detailed"),
    };

    /// <summary>
    /// 検討書「インデントガイド（縦線）」の3モード。既定は「折りたたみできる範囲のみ」。
    /// 値のidは<see cref="Graft.Editor.IndentGuideModeParser"/>と揃える（対応表を二重に持たない）。
    /// </summary>
    public IReadOnlyList<ChoiceOption> IndentGuideModeOptions { get; } = new[]
    {
        new ChoiceOption("表示しない", "none"),
        new ChoiceOption("折りたたみできる範囲のみ", "foldable"),
        new ChoiceOption("すべてのインデント", "all"),
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

    /// <summary>
    /// 9件目の不具合修正: この環境でクリップボード監視に対応しているか。非対応（Wayland等）
    /// ならチェックボックスを無効化する。15章「利用不可の環境では設定値にかかわらず無効として
    /// 扱い、設定画面にその理由を表示する」に従い、Tray/AutoStartと同じ扱いに揃える。
    /// </summary>
    public bool IsClipboardWatchSupported { get; } = PlatformServices.Current.Clipboard.IsSupported;

    /// <summary>クリップボード監視が使えない場合に画面へ表示する理由（利用可能なら null）。</summary>
    public string? ClipboardWatchUnsupportedReason => PlatformServices.Current.Clipboard.UnsupportedReason;

    /// <summary>
    /// テーマ。ComboBoxの選択が変わった瞬間にsetterへ届き、<see cref="ThemeManager"/> 経由で
    /// 即時プレビュー反映しつつ、他の項目と同じ経路で保存もスケジュールする。
    /// </summary>
    public string SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (!SetEditableProperty(ref _selectedTheme, value)) return;
            ThemeManager.SetTheme(ParseTheme(value));
        }
    }

    /// <summary>
    /// 「操作の説明」の表示レベル。テーマと同じく、ComboBoxの選択が変わった瞬間に
    /// <see cref="HelpTip.SetLevel"/>経由で即時反映する（開いている全ウィンドウのツールチップが
    /// 再起動なしで切り替わる）。保存の予約自体はSetEditablePropertyへ委ねる。
    /// </summary>
    public string SelectedTooltipDetail
    {
        get => _selectedTooltipDetail;
        set
        {
            if (!SetEditableProperty(ref _selectedTooltipDetail, value)) return;
            HelpTip.SetLevel(HelpTip.ParseLevel(value));
        }
    }

    public string SelectedApplyMode { get => _selectedApplyMode; set => SetEditableProperty(ref _selectedApplyMode, value); }
    public bool ShowPreview { get => _showPreview; set => SetEditableProperty(ref _showPreview, value); }
    public bool RequireSummary { get => _requireSummary; set => SetEditableProperty(ref _requireSummary, value); }
    public string Hotkey { get => _hotkey; set => SetEditableProperty(ref _hotkey, value); }
    public string SelectedLogLevel { get => _selectedLogLevel; set => SetEditableProperty(ref _selectedLogLevel, value); }
    public bool ClipboardWatchEnabled { get => _clipboardWatchEnabled; set => SetEditableProperty(ref _clipboardWatchEnabled, value); }
    public string SelectedClipboardAction { get => _selectedClipboardAction; set => SetEditableProperty(ref _selectedClipboardAction, value); }
    public bool ClipboardAutoParse { get => _clipboardAutoParse; set => SetEditableProperty(ref _clipboardAutoParse, value); }
    public bool ClipboardActivateOnDetect { get => _clipboardActivateOnDetect; set => SetEditableProperty(ref _clipboardActivateOnDetect, value); }
    public string MaxRevisionsText { get => _maxRevisionsText; set => SetEditableProperty(ref _maxRevisionsText, value); }
    public string MaxTotalMBText { get => _maxTotalMbText; set => SetEditableProperty(ref _maxTotalMbText, value); }
    public bool UseRecycleBin { get => _useRecycleBin; set => SetEditableProperty(ref _useRecycleBin, value); }
    public string SimilarityThresholdText { get => _similarityThresholdText; set => SetEditableProperty(ref _similarityThresholdText, value); }
    public bool AllowSimilarityMatch { get => _allowSimilarityMatch; set => SetEditableProperty(ref _allowSimilarityMatch, value); }
    public string RangeWarningLinesText { get => _rangeWarningLinesText; set => SetEditableProperty(ref _rangeWarningLinesText, value); }
    public string NewFileEncoding { get => _newFileEncoding; set => SetEditableProperty(ref _newFileEncoding, value); }
    public bool NewFileBom { get => _newFileBom; set => SetEditableProperty(ref _newFileBom, value); }
    public bool SyntaxEnabled { get => _syntaxEnabled; set => SetEditableProperty(ref _syntaxEnabled, value); }
    public bool ShowLineNumbers { get => _showLineNumbers; set => SetEditableProperty(ref _showLineNumbers, value); }
    public string ContextLinesText { get => _contextLinesText; set => SetEditableProperty(ref _contextLinesText, value); }
    public bool WordWrap { get => _wordWrap; set => SetEditableProperty(ref _wordWrap, value); }
    public bool ShowWhitespace { get => _showWhitespace; set => SetEditableProperty(ref _showWhitespace, value); }
    /// <summary>機能改善: diff表示を並列（左右）にするか統合（上下）にするか。既定はtrue（並列）。</summary>
    public bool SideBySide { get => _sideBySide; set => SetEditableProperty(ref _sideBySide, value); }
    public string AllowedExtensionsText { get => _allowedExtensionsText; set => SetEditableProperty(ref _allowedExtensionsText, value); }
    public string MaxFileSizeMBText { get => _maxFileSizeMbText; set => SetEditableProperty(ref _maxFileSizeMbText, value); }
    public string MaxFilesPerRevisionText { get => _maxFilesPerRevisionText; set => SetEditableProperty(ref _maxFilesPerRevisionText, value); }
    public bool RespectGitignore { get => _respectGitignore; set => SetEditableProperty(ref _respectGitignore, value); }
    public string TokenRatioText { get => _tokenRatioText; set => SetEditableProperty(ref _tokenRatioText, value); }
    public string TokenWarnThresholdText { get => _tokenWarnThresholdText; set => SetEditableProperty(ref _tokenWarnThresholdText, value); }
    public string HooksTimeoutSecText { get => _hooksTimeoutSecText; set => SetEditableProperty(ref _hooksTimeoutSecText, value); }
    public bool AutoCommit { get => _autoCommit; set => SetEditableProperty(ref _autoCommit, value); }

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

    /// <summary>
    /// 不具合修正: 最小化でタスクトレイへ格納するか（既定オフ）。チェックボックスのため、
    /// 変更した瞬間に即時反映する。実際の最小化ハンドラ側（StartupCoordinator.StartAsync）は
    /// このプロパティの保存先である<c>_settings</c>を毎回読み直すため、ここでの保存が
    /// そのまま次回の最小化から反映される。
    /// </summary>
    public bool MinimizeToTray { get => _minimizeToTray; set => SetEditableProperty(ref _minimizeToTray, value); }

    /// <summary>15章・4章 エディタ設定（12項目）。設定画面の「エディタ」タブが編集する。</summary>
    public string EditorFontSizeText { get => _editorFontSizeText; set => SetEditableProperty(ref _editorFontSizeText, value); }
    public bool EditorWordWrap { get => _editorWordWrap; set => SetEditableProperty(ref _editorWordWrap, value); }
    public bool EditorShowWhitespace { get => _editorShowWhitespace; set => SetEditableProperty(ref _editorShowWhitespace, value); }
    public bool EditorShowLineNumbers { get => _editorShowLineNumbers; set => SetEditableProperty(ref _editorShowLineNumbers, value); }
    public bool EditorHighlightCurrentLine { get => _editorHighlightCurrentLine; set => SetEditableProperty(ref _editorHighlightCurrentLine, value); }
    public string EditorTabSizeText { get => _editorTabSizeText; set => SetEditableProperty(ref _editorTabSizeText, value); }
    public bool EditorInsertSpaces { get => _editorInsertSpaces; set => SetEditableProperty(ref _editorInsertSpaces, value); }
    public bool EditorDetectIndent { get => _editorDetectIndent; set => SetEditableProperty(ref _editorDetectIndent, value); }
    public bool EditorAutoClosingBrackets { get => _editorAutoClosingBrackets; set => SetEditableProperty(ref _editorAutoClosingBrackets, value); }
    public bool EditorFolding { get => _editorFolding; set => SetEditableProperty(ref _editorFolding, value); }
    public bool EditorCompletion { get => _editorCompletion; set => SetEditableProperty(ref _editorCompletion, value); }
    public bool EditorGitGutter { get => _editorGitGutter; set => SetEditableProperty(ref _editorGitGutter, value); }

    /// <summary>
    /// 検討書「インデントガイド（縦線）」。ComboBoxのため、選択が変わった瞬間にsetterへ届く
    /// （<see cref="SetEditableProperty{T}"/>）。実際にエディタへ即時反映するのは
    /// <c>EditorPaneViewModel.IndentGuideMode</c>を経由した<c>Editor.UpdateSettings</c>の
    /// 呼び出し（StartupCoordinator側の保存完了コールバック）で、<see cref="SelectedTheme"/>と
    /// 違って本クラス自身が直接エディタへ触れることはしない（エディタの実体はView層
    /// （EditorPane）にあり、SettingsViewModelから直接参照できないため。他のeditor.*設定と
    /// 同じ経路）。
    /// </summary>
    public string SelectedIndentGuideMode
    {
        get => _selectedIndentGuideMode;
        set => SetEditableProperty(ref _selectedIndentGuideMode, value);
    }

    /// <summary>
    /// 検討書「フォント設定」。本文フォント。ComboBoxの選択が変わった瞬間にsetterへ届き、
    /// <see cref="AppFontManager"/>経由で即時プレビュー反映しつつ、他の項目と同じ経路で
    /// 保存もスケジュールする（<see cref="SelectedTheme"/>と同じ作法）。""は
    /// 「未指定＝アプリ既定のフォントを使う」（<see cref="FontFamilyOptions"/>の
    /// 先頭「(既定)」に対応）。
    /// </summary>
    public string SelectedFontFamily
    {
        get => _selectedFontFamily;
        set
        {
            if (!SetEditableProperty(ref _selectedFontFamily, value)) return;
            AppFontManager.SetBodyFontFamily(value);
        }
    }

    /// <summary>検討書「フォント設定」。等幅（コード用）フォント。<see cref="SelectedFontFamily"/>と同じ作法。</summary>
    public string SelectedMonospaceFontFamily
    {
        get => _selectedMonospaceFontFamily;
        set
        {
            if (!SetEditableProperty(ref _selectedMonospaceFontFamily, value)) return;
            AppFontManager.SetCodeFontFamily(value);
        }
    }

    /// <summary>
    /// 本文フォントの選択肢（先頭に「未指定＝既定を使う」を表す空文字の項目を1つ持つ）。
    /// <see cref="IFontCatalog.AllFamilyNames"/>への実際のアクセスはここで初めて発生する
    /// （Lazy化されているため、設定画面のフォント欄を開くまでフォント列挙・等幅判定の
    /// コストがかからない。コンストラクタのコメント参照）。
    /// </summary>
    public IReadOnlyList<ChoiceOption> FontFamilyOptions => BuildFontOptions(_fontCatalog.AllFamilyNames);

    /// <summary>等幅フォントの選択肢。<see cref="FontFamilyOptions"/>と同じ形。</summary>
    public IReadOnlyList<ChoiceOption> MonospaceFontFamilyOptions => BuildFontOptions(_fontCatalog.MonospaceFamilyNames);

    /// <summary>
    /// 検討書「フォントの列挙に失敗してもアプリが落ちないこと。失敗時は…設定欄はテキスト入力へ
    /// フォールバックする」。列挙が空（未対応環境・列挙失敗のいずれか）ならfalseになり、
    /// 画面側はComboBoxの代わりにテキスト入力を表示する（GeneralSettingsView.axaml参照）。
    /// </summary>
    public bool HasFontFamilyOptions => _fontCatalog.AllFamilyNames.Count > 0;

    /// <summary><see cref="HasFontFamilyOptions"/>と同じ理由。等幅フォント側。</summary>
    public bool HasMonospaceFontFamilyOptions => _fontCatalog.MonospaceFamilyNames.Count > 0;

    private static IReadOnlyList<ChoiceOption> BuildFontOptions(IReadOnlyList<string> familyNames)
    {
        var options = new List<ChoiceOption>(familyNames.Count + 1) { new("(既定)", string.Empty) };
        options.AddRange(familyNames.Select(name => new ChoiceOption(name, name)));
        return options;
    }

    /// <summary>
    /// エクスポート／インポートの範囲。trueなら設定のみ（プロジェクト定義を除外）。
    /// settings.jsonの項目ではなく画面だけのオプションなので、他の項目と違い変更しても
    /// 自動保存はスケジュールしない（<see cref="SetProperty{T}(ref T, T, string?)"/>のまま）。
    /// </summary>
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
    /// ウィンドウを閉じる直前に呼ぶ。即時反映方式では基本的に「閉じた時点で既にディスクへ
    /// 保存済み」だが、直前の変更がまだ<see cref="SaveDebounceInterval"/>のデバウンス待ちに
    /// 積まれている可能性があるため、待たずに今すぐ確定させてから閉じる
    /// （デバウンスは「連続変更の合流」が目的であり、ウィンドウを閉じる操作自体を
    /// 遅らせてよい理由にはならない）。以前あった「保存する／破棄して閉じる／キャンセル」の
    /// 確認ダイアログは、即時反映方式では「未保存の変更」という状態自体が存在しなくなった
    /// ため撤去した。
    /// </summary>
    public async Task FlushPendingSaveAsync()
    {
        if (!_hasPendingSave) return;

        _saveDebounceCts?.Cancel();
        await CommitPendingSaveAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// 機能改善: エディタ・差分表示のCtrl+マウスホイールでのフォントサイズ変更を、設定画面の
    /// 「フォントサイズ」欄と全く同じ経路（<see cref="EditorFontSizeText"/>のsetter→
    /// <see cref="ScheduleSave"/>の300msデバウンス→検証→保存→onLiveSettingsChanged）に乗せて
    /// 永続化する。保存ロジックを別途持つ（並行実装する）と、設定画面での変更と食い違う
    /// タイミング・検証規則で保存してしまう恐れがあるため、既存のテキスト欄用setterを
    /// そのまま呼ぶだけに留める（StartupCoordinator.ApplyLiveSettingsChangeが常駐の
    /// SettingsViewModelインスタンスに対してこれを呼ぶ。ShellViewModel.
    /// EditorFontSizeChangeRequested参照）。
    /// </summary>
    public void SetEditorFontSizeLive(double fontSize) => EditorFontSizeText = fontSize.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// 機能改善（差分の左右並列表示）: diff表示ヘッダーでの並列／統合表示の切り替えを、
    /// 設定画面の「diff表示」チェックボックスと全く同じ経路（<see cref="SideBySide"/>のsetter→
    /// ScheduleSaveの300msデバウンス→検証→保存→onLiveSettingsChanged）に乗せて永続化する
    /// （<see cref="SetEditorFontSizeLive"/>と同じ考え方・同じ理由。ShellViewModel.
    /// DiffSideBySideChangeRequested参照）。
    /// </summary>
    public void SetSideBySideLive(bool value) => SideBySide = value;

    private async Task LoadAsync(CancellationToken ct)
    {
        await RunBusyAsync(async () =>
        {
            var result = await _settingsStore.LoadAsync(ct).ConfigureAwait(true);
            await ApplyLoadedResultAsync(result).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    /// <summary>
    /// CheckBox/ComboBoxの変更・TextBoxの確定（LostFocus/Enter）から共通で辿り着く保存予約。
    /// 短いデバウンス（<see cref="SaveDebounceInterval"/>）を挟み、その間に新しい変更が来たら
    /// 古い予約を打ち切って合流させる。ドロップダウンを連続で切り替えたときや、複数の項目を
    /// 立て続けに変更したときに毎回ディスクへ書き込むのを避けるため（ファイルI/Oはキー入力や
    /// クリックに比べて重く、UIの応答性に影響しうる）。
    /// </summary>
    private void ScheduleSave()
    {
        _hasPendingSave = true;
        _saveDebounceCts?.Cancel();
        var cts = new CancellationTokenSource();
        _saveDebounceCts = cts;
        _ = RunDebouncedSaveAsync(cts.Token);
    }

    private async Task RunDebouncedSaveAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(SaveDebounceInterval, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // より新しい変更にすぐ合流させるための打ち切りであり、異常ではない。
            // この回の保存は行わず、後から積まれた予約（またはFlushPendingSaveAsync）に委ねる。
            return;
        }

        await CommitPendingSaveAsync().ConfigureAwait(true);
    }

    private async Task CommitPendingSaveAsync()
    {
        _hasPendingSave = false;

        // ここはAsyncRelayCommand経由の実行と違い、プロパティのsetterやタイマーから発火する
        // fire-and-forgetなタスクである。捕まえ損ねた例外は観測されない例外として静かに
        // 失われてしまう（「保存に失敗しても黙って失敗しないこと」という要件に反する）ため、
        // 必ずSafeHandler経由で捕捉し、GraftIssueと同じ通知経路（ダイアログ＋ログ）へ乗せる。
        await SafeHandler.RunAsync("設定の保存", CommitAndSaveAsync).ConfigureAwait(true);
    }

    /// <summary>
    /// 保留中の自動保存があれば、今すぐ確定させずに取り消す。settings.json全体を明示的に
    /// 上書きする操作（JSON直接編集タブの保存・インポート・既定値への復元）の直前に呼ぶ。
    /// これらは「今の入力欄の値」より新しい・別の内容でファイル全体を置き換えるので、
    /// 古いフィールド値に基づく自動保存予約が後から発火して上書きしてしまう競合を防ぐ。
    /// </summary>
    private void CancelPendingAutoSave()
    {
        _saveDebounceCts?.Cancel();
        _hasPendingSave = false;
    }

    /// <summary>
    /// 現在の入力欄から組み立てた設定を検証し、問題が無ければ保存する。
    ///
    /// <see cref="SettingsStore.ValidateOnly"/>が1件でも問題を返した場合は保存しない。
    /// <see cref="SettingsStore.LoadAsync"/>が使う検証（不正値を既定値へ差し替えて延命する）を
    /// そのまま保存前にも適用してしまうと、「画面に入力されている値」と「実際にディスクへ
    /// 保存された値」が黙って食い違う事故になる（不正な値を黙って捨てるのも、不正なまま
    /// 保存するのも避けたい、という要件のため）。直すまで保存を保留し、既存の
    /// <see cref="ValidationIssues"/>表示の仕組みでどこが悪いかを伝える。
    ///
    /// 【実質的に変化が無ければ書き込みを省略する】
    /// AvaloniaのComboBoxはTwoWayバインディングを使うと、コントロール自身のテンプレート適用・
    /// ItemsSourceの解決といった内部初期化の過程で、ユーザー操作とは無関係にSelectedValueを
    /// 一時的にViewModelへ書き戻すことがある（実機で確認済み: 設定画面を開いただけで
    /// テーマが一度別の値を経由して結局同じ値へ戻り、その結果<see cref="ScheduleSave"/>が
    /// 呼ばれてしまう）。この手の「setterには届いたが実質的には何も変わっていない」書き戻しで
    /// 毎回ディスクへ書き込むと、開いただけで（何も変更していないのに）settings.jsonの
    /// 更新日時が変わってしまう。ディスクへ書き込む直前に最後に確定した内容
    /// （<see cref="_settings"/>）と比較し、一致するなら書き込み自体を省略する。
    ///
    /// 【課題2・3: 実行中プロセスへの反映とOS側の副作用】
    /// settings.jsonへの保存だけでは、実行中のShellWindowや実際のスタートアップフォルダには
    /// 反映されない。保存が成功した直後に<see cref="_onLiveSettingsChanged"/>
    /// （StartupCoordinatorが渡すコールバック。ShellWindow.CloseBehaviorを書き換える）を呼び、
    /// LaunchAtStartupが変化していれば<see cref="ApplyAutoStartAsync"/>で実際の登録・解除も行う
    /// （<see cref="_settings"/>を上書きする前のLaunchAtStartupと比較する必要があるため、
    /// 保存前に控えておく）。
    /// </summary>
    private async Task CommitAndSaveAsync()
    {
        var candidate = BuildSettingsFromFields();
        if (SettingsContentEquals(candidate, _settings))
        {
            return;
        }

        var validated = SettingsStore.ValidateOnly(candidate);

        ValidationIssues.Clear();
        if (validated.Issues.Count > 0)
        {
            foreach (var issue in validated.Issues)
            {
                ValidationIssues.Add(issue);
            }
            StatusMessage = "入力値に誤りがあるため、保存していません。";
            return;
        }

        var previousLaunchAtStartup = _settings.LaunchAtStartup;

        await RunBusyAsync(async () =>
        {
            await _settingsStore.SaveAsync(candidate).ConfigureAwait(true);

            // candidateは検証済みでそのままディスクの内容と一致するため、改めて
            // LoadAsyncで読み直す必要はない。そのままGraftResult.Okで包み、明示保存
            // （SaveJsonAsync）と同じApplyLoadedResultAsyncへ渡して、_settings・
            // ValidationIssues・JsonText（JSONタブへの反映）・Templatesへの反映を1本化する。
            await ApplyLoadedResultAsync(GraftResult<Settings>.Ok(candidate)).ConfigureAwait(true);
        }).ConfigureAwait(true);
        StatusMessage = "設定を保存しました。";

        // 課題2: 実行中のShellWindow.CloseBehaviorへその場で反映する。
        _onLiveSettingsChanged?.Invoke(candidate);

        // 課題3: LaunchAtStartupが変化していれば、実際のスタートアップフォルダへ反映する。
        if (candidate.LaunchAtStartup != previousLaunchAtStartup)
        {
            await ApplyAutoStartAsync(candidate.LaunchAtStartup).ConfigureAwait(true);
        }
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

        // JSONタブは唯一の明示保存の例外だが、他タブでの変更が積んだ自動保存の予約とは
        // 同じsettings.jsonを取り合う関係にある。ここで先に打ち切っておかないと、
        // 数百ms後に発火する古い自動保存が（このJSON保存より前の）フィールドの値で
        // 上書きし、いま保存したJSONの内容を意図せず消してしまう恐れがある。
        CancelPendingAutoSave();

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

        // 明示保存（SaveJsonAsync）と同じ理由: これから読み込む内容全体で上書きするので、
        // 古いフィールド値に基づく自動保存予約が後から発火して上書きしてしまう競合を防ぐ。
        CancelPendingAutoSave();

        var scope = _exportSettingsOnly ? SettingsExportScope.SettingsOnly : SettingsExportScope.IncludeProjects;
        var result = await _settingsStore.ImportAsync(folder, scope).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            await ApplyLoadedResultAsync(result).ConfigureAwait(true);
        }

        var message = result.IsSuccess ? "設定を取り込みました。" : "インポートに失敗しました。";
        await _dialogService.ShowMessageAsync("インポート", message).ConfigureAwait(true);
    }

    /// <summary>
    /// 「既定に戻す」の実処理。破壊的操作なので必ず確認し、確認文言には対象が設定
    /// （settings.json）のみでプロジェクト定義・適用後フック・プロンプトテンプレート・
    /// リビジョン履歴には触れないことを明示する。
    /// </summary>
    private async Task ResetToDefaultsAsync()
    {
        var confirmed = await _dialogService.ConfirmAsync(
            "既定値に戻す",
            "すべての設定を既定値に戻します。よろしいですか？" +
            "\n対象は設定（テーマ・マッチング・安全機構などsettings.jsonの内容）のみです。" +
            "プロジェクト定義・適用後フック・プロンプトテンプレート・リビジョン履歴は変更されません。")
            .ConfigureAwait(true);
        if (!confirmed) return;

        // インポート・JSON保存と同じ理由: これから書く既定値全体で上書きするので、
        // 直前の入力に基づく自動保存予約が後から発火して上書きしてしまう競合を防ぐ。
        CancelPendingAutoSave();

        await RunBusyAsync(async () =>
        {
            var defaults = new Settings();
            await _settingsStore.SaveAsync(defaults).ConfigureAwait(true);
            var result = await _settingsStore.LoadAsync().ConfigureAwait(true);
            await ApplyLoadedResultAsync(result).ConfigureAwait(true);
        }).ConfigureAwait(true);
        StatusMessage = "設定を既定値に戻しました。";
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

    /// <summary>
    /// 読み込み・インポート・既定値への復元のいずれからも呼ばれる、保存済みの値を画面へ
    /// 映すだけの処理。<see cref="_isApplyingLoadedSettings"/>を立てている間は各プロパティの
    /// setterが自動保存をスケジュールしない（読み込んだ直後に無意味な保存が走るのを防ぐ）。
    /// なお<see cref="SelectedTheme"/>のsetterが行う<see cref="ThemeManager.SetTheme"/>呼び出し
    /// 自体はこのフラグに関わらず常に実行されるため、テーマのプレビュー反映はここでも正しく働く。
    /// </summary>
    private void PopulateEditableFields(Settings s)
    {
        _isApplyingLoadedSettings = true;
        try
        {
            PopulateEditableFieldsCore(s);
        }
        finally
        {
            _isApplyingLoadedSettings = false;
        }
    }

    private void PopulateEditableFieldsCore(Settings s)
    {
        SelectedTheme = s.Theme; SelectedTooltipDetail = s.TooltipDetail; SelectedApplyMode = s.ApplyMode; ShowPreview = s.ShowPreview;
        RequireSummary = s.RequireSummary; Hotkey = s.Hotkey; SelectedLogLevel = s.LogLevel;
        ClipboardWatchEnabled = s.ClipboardWatch.Enabled; SelectedClipboardAction = s.ClipboardWatch.Action;
        ClipboardAutoParse = s.ClipboardWatch.AutoParse; ClipboardActivateOnDetect = s.ClipboardWatch.ActivateOnDetect;
        MaxRevisionsText = s.Backup.MaxRevisions.ToString(CultureInfo.InvariantCulture);
        MaxTotalMBText = s.Backup.MaxTotalMB.ToString(CultureInfo.InvariantCulture); UseRecycleBin = s.Backup.UseRecycleBin;
        SimilarityThresholdText = s.Matching.SimilarityThreshold.ToString(CultureInfo.InvariantCulture);
        AllowSimilarityMatch = s.Matching.AllowSimilarityMatch;
        RangeWarningLinesText = s.Matching.RangeWarningLines.ToString(CultureInfo.InvariantCulture);
        NewFileEncoding = s.Encoding.NewFileEncoding; NewFileBom = s.Encoding.NewFileBom;
        SyntaxEnabled = s.Syntax.Enabled; ShowLineNumbers = s.Syntax.ShowLineNumbers;
        ContextLinesText = s.Diff.ContextLines.ToString(CultureInfo.InvariantCulture);
        WordWrap = s.Diff.WordWrap; ShowWhitespace = s.Diff.ShowWhitespace; SideBySide = s.Diff.SideBySide;
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
        SetProperty(ref _minimizeToTray, s.MinimizeToTray, nameof(MinimizeToTray));
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
        SelectedIndentGuideMode = e.IndentGuideMode;
        SelectedFontFamily = e.FontFamily ?? string.Empty;
        SelectedMonospaceFontFamily = e.MonospaceFontFamily ?? string.Empty;
    }

    private Settings BuildSettingsFromFields() => new()
    {
        Theme = _selectedTheme,
        TooltipDetail = _selectedTooltipDetail,
        ApplyMode = _selectedApplyMode,
        ShowPreview = _showPreview,
        RequireSummary = _requireSummary,
        Hotkey = _hotkey,
        LogLevel = _selectedLogLevel,
        CloseBehavior = _closeBehavior,
        LaunchAtStartup = _launchAtStartup,
        MinimizeToTray = _minimizeToTray,
        ClipboardWatch = new ClipboardWatchSettings
        {
            Enabled = _clipboardWatchEnabled, Action = _selectedClipboardAction, AutoParse = _clipboardAutoParse,
            ActivateOnDetect = _clipboardActivateOnDetect,
        },
        Backup = new BackupSettings { MaxRevisions = ParseInt(_maxRevisionsText), MaxTotalMB = ParseInt(_maxTotalMbText), UseRecycleBin = _useRecycleBin },
        Matching = new MatchingSettings
        {
            SimilarityThreshold = ParseDouble(_similarityThresholdText),
            AllowSimilarityMatch = _allowSimilarityMatch,
            RangeWarningLines = ParseInt(_rangeWarningLinesText),
        },
        Encoding = new EncodingSettings { NewFileEncoding = _newFileEncoding, NewFileBom = _newFileBom },
        Syntax = new SyntaxSettings { Enabled = _syntaxEnabled, ShowLineNumbers = _showLineNumbers },
        Diff = new DiffSettings { ContextLines = ParseInt(_contextLinesText), WordWrap = _wordWrap, ShowWhitespace = _showWhitespace, SideBySide = _sideBySide },
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
            IndentGuideMode = _selectedIndentGuideMode,
            FontFamily = string.IsNullOrWhiteSpace(_selectedFontFamily) ? null : _selectedFontFamily,
            MonospaceFontFamily = string.IsNullOrWhiteSpace(_selectedMonospaceFontFamily) ? null : _selectedMonospaceFontFamily,
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
    // 課題3: 自動起動（スタートアップフォルダ）の実際の登録・解除。
    // LaunchAtStartupの保存自体は既存のSetEditableProperty/ScheduleSave/CommitAndSaveAsync
    // （既存項目と共通の即時反映インフラ）に乗せ、OS側への反映だけをここで担う。
    // ------------------------------------------------------------------

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

    // 解析に失敗した場合はあえてどの許容範囲にも収まらない値を返す。SettingsStore側の
    // 検証（NormalizeMin/NormalizeRange）がこれを「範囲外」として検出することで、
    // 数字として読めない入力（空文字・記号など）も他の不正値と同じ1つの経路
    // （CommitAndSaveAsync内のValidateOnly）で「保存しない＋ValidationIssuesへ表示」に
    // 一本化できる。かつてのLoadAsync専用だった頃は「既定値へのフォールバック」の
    // トリガーだったが、即時反映方式では「保存を保留する」トリガーとして使う。
    private static int ParseInt(string text) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : int.MinValue;

    private static double ParseDouble(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.MinValue;

    private static List<string> ParseExtensions(string text) => text
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToList();

    /// <summary>
    /// 2つのSettingsが内容として同じかどうかを比較する。<see cref="Settings"/>はrecordだが
    /// <see cref="SafetySettings.AllowedExtensions"/>のList&lt;string&gt;など既定の構造的等価性が
    /// 効かないフィールドを含むため、record同士の==ではなく、JsonTextと同じ手段
    /// （JSONへ直列化した文字列）で比較する。
    /// </summary>
    private static bool SettingsContentEquals(Settings a, Settings b)
        => JsonSerializer.Serialize(a, JsonFileStore.DefaultOptions)
            == JsonSerializer.Serialize(b, JsonFileStore.DefaultOptions);

    /// <summary>
    /// 即時反映方式の対象になっている入力欄（CheckBox/ComboBox/TextBox）が共通で使う
    /// フィールドセッター。値が実際に変わった場合のみ<see cref="ScheduleSave"/>で保存を
    /// 予約する。種別ごとの確定タイミングの違い（変更した瞬間か、フォーカスを外した瞬間か）
    /// はXAML側のバインディングトリガーだけが担い、ここでは「setterに値が届いた＝確定」
    /// という単純な規則1本に統一している。読み込み・インポート・既定値への復元の最中
    /// （<see cref="_isApplyingLoadedSettings"/>）は保存を予約しない。
    /// </summary>
    private bool SetEditableProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (!SetProperty(ref field, value, propertyName)) return false;
        if (!_isApplyingLoadedSettings) ScheduleSave();
        return true;
    }

    /// <summary>選択肢1件（表示ラベルと設定値の組）。</summary>
    public sealed record ChoiceOption(string Label, string Value);
}
