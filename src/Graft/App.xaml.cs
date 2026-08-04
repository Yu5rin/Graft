using System.Windows;
using Graft.Themes;

namespace Graft;

/// <summary>
/// アプリケーションのエントリポイント。
/// ここではテーマ基盤の初期化のみを行う。多重起動防止、コマンドライン引数の
/// 解釈、初回起動ガイドの表示などの起動処理本体は他担当が追記する。
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ThemeManager.Initialize();
    }
}
