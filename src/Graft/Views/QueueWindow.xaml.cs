using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Graft.ViewModels;

namespace Graft.Views;

/// <summary>キューが空かどうかを <see cref="EmptyStateMode"/> へ変換する。</summary>
public sealed class QueueEmptyStateConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? EmptyStateMode.Empty : EmptyStateMode.None;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 仕様書4.10 パッチキュー管理ウィンドウ。キュー内のブロックを一覧表示し、個別削除・全削除・
/// 結合して適用フローへ渡す操作を行う。DataContextには <see cref="QueueViewModel"/> を割り当てる。
/// 8.10: Escで閉じる。
/// </summary>
public partial class QueueWindow : Window
{
    public QueueWindow(QueueViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    /// <summary>「結合して適用」はメインウィンドウ側の解析・適用フローへ委譲するため、ここでは閉じるのみ。</summary>
    private void OnMergeClicked(object sender, RoutedEventArgs e)
    {
        ((QueueViewModel)DataContext).MergeCommand.Execute(null);
        Close();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }
}
