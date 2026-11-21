using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Graft.ViewModels;

namespace Graft.Views;

/// <summary>
/// 仕様書8.7 diffプレビュー本体。DataContext に <see cref="DiffViewModel"/> を受け取る。
/// v2.0のWPF版からの移植（19章 L3）。
/// </summary>
public partial class DiffView : UserControl
{
    public DiffView()
    {
        InitializeComponent();

        // 不具合1対応（実機フリーズ）: 「並列／統合」トグルの2つのRadioButtonは、以前は
        // XAML側で固定文字列 GroupName="DiffMode" を共有していた。RadioButtonの排他制御
        // （Avalonia.Controls.RadioButtonGroupManager）はGroupNameが同じもの同士をウィンドウ
        // 全体（Visual Root単位）でグループ化するため、DiffViewのインスタンスが複数同時に
        // 存在すると（通常の差分タブ＋履歴差分タブ、または履歴差分タブが複数ファイルを表示する
        // 場合）、本来は無関係なはずの別インスタンスのRadioButton同士が同じ排他グループに
        // 入ってしまっていた。
        //
        // 実機不具合の再現・原因特定: 履歴差分タブを開いた状態（HistoryDiffHostが実データを
        // 保持）で新しいパッチを解析すると、選択ブロックの差分がエディタ領域の新しいタブとして
        // 開く（EditorPaneViewModel.ShowDiffTab→ActiveTab変更）。このときEditorPane.Diff.axaml.cs
        // のApplyDiffTabは「DiffHost.DataContext = tab.Diff」を実行してから
        // 「HistoryDiffHost.DataContext = null」を実行する（実装上この順序）。この2行の間、
        // 一瞬だけ両方のDiffViewインスタンス（新しい差分タブ側・履歴差分タブ側）が同時に
        // 実データを保持した状態になる。この瞬間、DiffHost側のRadioButtonが
        // IsSideBySideの初期値（既定true）で新たにチェック状態になろうとし、
        // RadioButtonGroupManagerが「同じグループの中で既にチェックされている
        // 履歴差分タブ側のRadioButton」を強制的に未チェックへ倒す。ところがその未チェック化は
        // 履歴差分タブ側のDiffViewModel.IsSideBySideへの書き戻し（双方向バインディング）を伴い、
        // その結果としてペアのRadioButton（統合側）がチェックされ、それが今度は新しい差分タブ側の
        // RadioButtonを未チェックへ強制する……という形で、2つの独立したDiffViewModelインスタンスの
        // IsSideBySideが互いを永遠に打ち消し合う（両者とも「自分のペアのうち必ず1つはチェック
        // されているべき」というローカルな制約を持つのに対し、RadioButtonGroupManagerは
        // 「グループ全体で高々1つだけチェックされているべき」というグローバルな制約を強制する
        // ため、2つの制約が両立せず無限に振動し続ける）。UIスレッドはこの同期的な
        // PropertyChangedの連鎖から抜け出せず完全に応答不能になる（ヘッドレステストでの
        // 実測: dotnet-dumpで採取したハングダンプのコールスタックで
        // DiffViewModel.set_IsSideBySide → ... → RadioButtonGroupManager.OnCheckedChanged →
        // ... → DiffViewModel.set_IsSideBySide という再入を確認済み）。
        //
        // 対処: 「並列／統合」の排他はそもそも双方向バインディング
        // （IsChecked="{Binding IsSideBySide}" / IsChecked="{Binding !IsSideBySide}"）だけで
        // 完結しており、RadioButtonGroupManagerによる自動排他は本質的には不要（クリックで
        // IsSideBySideが確定し、その変化がペア側のバインディングへ伝播して自動的に排他される）。
        // ただしGroupNameを完全に取り除くと、キーボード操作（矢印キーでのグループ内移動）や
        // スクリーンリーダーへの「排他選択肢のペアである」という意味付け（8.14 アクセシビリティ）
        // が失われるため、GroupNameという仕組み自体は残しつつ、値をDiffViewのインスタンスごとに
        // 一意にする（Guid採番）。これにより「ペア内では排他」という意図した挙動は保ったまま、
        // 別インスタンス間の意図しない相互干渉を断つ。
        // 仕様書_VBA連携 7.6: 右クリックした行を覚えておく（メニューが開く前に、トンネル段階で行を特定する）。
        DiffListBox.AddHandler(ContextRequestedEvent, OnDiffContextRequested, RoutingStrategies.Tunnel);

        var groupName = "DiffMode-" + Guid.NewGuid().ToString("N");
        SideBySideRadio.GroupName = groupName;
        UnifiedRadio.GroupName = groupName;

        // 8.4: コード表示のフォントサイズはCtrl+マウスホイールで変更する（プロジェクトごとの
        // 記憶自体はDiffViewModel.CodeFontSizeの変更を受けてシェル側が行う）。
        // AvaloniaにPreviewMouseWheelは無いため、トンネリング段階でPointerWheelChangedを拾う。
        AddHandler(PointerWheelChangedEvent, OnPointerWheelChanged, RoutingStrategies.Tunnel);

        // 監査 H1: 本文の横スクロール。スクロールバーの値と DiffViewModel.HorizontalOffset を
        // 同期し、幅・フォント・折り返し・行数のどれかが変わるたびに最大値を計算し直す。
        CodeHScroll.PropertyChanged += (_, e) =>
        {
            if (e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty
                && DataContext is DiffViewModel vm && Math.Abs(vm.HorizontalOffset - CodeHScroll.Value) > 0.01)
            {
                vm.HorizontalOffset = CodeHScroll.Value;
            }
        };
        PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty) UpdateHorizontalScroll();
        };
        DataContextChanged += (_, _) =>
        {
            if (_observed is not null) _observed.PropertyChanged -= OnViewModelPropertyChanged;
            _observed = DataContext as DiffViewModel;
            if (_observed is not null) _observed.PropertyChanged += OnViewModelPropertyChanged;
            UpdateHorizontalScroll();
        };
    }

    private DiffViewModel? _observed;

    // 右クリックした差分の行（VBE でこの行を開く用）。行の上でなければ null（1 行目として扱う）。
    private DiffLineViewModel? _contextRow;

    private void OnDiffContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        _contextRow = null;
        for (var visual = e.Source as Avalonia.Visual; visual is not null; visual = Avalonia.VisualTree.VisualExtensions.GetVisualParent(visual))
        {
            if (visual is ListBoxItem { DataContext: DiffLineViewModel row })
            {
                _contextRow = row;
                break;
            }
        }
    }

    /// <summary>右クリックメニューを開く直前。Excel VBA プロジェクトのミラー内のファイルの差分のときだけ「VBE でこの行を開く」を出す。</summary>
    private void OnDiffContextMenuOpening(object? sender, CancelEventArgs e)
    {
        var visible = DataContext is DiffViewModel { IsShowInVbeAvailable: true };
        ShowInVbeMenuItem.IsVisible = visible;
        ShowInVbeSeparator.IsVisible = visible;
    }

    private async void OnShowInVbeClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DiffViewModel vm) return;
        await SafeHandler.RunAsync("VBE で開く", () => vm.OpenInVbeAsync(_contextRow)).ConfigureAwait(true);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DiffViewModel.MaxLineCells):
            case nameof(DiffViewModel.WordWrap):
            case nameof(DiffViewModel.IsSideBySide):
            case nameof(DiffViewModel.CodeFontSize):
            case nameof(DiffViewModel.ShowLineNumbers):
                UpdateHorizontalScroll();
                break;
            case nameof(DiffViewModel.HorizontalOffset):
                if (_observed is not null && Math.Abs(CodeHScroll.Value - _observed.HorizontalOffset) > 0.01)
                {
                    CodeHScroll.Value = _observed.HorizontalOffset;
                }
                break;
        }
    }

    // 行番号2列（40px×2）＋左端バー（3px）＋本文の左余白（8px）。DiffView.axaml の CellTemplate と合わせること。
    private const double CellFixedWidthWithNumbers = 40 + 40 + 3 + 8;
    private const double CellFixedWidthWithoutNumbers = 3 + 8;

    /// <summary>
    /// 本文の表示幅と、最も長い行の幅から、横スクロールの範囲を決める。1桁のピクセル幅は
    /// コード用フォントで "0" を実測して出し、全角は2桁として数えた桁数（MaxLineCells）に掛ける。
    /// 見積もりが多少ずれても行が見えなくならないよう、右に16pxの余白を足す。
    /// 折り返し中、またははみ出しが無いときは、スクロールバーを隠して位置を0へ戻す。
    /// </summary>
    private void UpdateHorizontalScroll()
    {
        if (DataContext is not DiffViewModel vm || Bounds.Width <= 0)
        {
            CodeHScroll.IsVisible = false;
            return;
        }

        var columns = vm.IsSideBySide ? 2 : 1;
        var fixedWidth = vm.ShowLineNumbers ? CellFixedWidthWithNumbers : CellFixedWidthWithoutNumbers;
        var viewport = Math.Max(0, Bounds.Width / columns - fixedWidth);
        var contentWidth = vm.MaxLineCells * MeasureCellWidth(vm.CodeFontSize) + 16;
        var max = vm.WordWrap ? 0 : Math.Max(0, contentWidth - viewport);

        CodeHScroll.Maximum = max;
        CodeHScroll.ViewportSize = viewport;
        CodeHScroll.LargeChange = Math.Max(40, viewport * 0.8);
        CodeHScroll.SmallChange = 40;
        CodeHScroll.IsVisible = max > 0;
        if (max <= 0 && vm.HorizontalOffset != 0) vm.HorizontalOffset = 0;
        else if (vm.HorizontalOffset > max) vm.HorizontalOffset = max;
    }

    private double MeasureCellWidth(double fontSize)
    {
        var family = this.TryFindResource("CodeFontFamily", out var f) && f is FontFamily ff
            ? ff
            : FontFamily.Default;
        var text = new FormattedText("0", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(family), fontSize, Brushes.Black);
        return Math.Max(1, text.WidthIncludingTrailingWhitespace);
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        // 横方向のホイール（トラックパッドの横スワイプ・Shift+ホイール）は本文の横スクロールへ。
        if (DataContext is DiffViewModel hv && CodeHScroll.IsVisible
            && (e.KeyModifiers == KeyModifiers.Shift || Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y)))
        {
            var delta = e.KeyModifiers == KeyModifiers.Shift && Math.Abs(e.Delta.X) < 0.001 ? e.Delta.Y : e.Delta.X;
            hv.HorizontalOffset = Math.Clamp(hv.HorizontalOffset - delta * 60, 0, CodeHScroll.Maximum);
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers != KeyModifiers.Control) return;
        if (DataContext is not DiffViewModel vm) return;

        vm.AdjustCodeFontSize(e.Delta.Y > 0 ? 1 : -1);
        e.Handled = true;
    }
}
