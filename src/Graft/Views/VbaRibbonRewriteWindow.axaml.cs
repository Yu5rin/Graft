using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Graft.ViewModels;

namespace Graft.Views;

/// <summary>
/// リボンの書き換えの確認（仕様書_ブックの情報と依頼の観点 16.9）。ロジックと文面は <see cref="VbaRibbonRewriteViewModel"/> にあり、
/// ここは窓の開閉だけを持つ。Esc・×・Enter で閉じたときは、すべて「中止」（<see cref="VbaRibbonRewriteViewModel.Confirmed"/> が false）。
/// </summary>
public partial class VbaRibbonRewriteWindow : Window
{
    /// <summary>headlessテスト・デザイナ用の引数なしコンストラクタ。</summary>
    public VbaRibbonRewriteWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnTunnelKeyDown, RoutingStrategies.Tunnel);
        // 既定（Enter）は「中止」。開いた直後のフォーカスも「中止」に置き、うっかり Space で「書き換える」を押させない。
        Loaded += (_, _) => CancelButton.Focus();
    }

    public VbaRibbonRewriteWindow(VbaRibbonRewriteViewModel viewModel) : this()
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        Title = viewModel.TitleText;
        AutomationProperties.SetHelpText(this, viewModel.AccessibleText);
        viewModel.CloseRequested += (_, _) => Close();
    }

    /// <summary>モーダル表示し、閉じるまで待って、「書き換える」を選んだかを返す。</summary>
    public async Task<bool> ShowAndConfirmAsync(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        await ShowDialog(owner).ConfigureAwait(true);
        return (DataContext as VbaRibbonRewriteViewModel)?.Confirmed == true;
    }

    private void OnTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }
}
