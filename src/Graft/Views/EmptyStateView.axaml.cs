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
        else if (change.Property == IssueProperty) ApplyIssue(change.GetNewValue<GraftIssue?>());
    }

    private void ApplyActionText(string text)
    {
        ActionButton.IsVisible = !string.IsNullOrEmpty(text);
        AutomationProperties.SetName(ActionButton, text);
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
