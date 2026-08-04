using System.Windows.Controls;

namespace Graft.Views.SettingsPanels;

/// <summary>14章 settings.jsonの直接編集タブ。DataContextは<see cref="Graft.ViewModels.SettingsViewModel"/>を継承する。</summary>
public partial class RawJsonSettingsView : UserControl
{
    public RawJsonSettingsView()
    {
        InitializeComponent();
    }
}
