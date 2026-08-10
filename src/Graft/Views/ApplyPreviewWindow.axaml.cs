using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Graft.ViewModels;

namespace Graft.Views;

/// <summary>
/// 課題1（設定「適用前にプレビューを表示する」/ Settings.ShowPreview）の適用前プレビュー
/// ダイアログ。DataContextには<see cref="ApplyPreviewViewModel"/>を受け取る。
///
/// MainViewModel.ApplyAsync（MainViewModel.Apply.cs）が発火する
/// <see cref="MainViewModel.ApplyPreviewRequested"/>をShellWindowが購読して開き、
/// 結果（「適用」が押されたかどうか）を<see cref="ShowAndConfirmAsync"/>の戻り値で
/// 呼び出し元へ返す。
/// </summary>
public partial class ApplyPreviewWindow : Window
{
    private bool _confirmed;

    /// <summary>headlessテスト・デザイナ用の引数なしコンストラクタ。</summary>
    public ApplyPreviewWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnTunnelKeyDown, RoutingStrategies.Tunnel);
        // 細かいユーザビリティ改善5: 入力欄が無いウィンドウのため、初期フォーカスは既定ボタンへ。
        Loaded += (_, _) => ApplyConfirmButton.Focus();
    }

    public ApplyPreviewWindow(ApplyPreviewViewModel viewModel) : this()
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
    }

    /// <summary>
    /// モーダル表示し、閉じるまで待ってから「適用」が押されたかどうかを返す。
    /// Escや×で閉じた場合はキャンセル扱い（false）とする。
    /// </summary>
    public async Task<bool> ShowAndConfirmAsync(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        await ShowDialog(owner).ConfigureAwait(true);
        return _confirmed;
    }

    private void OnApplyClicked(object? sender, RoutedEventArgs e)
    {
        _confirmed = true;
        Close();
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close();

    private void OnTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }
}
