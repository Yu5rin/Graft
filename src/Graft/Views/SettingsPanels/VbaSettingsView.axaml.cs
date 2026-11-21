using Avalonia.Controls;

namespace Graft.Views.SettingsPanels;

/// <summary>
/// 設定画面のタブ1枚（Excel VBA 連携。仕様書_VBA連携 8章）。DataContext は SettingsViewModel を継承する。
/// Windows 以外では、中身（<c>VbaSettingsContent</c>）が非表示になり、案内（<c>VbaUnsupportedPanel</c>）だけが出る。
/// </summary>
public partial class VbaSettingsView : UserControl
{
    public VbaSettingsView() => InitializeComponent();
}
