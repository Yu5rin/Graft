using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Graft.ViewModels;

namespace Graft.Views;

/// <summary>
/// 左ペイン下段「リビジョン履歴」（仕様書7.1〜7.3・8.2）。
/// summary全文検索・type絞り込み・日付範囲での絞り込みと、選択行の復元を提供する。
/// DataContextには <see cref="HistoryPaneViewModel"/> を親（ShellWindow）から割り当てる。
/// </summary>
public partial class HistoryPane : UserControl
{
    public HistoryPane()
    {
        InitializeComponent();
        // 修正2: 右クリックメニュー。Avaloniaの既定動作では右クリックだけでは選択行が
        // 変わらないため（左クリックのみ選択を更新する）、ContextMenuが開く前に
        // 明示的にその行を選択状態へ更新する（EditorPane.axaml.csのOnTabStripPointerPressed
        // と同じトンネル段階での先取り処理）。
        RevisionListBox.AddHandler(PointerPressedEvent, OnRevisionListPointerPressed, RoutingStrategies.Tunnel);
    }

    private void OnRevisionListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(RevisionListBox);
        if (!point.Properties.IsRightButtonPressed) return;
        if (FindAncestor<ListBoxItem>(e.Source as Visual) is not { DataContext: RevisionRowViewModel row }) return;

        RevisionListBox.SelectedItem = row;
    }

    private static T? FindAncestor<T>(Visual? node) where T : Visual
    {
        while (node is not null and not T)
        {
            node = node.GetVisualParent();
        }

        return node as T;
    }

    /// <summary>F6でのペイン間フォーカス移動先として、外部（ShellWindow）から参照する。</summary>
    public ListBox ListBoxElement => RevisionListBox;

    /// <summary>
    /// 画面上のチュートリアル（コーチマーク）が「復元を体験」ステップで指す対象として、
    /// 外部（ShellWindow.Tutorial.cs）から参照する単発復元（「このリビジョンを取り消す」）ボタン。
    /// </summary>
    public Button RestoreButtonElement => RestoreButton;

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
