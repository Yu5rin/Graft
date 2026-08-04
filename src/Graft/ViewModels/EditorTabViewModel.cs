using System.Windows.Input;

namespace Graft.ViewModels;

/// <summary>
/// エディタ領域の1タブ（4.3節）。<see cref="Graft.Editor.DocumentSession"/>を1件保持し、
/// タブ見出し表示（ファイル名・未保存マーカー・プレビュー表示）とカーソル位置を公開する。
/// </summary>
public sealed class EditorTabViewModel : ObservableObject
{
    private readonly Func<EditorTabViewModel, Task> _closeRequested;
    private bool _isPreview;
    private int _caretLine = 1;
    private int _caretColumn = 1;
    private bool _isModified;
    private bool _indentUseTabs;
    private int _indentWidth = 4;

    public EditorTabViewModel(Graft.Editor.DocumentSession session, Func<EditorTabViewModel, Task> closeRequested)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        _closeRequested = closeRequested ?? throw new ArgumentNullException(nameof(closeRequested));
        _isModified = session.IsModified;
        session.ModifiedChanged += OnSessionModifiedChanged;

        CloseCommand = new AsyncRelayCommand(() => _closeRequested(this));
    }

    /// <summary>このタブが編集しているファイルのセッション。</summary>
    public Graft.Editor.DocumentSession Session { get; }

    /// <summary>タブ見出しに表示するファイル名。</summary>
    public string Title => Session.FileName;

    /// <summary>ツールチップに表示するプロジェクト相対パス。</summary>
    public string ToolTipText => Session.RelativePath;

    /// <summary>未保存の変更があるかどうか（タブの●マーカー）。</summary>
    public bool IsModified { get => _isModified; private set => SetProperty(ref _isModified, value); }

    /// <summary>プレビュータブかどうか（イタリック表示・次のプレビューで置換される）。</summary>
    public bool IsPreview { get => _isPreview; set => SetProperty(ref _isPreview, value); }

    /// <summary>現在のカーソル行（1始まり）。タブ非表示中も直近の位置を保持する。</summary>
    public int CaretLine { get => _caretLine; set => SetProperty(ref _caretLine, Math.Max(1, value)); }

    /// <summary>現在のカーソル列（1始まり）。</summary>
    public int CaretColumn { get => _caretColumn; set => SetProperty(ref _caretColumn, Math.Max(1, value)); }

    /// <summary>15章 detectIndent により検出された、このファイルのインデント種別。</summary>
    public bool IndentUseTabs { get => _indentUseTabs; set => SetProperty(ref _indentUseTabs, value); }

    /// <summary>15章 detectIndent により検出された、このファイルのインデント幅。</summary>
    public int IndentWidth { get => _indentWidth; set => SetProperty(ref _indentWidth, Math.Max(1, value)); }

    /// <summary>タブを閉じる（未保存なら保存確認を挟む）。</summary>
    public ICommand CloseCommand { get; }

    /// <summary>タブが一覧から取り除かれる際に呼び出し側から呼ぶ。イベント購読を解除する。</summary>
    public void DetachEvents() => Session.ModifiedChanged -= OnSessionModifiedChanged;

    private void OnSessionModifiedChanged(object? sender, EventArgs e) => IsModified = Session.IsModified;
}
