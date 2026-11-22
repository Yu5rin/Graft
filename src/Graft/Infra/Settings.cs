namespace Graft.Infra;

/// <summary>
/// アプリ全体設定（settings.json）のルートモデル。14章のJSON構造・既定値と1:1で対応する。
/// System.Text.Json によりcamelCaseのキー名へ自動変換される
/// （<see cref="JsonFileStore.DefaultOptions"/> 参照）。
/// </summary>
public sealed record Settings
{
    /// <summary>
    /// テーマ。"dark" / "light" / "system" に加え、テーマプリセット7種
    /// （"sepia" / "github" / "solarized-light" / "solarized-dark" / "nord" / "dracula" /
    /// "night"）のいずれか。<see cref="Graft.Themes.ThemeManager.ParseTheme"/>参照。
    /// </summary>
    public string Theme { get; init; } = "system";

    /// <summary>
    /// 「操作の説明」（ツールチップ）の表示レベル。"off"（表示しない） / "minimal"（最低限、
    /// 現在の値だけ） / "standard"（標準の説明、既定） / "detailed"（くわしい説明）のいずれか。
    /// <see cref="Graft.Views.HelpTip"/>参照。
    /// </summary>
    public string TooltipDetail { get; init; } = "standard";

    /// <summary>
    /// 適用モード。"allOrNothing" / "partial" のいずれか。既定は"partial"。
    /// 実機不具合対応: 既定が"allOrNothing"だった頃は、AIの回答の一部だけがコードに
    /// 当てはまらない（ごくありふれた）状況で、適用可能なブロックが1件でもあるにもかかわらず
    /// 適用処理全体がエラー扱いになり、履歴への記録も成功の通知も出なかった。ほとんどの利用者は
    /// この設定を一度も開かないため、既定値そのものが実質的な不具合として体感されていた。
    /// 「全件適用（All or Nothing）」は複数ブロックが相互依存するパッチ向けの安全機構として
    /// 引き続き選択可能にしつつ、既定は「適用できるものは適用し、できなかったものは
    /// 一覧に残す」（部分適用可）に変更する。
    /// 移行についての注意: この既定値はSettings.jsonが無い（新規インストール）場合、または
    /// applyModeキー自体が無い/不正な場合にのみ使われる（SettingsStore.Validate参照）。
    /// 既に保存済みのsettings.jsonでapplyModeが明示されている（allOrNothing・partialの
    /// どちらであっても）場合はその値をそのまま尊重し、この既定値変更によって勝手に
    /// 書き換わることはない。
    /// </summary>
    public string ApplyMode { get; init; } = "partial";

    /// <summary>適用前にプレビューを表示するか（8.7章）。</summary>
    public bool ShowPreview { get; init; } = true;

    /// <summary>パッチメタデータの summary を必須にするか（4.2章）。</summary>
    public bool RequireSummary { get; init; } = true;

    /// <summary>クリップボード監視設定（9章）。</summary>
    public ClipboardWatchSettings ClipboardWatch { get; init; } = new();

    /// <summary>グローバルホットキー（8.10章）。</summary>
    public string Hotkey { get; init; } = "Ctrl+Alt+V";

    /// <summary>
    /// 「AIへの依頼文をコピー」のOS全体のホットキー（4.8.4）。既定はCtrl+Shift+C。
    /// 空にするとOS全体には登録せず、Graftの画面内（Ctrl+Shift+C）だけで効く。
    /// 他のアプリ（ブラウザの開発者ツール等）が同じ組み合わせを使っていて衝突する場合の逃げ道。
    /// settings.json に項目が無い古い設定は、この既定値で読み込まれる。
    /// </summary>
    public string PromptHotkey { get; init; } = "Ctrl+Shift+C";

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

    /// <summary>
    /// 課題2: ウィンドウを×で閉じたときの動作。"exit"（終了する。既定）/
    /// "tray"（タスクトレイに常駐する）のいずれか。トレイが使えない環境では
    /// "tray"を選んでいても実際には終了する（縮退。<c>ShellWindow.OnClosing</c>参照）。
    /// </summary>
    public string CloseBehavior { get; init; } = "exit";

    /// <summary>課題3: PC起動時に自動で起動するか。既定はオフ。</summary>
    public bool LaunchAtStartup { get; init; } = false;

