using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Graft.ViewModels;

namespace Graft.Views;

/// <summary>
/// <see cref="ExplorerViewModel.HasProject"/> を <see cref="EmptyStateMode"/> へ変換する
/// （仕様書9.2の空状態表示）。読み込み中インジケータはビュー上端の専用ProgressBarで表すため、
/// ここでは Empty（プロジェクト未選択）／None（本体のツリーを表示）の2状態のみ扱う。
/// </summary>
public sealed class ExplorerStateConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? EmptyStateMode.None : EmptyStateMode.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// ノードの除外状態と「除外ファイルを表示」トグルから表示可否を決める
/// （仕様書4.2「除外中のノードはグレー表示」「除外ファイルを表示トグル」）。
/// </summary>
public sealed class ExcludedNodeVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var isExcluded = values.Length > 0 && values[0] is true;
        var showExcluded = values.Length > 1 && values[1] is true;
        return !isExcluded || showExcluded ? Visibility.Visible : Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// サイドビューの「エクスプローラ」（仕様書4.2）。プロジェクトルート配下のファイルツリー表示・
/// 操作・監視反映を行う。DataContextには<see cref="ExplorerViewModel"/>を親から割り当てる。
/// </summary>
public partial class ExplorerView : UserControl
{
    public ExplorerView()
    {
        InitializeComponent();
    }

    private ExplorerViewModel? ViewModel => DataContext as ExplorerViewModel;

    /// <summary>TreeViewのSelectedItemは読み取り専用のため、コードビハインドでViewModelへ橋渡しする。</summary>
    private void OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (ViewModel is { } vm) vm.SelectedNode = e.NewValue as FileNodeViewModel;
    }

    /// <summary>
    /// TreeViewItemのMouseDoubleClickはネストした全ての祖先アイテムへバブリングするため、
    /// 実際にクリックされた（選択状態になった）アイテムのみで処理する。
    /// </summary>
    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TreeViewItem { IsSelected: true, DataContext: FileNodeViewModel node } || ViewModel is not { } vm)
        {
            return;
        }
        if (vm.OpenCommand.CanExecute(node)) vm.OpenCommand.Execute(node);
        e.Handled = true;
    }
}
