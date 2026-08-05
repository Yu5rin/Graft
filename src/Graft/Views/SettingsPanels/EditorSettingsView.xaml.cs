using System.Windows.Controls;

namespace Graft.Views.SettingsPanels;

/// <summary>15章・4章「エディタ」区分の設定タブ。DataContextは<see cref="Graft.ViewModels.SettingsViewModel"/>を継承する。</summary>
public partial class EditorSettingsView : UserControl
{
    public EditorSettingsView()
    {
        InitializeComponent();
    }
}
