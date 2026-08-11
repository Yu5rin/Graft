using Graft.Core;

namespace Graft.Platform;

/// <summary>
/// 仕様書2.4 プラットフォームサービスの抽象化。OS固有機能に共通のインターフェース。
/// 利用可否を <see cref="IsSupported"/> で表明し、利用不可でも例外を投げず静かに何もしない
/// ことを各実装（<c>Platform/Windows</c>・<c>Platform/Null</c>）の要件とする。
/// </summary>
public interface IPlatformService
{
    /// <summary>この環境でこのサービスが利用できるかどうか。</summary>
    bool IsSupported { get; }

    /// <summary>
    /// 利用できない場合に設定画面へ表示する理由（日本語）。利用可能なら null。
    /// </summary>
    string? UnsupportedReason { get; }
}

/// <summary>
/// 仕様書8.12 トレイ常駐。v2.0での実装元は <c>Views/TrayIconHost.cs</c>・<c>TrayIconRenderer.cs</c>・
/// <c>TrayNativeMethods.cs</c>。UIフレームワークのメニュー型へ依存させないため、
/// 右クリックメニューの内容は <see cref="TrayMenuDescriptor"/>（本ファイル）を介して
/// データとコールバックのみで表現する。アイコン画像は実装側（Windows実装）が
/// ベクター資源から実行時に生成する（附録A.5「アイコンにラスタ画像を使わない」）。
/// </summary>
public interface ITrayIcon : IPlatformService, IDisposable
{
    /// <summary>右クリックメニューの内容を設定する。<see cref="Show"/> の前に呼び出すこと。</summary>
    void Configure(TrayMenuDescriptor menu);

    /// <summary>トレイアイコンを追加する。利用不可の環境では何もしない。</summary>
    void Show();

    /// <summary>トレイ通知（バルーン）を表示する。9章「トレイ通知のみ」の挙動で使う。</summary>
    void ShowBalloon(string title, string text);
}

/// <summary>
/// <see cref="ITrayIcon"/> の右クリックメニューを表すデータ。WPFのメニュー型に依存しないための
/// 抽象で、実際のメニュー構築は各プラットフォーム実装（<c>Platform/Windows</c> 等）が行う。
/// </summary>
public sealed record TrayMenuDescriptor
{
    /// <summary>クリップボード監視が現在有効かどうか（メニューのチェック状態に反映する）。</summary>
    public required bool ClipboardWatchEnabled { get; init; }

    /// <summary>クリップボード監視のON/OFFが切り替えられたときに呼び出す。</summary>
    public required Action<bool> OnToggleClipboardWatch { get; init; }

    /// <summary>「直近のプロジェクトへ切り替え」に列挙する項目。</summary>
    public required IReadOnlyList<TrayRecentProjectItem> RecentProjects { get; init; }

    /// <summary>左クリック、または直近プロジェクト選択時にメインウィンドウを前面表示する。</summary>
    public required Action OnRestoreMainWindow { get; init; }

    /// <summary>「設定」項目のクリックで呼び出す。</summary>
    public required Action OnOpenSettings { get; init; }

    /// <summary>「終了」項目のクリックで呼び出す。</summary>
    public required Action OnExit { get; init; }
}

/// <summary>トレイメニューの「直近のプロジェクトへ切り替え」に列挙する1項目。</summary>
public sealed record TrayRecentProjectItem(string Name, Action OnSelect);

/// <summary>
/// 仕様書8.10 グローバルホットキー。v2.0での実装元は <c>Features/HotkeyManager.cs</c>・
/// <c>NativeMethods.cs</c>。ウィンドウメッセージの配線（<c>HwndSource.AddHook</c> 等）は
/// UI層の責務のまま残し、本インターフェースは <see cref="Attach"/> でウィンドウハンドルを
/// 受け取ったうえで登録・メッセージ処理のみを担う。
/// </summary>
public interface IGlobalHotkeys : IPlatformService, IDisposable
{
    /// <summary>メッセージ受信に使うウィンドウハンドルを関連付ける。登録前に1度呼び出すこと。</summary>
    void Attach(IntPtr hwnd);

    /// <summary>
    /// "Ctrl+Alt+V" のような文字列を解釈してグローバルホットキーとして登録する。
    /// 利用できない環境や解釈できないキー指定では例外を投げず失敗を返す。
    /// </summary>
    GraftResult<int> Register(string gesture, Action callback);

    /// <summary>登録済みのすべてのホットキーを解除する。</summary>
    void UnregisterAll();

