using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Graft.ViewModels;

namespace Graft.Views;

/// <summary>
/// パッチキュー管理ウィンドウ（仕様書4.10）。DataContextには<see cref="QueueViewModel"/>を
/// 受け取る。v2.0のWPF版からの移植（19章 L3）。
/// </summary>
public partial class QueueWindow : Window
{
    /// <summary>headlessテスト・デザイナ用の引数なしコンストラクタ。</summary>
    public QueueWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnTunnelKeyDown, RoutingStrategies.Tunnel);
        // 細かいユーザビリティ改善5: 入力欄が無いウィンドウのため既定ボタンへ初期フォーカスを
        // 当てる。ただしキューが空のとき「結合して適用へ進む」はCanExecute=falseで無効化される
        // （MergeCommand参照）ため、その場合は代わりに「閉じる」へフォーカスする
        // （無効なコントロールへのFocus()は静かに無視されるだけで例外にはならないが、
        // それだと結局どこにもフォーカスが当たらずキーボード操作の起点を失ってしまうため）。
        // IsEnabledではなくIsEffectivelyEnabledを見るのは、Commandバインディングによる
        // 無効化はIsEnabled自体は変えずIsEffectivelyEnabledにのみ反映されるため
        // （DialogKeyboardCoverageTests.パッチキューのキー操作が揃っている参照）。
        Loaded += (_, _) =>
        {
            if (MergeButton.IsEffectivelyEnabled) MergeButton.Focus();
            else CloseButton.Focus();
        };
    }

    public QueueWindow(QueueViewModel viewModel) : this()
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        // 「結合して適用」の実処理（結合・ドライラン）はMainViewModel側が
        // MergeRequestedを購読して行う。ここでは完了合図を受けて自分自身を閉じるだけでよい。
        viewModel.MergeRequested += OnMergeRequested;
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();

    private void OnMergeRequested(object? sender, EventArgs e) => Close();

    private void OnTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }
}
