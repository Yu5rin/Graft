using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Graft.ViewModels;

namespace Graft.Views;

/// <summary>
/// <see cref="ProjectPaneState"/> を <see cref="EmptyStateMode"/> へ変換する。
/// Content は「本体の一覧をそのまま見せる」ため <see cref="EmptyStateMode.None"/> に対応する。
/// </summary>
public sealed class ProjectPaneStateConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ProjectPaneState.Loading => EmptyStateMode.Loading,
        ProjectPaneState.Empty => EmptyStateMode.Empty,
        ProjectPaneState.Error => EmptyStateMode.Error,
        _ => EmptyStateMode.None,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 左ペイン上段「プロジェクト一覧」（仕様書3.2・8.2）。
/// D&Dまたはフォルダ選択ボタンでプロジェクトを登録できる。DataContextには
/// <see cref="ProjectPaneViewModel"/> を親（MainWindow）から割り当てる。
/// </summary>
public partial class ProjectPane : UserControl
{
    public ProjectPane()
    {
        InitializeComponent();
    }

    /// <summary>F6でのペイン間フォーカス移動先として、外部（MainWindow）から参照する。</summary>
    public ListBox ListBoxElement => ProjectListBox;

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>フォルダのドラッグ＆ドロップによる登録（仕様書3.2）。</summary>
    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }
        if (DataContext is not ProjectPaneViewModel viewModel)
        {
            return;
        }

        var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
        foreach (var path in paths)
        {
            var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder))
            {
                await viewModel.RegisterFolderAsync(folder).ConfigureAwait(true);
            }
        }
    }
}
