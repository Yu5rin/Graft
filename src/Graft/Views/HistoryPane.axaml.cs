using Avalonia.Controls;
using Avalonia.Interactivity;
using Graft.ViewModels;

namespace Graft.Views;

/// <summary>
/// 左ペイン下段「リビジョン履歴」（仕様書7.1〜7.3・8.2）。
/// summary全文検索・type絞り込み・日付範囲での絞り込みと、選択行の復元を提供する。
/// DataContextには <see cref="HistoryPaneViewModel"/> を親（ShellWindow）から割り当てる。
/// </summary>
public partial class HistoryPane : UserControl
{
    public HistoryPane() => InitializeComponent();

    /// <summary>F6でのペイン間フォーカス移動先として、外部（ShellWindow）から参照する。</summary>
    public ListBox ListBoxElement => RevisionListBox;

    /// <summary>Ctrl+F でのフォーカス移動先として、外部（ShellWindow）から参照する。</summary>
    public TextBox SearchBoxElement => KeywordBox;

    private void OnClearTypeFilter(object? sender, RoutedEventArgs e) => TypeCombo.SelectedItem = null;
}