    /// <summary>
    /// 不具合修正: ウィンドウを最小化したときにタスクトレイへ格納するか。既定はオフ
    /// （＝Windowsの通常の慣習どおり、最小化してもタスクバーに残る）。
    /// オンにすると、最小化した瞬間にウィンドウがタスクバーからも消え、タスクトレイの
    /// アイコンからのみ復帰できるようになる（クリップボード監視やホットキー貼り付けを
    /// すぐ使えるよう、常に起動しておきたい利用者向け）。トレイが使えない環境では、
    /// この設定がオンでも実際には通常の最小化のまま（縮退。<c>StartupCoordinator.
    /// StartAsync</c>のwindow.PropertyChangedハンドラ参照）。
    /// </summary>
    public bool MinimizeToTray { get; init; } = false;

    /// <summary>自動更新（機能追加）。既定はオン（起動時に確認する）。</summary>
    public UpdateSettings Update { get; init; } = new();

    /// <summary>
    /// Excel VBA 連携（仕様書_VBA連携 8章）。settings.json に <c>vba</c> キーが無い旧版の設定は
    /// 既定値で読まれる。<see cref="Graft.ViewModels.SettingsViewModel"/> の保存は <c>new Settings</c> で
    /// 全項目を組み立て直すため、そちらにも引き継ぎを書かないと、設定画面で何か保存するたびに
    /// この値が既定へ戻る。
    /// </summary>
    public VbaSettings Vba { get; init; } = new();
}

/// <summary>
/// Excel VBA 連携の設定（仕様書_VBA連携 8章）。プロジェクトごとの自動反映・ブック保存は
/// <c>Project.Vba</c>（VbaLink）の項目で、ここには置かない。
/// <para>
/// ホットキー（<c>PullHotkey</c>）は初版から設けない。既存のホットキーは <see cref="Settings.Hotkey"/>・
/// <see cref="Settings.PromptHotkey"/> の2本固定で、再登録（<c>ReapplyHotkey</c> は個別解除なしの
/// <c>UnregisterAll</c>）の作り直しが大きいため。
/// </para>
/// </summary>
public sealed record VbaSettings
{
    /// <summary>
    /// 機能全体の有効・無効。false のときは COM を一切呼ばず、VBA プロジェクトも
    /// 普通のファイルプロジェクトとして扱う（ミラーは編集・適用できるが同期の操作は無効）。
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>コンテキスト収集の走査の前に、Excel から自動で取り込む（Pull）か。</summary>
    public bool PullOnContextOpen { get; init; } = true;

    /// <summary>
    /// 解析（ドライラン）の直前に対象モジュールを自動で Pull するか。false のときは
    /// 同じ内容を確認ダイアログ（取り込む／取り込まずに続ける／中止）で行う。
    /// </summary>
    public bool PullBeforeDryRun { get; init; } = true;

    /// <summary>
    /// COM が「拒否」を返した（セル編集中・ダイアログ表示中など）ときの再試行の上限（ミリ秒）。
    /// 範囲 1000〜30000。超えたら E805。
    /// </summary>
    public int ComTimeoutMs { get; init; } = 5000;

    /// <summary>
    /// COM 呼び出し全体の時間切れ（ミリ秒）。範囲 5000〜120000、<see cref="ComTimeoutMs"/> 以上。
    /// 再試行は「拒否」が返る場合にしか効かず、マクロ実行中などは呼び出しが戻らない見込みなので、
    /// 待つ側がこの時間で諦めて E805 にする。
    /// </summary>
    public int CallTimeoutMs { get; init; } = 30000;

    /// <summary>
    /// コンテキスト収集・依頼文コピーで、ブックの情報（シート・テーブル・名前・見出し・参照設定。
    /// 仕様書_ブックの情報と依頼の観点 5章）を Excel から取るか。false のときは Excel を呼ばない。
    /// <see cref="PullOnContextOpen"/> とは別の設定にしている（Pull を切ってもブックの情報だけ欲しい／
    /// その逆の利用者がいるため。仕様書付録A-10）。
    /// </summary>
    public bool CollectWorkbookInfo { get; init; } = true;

    /// <summary>
    /// 「見出し＋N 行」の N の既定（シートの見せ方でその見せ方を選んだときの最初の行数）。範囲 1〜20。
    /// v0.4 より前は「値の見本の行数」で、見本がオンだった既存のプロジェクトを「全シート 見出し＋N 行」へ移すときの N にもなる
    /// （仕様書_ブックの情報と依頼の観点 5.2 の移行）。
    /// </summary>
    public int WorkbookInfoSampleRows { get; init; } = 5;

    /// <summary>ブックの情報の見出し・見本の最大列数。範囲 5〜100。</summary>
    public int WorkbookInfoMaxColumns { get; init; } = 30;

