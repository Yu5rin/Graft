using System.Windows;
using System.Windows.Input;
using Graft.ViewModels;

namespace Graft.Views;

/// <summary>
/// 14章の設定画面。settings.jsonの全項目編集、json直接編集、プロンプトテンプレート管理
/// （4.8章）、トークン統計（12章）、バージョン情報（8.15章）をタブでまとめる。
/// 8.10: Escで閉じる。
/// </summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        InitializeComponent();
        DataContext = vm;
        Loaded += async (_, _) => await vm.InitializeAsync().ConfigureAwait(true);
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }
}
