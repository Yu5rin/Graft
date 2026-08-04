using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Graft.ViewModels;

namespace Graft.Views;

/// <summary><see cref="HistoryPaneState"/> を <see cref="EmptyStateMode"/> へ変換する。</summary>
public sealed class HistoryPaneStateConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        HistoryPaneState.Loading => EmptyStateMode.Loading,
        HistoryPaneState.Empty => EmptyStateMode.Empty,
        HistoryPaneState.Error => EmptyStateMode.Error,
        _ => EmptyStateMode.None,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>true を <see cref="Visibility.Collapsed"/> に変換する（復元可否表示の反転用）。</summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// <see cref="DatePicker.SelectedDate"/>（DateTime?）と <see cref="HistoryPaneViewModel.DateFrom"/>
/// 等（DateTimeOffset?）を橋渡しする。
/// </summary>
public sealed class DateTimeOffsetConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is DateTimeOffset offset ? offset.LocalDateTime.Date : (DateTime?)null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is DateTime date ? new DateTimeOffset(date) : (DateTimeOffset?)null;
}

/// <summary>
/// 左ペイン下段「リビジョン履歴」（仕様書7.1〜7.3・8.2）。
/// summary全文検索・type絞り込み・日付範囲での絞り込みと、選択行の復元を提供する。
/// DataContextには <see cref="HistoryPaneViewModel"/> を親（MainWindow）から割り当てる。
/// </summary>
public partial class HistoryPane : UserControl
{
    public HistoryPane()
    {
        InitializeComponent();
    }

    /// <summary>F6でのペイン間フォーカス移動先として、外部（MainWindow）から参照する。</summary>
    public ListBox ListBoxElement => RevisionListBox;

    /// <summary>Ctrl+F でのフォーカス移動先として、外部（MainWindow）から参照する。</summary>
    public TextBox SearchBoxElement => KeywordBox;

    private void OnClearTypeFilter(object sender, RoutedEventArgs e)
    {
        TypeCombo.SelectedItem = null;
    }
}
