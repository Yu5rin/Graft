using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Graft;

/// <summary>
/// アプリケーション本体。テーマ辞書の読み込みと起動処理の入り口を担う。
/// 起動処理の中身（多重起動防止・起動時検証・トレイ配線）はフェーズL3以降で移植する。
/// </summary>
public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new Views.ShellWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
