using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Graft.ViewModels;

namespace Graft.Views;

/// <summary>
/// 仕様書8.7 diffプレビュー本体。DataContext に <see cref="DiffViewModel"/> を受け取る。
/// </summary>
public partial class DiffView : UserControl
{
    public DiffView()
    {
        InitializeComponent();
    }

    // 8.4: コード表示のフォントサイズはCtrl+マウスホイールで変更する（プロジェクトごとの
    // 記憶自体はDiffViewModel.CodeFontSizeの変更を受けてメインウィンドウ側が行う）。
    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        if (DataContext is not DiffViewModel vm) return;

        vm.CodeFontSize += e.Delta > 0 ? 1 : -1;
        e.Handled = true;
    }
}

/// <summary>bool を GridLength（true: 1*、false: 0）へ変換する。並列表示の右カラム幅制御に使う。</summary>
public sealed class BoolToStarGridLengthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>bool を TextWrapping へ変換する。8.13の折り返しトグル用。</summary>
public sealed class BoolToTextWrappingConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? TextWrapping.Wrap : TextWrapping.NoWrap;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
