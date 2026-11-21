using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Graft.Core.Vba;
using Graft.ViewModels;

namespace Graft.Views;

/// <summary>
/// 衝突ダイアログ（仕様書_VBA連携 7.5）。ロジックは <see cref="VbaConflictViewModel"/> にあり、ここは窓の開閉
/// （結果が決まったら閉じる、Esc・×で閉じたときは既定の結果にする）と、大きさの上限だけを持つ。
/// </summary>
public partial class VbaConflictWindow : Window
{
    private readonly VbaConflictViewModel? _viewModel;

    /// <summary>headlessテスト・デザイナ用の引数なしコンストラクタ。</summary>
    public VbaConflictWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnTunnelKeyDown, RoutingStrategies.Tunnel);
        // 入力欄が無いウィンドウのため、初期フォーカスは既定のボタンへ。
        Loaded += (_, _) =>
        {
            if (_viewModel?.CanAdoptExcel == true) AdoptButton.Focus();
            else if (_viewModel?.CanSkip == true) SkipButton.Focus();
        };
    }

    public VbaConflictWindow(VbaConflictViewModel viewModel) : this()
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => Close();
    }

    /// <summary>
    /// モーダル表示し、閉じるまで待ってから利用者の選択を返す。Esc・×で閉じた場合は
    /// <see cref="VbaConflictRequest.DefaultDecision"/>（中止、または「反映しない」）。
    /// </summary>
    public async Task<VbaConflictDecision> ShowAndResolveAsync(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        // 初期の大きさが、親の窓より大きくならないよう上限を掛ける（ApplyPreviewWindow と同じ）。
        if (owner.Bounds.Width > 0 && owner.Bounds.Height > 0)
        {
            Width = Math.Max(MinWidth, Math.Min(Width, owner.Bounds.Width - 40));
            Height = Math.Max(MinHeight, Math.Min(Height, owner.Bounds.Height - 40));
        }

        await ShowDialog(owner).ConfigureAwait(true);
        _viewModel!.CloseWithoutChoosing();
        return _viewModel.Result!;
    }

    private void OnTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        // 中止のないとき（適用の後・復元の後）の Esc は、「反映しない」と同じ（CloseWithoutChoosing の既定）。
        if (e.Key == Key.Escape) Close();
    }
}