    /// <summary>見本・見出しの 1 セルの最大文字数（超えたら「…」つきで切る）。範囲 10〜500。</summary>
    public int WorkbookInfoMaxCellChars { get; init; } = 60;

    /// <summary>ブックの情報の出力全体の最大文字数（超えたら値から順に落とす）。範囲 2000〜100000。</summary>
    public int WorkbookInfoMaxChars { get; init; } = 20000;

    /// <summary>
    /// シートの見せ方「全部」「範囲を指定」で、1 シートから読む最大の行数（超えた分は読まず、切ったことを依頼文に明記する）。
    /// 範囲 10〜5000。
    /// </summary>
    public int WorkbookInfoAllMaxRows { get; init; } = 500;

    /// <summary>シートの見せ方「全部」「範囲を指定」で、1 シートから読む最大の列数。範囲 5〜200。</summary>
    public int WorkbookInfoAllMaxColumns { get; init; } = 50;

    /// <summary>
    /// 1 モジュールあたりの自動控え（反映前の控えと、戻す前のミラーの控え）の世代数。範囲 1〜200。
    /// 超えた古い方から自動で消す。衝突の上書き前などの従来の控え（<c>Legacy</c>）は数えず、消さない
    /// （仕様書_ブックの情報と依頼の観点 7.3）。
    /// </summary>
    public int RescueMaxGenerations { get; init; } = 20;

    /// <summary>控え全体（従来の控えを含む）の容量（MB）。範囲 5〜1000。超えたら自動控えを古い順に消す（同 7.3）。</summary>
    public int RescueMaxTotalMB { get; init; } = 50;
}

/// <summary>
/// 自動更新設定（機能追加）。仕様書の改定に伴う追加。GitHub Releases（<see cref="CheckUrl"/>）への
/// 通信は、起動時1日1回まで（前回確認からの経過時間は設定ファイルではなく
/// <see cref="Graft.Core.Update.UpdateCheckStateStore"/>がexeと同じ階層のupdate-check.jsonへ
/// 別途持つ。<see cref="Infra.AppPaths.UpdateCheckStateFilePath"/>参照）と、
/// 「今すぐ更新を確認」ボタンでの手動実行時にのみ発生する。それ以外の外部通信は行わない。
/// </summary>
public sealed record UpdateSettings
{
    /// <summary>起動時に更新を確認するか。既定オン。オフでも「今すぐ更新を確認」は使える。</summary>
    public bool CheckOnStartup { get; init; } = true;

    /// <summary>
    /// 新しい版が見つかったとき、確認ダイアログを出さずに裏でダウンロード・検証・入れ替えまで
    /// 進めるか。<b>既定はオフ</b>（機能追加 1.0.25）。
    ///
    /// 【既定をオフにした理由】 確認なしの入れ替えは「利用者が見ていない場面で実行ファイルを
    /// 書き換える」操作であり、これまでの「同意したときだけ更新する」（かつ「無断でダウンロード
    /// されることはない」とHelpTipで案内している）という約束を変える。明示的にオンにした人だけが
    /// 対象になるようにする。settings.jsonにこの項目が無い（1.0.24以前から引き継いだ）場合も
    /// init既定値のfalseで読めるため、更新しただけで挙動が変わることはない。
    ///
    /// 【オンでも従来の確認ダイアログに戻す場合】 SHA256を照合できない・自分で入れ替えられない
    /// OS／置き場所・更新確認先が既定から変更されている・プレリリースや現在以下の版、の各場合。
    /// 判断は<see cref="Graft.Core.Update.AutoUpdatePolicy"/>に集めてある。
    /// 「起動時に更新を確認する」（<see cref="CheckOnStartup"/>）がオフなら起動時には確認自体が
    /// 行われないため、この設定は意味を持たない（手動の「今すぐ更新を確認」は、この設定に関わらず
    /// 常に確認ダイアログを出す）。
    /// </summary>
    public bool AutoInstall { get; init; } = false;

    /// <summary>
    /// 更新確認先URL（GitHub ReleasesのAPIエンドポイント）。既定はGraft本体のリポジトリ。
    /// コードへ直書きせずここへ持たせているのは、将来配布リポジトリを移した場合に設定変更だけで
    /// 追従できるようにするためと、「どこへ通信するのか」を利用者が設定画面から確認・変更できる
    /// ようにするため。
    /// </summary>
    public string CheckUrl { get; init; } = Graft.Core.Update.UpdateHostPolicy.DefaultCheckUrl;
}

/// <summary>コードエディタ設定（v2.0 仕様書15章・4章）。</summary>
public sealed record EditorSettings
{
    /// <summary>コード表示のフォントサイズ。Ctrl+マウスホイールで変更できる。</summary>
    public double FontSize { get; init; } = 13;