    /// <summary>
    /// ウィンドウプロシージャから転送されたメッセージを処理する。該当メッセージであれば
    /// 対応するコールバックを呼び出して true を返す。
    /// </summary>
    bool HandleMessage(int msg, IntPtr wParam, IntPtr lParam);
}

/// <summary>
/// 仕様書9章・10章 クリップボード監視。v2.0での実装元は <c>Features/ClipboardWatcher.cs</c>・
/// <c>NativeMethods.cs</c>。ブロックヘッダのパターン判定はこの層で完結させ、読み取った内容は
/// どこにも保持しない方針を維持する。
/// </summary>
public interface IClipboardMonitor : IPlatformService, IDisposable
{
    /// <summary>メッセージ受信に使うウィンドウハンドルを関連付ける。<see cref="Start"/> の前に呼び出すこと。</summary>
    void Attach(IntPtr hwnd);

    /// <summary>監視が現在有効かどうか。</summary>
    bool IsEnabled { get; }

    /// <summary>ブロックヘッダのパターンを含むテキストがクリップボードに現れたときに発火する。</summary>
    event EventHandler<string>? PatchDetected;

    /// <summary>
    /// 11件目の不具合修正: クリップボードの内容が変化したが、パッチ形式ではなかったときに
    /// 発火する。以前は<see cref="PatchDetected"/>しか無く、「パッチ形式と判定したときだけ
    /// 発火する」設計だったため、直前にパッチ検知の通知を出した後で通常のテキストを
    /// コピーしても、それを知らせる経路が無く通知が出たままになっていた（実機報告）。
    /// 通知を消すためのシグナルとしてのみ使う想定で、テキスト自体は渡さない
    /// （クリップボードの中身を不必要に保持・ログ出力しない方針を維持するため）。
    /// 非テキストのコピー（画像等）や、一時的な読み取り失敗（他アプリがクリップボードを
    /// 保持中等）では発火しない（誤って通知を消してしまわないための保守的な判断。
    /// 各実装のコメント参照）。
    /// </summary>
    event EventHandler? NonPatchTextChanged;

    /// <summary>クリップボード変更通知の受信を開始する。利用できない環境では何もせず失敗を返す。</summary>
    GraftResult<bool> Start();

    /// <summary>クリップボード変更通知の受信を停止する。</summary>
    void Stop();

    /// <summary>
    /// ウィンドウプロシージャから転送されたメッセージを処理する。該当メッセージであれば
    /// 処理して true を返す。
    /// </summary>
    bool HandleMessage(int msg, IntPtr wParam, IntPtr lParam);
}

/// <summary>
/// 仕様書7.4・14章 ごみ箱への削除。v2.0での実装元は <c>Core/RevisionIndex.cs</c> の <c>RecycleBin</c>。
/// </summary>
public interface ITrashService : IPlatformService
{
    /// <summary>指定パス（ファイルまたはフォルダ）をごみ箱へ送る。成功時 true。</summary>
    bool Send(string path);
}

/// <summary>
/// 仕様書4.2 ファイルマネージャで表示。v2.0での実装元は <c>Features/FileTreeService.cs</c> の
/// <c>RevealInFileExplorer</c>。
/// </summary>
public interface IFileManagerLauncher : IPlatformService
{
    /// <summary>指定パスをファイルマネージャで選択表示する。</summary>
    void Reveal(string fullPath);
}

/// <summary>
/// Markdownプレビュー機能: 外部リンク（<c>https://</c>等）を既定のブラウザで開く。
/// <see cref="IFileManagerLauncher"/>と同じ「OS固有のプロセス起動」という性質を持つため、
/// 同じ抽象化の作法（<see cref="IPlatformService"/>を実装し、利用不可でも例外を投げず
/// 静かに縮退する）に揃えて追加した。悪意あるMarkdownファイルからの無警告な外部遷移を
/// 避けるため、呼び出し側（<c>Views/EditorPane.axaml.cs</c>）は必ず確認ダイアログ
/// （<see cref="IDialogService.ConfirmAsync"/>）を経てからこのメソッドを呼ぶ。
/// </summary>
public interface IExternalLinkLauncher : IPlatformService
{
    /// <summary>指定URLを既定のブラウザで開く。起動に失敗しても例外は投げない。</summary>
    void Open(string url);
}

