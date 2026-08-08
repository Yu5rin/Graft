using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Graft.Views;

/// <summary>
/// キーボードショートカット一覧ウィンドウ。<see cref="ShellWindow"/>のキーボード操作
/// （ShellWindow.Keyboard.cs）に定義されているショートカットを機能分類ごとに一覧表示する
/// だけの静的な画面のため、ViewModelは持たない。開く手段はShellWindow.axamlの「?」ボタンと
/// Ctrl+/の2通り。v2.0のWPF版には存在しない新規画面（利用者からの指摘対応）。
/// </summary>
public partial class ShortcutsWindow : Window
{
    public ShortcutsWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnTunnelKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();

    private void OnTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }
}
