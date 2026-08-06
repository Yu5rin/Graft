using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Graft.Views;

/// <summary>シェルウィンドウ（仕様書v2.1 9.2）。中身の移植はフェーズL3で行う。</summary>
public partial class ShellWindow : Window
{
    public ShellWindow() => AvaloniaXamlLoader.Load(this);
}