/// <summary>
/// 仕様書8.3 システムテーマの判定と変更通知。v2.0での実装元は <c>Themes/ThemeManager.cs</c> の
/// <c>TryReadAppsUseLightTheme</c> と、それに付随するシステム設定変更監視。
/// </summary>
public interface ISystemThemeWatcher : IPlatformService, IDisposable
{
    /// <summary>
    /// システムのライト/ダーク設定を読み取り専用で参照する。判定できない場合は null を返し、
    /// 呼び出し側で既定（ダーク）へフォールバックさせる。
    /// </summary>
    bool? TryReadIsLightTheme();

    /// <summary>システムのテーマ設定が変化した可能性があるときに発火する。</summary>
    event EventHandler? Changed;

    /// <summary>システム設定変更の監視を開始する。</summary>
    void StartWatching();

    /// <summary>監視を停止する。</summary>
    void StopWatching();
}

/// <summary>
/// 仕様書6.8 多重起動の防止と既存ウィンドウの前面化。v2.0での実装元は <c>Core/SingleInstanceGuard.cs</c>
/// （取得・解放。プラットフォームを問わず動作するためロジックはそのまま利用する）と
/// <c>Views/StartupCoordinator.cs</c> の前面化用P/Invoke（<c>FindWindow</c> 等、Windows固有）。
/// </summary>
public interface ISingleInstanceGuard : IPlatformService, IDisposable
{
    /// <summary>
    /// 指定名でロックの取得を試みる。既に他プロセスが起動中で取得できない場合は false を返す。
    /// </summary>
    bool TryAcquire(string name);

    /// <summary>
    /// 既に起動している既存ウィンドウを前面表示する。<see cref="TryAcquire"/> が false を
    /// 返した場合に呼び出す想定。利用できない環境では何もしない。
    ///
    /// 機能追加: クリップボード監視でのパッチ検知時の前面化（<c>Views/StartupCoordinator.
    /// ClipboardActivation.cs</c>）でも同じ経路を再利用するため、成否を戻り値で返す。
    /// Windowsでは<c>SetForegroundWindow</c>がOSのフォーカス窃取防止で拒否されタスクバーの
    /// アイコン点滅に縮退することがあり、その場合は false（呼び出し側はエラー扱いにせず
    /// ログにのみ記録する。多重起動検出時の前面化はこの戻り値を見ない＝従来どおり静かに
    /// 縮退するのみで挙動は変わらない）。
    ///
    /// 不具合修正: このメソッドは「タイトルで探し直して」別プロセスのウィンドウを前面化する、
    /// 多重起動検出専用の経路として使うこと。同一プロセス内の自分のウィンドウ
    /// （既に<see cref="Avalonia.Controls.Window"/>を持っている場合）を前面化したいときは、
    /// このメソッドではなく<see cref="ActivateWindowHandle"/>を使う（理由はそちらのコメントを参照）。
    /// </summary>
    bool ActivateExistingInstance(string mainWindowTitle);

    /// <summary>
    /// 不具合修正: 自分のプロセスが既に持っているウィンドウを、タイトルの再検索を経由せず
    /// ハンドル指定で直接前面化する。クリップボード監視でのパッチ検知時の前面化
    /// （<c>Views/StartupCoordinator.ClipboardActivation.cs</c>）向け。<paramref name="handle"/>には
    /// <c>Window.TryGetPlatformHandle()?.Handle</c>で取得した実際のウィンドウハンドル
    /// （Windowsでは HWND、LinuxのX11環境では XID）を渡す。
    ///
    /// 【なぜ<see cref="ActivateExistingInstance"/>（タイトル検索）を使い回してはいけないか】
    /// Windows実機検証で次の事実が判明した（同じ「他アプリを操作中」という状況での比較）。
    ///
    /// <list type="bullet">
    /// <item>Graftが背面（表示中・非最小化）の状態から<see cref="ActivateExistingInstance"/>
    /// （<c>FindWindow</c>でタイトルから探し直す経路）を呼んでも前面に出ない。</item>
    /// <item>Graftが最小化の状態からは出る。ただしこれは<c>WindowState = Normal</c>への変更
    /// 自体の副作用で復帰しているだけで、前面化そのものが機能しているわけではない。</item>
    /// <item>前面化設定オフ＋自動解析オンの経路（<c>Window.Activate()</c>。自分が既に持っている
    /// Windowオブジェクトへ直接作用する）は同じ状況でも成功する。</item>
    /// </list>
    ///
    /// 同じ状況で経路によって結果が割れたことから、原因はOSのフォーカス窃取防止だけではなく、
    /// 「既に自分が掴んでいるウィンドウを、わざわざウィンドウタイトルの文字列から再度探し直す」
    /// という経路そのものにある。<see cref="ActivateExistingInstance"/>は本来、多重起動検出
    /// （別プロセスのウィンドウをタイトルで見つける必要がある）のための正しい実装であり、
    /// そちらは変更しない。クリップボード監視は同一プロセスの自分のウィンドウが対象という
    /// 前提が異なるのに同じ経路を誤って再利用していたことが不具合の原因だったため、
    /// このメソッドではタイトルを再検索せず、渡されたハンドルへ直接作用する。
    ///
    /// <paramref name="handle"/>が<see cref="IntPtr.Zero"/>、またはハンドル経由の前面化が
    /// この環境では使えない場合は、<paramref name="fallbackWindowTitle"/>を使って
    /// <see cref="ActivateExistingInstance"/>（タイトル検索）へ縮退してよい（要否・可否は
    /// 各プラットフォーム実装のコメント参照）。戻り値の意味は<see cref="ActivateExistingInstance"/>
    /// と同じ（OS側の制約による拒否＝縮退の判定は、呼び出し側で同様にfalseとして扱われる）。
    /// </summary>
    bool ActivateWindowHandle(IntPtr handle, string fallbackWindowTitle);
}

