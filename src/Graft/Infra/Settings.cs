namespace Graft.Infra;

/// <summary>
/// アプリ全体設定（settings.json）のルートモデル。14章のJSON構造・既定値と1:1で対応する。
/// System.Text.Json によりcamelCaseのキー名へ自動変換される
/// （<see cref="JsonFileStore.DefaultOptions"/> 参照）。
/// </summary>
public sealed record Settings
{
    /// <summary>テーマ。"dark" / "light" / "system" のいずれか。</summary>
    public string Theme { get; init; } = "system";

    /// <summary>適用モード。"allOrNothing" / "partial" のいずれか。</summary>
    public string ApplyMode { get; init; } = "allOrNothing";

    /// <summary>適用前にプレビューを表示するか（8.7章）。</summary>
    public bool ShowPreview { get; init; } = true;

    /// <summary>パッチメタデータの summary を必須にするか（4.2章）。</summary>
    public bool RequireSummary { get; init; } = true;

    /// <summary>クリップボード監視設定（9章）。</summary>
    public ClipboardWatchSettings ClipboardWatch { get; init; } = new();

    /// <summary>グローバルホットキー（8.10章）。</summary>
    public string Hotkey { get; init; } = "Ctrl+Alt+V";

    /// <summary>バックアップ・世代管理設定（7.4章）。</summary>
    public BackupSettings Backup { get; init; } = new();

    /// <summary>マッチングエンジン設定（5章）。</summary>
    public MatchingSettings Matching { get; init; } = new();

    /// <summary>新規ファイル作成時のエンコーディング設定（6.4章）。</summary>
    public EncodingSettings Encoding { get; init; } = new();

    /// <summary>シンタックスハイライト設定（8.6章）。</summary>
    public SyntaxSettings Syntax { get; init; } = new();

    /// <summary>diff表示設定（8.13章）。</summary>
    public DiffSettings Diff { get; init; } = new();

    /// <summary>安全機構設定（13章）。</summary>
    public SafetySettings Safety { get; init; } = new();

    /// <summary>コンテキスト収集設定（10章）。</summary>
    public ContextSettings Context { get; init; } = new();

    /// <summary>適用後フック設定（6.5章）。</summary>
    public HookSettings Hooks { get; init; } = new();

    /// <summary>Git連携設定（7.5章）。</summary>
    public GitSettings Git { get; init; } = new();

    /// <summary>コードエディタ設定（v2.0 仕様書15章）。</summary>
    public EditorSettings Editor { get; init; } = new();

    /// <summary>ログ出力レベル。trace/debug/info/warn/error のいずれか（15章）。</summary>
    public string LogLevel { get; init; } = "info";
}

/// <summary>コードエディタ設定（v2.0 仕様書15章・4章）。</summary>
public sealed record EditorSettings
{
    /// <summary>コード表示のフォントサイズ。Ctrl+マウスホイールで変更できる。</summary>
    public double FontSize { get; init; } = 13;

    /// <summary>長い行を折り返すかどうか。</summary>
    public bool WordWrap { get; init; } = false;

    /// <summary>タブ・行末空白を可視化するかどうか。</summary>
    public bool ShowWhitespace { get; init; } = false;

    /// <summary>行番号を表示するかどうか。</summary>
    public bool ShowLineNumbers { get; init; } = true;

    /// <summary>現在行をハイライトするかどうか。</summary>
    public bool HighlightCurrentLine { get; init; } = true;

    /// <summary>タブ幅。<see cref="DetectIndent"/> が false のとき、または検出不能時に使う。</summary>
    public int TabSize { get; init; } = 4;

    /// <summary>タブの代わりにスペースを挿入するかどうか。</summary>
    public bool InsertSpaces { get; init; } = true;

    /// <summary>開いた時点でファイルの優勢なインデントを検出して適用するかどうか。</summary>
    public bool DetectIndent { get; init; } = true;

    /// <summary>括弧の自動閉じと対応括弧の強調を行うかどうか。</summary>
    public bool AutoClosingBrackets { get; init; } = true;

    /// <summary>コードの折りたたみを有効にするかどうか。</summary>
    public bool Folding { get; init; } = true;

    /// <summary>単語ベースの簡易補完を有効にするかどうか。</summary>
    public bool Completion { get; init; } = true;

    /// <summary>行番号ガターにGitの変更状態を表示するかどうか。</summary>
    public bool GitGutter { get; init; } = true;
}

