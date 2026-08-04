using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using Graft.Core;
using Graft.ViewModels;

namespace Graft.Views;

/// <summary>
/// 10章のコンテキスト収集UI。収集モードの選択、ファイルのチェック選択、除外規則の確認、
/// 出力前の概算トークン数表示とコピーを行う。8.10: Escで閉じる。
/// </summary>
public partial class ContextCollectWindow : Window
{
    public ContextCollectWindow(ContextCollectViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        InitializeComponent();
        DataContext = vm;
        Loaded += async (_, _) => await vm.InitializeAsync().ConfigureAwait(true);
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }
}

// InverseBooleanToVisibilityConverter・InverseBooleanConverter は
// Views/HistoryPane.xaml.cs・Views/EmptyStateView.xaml.cs（他担当）に同名・同機能のものが
// 既に定義されているため、ここでは重複定義せずそちらを再利用する（同一名前空間のため
// using 追加は不要）。

/// <summary>値がnullでなければ<see cref="Visibility.Visible"/>、nullなら<see cref="Visibility.Collapsed"/>を返す。</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary><see cref="GraftIssue"/>を「コード＋内容＋対処方法」の1行表示文字列へ変換する（8.8章）。</summary>
public sealed class IssueToDisplayTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is GraftIssue issue ? $"{issue.ToDisplayText()}（対処: {issue.Remedy}）" : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// チェックボックスの活性状態を、「除外されていない」かつ「収集モードがファイル選択を伴う」の
/// 両方を満たす場合のみ true にする（ツリーのみモードではチェックしても意味が無いため無効化する）。
/// </summary>
public sealed class FileCheckEnabledConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var isExcluded = values.Length > 0 && values[0] is true;
        var showFileTree = values.Length > 1 && values[1] is true;
        return !isExcluded && showFileTree;
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>コレクションの件数が1件以上なら<see cref="Visibility.Visible"/>、0件なら<see cref="Visibility.Collapsed"/>を返す。</summary>
public sealed class CollectionCountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is System.Collections.ICollection { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>コレクションの件数が0件なら<see cref="Visibility.Visible"/>、1件以上なら<see cref="Visibility.Collapsed"/>を返す（空状態表示用）。</summary>
public sealed class InverseCollectionCountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is System.Collections.ICollection { Count: > 0 } ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>ファイルツリーの階層深さ（int）を左インデント用の<see cref="Thickness"/>へ変換する。</summary>
public sealed class IndentToMarginConverter : IValueConverter
{
    private const double IndentPerLevel = 16.0;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var level = value is int i ? i : 0;
        return new Thickness(level * IndentPerLevel, 2, 0, 2);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
