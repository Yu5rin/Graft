using System.Windows.Controls;

namespace Graft.Views.Settings;

/// <summary>14章「一般」区分の設定タブ。DataContextは<see cref="Graft.ViewModels.SettingsViewModel"/>を継承する。</summary>
public partial class GeneralSettingsView : UserControl
{
    public GeneralSettingsView()
    {
        InitializeComponent();
    }
}
