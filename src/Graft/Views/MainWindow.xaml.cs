using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Graft.ViewModels;

namespace Graft.Views;

/// <summary><see cref="CenterPaneState"/> を <see cref="EmptyStateMode"/> へ変換する。</summary>
public sealed class CenterPaneStateConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        CenterPaneState.Loading => EmptyStateMode.Loading,
        CenterPaneState.Empty => EmptyStateMode.Empty,
        CenterPaneState.Error => EmptyStateMode.Error,
        _ => EmptyStateMode.None,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// メインウィンドウ（仕様書8.2）。3ペイン固定レイアウト、コマンドバー、ステータスバーを持つ。
/// 貼り付けから適用完了までをキーボードのみで完結できるよう、主要ショートカット（8.10）を
/// このコードビハインドで一元的に処理する。
/// </summary>
public partial class MainWindow : Window
{
    private const double SplitterThickness = 4;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

        Loaded += OnLoaded;
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
        viewModel.RequestOpenQueue += OnRequestOpenQueue;
    }

    /// <summary>4.10: キュー管理ウィンドウを開く（コマンドバー「キュー」ボタン）。</summary>
    private void OnRequestOpenQueue(object? sender, EventArgs e)
    {
        var window = new QueueWindow(ViewModel.Queue) { Owner = this };
        window.ShowDialog();
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.InitializeAsync().ConfigureAwait(true);
        ApplyLayoutToWindow();
    }

    /// <summary>仕様書8.11: 保存済みレイアウトをウィンドウ・ペインへ反映する。</summary>
    private void ApplyLayoutToWindow()
    {
        var layout = ViewModel.Layout;
        var bounds = WindowLayoutStore.ResolveWindowBounds(layout, MinWidth, MinHeight);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;
        WindowState = layout.IsMaximized ? WindowState.Maximized : WindowState.Normal;

        var paneLayout = ViewModel.GetCurrentPaneLayout();
        LeftColumn.Width = new GridLength(paneLayout.ProjectColumnWidth);
        CenterColumn.Width = new GridLength(paneLayout.BlockColumnWidth);

        var ratio = Math.Clamp(layout.LeftPaneSplitRatio, 0.1, 0.9);
        ProjectRow.Height = new GridLength(ratio, GridUnitType.Star);
        HistoryRow.Height = new GridLength(1 - ratio, GridUnitType.Star);
    }

    /// <summary>
    /// 仕様書8.11: 終了時に現在のウィンドウ位置・サイズ・最大化状態・ペイン比率を保存する。
    /// アプリ終了処理の途中で非同期継続が中断される可能性があるため、書き込みは同期的に待機する。
    /// </summary>
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var layout = ViewModel.Layout;
        layout.IsMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            layout.Left = Left;
            layout.Top = Top;
            layout.Width = Width;
            layout.Height = Height;
        }

        var totalRowStars = ProjectRow.Height.Value + HistoryRow.Height.Value;
        if (totalRowStars > 0)
        {
            layout.LeftPaneSplitRatio = ProjectRow.Height.Value / totalRowStars;
        }

        var paneLayout = ViewModel.GetCurrentPaneLayout();
        paneLayout.ProjectColumnWidth = LeftColumn.ActualWidth;
        paneLayout.BlockColumnWidth = CenterColumn.ActualWidth;

        ViewModel.SaveLayoutAsync().GetAwaiter().GetResult();
    }

    // ------------------------------------------------------------------
    // 8.10 キーボード操作
    // ------------------------------------------------------------------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ViewModel.DiscardCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F6)
        {
            CyclePaneFocus();
            e.Handled = true;
            return;
        }

        // 4.8.4: Ctrl+Shift+C はテキスト入力中でも有効にする（標準の編集操作と衝突しないため）。
        if (e.Key == Key.C && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            ViewModel.CopyPromptCommand.Execute(null);
            e.Handled = true;
            return;
        }

        var focused = Keyboard.FocusedElement as DependencyObject;
        var inTextInput = IsTextInput(focused);

        if (e.Key == Key.Space && !inTextInput && IsDescendant(focused, BlockListBox))
        {
            ViewModel.SelectedBlock?.Toggle();
            e.Handled = true;
            return;
        }

        if (inTextInput)
        {
            // テキスト編集中は Ctrl+V 等のアプリ全体ショートカットより通常の編集操作を優先する。
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = HandleControlShortcut(e.Key);
            return;
        }

        if (e.Key is >= Key.D1 and <= Key.D9)
        {
            ViewModel.ProjectPane.SelectByShortcut(e.Key - Key.D0);
            e.Handled = true;
        }
    }

    private bool HandleControlShortcut(Key key)
    {
        switch (key)
        {
            case Key.V:
                ViewModel.PasteAndParseCommand.Execute(null);
                return true;
            case Key.Enter:
                ViewModel.ApplyCommand.Execute(null);
                return true;
            case Key.Z:
                ViewModel.UndoCommand.Execute(null);
                return true;
            case Key.H:
                ViewModel.ShowHistoryCommand.Execute(null);
                return true;
            case Key.OemComma:
                ViewModel.OpenSettingsCommand.Execute(null);
                return true;
            case Key.F:
                ViewModel.FocusSearchCommand.Execute(null);
                return true;
            default:
                return false;
        }
    }

    /// <summary>F6: プロジェクト一覧 → 履歴 → ブロック一覧 → diff の順にフォーカスを巡回する。</summary>
    private void CyclePaneFocus()
    {
        var targets = new FrameworkElement?[]
        {
            ProjectPaneControl.ListBoxElement,
            HistoryPaneControl.ListBoxElement,
            BlockListBox,
            DiffPaneHost,
        };

        var current = Keyboard.FocusedElement as DependencyObject;
        var currentIndex = Array.FindIndex(targets, t => t is not null && IsDescendant(current, t));
        for (var offset = 1; offset <= targets.Length; offset++)
        {
            var candidate = targets[(currentIndex + offset + targets.Length) % targets.Length];
            if (candidate is null)
            {
                continue;
            }
            candidate.Focus();
            break;
        }
    }

    private static bool IsTextInput(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is TextBoxBase)
            {
                return true;
            }
            element = GetParent(element);
        }
        return false;
    }

    private static bool IsDescendant(DependencyObject? element, DependencyObject? ancestor)
    {
        if (ancestor is null)
        {
            return false;
        }
        while (element is not null)
        {
            if (Equals(element, ancestor))
            {
                return true;
            }
            element = GetParent(element);
        }
        return false;
    }

    private static DependencyObject? GetParent(DependencyObject element)
        => element is Visual or Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
}
