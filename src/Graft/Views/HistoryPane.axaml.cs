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

    /// <summary>課題2-2: 種別絞り込みの既定表示（「すべての種別」）をテストから確認できるよう公開する。</summary>
    public ComboBox TypeComboElement => TypeCombo;

    /// <summary>
    /// 課題2-2: 絞り込み解除ボタン。ViewModel側のTypeFilter（絞り込みの実体、null＝解除）を
    /// 直接nullへ戻す。TypeFilterのsetterがSelectedTypeOptionの変更通知も出すため、
    /// ドロップダウンの表示は自動的に「すべての種別」へ戻る（ComboBox.SelectedItemを
    /// 直接操作しないのは、AllTypesOptionという表示専用の値をコードビハインドにも
    /// 持たせる二重管理を避けるため）。
    /// </summary>
    private void OnClearTypeFilter(object? sender, RoutedEventArgs e)
    {
        if (DataContext is HistoryPaneViewModel vm) vm.TypeFilter = null;
    }
}
