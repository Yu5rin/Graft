using System.Windows.Controls;

namespace Graft.Views.SettingsTabs;

/// <summary>14章「safety」区分の設定タブ。DataContextは<see cref="Graft.ViewModels.SettingsViewModel"/>を継承する。</summary>
public partial class SafetySettingsView : UserControl
{
    public SafetySettingsView()
    {
        InitializeComponent();
    }
}
