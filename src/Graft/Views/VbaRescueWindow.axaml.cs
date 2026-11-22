using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Graft.ViewModels;

namespace Graft.Views;

/// <summary>
/// 「Excel 側の控えを見る」窓（仕様書_ブックの情報と依頼の観点 7.4）。ロジックは <see cref="VbaRescueViewModel"/> にあり、
/// ここは窓の開閉（Esc・×で閉じる）と、確認ダイアログ（<see cref="VbaChoiceWindow"/>）の配線だけを持つ。
/// </summary>
public partial class VbaRescueWindow : Window
{
    private readonly VbaRescueViewModel? _viewModel;

    /// <summary>headlessテスト・デザイナ用の引数なしコンストラクタ。</summary>
    public VbaRescueWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnTunnelKeyDown, RoutingStrategies.Tunnel);
        // 入力欄が無い窓なので、初期フォーカスは一覧へ（キーボードで控えを選べるように）。
        Loaded += (_, _) => ItemsListBox.Focus();
    }

    public VbaRescueWindow(VbaRescueViewModel viewModel) : this()
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => Close();
        // 「ミラーへ戻す」「控えを削除」の確認は、この窓を親にして出す。
        viewModel.ChooseAsync ??= request => new VbaChoiceWindow(request).ShowAndChooseAsync(this);
        Opened += async (_, _) => await viewModel.InitializeAsync().ConfigureAwait(true);
    }

    /// <summary>モーダル表示し、閉じるまで待つ。</summary>
    public async Task ShowAndWaitAsync(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        // 初期の大きさが、親の窓より大きくならないよう上限を掛ける（衝突ダイアログと同じ）。
        if (owner.Bounds.Width > 0 && owner.Bounds.Height > 0)
        {
            Width = Math.Max(MinWidth, Math.Min(Width, owner.Bounds.Width - 40));
            Height = Math.Max(MinHeight, Math.Min(Height, owner.Bounds.Height - 40));
        }

        await ShowDialog(owner).ConfigureAwait(true);
    }

    private void OnTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }
}
