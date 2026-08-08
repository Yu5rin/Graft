using System.Windows.Input;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Graft.Core;

namespace Graft.Views;

/// <summary>一覧・ペインが取りうる3状態（仕様書8.8）。None は「本体のリストをそのまま表示」を表す。</summary>
public enum EmptyStateMode
{
    None,
    Loading,
    Empty,
    Error,
}

/// <summary>
/// 空・読み込み中・エラーの3状態を1つで表現する再利用可能なオーバーレイ（仕様書8.8）。
/// 一覧・ペイン側は本体コンテンツと同じセルにこのコントロールを重ねて使う想定。
/// v2.0のWPF版からの移植（19章 L3）。DependencyPropertyは<see cref="StyledProperty{TValue}"/>へ、
/// Visibilityの切り替えは<see cref="Visual.IsVisible"/>へ置き換えている。
/// </summary>
public partial class EmptyStateView : UserControl
{
    /// <summary>読み込み中インジケータの表示遅延。200ms未満で完了する処理では表示しない（8.8）。</summary>
    private static readonly TimeSpan LoadingIndicatorDelay = TimeSpan.FromMilliseconds(200);

    public static readonly StyledProperty<EmptyStateMode> StateProperty =
        AvaloniaProperty.Register<EmptyStateView, EmptyStateMode>(nameof(State));

    public static readonly StyledProperty<Geometry?> IconGeometryProperty =
        AvaloniaProperty.Register<EmptyStateView, Geometry?>(nameof(IconGeometry));

    public static readonly StyledProperty<string> MessageProperty =
        AvaloniaProperty.Register<EmptyStateView, string>(nameof(Message), string.Empty);

    public static readonly StyledProperty<string> ActionTextProperty =
        AvaloniaProperty.Register<EmptyStateView, string>(nameof(ActionText), string.Empty);

    public static readonly StyledProperty<ICommand?> ActionCommandProperty =
        AvaloniaProperty.Register<EmptyStateView, ICommand?>(nameof(ActionCommand));

    /// <summary>
    /// 主要アクションボタンのツールチップ（標準の説明）。利用者からの指摘（ボタン名だけでは
    /// 機能が伝わらない）への対応。空状態は複数の画面（GraftPanel・ProjectPane等）で使い回すため、
    /// 文言はActionTextと同様に呼び出し側が用途に応じて渡す。未指定なら何も表示しない。
    /// </summary>
    public static readonly StyledProperty<string?> ActionTooltipProperty =
        AvaloniaProperty.Register<EmptyStateView, string?>(nameof(ActionTooltip));

    /// <summary>
    /// 主要アクションボタンのツールチップ（くわしい説明）。設定「操作の説明」で「くわしい説明」を
    /// 選んだときに<see cref="ActionTooltip"/>の代わりに使う（<see cref="HelpTip"/>参照）。
    /// 未指定なら「くわしい説明」を選んでいても<see cref="ActionTooltip"/>にフォールバックする。
    /// </summary>
    public static readonly StyledProperty<string?> ActionTooltipDetailedProperty =
        AvaloniaProperty.Register<EmptyStateView, string?>(nameof(ActionTooltipDetailed));

    public static readonly StyledProperty<string> SecondaryActionTextProperty =
        AvaloniaProperty.Register<EmptyStateView, string>(nameof(SecondaryActionText), string.Empty);

    public static readonly StyledProperty<ICommand?> SecondaryActionCommandProperty =
        AvaloniaProperty.Register<EmptyStateView, ICommand?>(nameof(SecondaryActionCommand));

    /// <summary>副次アクションボタンのツールチップ（標準の説明）。<see cref="ActionTooltipProperty"/>と同じ考え方。</summary>
    public static readonly StyledProperty<string?> SecondaryActionTooltipProperty =
        AvaloniaProperty.Register<EmptyStateView, string?>(nameof(SecondaryActionTooltip));

    /// <summary>副次アクションボタンのツールチップ（くわしい説明）。<see cref="ActionTooltipDetailedProperty"/>と同じ考え方。</summary>
    public static readonly StyledProperty<string?> SecondaryActionTooltipDetailedProperty =
        AvaloniaProperty.Register<EmptyStateView, string?>(nameof(SecondaryActionTooltipDetailed));

    public static readonly StyledProperty<GraftIssue?> IssueProperty =
        AvaloniaProperty.Register<EmptyStateView, GraftIssue?>(nameof(Issue));

    private readonly DispatcherTimer _loadingTimer;

    public EmptyStateView()
    {
        InitializeComponent();

        _loadingTimer = new DispatcherTimer { Interval = LoadingIndicatorDelay };
        _loadingTimer.Tick += (_, _) =>
        {
            _loadingTimer.Stop();
            LoadingBar.IsVisible = true;
        };

        ApplyState(State);
        ApplyActionText(ActionText);
        ApplySecondaryActionText(SecondaryActionText);
        ApplyIssue(Issue);
    }

