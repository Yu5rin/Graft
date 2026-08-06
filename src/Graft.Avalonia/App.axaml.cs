using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Graft.Platform.Null;
using Graft.Themes;
using Graft.ViewModels;

namespace Graft;

/// <summary>
/// アプリケーション本体。テーマ辞書の読み込みと起動処理の入り口を担う。
/// 起動処理の中身（多重起動防止・起動時検証・トレイ配線）はフェーズL3以降で移植する。
/// </summary>
public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // 9.3・附録A.7: Dark.axaml/Light.axamlはApp.axaml側の静的なマージではなく、
        // ThemeManagerが実行時にMergedDictionariesへ追加する（テーマ切り替えの
        // 差し替え対象にするため）。Initialize()はheadlessテストを含め、Appが
        // 構築されるたびに必ず呼ばれるため、ここで初期化しておけばOnFrameworkInitialization
        // CompletedがCLIの起動経路以外で呼ばれない場合でもトークンが解決できる。
        // システムテーマ判定の実装（Platform/Windows・Platform/Linux）はL4の担当のため、
        // 現時点では何もしない実装を渡し、AppTheme.Systemは常にダークへ解決される。
        ThemeManager.Initialize(new NullSystemThemeWatcher());
        EnableCommandRequery();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new Views.ShellWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// AvaloniaにはWPFの<c>CommandManager</c>に相当するアプリ全体の再評価機構が無いため、
    /// 同等のタイミング（ポインタ操作・キー入力・フォーカス移動の後）で
    /// <see cref="CommandRequery.Invalidate"/>を呼ぶよう配線する（仕様書v2.1 19章 L3）。
    /// トンネリング段階で購読するのは、各コントロールが処理を終えた直後ではなく
    /// 入力が届いた確実なタイミングで一度だけ拾うため。
    /// </summary>
    private static void EnableCommandRequery()
    {
        InputElement.PointerReleasedEvent.AddClassHandler<TopLevel>(
            (_, _) => CommandRequery.Invalidate(), RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        InputElement.KeyUpEvent.AddClassHandler<TopLevel>(
            (_, _) => CommandRequery.Invalidate(), RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        InputElement.GotFocusEvent.AddClassHandler<TopLevel>(
            (_, _) => CommandRequery.Invalidate(), RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
    }
}
