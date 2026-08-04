using System.Windows.Controls;

namespace Graft.Views;

/// <summary>
/// ステータスバー（仕様書9.2・9.9、24px）。エディタの状態と接ぎ木状態を表示するのみの
/// 薄いUserControl。DataContextはShellWindowから継承する（ShellViewModel）。
/// </summary>
public partial class StatusBarView : UserControl
{
    public StatusBarView()
    {
        InitializeComponent();
    }
}
