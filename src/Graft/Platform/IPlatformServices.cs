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
/// 仕様書8.12 トレイ常駐。移設元は <c>Views/TrayIconHost.cs</c>・<c>TrayIconRenderer.cs</c>・
/// <c>TrayNativeMethods.cs</c>。WPFの <see cref="System.Windows.Controls.ContextMenu"/> 等へ
/// 依存できないため、右クリックメニューの内容は <see cref="TrayMenuDescriptor"/>（本ファイル）
/// を介してデータとコールバックのみで表現する。アイコン画像は実装側（Windows実装）が
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
/// 仕様書8.10 グローバルホットキー。移設元は <c>Features/HotkeyManager.cs</c>・
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
/// 仕様書9章・10章 クリップボード監視。移設元は <c>Features/ClipboardWatcher.cs</c>・
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
/// 仕様書7.4・14章 ごみ箱への削除。移設元は <c>Core/RevisionIndex.cs</c> の <c>RecycleBin</c>。
/// </summary>
public interface ITrashService : IPlatformService
{
    /// <summary>指定パス（ファイルまたはフォルダ）をごみ箱へ送る。成功時 true。</summary>
    bool Send(string path);
}

/// <summary>
/// 仕様書4.2 ファイルマネージャで表示。移設元は <c>Features/FileTreeService.cs</c> の
/// <c>RevealInFileExplorer</c>。
/// </summary>
public interface IFileManagerLauncher : IPlatformService
{
    /// <summary>指定パスをファイルマネージャで選択表示する。</summary>
    void Reveal(string fullPath);
}

/// <summary>
/// 仕様書8.3 システムテーマの判定と変更通知。移設元は <c>Themes/ThemeManager.cs</c> の
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
/// 仕様書6.8 多重起動の防止と既存ウィンドウの前面化。移設元は <c>Core/SingleInstanceGuard.cs</c>
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
    /// </summary>
    void ActivateExistingInstance(string mainWindowTitle);
}

/// <summary>
/// OS固有機能の入口。実行中のOSに応じた実装一式を提供する（<see cref="PlatformServices"/> が
/// ファクトリ）。UI層・機能層はこのインターフェースにのみ依存し、個別のOS APIを直接呼ばない。
/// </summary>
public interface IPlatformServices
{
    /// <summary>トレイ常駐。</summary>
    ITrayIcon Tray { get; }

    /// <summary>グローバルホットキー。</summary>
    IGlobalHotkeys Hotkeys { get; }

    /// <summary>クリップボード監視。</summary>
    IClipboardMonitor Clipboard { get; }

    /// <summary>ごみ箱への削除。</summary>
    ITrashService Trash { get; }

    /// <summary>ファイルマネージャで表示。</summary>
    IFileManagerLauncher FileManager { get; }

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