/// <summary>クリップボード監視設定（9章）。</summary>
public sealed record ClipboardWatchSettings
{
    /// <summary>監視を有効にするか。</summary>
    public bool Enabled { get; init; } = false;

    /// <summary>
    /// 反応時の挙動。"notify"（トレイ通知のみ）/ "passive"（非アクティブ表示）/
    /// "active"（アクティブ表示）のいずれか。
    /// </summary>
    public string Action { get; init; } = "notify";
}

/// <summary>バックアップ・世代管理設定（7.4章）。</summary>
public sealed record BackupSettings
{
    /// <summary>プロジェクトあたりの最大リビジョン保持数。</summary>
    public int MaxRevisions { get; init; } = 100;

    /// <summary>バックアップ合計サイズの上限（MB）。</summary>
    public int MaxTotalMB { get; init; } = 500;

    /// <summary>世代整理時にごみ箱を使用するか。</summary>
    public bool UseRecycleBin { get; init; } = true;
}

/// <summary>マッチングエンジン設定（5章）。</summary>
public sealed record MatchingSettings
{
    /// <summary>段階5（類似度マッチ）の判定閾値（0〜1）。</summary>
    public double SimilarityThreshold { get; init; } = 0.85;

    /// <summary>段階5（類似度マッチ）を許可するか。</summary>
    public bool AllowSimilarityMatch { get; init; } = true;

    /// <summary>アンカー省略記法（4.4章）で警告を出す範囲行数の閾値。</summary>
    public int RangeWarningLines { get; init; } = 300;
}

/// <summary>新規ファイル作成時のエンコーディング設定（6.4章）。</summary>
public sealed record EncodingSettings
{
    /// <summary>新規ファイルのエンコーディング。</summary>
    public string NewFileEncoding { get; init; } = "utf-8";

    /// <summary>新規ファイルにBOMを付与するか。</summary>
    public bool NewFileBom { get; init; } = false;
}

/// <summary>シンタックスハイライト設定（8.6章）。</summary>
public sealed record SyntaxSettings
{
    /// <summary>ハイライトを有効にするか。</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>行番号を表示するか。</summary>
    public bool ShowLineNumbers { get; init; } = true;
}

/// <summary>diff表示設定（8.13章）。</summary>
public sealed record DiffSettings
{
    /// <summary>変更なし範囲の前後コンテキスト行数。</summary>
    public int ContextLines { get; init; } = 3;

    /// <summary>長い行を折り返すか。</summary>
    public bool WordWrap { get; init; } = false;

    /// <summary>空白文字（タブ・行末空白）を可視化するか。</summary>
    public bool ShowWhitespace { get; init; } = false;
}

/// <summary>安全機構設定（13章）。</summary>
public sealed record SafetySettings
{
    /// <summary>既定で許可する拡張子の一覧。</summary>
    public List<string> AllowedExtensions { get; init; } = new()
    {
        ".py", ".js", ".ts", ".tsx", ".cs", ".java", ".go",
        ".rs", ".html", ".css", ".json", ".yaml", ".yml",
        ".md", ".sql", ".xml", ".txt",
    };

    /// <summary>1ファイルあたりの最大サイズ（MB）。</summary>
    public int MaxFileSizeMB { get; init; } = 10;

    /// <summary>1リビジョンあたりの最大ファイル数。</summary>
    public int MaxFilesPerRevision { get; init; } = 200;
}

/// <summary>コンテキスト収集設定（10章）。</summary>
public sealed record ContextSettings
{
    /// <summary>.gitignore を尊重するか。</summary>
    public bool RespectGitignore { get; init; } = true;

    /// <summary>トークン概算に使う「文字数 / この値」の比率。</summary>
    public double TokenRatio { get; init; } = 2.5;

    /// <summary>トークン数警告の閾値。</summary>
    public int TokenWarnThreshold { get; init; } = 50000;
}

/// <summary>適用後フック設定（6.5章）。</summary>
public sealed record HookSettings
{
    /// <summary>フック実行のタイムアウト（秒）。</summary>
    public int TimeoutSec { get; init; } = 120;
}

/// <summary>Git連携設定（7.5章）。</summary>
public sealed record GitSettings
{
    /// <summary>適用後に自動でコミットするか。</summary>
    public bool AutoCommit { get; init; } = false;
}
