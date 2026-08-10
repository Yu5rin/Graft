using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Graft.Platform;

namespace Graft.Views;

/// <summary>
/// 機能2（ログの参照手段）「最新のログを表示」の専用ウィンドウ。<see cref="ShortcutsWindow"/>と
/// 同じく静的表示が主体のためViewModelは持たず、表示内容はコンストラクタで受け取る。
/// クリップボードへの書き込みは、機能1の「詳細をコピー」と同じ既定経路
/// （<see cref="AvaloniaUiServices.SharedClipboard"/>。Linuxでは自前のX11実装を優先する）を再利用する。
/// </summary>
public partial class LogViewerWindow : Window
{
    private readonly string _tailText;

    /// <summary>デザイナ・XAMLローダー向けの既定コンストラクタ。実際の表示には<see cref="LogViewerWindow(string, string)"/>を使う。</summary>
    public LogViewerWindow() : this(string.Empty, string.Empty)
    {
    }

    public LogViewerWindow(string filePath, string tailText)
    {
        InitializeComponent();
        _tailText = tailText;
        FilePathText.Text = string.IsNullOrEmpty(filePath) ? string.Empty : filePath;
        LogContentText.Text = tailText;

        AddHandler(KeyDownEvent, OnTunnelKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnCopyClicked(object? sender, RoutedEventArgs e)
        => AvaloniaUiServices.SharedClipboard.SetText(_tailText);

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();

    private void OnTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }
}
