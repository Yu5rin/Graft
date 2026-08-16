using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Graft.Editor;
using Graft.ViewModels;

namespace Graft.Views;

/// <summary>
/// エディタ内検索・置換オーバーレイの見た目（4.4節）。<see cref="Attach"/>で対象の
/// <see cref="TextEditor"/>へ接続し、ヒットは<see cref="SearchHighlightRenderer"/>
/// （<see cref="IBackgroundRenderer"/>）で強調表示する。配置・Ctrl+F/Ctrl+Hからの
/// 呼び出しはEditorPane側から<see cref="OpenFind"/>/<see cref="OpenReplace"/>を
/// 呼ぶ形で行う。v2.0のWPF版からの移植（19章 L3）。
/// </summary>
public partial class SearchOverlay : UserControl
{
    private SearchHighlightRenderer? _renderer;
    private TextEditor? _editor;
    private SearchOverlayViewModel? _viewModel;

    public SearchOverlay()
    {
        InitializeComponent();

        // AvaloniaにはPreviewKeyDownが無く、トンネリング段階の購読はAddHandlerで行う。
        AddHandler(KeyDownEvent, OnTunnelKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    /// <summary>
    /// 検索の状態。<see cref="Attach"/> より前は未初期化のため参照できない。
    /// XAMLから引数なしで生成されるコントロールのため、UI機能（デバウンス用タイマー）は
    /// <see cref="Attach"/> で受け取る。
    /// </summary>
    public SearchOverlayViewModel ViewModel
        => _viewModel ?? throw new InvalidOperationException("Attachより前に参照されました。");

    /// <summary>
    /// 検索・置換オーバーレイが開いているかどうか。<see cref="ViewModel"/>と異なり、
    /// <see cref="Attach"/>より前でも例外を投げずfalseを返す（Markdownプレビュー機能:
    /// <see cref="ShellWindow"/>がEscapeの割り当て判断＝キューの破棄より検索を優先するかを
    /// 判定するために使う。ShellWindow.Keyboard.cs参照）。
    /// </summary>
    public bool IsOpen => _viewModel?.IsOpen ?? false;

    /// <summary>対象のエディタへ接続する。タブ切替のたびに呼び直す。</summary>
    public void Attach(TextEditor editor, Graft.Platform.IUiServices ui)
    {
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(ui);

        if (_viewModel is null)
        {
            _viewModel = new SearchOverlayViewModel(ui);
            DataContext = _viewModel;
            _renderer = new SearchHighlightRenderer(_viewModel);
            _viewModel.MatchesChanged += OnMatchesChanged;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        if (!ReferenceEquals(_editor, editor))
        {
            if (_editor is not null && _renderer is not null)
            {
                _editor.TextArea.TextView.BackgroundRenderers.Remove(_renderer);
            }
            _editor = editor;
            if (_renderer is not null) _editor.TextArea.TextView.BackgroundRenderers.Add(_renderer);
        }
        ViewModel.Attach(new Graft.Editor.AvaloniaEditTextAccess(editor));
    }

    /// <summary>Ctrl+F。選択中の文字列があれば検索欄へ引き継ぐ。</summary>
    public void OpenFind() => ViewModel.OpenFind(CurrentSelectionOrNull());

    /// <summary>Ctrl+H。</summary>
    public void OpenReplace() => ViewModel.OpenReplace(CurrentSelectionOrNull());

    private string? CurrentSelectionOrNull()
        => _editor is { SelectionLength: > 0 } ? _editor.SelectedText : null;

    private void OnTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _viewModel is null) return;
        ViewModel.Close();
        _editor?.Focus();
        e.Handled = true;
    }

    private void OnFindBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (e.KeyModifiers == KeyModifiers.Shift) ViewModel.CommitAndFindPrevious();
        else ViewModel.CommitAndFindNext();
        e.Handled = true;
    }

    private void OnReplaceBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ViewModel.ReplaceCommand.Execute(null);
        e.Handled = true;
    }

    // 実機での指摘（Windows、折りたたみマーカーのホバー強調のちらつき）の調査で判明した
    // 落とし穴（Graft.Editor.TextViewRedrawのクラスコメント参照）: TextView.InvalidateLayerは
    // 実質TextView.InvalidateMeasure()であり、呼ぶたびに可視行が作り直され、
    // FoldingMarginが折りたたみマーカーを全部再生成してしまう（マウスがマーカー上に
    // あるとホバー強調がちらつく）。検索ヒットの強調表示もレイアウトの変化を伴わない
    // （ハイライト矩形の位置・色が変わるだけ）ため、測り直し無しに再描画する。
    private void OnMatchesChanged(object? sender, EventArgs e)
    {
        if (_editor is not null) TextViewRedraw.WithoutRemeasure(_editor.TextArea.TextView);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SearchOverlayViewModel.IsOpen) || !ViewModel.IsOpen) return;
        Dispatcher.UIThread.Post(() =>
        {
            FindBox.Focus();
            FindBox.SelectAll();
        });
    }

    /// <summary>ヒット箇所を強調表示する背景レンダラ。現在位置は<c>Accent</c>、それ以外の
    /// ヒットは<c>StateWarn</c>を半透明で使う（色だけに依存しない9.4の観点は、件数表示
    /// （StatusText）と選択状態（現在ヒットは実選択範囲でもある）で補っている）。
    /// AvaloniaのBrushにはFreeze()に相当するAPIが無いため呼び出さない。</summary>
    private sealed class SearchHighlightRenderer : IBackgroundRenderer
    {
        private readonly SearchOverlayViewModel _viewModel;

        public SearchHighlightRenderer(SearchOverlayViewModel viewModel) => _viewModel = viewModel;

        public KnownLayer Layer => KnownLayer.Selection;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (!_viewModel.IsOpen || _viewModel.Matches.Count == 0) return;
            textView.EnsureVisualLines();

            var allBrush = ResolveBrush("StateWarnColor", 0.35);
            var currentBrush = ResolveBrush("AccentColor", 0.45);
            for (var i = 0; i < _viewModel.Matches.Count; i++)
            {
                var brush = i == _viewModel.CurrentIndex ? currentBrush : allBrush;
                if (brush is null) continue;
                var match = _viewModel.Matches[i];
                DrawMatch(textView, drawingContext, brush, match.Index, match.Length);
            }
        }

        private static void DrawMatch(TextView textView, DrawingContext dc, IBrush brush, int offset, int length)
        {
            var builder = new BackgroundGeometryBuilder { AlignToWholePixels = true, CornerRadius = 2 };
            builder.AddSegment(textView, new TextSegment { StartOffset = offset, EndOffset = offset + length });
            var geometry = builder.CreateGeometry();
            if (geometry is not null) dc.DrawGeometry(brush, null, geometry);
        }

        private static IBrush? ResolveBrush(string colorKey, double opacity)
        {
            if (Application.Current is not { } app
                || !app.TryFindResource(colorKey, null, out var value)
                || value is not Color color)
            {
                return null;
            }

            return new SolidColorBrush(color) { Opacity = opacity };
        }
    }
}
