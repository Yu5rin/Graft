using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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
        var groupName = "DiffMode-" + Guid.NewGuid().ToString("N");
        SideBySideRadio.GroupName = groupName;
        UnifiedRadio.GroupName = groupName;

        // 8.4: コード表示のフォントサイズはCtrl+マウスホイールで変更する（プロジェクトごとの
        // 記憶自体はDiffViewModel.CodeFontSizeの変更を受けてシェル側が行う）。
        // AvaloniaにPreviewMouseWheelは無いため、トンネリング段階でPointerWheelChangedを拾う。
        AddHandler(PointerWheelChangedEvent, OnPointerWheelChanged, RoutingStrategies.Tunnel);
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.Control) return;
        if (DataContext is not DiffViewModel vm) return;

        vm.AdjustCodeFontSize(e.Delta.Y > 0 ? 1 : -1);
        e.Handled = true;
    }
}