    /// <summary>
    /// UIフォント（検討書「フォント設定」）。既定null（未指定＝アプリ既定のフォント
    /// フォールバック列 <see cref="Graft.Themes.Tokens"/> の UiFontFamily をそのまま使う）。
    /// メニュー・ボタン・一覧など画面全体のUI文字に効く（<see cref="Graft.Themes.AppFontManager"/>
    /// 参照）。<see cref="FontSize"/>とは独立した項目で、フォントの種類だけを選ぶ
    /// （文字サイズは変更しない）。
    ///
    /// 【キー名がbodyFontFamilyのままである理由（利用者の困りごと対応）】
    /// このキーはv1.0.20までは「本文フォント」という名前・意味で、Markdownプレビューの
    /// 本文にも画面全体のUIにも同時に効いていた。しかし本文用に選んだフォントがメニュー・
    /// ボタン・一覧まで巻き込んでしまい、字の縦位置がずれて見えるという指摘を受け、
    /// v1.0.21でUI用と本文用（<see cref="BodyTextFontFamily"/>）の2つに分割した。
    /// JSON上のキー名（camelCase変換後は"fontFamily"）自体は変更していない
    /// （既にこのキーを設定している利用者の設定ファイルをそのまま読めるようにするため。
    /// 移行処理も不要になる）。効果の実態（画面全体のUI）に合わせて、C#側のプロパティ名は
    /// 変えずコメントの意味だけをUIフォントへ更新した。
    /// </summary>
    public string? FontFamily { get; init; }

    /// <summary>
    /// 本文フォント（検討書「フォント設定」・v1.0.21で新設）。既定null（未指定＝
    /// <see cref="Graft.Themes.Tokens"/> の BodyTextFontFamily をそのまま使う。既定値は
    /// UiFontFamilyと同じフォールバック列のため、これを設定していない利用者の見た目は
    /// v1.0.21では一切変わらない）。取扱説明書（<see cref="Graft.Views.ManualWindow"/>）と
    /// Markdownプレビュー（<see cref="Graft.Views.MarkdownPreviewView"/>）の地の文
    /// （見出し・段落・箇条書き・表など）にのみ効く。コードブロック・インラインコードは
    /// 引き続き<see cref="MonospaceFontFamily"/>（等幅）に従うため、この設定の影響を受けない。
    /// <see cref="FontFamily"/>（UIフォント）とは別枠の設定で、ボタン・検索欄などのUI部品には
    /// 効かない（<see cref="Graft.Themes.AppFontManager"/>参照）。
    /// </summary>
    public string? BodyTextFontFamily { get; init; }

    /// <summary>
    /// 等幅（コード用）フォント（検討書「フォント設定」）。既定null（未指定＝アプリ既定の
    /// CodeFontFamilyをそのまま使う）。コードエディタ（AvaloniaEdit）とMarkdownプレビューの
    /// コードブロック、diff表示等の「コード扱いの文字」に効く（<see cref="FontFamily"/>・
    /// <see cref="BodyTextFontFamily"/>とは別枠。<see cref="Graft.Themes.AppFontManager"/>参照）。
    /// </summary>
    public string? MonospaceFontFamily { get; init; }

    /// <summary>
    /// 長い行を折り返すかどうか。既定はオン（課題3の再設計で false→true へ変更）。
    /// 以前は「極端に長い行（20,000文字超）を含むファイルでは、この設定に関わらず
    /// 強制的にオフにする」という例外があったが、利用者の設定を無断で上書きすること
    /// 自体が問題という指摘を受けて廃止した。1行10万文字クラスのファイルで折り返しを
    /// 有効なまま開くと書式計算が数百ms→1.5秒前後に悪化する実測はあるが、これは
    /// 利用者が選べばよいコストと整理し、代わりにそのファイルに限って折り返しを切れる
    /// 逃げ道（通知バーの「このファイルでは折り返しを無効にする」）を用意した
    /// （<see cref="Graft.ViewModels.EditorTabViewModel.WordWrapDisabledForTab"/>・
    /// <see cref="Graft.Editor.DocumentSession.LongLineThreshold"/>）。
    /// </summary>
    public bool WordWrap { get; init; } = true;

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

    /// <summary>
    /// 検討書「コード中のカラープレビュー」。コード中の<c>#RRGGBB</c>・<c>rgb()</c>・<c>hsl()</c>の
    /// 直前にスウォッチ（色見本）を表示し、クリックでカラーピッカーを開けるようにするかどうか。
    /// 既定true（Pane <c>colorPreviewInCode</c>と同じ既定値）。このキーが無い古い<c>settings.json</c>
    /// でも既定trueとして動く。
    /// </summary>
    public bool ColorPreviewInCode { get; init; } = true;

