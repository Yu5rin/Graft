using System.Windows.Controls;

namespace Graft.Views.SettingsTabs;

/// <summary>
/// 14章「syntax」「diff」「context」「encoding」「clipboardWatch」区分の設定タブ。
/// DataContextは<see cref="Graft.ViewModels.SettingsViewModel"/>を継承する。
/// </summary>
public partial class DiffSettingsView : UserControl
{
    public DiffSettingsView()
    {
        InitializeComponent();
    }
}
