using Avalonia.Controls;

namespace Graft.Views.SettingsPanels;

/// <summary>設定画面のタブ1枚（仕様書14章・6.5章）。DataContextはSettingsViewModelを継承する。</summary>
public partial class HookSettingsView : UserControl
{
    public HookSettingsView() => InitializeComponent();
}
