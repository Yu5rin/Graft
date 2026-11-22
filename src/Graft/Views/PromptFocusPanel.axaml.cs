using Avalonia.Controls;

namespace Graft.Views;

/// <summary>
/// 依頼の観点のチップを並べるパネル（仕様書_ブックの情報と依頼の観点 6.2）。
/// DataContextには <see cref="Graft.ViewModels.PromptFocusSelectionViewModel"/> を受け取る。
/// 依頼文コピーのドロップダウンとコンテキスト収集の窓の両方に置く。
/// </summary>
public partial class PromptFocusPanel : UserControl
{
    public PromptFocusPanel() => InitializeComponent();
}
