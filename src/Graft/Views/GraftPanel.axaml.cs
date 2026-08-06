using Avalonia.Controls;

namespace Graft.Views;

/// <summary>
/// 接ぎ木パネル（仕様書9.2 下部パネル）。9.2の全面改訂によりdiffはエディタ領域の専用タブへ
/// 移設したため、本パネルはブロック一覧・適用サマリ・操作ボタンに絞られる。開閉状態
/// そのものはShellViewModel.IsGraftPanelOpen（XAML側でElementName="Root"を介して参照）に
/// 従うため、コードビハインドはF6ペイン巡回用の参照公開のみを担う。
/// </summary>
public partial class GraftPanel : UserControl
{
    public GraftPanel() => InitializeComponent();

    /// <summary>F6のペイン巡回・Space判定で使うブロック一覧。</summary>
    public ListBox ListBoxElement => BlockListBox;

    /// <summary>
    /// F6のペイン巡回の4番目の停留先。diffがエディタ領域のタブへ移設され、このパネル内に
    /// diff表示が無くなったため、代わりに本パネル内で最後の操作要素（適用ボタン）を指す。
    /// </summary>
    public Control DiffHost => ApplyButtonElement;
}
