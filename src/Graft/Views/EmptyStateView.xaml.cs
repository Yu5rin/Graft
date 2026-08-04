using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Graft.Core;

namespace Graft.Views;

/// <summary>
/// 空文字列（または既定値）を <see cref="Visibility.Collapsed"/> に変換する汎用コンバータ。
/// 一覧の補助テキスト（タグ・ショートカット番号バッジ等）の表示可否に共通で使う。
/// </summary>
public sealed class EmptyStringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>真偽値を反転する汎用コンバータ。</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && !b;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && !b;
}

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
/// </summary>
public partial class EmptyStateView : UserControl
{
    /// <summary>読み込み中インジケータの表示遅延。200ms未満で完了する処理では表示しない（8.8）。</summary>
    private static readonly TimeSpan LoadingIndicatorDelay = TimeSpan.FromMilliseconds(200);

    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(EmptyStateMode), typeof(EmptyStateView),
        new PropertyMetadata(EmptyStateMode.None, OnStateChanged));

    public static readonly DependencyProperty IconGeometryProperty = DependencyProperty.Register(
        nameof(IconGeometry), typeof(Geometry), typeof(EmptyStateView), new PropertyMetadata(null));

    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(string), typeof(EmptyStateView), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ActionTextProperty = DependencyProperty.Register(
        nameof(ActionText), typeof(string), typeof(EmptyStateView),
        new PropertyMetadata(string.Empty, OnActionTextChanged));

    public static readonly DependencyProperty ActionCommandProperty = DependencyProperty.Register(
        nameof(ActionCommand), typeof(ICommand), typeof(EmptyStateView), new PropertyMetadata(null));

    public static readonly DependencyProperty IssueProperty = DependencyProperty.Register(
        nameof(Issue), typeof(GraftIssue), typeof(EmptyStateView), new PropertyMetadata(null, OnIssueChanged));

    private readonly DispatcherTimer _loadingTimer;

    public EmptyStateView()
    {
        InitializeComponent();
        _loadingTimer = new DispatcherTimer { Interval = LoadingIndicatorDelay };
        _loadingTimer.Tick += (_, _) =>
        {
            _loadingTimer.Stop();
            LoadingBar.Visibility = Visibility.Visible;
        };
    }

    /// <summary>現在の表示状態。</summary>
    public EmptyStateMode State
    {
        get => (EmptyStateMode)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    /// <summary>空状態で表示するアイコンのジオメトリ。呼び出し側が用途に応じたアイコンを渡す。</summary>
    public Geometry? IconGeometry
    {
        get => (Geometry?)GetValue(IconGeometryProperty);
        set => SetValue(IconGeometryProperty, value);
    }

    /// <summary>空状態の1行説明。次の操作を示す文言にする（例:「AIの出力をコピーして Ctrl+V」）。</summary>
    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    /// <summary>空状態の主要アクションのラベル。未指定（空文字）ならボタンごと非表示にする。</summary>
    public string ActionText
    {
        get => (string)GetValue(ActionTextProperty);
        set => SetValue(ActionTextProperty, value);
    }

    /// <summary>空状態の主要アクションのコマンド。</summary>
    public ICommand? ActionCommand
    {
        get => (ICommand?)GetValue(ActionCommandProperty);
        set => SetValue(ActionCommandProperty, value);
    }

    /// <summary>エラー状態で表示する問題。エラーコードと対処方法を併記する（8.8）。</summary>
    public GraftIssue? Issue
    {
        get => (GraftIssue?)GetValue(IssueProperty);
        set => SetValue(IssueProperty, value);
    }

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((EmptyStateView)d).ApplyState((EmptyStateMode)e.NewValue);

    private static void OnActionTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (EmptyStateView)d;
        var text = (string)e.NewValue;
        view.ActionButton.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(view.ActionButton, text);
    }

    private static void OnIssueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (EmptyStateView)d;
        var issue = (GraftIssue?)e.NewValue;
        view.ErrorSummaryText.Text = issue?.ToDisplayText() ?? string.Empty;
        view.ErrorRemedyText.Text = issue is null ? string.Empty : $"対処: {issue.Remedy}";
    }

    private void ApplyState(EmptyStateMode mode)
    {
        _loadingTimer.Stop();
        LoadingBar.Visibility = Visibility.Collapsed;
        EmptyPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;

        switch (mode)
        {
            case EmptyStateMode.Loading:
                _loadingTimer.Start();
                break;
            case EmptyStateMode.Empty:
                EmptyPanel.Visibility = Visibility.Visible;
                break;
            case EmptyStateMode.Error:
                ErrorPanel.Visibility = Visibility.Visible;
                break;
            case EmptyStateMode.None:
            default:
                break;
        }
    }
}
