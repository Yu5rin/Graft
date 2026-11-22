using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Graft.Views;

/// <summary>
/// 依頼の観点（チップ）のパネル（仕様書_ブックの情報と依頼の観点 6.2）。見た目は PromptFocusPanel.axaml。
/// </summary>
public partial class PromptFocusPanel : UserControl
{
    /// <summary>
    /// 「たたむ」ボタンを出すか。収集の窓だけ true にする（依頼文コピーのドロップダウンは幅が狭く、
    /// 縦に積まれるので、従来どおりボタン無し）。
    /// </summary>
    public static readonly StyledProperty<bool> IsCollapsibleProperty =
        AvaloniaProperty.Register<PromptFocusPanel, bool>(nameof(IsCollapsible));

    /// <summary>
    /// たたんでいるか。既定は開いた状態（チップを最初からすべて並べる、の決定のまま）。
    /// 記憶しない（ViewModel・設定のどちらにも持たない）ので、窓を開くたびに開いた状態になる。
    /// </summary>
    public static readonly StyledProperty<bool> IsCollapsedProperty =
        AvaloniaProperty.Register<PromptFocusPanel, bool>(nameof(IsCollapsed));

    public PromptFocusPanel()
    {
        InitializeComponent();
        UpdateCollapseButton();
    }

    public bool IsCollapsible
    {
        get => GetValue(IsCollapsibleProperty);
        set => SetValue(IsCollapsibleProperty, value);
    }

    public bool IsCollapsed
    {
        get => GetValue(IsCollapsedProperty);
        set => SetValue(IsCollapsedProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsCollapsedProperty) UpdateCollapseButton();
    }

    /// <summary>
    /// ボタンの読み上げ名を、いま押すと起きることに合わせる（「たたむ」「開く」を 1 つの名前に並べない）。
    /// 状態そのものは、見えている部品（開いているときは一覧、たたんでいるときは 1 行の要約）で伝わる。
    /// </summary>
    private void UpdateCollapseButton()
        => AutomationProperties.SetName(CollapseButton, IsCollapsed ? "依頼の観点の一覧を開く" : "依頼の観点の一覧をたたむ");

    private void OnCollapseClicked(object? sender, RoutedEventArgs e) => IsCollapsed = !IsCollapsed;
}
