using System.Globalization;
using System.Windows.Data;
using Graft.ViewModels;

namespace Graft.Views;

/// <summary><see cref="CenterPaneState"/> を <see cref="EmptyStateMode"/> へ変換する。</summary>
public sealed class CenterPaneStateConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        CenterPaneState.Loading => EmptyStateMode.Loading,
        CenterPaneState.Empty => EmptyStateMode.Empty,
        CenterPaneState.Error => EmptyStateMode.Error,
        _ => EmptyStateMode.None,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
