using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Graft.ViewModels;

namespace Graft.Views;

/// <summary>
/// <see cref="EditorPane"/> の分割ファイル（1ファイル400行上限のため）。
/// 「タブが増えたときに目的のタブへ到達できない」問題への対応をまとめる:
///   1. タブ幅の自動縮小そのものは<see cref="TabStripPanel"/>が担当し、ここでは触らない。
///   2. 左右スクロールボタンのクリック・状態（表示/有効）の同期。
///   3. タブ一覧ドロップダウン（検索して選ぶ）。
///   4. タブ列の上でのマウスホイール横スクロール。
///   + 選択中のタブが常に見えるようにする自動スクロール（Ctrl+Tab・クイックオープン等、
///     ActiveTabが変わる経路すべてに共通のフックとして<see cref="ApplyActiveTab"/>から呼ぶ）。
/// </summary>
public partial class EditorPane
{
    private TabStripPanel? _tabStripPanel;
    private readonly ObservableCollection<EditorTabViewModel> _tabPickerItems = new();

    /// <summary>コンストラクタから1回だけ呼ぶ（EditorPane.axaml.cs参照）。</summary>
    private void InitializeTabStrip()
    {
        TabPickerList.ItemsSource = _tabPickerItems;

        // 依頼3: ドロップダウンを開くたびに検索欄をリセットし、絞り込み結果を作り直す。
        // FlyoutはButton.FlyoutプロパティのXAML要素にx:Nameを付けても名前付きフィールドが
        // 生成されない（テンプレート的な扱いのため）ので、実行時にButtonから取得して購読する。
        if (TabListDropDownButton.Flyout is FlyoutBase flyout) flyout.Opened += OnTabListFlyoutOpened;

        // 依頼4: タブ列の上でのマウスホイール横スクロール。ドラッグ並べ替え（PointerPressed/
        // Moved/Released）と同じ理由でトンネル段階で拾う。TabStripRow全体（左右ボタン・
        // ドロップダウンも含む）を対象にすることで、タブ本体以外の余白にホイールを当てても
        // 一貫して動く。
        TabStripRow.AddHandler(PointerWheelChangedEvent, OnTabStripPointerWheelChanged, RoutingStrategies.Tunnel);

        // TabStripPanel（ItemsPanel）はListBoxの視覚ツリーが実際に構築されるまで存在しない
        // （テンプレート適用はレイアウトの一部として遅延する）ため、LayoutUpdatedのたびに
        // 解決を試み、見つかり次第ScrollStateChangedへ購読する（依頼2のボタン表示/有効状態の
        // 同期。ウィンドウのリサイズで収まる/収まらないが変わる場合もここで拾える）。
        // 選択中タブの自動スクロールはこれとは別に、ActiveTabが変わった時点で1回だけ
        // Dispatcher.Background優先度で試みる（ScheduleEnsureTabVisible参照。あらゆる
        // LayoutUpdatedのたびに追従させると、依頼4のホイール/依頼2のボタンによる手動スクロール
        // 自体もレイアウト変化を起こすため、そのたびにアクティブタブへスクロールが押し戻されて
        // しまい、手動スクロールが機能しなくなる）。
        TabStrip.LayoutUpdated += (_, _) => ResolveTabStripPanel();
    }

    private void UninitializeTabStrip()
    {
        if (_tabStripPanel is not null) _tabStripPanel.ScrollStateChanged -= OnTabStripScrollStateChanged;
        if (TabListDropDownButton.Flyout is FlyoutBase flyout) flyout.Opened -= OnTabListFlyoutOpened;
    }

    // ------------------------------------------------------------------
    // 選択中タブの自動スクロール（忘れられがちな注意点として依頼に明記されている）
    // ------------------------------------------------------------------

    /// <summary>
    /// ActiveTabが変わるたびに<see cref="ApplyActiveTab"/>から呼ぶ。そのタブが画面外にあれば
    /// スクロールして見せる。開いた直後・Document/Diff間の切替直後はレイアウトが未確定で
    /// タブのコンテナ（ListBoxItem）がまだ無いことがあるため、<see cref="RestoreViewStateFrom"/>の
    /// 遅延スクロール復元と同じ理由でDispatcherPriority.Background（レイアウト・描画より後に
    /// 走る）まで1テンポ遅らせてから試みる。
    /// </summary>
    private void ScheduleEnsureTabVisible(EditorTabViewModel? tab)
    {
        if (tab is null) return;
        Dispatcher.UIThread.Post(() =>
        {
            // 遅延実行中に別タブへ切り替わっていたら何もしない（RestoreViewStateFromと同じガード）。
            if (!ReferenceEquals(_viewModel?.ActiveTab, tab)) return;

            var panel = ResolveTabStripPanel();
            var container = panel is null ? null : FindTabContainer(tab);
            if (panel is null || container is null) return;

            panel.EnsureVisible(container);
        }, DispatcherPriority.Background);
    }

