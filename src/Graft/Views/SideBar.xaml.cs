using System.Windows.Controls;

namespace Graft.Views;

/// <summary>
/// サイドバー（仕様書9.2、左端48px固定）。エクスプローラ／プロジェクト／履歴／検索の
/// 4アイコンのみを持つ薄いUserControl。DataContextはShellWindowから継承する
/// （ShellViewModel）ため、独自のコードビハインドロジックは持たない。
/// </summary>
public partial class SideBar : UserControl
{
    public SideBar()
    {
        InitializeComponent();
    }
}
