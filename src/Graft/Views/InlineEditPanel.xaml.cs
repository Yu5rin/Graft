using System.Windows.Controls;

namespace Graft.Views;

/// <summary>
/// 仕様書8.7: マッチ失敗ブロックのSEARCH部インライン編集パネル。
/// DataContext に <see cref="Graft.ViewModels.InlineEditViewModel"/> を受け取る。
/// </summary>
public partial class InlineEditPanel : UserControl
{
    public InlineEditPanel()
    {
        InitializeComponent();
    }
}