    private ListBoxItem? FindTabContainer(EditorTabViewModel tab)
        => TabStrip.GetVisualDescendants().OfType<ListBoxItem>()
            .FirstOrDefault(i => ReferenceEquals(i.DataContext, tab));

    private TabStripPanel? ResolveTabStripPanel()
    {
        if (_tabStripPanel is not null) return _tabStripPanel;
        var found = TabStrip.GetVisualDescendants().OfType<TabStripPanel>().FirstOrDefault();
        if (found is null) return null;

        _tabStripPanel = found;
        _tabStripPanel.ScrollStateChanged += OnTabStripScrollStateChanged;
        OnTabStripScrollStateChanged(_tabStripPanel, EventArgs.Empty); // 初期状態を即座に反映する。
        return _tabStripPanel;
    }

    // ------------------------------------------------------------------
    // 依頼2: 左右スクロールボタン
    // ------------------------------------------------------------------

    private void OnTabStripScrollStateChanged(object? sender, EventArgs e)
    {
        if (sender is not TabStripPanel panel) return;

        // 収まっているとき（縮小だけで全タブが入ったとき）はボタンごと隠す。
        // 両端そろって出し、端に着いた側だけIsEnabled=falseで押せなくする。
        TabScrollLeftButton.IsVisible = panel.HasOverflow;
        TabScrollRightButton.IsVisible = panel.HasOverflow;
        TabScrollLeftButton.IsEnabled = panel.Offset > 0.5;
        TabScrollRightButton.IsEnabled = panel.Offset < panel.MaxOffset - 0.5;
    }

    private void OnTabScrollLeftClicked(object? sender, RoutedEventArgs e)
    {
        if (ResolveTabStripPanel() is { } panel) panel.Offset -= TabStripPanel.ScrollStep;
    }

    private void OnTabScrollRightClicked(object? sender, RoutedEventArgs e)
    {
        if (ResolveTabStripPanel() is { } panel) panel.Offset += TabStripPanel.ScrollStep;
    }

    // ------------------------------------------------------------------
    // 依頼4: タブ列の上でのマウスホイール横スクロール
    // ------------------------------------------------------------------

    /// <summary>
    /// ドラッグ並べ替え（<see cref="_isDragging"/>）と同じ領域で起きるため、ドラッグ中は
    /// ホイールでの横スクロールを無視して干渉を避ける（依頼の実装上の注意）。
    /// </summary>
    private void OnTabStripPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_isDragging) return;
        var panel = ResolveTabStripPanel();
        if (panel is null) return;

        var delta = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;
        if (delta == 0) return;

        panel.Offset -= delta * TabStripPanel.ScrollStep;
        e.Handled = true;
    }

    // ------------------------------------------------------------------
    // 依頼3: タブ一覧ドロップダウン
    // ------------------------------------------------------------------

    private void OnTabListFlyoutOpened(object? sender, EventArgs e)
    {
        TabPickerSearchBox.Text = string.Empty;
        RefreshTabPickerItems(string.Empty);
        // Flyoutが開いた直後はまだフォーカスを受け取れないことがあるため、Background優先度で
        // 1テンポ遅らせる（RestoreViewStateFromの遅延スクロールと同じ理由）。
        Dispatcher.UIThread.Post(() => TabPickerSearchBox.Focus(), DispatcherPriority.Background);
    }

    private void OnTabPickerSearchTextChanged(object? sender, TextChangedEventArgs e)
        => RefreshTabPickerItems(TabPickerSearchBox.Text ?? string.Empty);

    /// <summary>ファイル名（Title）またはツールチップ（相対パス等）に部分一致するタブだけを
    /// 一覧へ反映する。Tabsの表示順のまま絞り込むだけなので、並び順はタブ見出しと一致する。</summary>
    private void RefreshTabPickerItems(string filter)
    {
        _tabPickerItems.Clear();
        if (_viewModel is null) return;

        foreach (var tab in _viewModel.Tabs)
        {
            if (filter.Length == 0
                || tab.Title.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || tab.ToolTipText.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                _tabPickerItems.Add(tab);
            }
        }
    }

    private void OnTabPickerSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is null) return;
        if (TabPickerList.SelectedItem is EditorTabViewModel tab)
        {
            _viewModel.ActiveTab = tab;
            TabListDropDownButton.Flyout?.Hide();
        }
    }

    /// <summary>検索欄でEnter: 絞り込み結果の先頭（＝Tabsの表示順で最初に一致したタブ）を開く。</summary>
    private void OnTabPickerSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _viewModel is null || _tabPickerItems.Count == 0) return;

        _viewModel.ActiveTab = _tabPickerItems[0];
        TabListDropDownButton.Flyout?.Hide();
        e.Handled = true;
    }
}