    /// <summary>現在の表示状態。</summary>
    public EmptyStateMode State
    {
        get => GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    /// <summary>空状態で表示するアイコンのジオメトリ。呼び出し側が用途に応じたアイコンを渡す。</summary>
    public Geometry? IconGeometry
    {
        get => GetValue(IconGeometryProperty);
        set => SetValue(IconGeometryProperty, value);
    }

    /// <summary>空状態の1行説明。次の操作を示す文言にする（例:「AIの出力をコピーして Ctrl+V」）。</summary>
    public string Message
    {
        get => GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    /// <summary>空状態の主要アクションのラベル。未指定（空文字）ならボタンごと非表示にする。</summary>
    public string ActionText
    {
        get => GetValue(ActionTextProperty);
        set => SetValue(ActionTextProperty, value);
    }

    /// <summary>空状態の主要アクションのコマンド。</summary>
    public ICommand? ActionCommand
    {
        get => GetValue(ActionCommandProperty);
        set => SetValue(ActionCommandProperty, value);
    }

    /// <summary>主要アクションボタンのツールチップ（目的と使いどころを1〜2文で）。未指定なら表示しない。</summary>
    public string? ActionTooltip
    {
        get => GetValue(ActionTooltipProperty);
        set => SetValue(ActionTooltipProperty, value);
    }

    /// <summary>主要アクションボタンのくわしい説明。<see cref="ActionTooltipDetailedProperty"/>参照。</summary>
    public string? ActionTooltipDetailed
    {
        get => GetValue(ActionTooltipDetailedProperty);
        set => SetValue(ActionTooltipDetailedProperty, value);
    }

    /// <summary>
    /// 空状態の副次アクションのラベル（4.1「ファイルから解析」等）。未指定（空文字）なら
    /// ボタンごと非表示にする。主要アクションと違い、無くても空状態として成立する用途向け。
    /// </summary>
    public string SecondaryActionText
    {
        get => GetValue(SecondaryActionTextProperty);
        set => SetValue(SecondaryActionTextProperty, value);
    }

    /// <summary>空状態の副次アクションのコマンド。</summary>
    public ICommand? SecondaryActionCommand
    {
        get => GetValue(SecondaryActionCommandProperty);
        set => SetValue(SecondaryActionCommandProperty, value);
    }

    /// <summary>副次アクションボタンのツールチップ。<see cref="ActionTooltip"/>と同じ考え方。</summary>
    public string? SecondaryActionTooltip
    {
        get => GetValue(SecondaryActionTooltipProperty);
        set => SetValue(SecondaryActionTooltipProperty, value);
    }

    /// <summary>副次アクションボタンのくわしい説明。<see cref="ActionTooltipDetailed"/>と同じ考え方。</summary>
    public string? SecondaryActionTooltipDetailed
    {
        get => GetValue(SecondaryActionTooltipDetailedProperty);
        set => SetValue(SecondaryActionTooltipDetailedProperty, value);
    }

    /// <summary>エラー状態で表示する問題。エラーコードと対処方法を併記する（8.8）。</summary>
    public GraftIssue? Issue
    {
        get => GetValue(IssueProperty);
        set => SetValue(IssueProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == StateProperty) ApplyState(change.GetNewValue<EmptyStateMode>());
        else if (change.Property == ActionTextProperty) ApplyActionText(change.GetNewValue<string>());
        else if (change.Property == SecondaryActionTextProperty) ApplySecondaryActionText(change.GetNewValue<string>());
        else if (change.Property == IssueProperty) ApplyIssue(change.GetNewValue<GraftIssue?>());
    }

    private void ApplyActionText(string text)
    {
        ActionButton.IsVisible = !string.IsNullOrEmpty(text);
        AutomationProperties.SetName(ActionButton, text);
    }

    private void ApplySecondaryActionText(string text)
    {
        SecondaryActionButton.IsVisible = !string.IsNullOrEmpty(text);
        AutomationProperties.SetName(SecondaryActionButton, text);
    }

    private void ApplyIssue(GraftIssue? issue)
    {
        ErrorSummaryText.Text = issue?.ToDisplayText() ?? string.Empty;
        ErrorRemedyText.Text = issue is null ? string.Empty : $"対処: {issue.Remedy}";
    }

    private void ApplyState(EmptyStateMode mode)
    {
        _loadingTimer.Stop();
        LoadingBar.IsVisible = false;
        EmptyPanel.IsVisible = false;
        ErrorPanel.IsVisible = false;

        switch (mode)
        {
            case EmptyStateMode.Loading:
                _loadingTimer.Start();
                break;
            case EmptyStateMode.Empty:
                EmptyPanel.IsVisible = true;
                break;
            case EmptyStateMode.Error:
                ErrorPanel.IsVisible = true;
                break;
            case EmptyStateMode.None:
            default:
                break;
        }
    }
}
