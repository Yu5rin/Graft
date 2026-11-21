using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Graft.ViewModels;

namespace Graft.Views;

/// <summary>
/// 「Excel ブックから追加」の選択画面（仕様書_VBA連携 7.1）。ロジックは <see cref="VbaWorkbookPickerViewModel"/> にあり、
/// ここは画面の開閉（Enter で追加、Esc でキャンセル）と、一覧が空のときの案内の出し分けだけを持つ。
/// </summary>
public partial class VbaWorkbookPickerWindow : Window
{
    private bool _confirmed;

    public VbaWorkbookPickerWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnTunnelKeyDown, RoutingStrategies.Tunnel);
    }

    public VbaWorkbookPickerWindow(VbaWorkbookPickerViewModel viewModel) : this()
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        viewModel.Workbooks.CollectionChanged += OnWorkbooksChanged;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;

        // 開いたらすぐ一覧を読み込む（Excel が応答しなくても、画面は先に開く。読み込み中は更新と追加が無効）。
        Opened += async (_, _) =>
        {
            await SafeHandler.RunAsync("Excel ブック一覧の読み込み", viewModel.LoadAsync).ConfigureAwait(true);
            UpdateEmptyMessage();
        };
    }

    /// <summary>画面を開いて、追加が確定されたか（<c>true</c>）を返す。</summary>
    public async Task<bool> ShowAndConfirmAsync(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        await ShowDialog(owner).ConfigureAwait(true);
        return _confirmed;
    }

    private async void OnConfirmClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not VbaWorkbookPickerViewModel viewModel) return;

        // 確定はミラーの作成（Excel からの読み取り）を伴う。失敗したら画面を閉じず、理由をその場に出す。
        var ok = false;
        await SafeHandler.RunAsync("Excel ブックのミラー作成", async () => ok = await viewModel.ConfirmAsync().ConfigureAwait(true))
            .ConfigureAwait(true);
        if (!ok) return;

        _confirmed = true;
        Close();
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close();

    private void OnTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    private void OnWorkbooksChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateEmptyMessage();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(VbaWorkbookPickerViewModel.HasLoaded) or nameof(VbaWorkbookPickerViewModel.HasError)) UpdateEmptyMessage();
    }

    private void UpdateEmptyMessage()
    {
        if (DataContext is not VbaWorkbookPickerViewModel viewModel) return;
        EmptyMessage.IsVisible = viewModel.HasLoaded && !viewModel.HasError && viewModel.Workbooks.Count == 0;
    }
}
