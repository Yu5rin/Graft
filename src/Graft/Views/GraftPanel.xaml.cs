using System.Windows;
using System.Windows.Controls;

namespace Graft.Views;

/// <summary>
/// 接ぎ木パネル（仕様書9.2 下部パネル）。旧MainWindowの中央ブロック一覧・右ペインdiffを
/// 移設したもの。開閉状態そのものはShellViewModel.IsGraftPanelOpen（XAML側でElementName="Root"
/// を介して参照）に従うため、コードビハインドはF6ペイン巡回用の参照公開のみを担う。
/// </summary>
public partial class GraftPanel : UserControl
{
    public GraftPanel()
    {
        InitializeComponent();
    }

    /// <summary>F6のペイン巡回・OnPreviewKeyDownのSpace判定で使うブロック一覧。</summary>
    public ListBox ListBoxElement => BlockListBox;

    /// <summary>F6のペイン巡回で使うdiff表示ペインのホスト。</summary>
    public FrameworkElement DiffHost => DiffPaneHost;
}