/// <summary>
/// 課題3: PC起動時の自動起動。仕様書2.1「レジストリ書き込みは行わない（読み取りのみ許可）」
/// に従い、レジストリの Run キーは使わない。代わりにOSごとの「スタートアップフォルダ」
/// 方式（Windows: スタートアップフォルダへの起動スクリプト配置／Linux: XDG autostart仕様の
/// .desktopファイル）で実現する。
/// </summary>
public interface IAutoStartService : IPlatformService
{
    /// <summary>現在、自動起動が登録されているかどうかを実際のファイルの有無から判定する。</summary>
    bool IsRegistered { get; }

    /// <summary>
    /// 自動起動を登録する。既に登録されている場合も、現在の実行ファイルの絶対パスで
    /// 常に書き直す（アプリを別の場所へ移動した後でも、登録し直せば古いパスの
    /// 残骸が残らないようにするため）。
    /// </summary>
    AutoStartResult Enable();

    /// <summary>自動起動の登録を解除する。登録されていない場合は何もせず成功を返す。</summary>
    AutoStartResult Disable();
}

/// <summary>
/// <see cref="IAutoStartService.Enable"/>・<see cref="IAutoStartService.Disable"/> の結果。
/// 失敗時は<see cref="ErrorMessage"/>に日本語の理由が入り、呼び出し側（設定画面）が
/// そのまま利用者へ表示できる（「登録・解除に失敗した場合は黙って失敗させず伝える」の対応）。
/// </summary>
public readonly record struct AutoStartResult(bool Success, string? ErrorMessage)
{
    public static AutoStartResult Ok() => new(true, null);

    public static AutoStartResult Fail(string message) => new(false, message);
}

/// <summary>
/// OS固有機能の入口。実行中のOSに応じた実装一式を提供する（<see cref="PlatformServices"/> が
/// ファクトリ）。UI層・機能層はこのインターフェースにのみ依存し、個別のOS APIを直接呼ばない。
/// </summary>
public interface IPlatformServices
{
    /// <summary>トレイ常駐。</summary>
    ITrayIcon Tray { get; }

    /// <summary>PC起動時の自動起動。</summary>
    IAutoStartService AutoStart { get; }

    /// <summary>グローバルホットキー。</summary>
    IGlobalHotkeys Hotkeys { get; }

    /// <summary>クリップボード監視。</summary>
    IClipboardMonitor Clipboard { get; }

    /// <summary>ごみ箱への削除。</summary>
    ITrashService Trash { get; }

    /// <summary>ファイルマネージャで表示。</summary>
    IFileManagerLauncher FileManager { get; }

    /// <summary>Markdownプレビュー機能: 外部リンクを既定のブラウザで開く。</summary>
    IExternalLinkLauncher ExternalLinks { get; }

    /// <summary>システムテーマの判定と変更通知。</summary>
    ISystemThemeWatcher Theme { get; }

    /// <summary>多重起動の防止と既存ウィンドウの前面化。</summary>
    ISingleInstanceGuard SingleInstance { get; }

    /// <summary>
    /// 16章: 起動時にログへ記録するプラットフォーム情報（OS種別・バージョン・利用可能な
    /// サービス）を1行の日本語で返す。
    /// </summary>
    string DescribeEnvironment();
}
