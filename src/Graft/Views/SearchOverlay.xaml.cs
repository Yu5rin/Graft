using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using Graft.ViewModels;

namespace Graft.Views;

/// <summary>
/// エディタ内検索・置換オーバーレイの見た目（4.4節）。<see cref="Attach"/>で対象の
/// <see cref="TextEditor"/>へ接続し、ヒットは<see cref="SearchHighlightRenderer"/>
/// （<see cref="IBackgroundRenderer"/>）で強調表示する。配置・Ctrl+F/Ctrl+Hからの
/// 呼び出しは統合担当がEditorPane側から<see cref="OpenFind"/>/<see cref="OpenReplace"/>を
/// 呼ぶ形で行う。
/// </summary>
public partial class SearchOverlay : UserControl
{
    private SearchHighlightRenderer? _renderer;
    private TextEditor? _editor;
    private SearchOverlayViewModel? _viewModel;

    public SearchOverlay() => InitializeComponent();

    /// <summary>
    /// 検索の状態。<see cref="Attach"/> より前は未初期化のため参照できない。
    /// XAMLから引数なしで生成されるコントロールのため、UI機能（デバウンス用タイマー）は
    /// <see cref="Attach"/> で受け取る。
    /// </summary>
    public SearchOverlayViewModel ViewModel
        => _viewModel ?? throw new InvalidOperationException("Attachより前に参照されました。");

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
            _editor?.TextArea.TextView.BackgroundRenderers.Remove(_renderer);
            _editor = editor;
            _editor.TextArea.TextView.BackgroundRenderers.Add(_renderer);
        }
        ViewModel.Attach(editor);
    }

    /// <summary>Ctrl+F。選択中の文字列があれば検索欄へ引き継ぐ。</summary>
    public void OpenFind() => ViewModel.OpenFind(CurrentSelectionOrNull());

    /// <summary>Ctrl+H。</summary>
    public void OpenReplace() => ViewModel.OpenReplace(CurrentSelectionOrNull());

    private string? CurrentSelectionOrNull()
        => _editor is { SelectionLength: > 0 } ? _editor.SelectedText : null;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        ViewModel.Close();
        _editor?.Focus();
        e.Handled = true;
    }

    private void OnFindBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (Keyboard.Modifiers == ModifierKeys.Shift) ViewModel.CommitAndFindPrevious();
        else ViewModel.CommitAndFindNext();
        e.Handled = true;
    }

    private void OnReplaceBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ViewModel.ReplaceCommand.Execute(null);
        e.Handled = true;
    }

    private void OnMatchesChanged(object? sender, EventArgs e)
        => _editor?.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SearchOverlayViewModel.IsOpen) || !ViewModel.IsOpen) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            FindBox.Focus();
            FindBox.SelectAll();
        }));
    }

    /// <summary>ヒット箇所を強調表示する背景レンダラ。現在位置は<c>Accent</c>、それ以外の
    /// ヒットは<c>StateWarn</c>を半透明で使う（色だけに依存しない9.4の観点は、件数表示
    /// （StatusText）と選択状態（現在ヒットは実選択範囲でもある）で補っている）。</summary>
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

        private static void DrawMatch(TextView textView, DrawingContext dc, Brush brush, int offset, int length)
        {
            var builder = new BackgroundGeometryBuilder { AlignToWholePixels = true, CornerRadius = 2 };
            builder.AddSegment(textView, new TextSegment { StartOffset = offset, EndOffset = offset + length });
            var geometry = builder.CreateGeometry();
            if (geometry is not null) dc.DrawGeometry(brush, null, geometry);
        }

        private static Brush? ResolveBrush(string colorKey, double opacity)
        {
            if (Application.Current?.TryFindResource(colorKey) is not Color color) return null;
            var brush = new SolidColorBrush(color) { Opacity = opacity };
            brush.Freeze();
            return brush;
        }
    }
}
