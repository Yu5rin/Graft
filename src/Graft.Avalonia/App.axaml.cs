using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Graft.Platform.Null;
using Graft.Themes;

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
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new Views.ShellWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