    /// <summary>
    /// 検討書「インデントガイド（縦線）」。インデントの深さを示す縦線の表示モード。
    /// "none"（表示しない） / "foldable"（折りたたみできる範囲のみ、既定） / "all"（すべての
    /// インデント）のいずれか。<see cref="Graft.Editor.IndentGuideModeParser"/>参照。
    /// 未知の値・このキー自体が無い古いsettings.jsonは既定の"foldable"として扱う。
    /// </summary>
    public string IndentGuideMode { get; init; } = "foldable";
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

    /// <summary>
    /// 機能追加: パッチ形式を検知したら、その場で自動的に解析するか。既定はオン。
    /// オフの場合は従来どおり通知のみで、通知をクリックするまで解析しない。
    /// オンでも、解析結果やパッチキューに未処理の内容が残っている間は自動解析せず通知に
    /// 留める（StartupCoordinator.OnClipboardPatchDetected参照）。
    /// </summary>
    public bool AutoParse { get; init; } = true;

    /// <summary>
    /// 機能追加: パッチ形式を検知したら、Graftのウィンドウを前面に表示するか。既定はオン。
    /// <see cref="AutoParse"/>の有無に関わらず、この設定がオンの間は常に前面化する
    /// （検知したこと自体を伝えるのが目的であり、解析の有無は別軸のため）。
    ///
    /// 不具合修正: この設定がオフの場合、自動解析の有無に関わらず<see cref="Action"/>
    /// （「反応時の挙動」）へ厳密に従う。以前は自動解析した場合に限り<see cref="Action"/>を
    /// 無視して無条件に前面化する特例があったが、自動解析は既定オンのため、この設定を
    /// オフにしていても実機ではほぼ常にその特例へ入ってしまい、「反応時の挙動＝トレイ通知のみ」
    /// を選んでいてもウィンドウが前面に出てしまう不具合になっていた。この特例は廃止した。
    ///
    /// 前面化そのものは多重起動検出時の前面化と同じ実装が提供する
    /// <c>ISingleInstanceGuard.ActivateWindowHandle</c>（自分のウィンドウのハンドルを直接
    /// 指定する経路。タイトルを再検索する<c>ActivateExistingInstance</c>とは異なる）を再利用する
    /// （StartupCoordinator.ClipboardActivation.cs参照）。
    /// </summary>
    public bool ActivateOnDetect { get; init; } = true;
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

    /// <summary>
    /// 新規ファイルの改行コード。<c>auto</c>（既定）・<c>crlf</c>・<c>lf</c>。
    /// <c>auto</c>は同じフォルダ（無ければプロジェクト）の既存テキストファイルで優勢な改行に合わせ、
    /// 分からなければOSの既定にする（<see cref="Graft.Core.NewLineResolver"/>）。
    /// settings.json にこの項目が無い旧版の設定ファイルは <c>auto</c> として読まれる。
    /// </summary>
    public string NewFileNewLine { get; init; } = "auto";
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

    /// <summary>
    /// 機能改善: diff表示を並列（左右、既定）にするか統合（上下）にするか。
    /// DiffViewModel.IsSideBySideの初期値・既定値として使う。diff表示ヘッダーの
    /// 切り替えボタンで変更すると即座にここへ永続化される（DiffViewModel.
    /// SideBySideChangeCommitted参照。既存のCtrl+マウスホイールでのフォントサイズ確定と
    /// 同じ即時反映の作法）。
    /// </summary>
    public bool SideBySide { get; init; } = true;
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

    /// <summary>
    /// プロンプトのコピー（コマンドバー「プロンプト」・Ctrl+Shift+C）で、指示文の後ろに
    /// コンテキスト収集で選んだファイルも付けるか。既定はオフ（従来どおり指示文だけ）。
    ///
    /// 【既定をオフにする理由】既定の「初回用（完全版）」などは <c>{{files}}</c> を含まず、
    /// 指示文とファイルを別々にコピーして2回貼る運用が既に定着している。既定で付けると、
    /// 利用者が知らないうちにコピー内容が数万トークン増える。設定として保存するのは、
    /// 一度オンにした利用者が毎回選び直さなくて済むようにするため。
    /// テンプレートの本文自体は書き換えない（利用者が編集したものを壊さない）。
    /// </summary>
    public bool AppendFilesToPrompt { get; init; } = false;
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
