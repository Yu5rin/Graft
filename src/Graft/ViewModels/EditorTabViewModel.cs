using System.ComponentModel;
using System.Windows.Input;

namespace Graft.ViewModels;

/// <summary>
/// エディタ領域のタブ種別（仕様書9.2）。<see cref="Document"/>は通常の編集対象ファイル、
/// <see cref="Diff"/>は接ぎ木ブロックの差分プレビュー（読み取り専用・保存確認の対象外）を表す。
/// <see cref="HistoryDiff"/>は修正1: 履歴のリビジョン選択に連動する差分専用タブ（1リビジョンが
/// 変更した複数ファイルをまとめて表示する。<see cref="Diff"/>とは異なるビューモデルを持つ）。
/// </summary>
public enum EditorTabKind
{
    Document,
    Diff,
    HistoryDiff,
}

/// <summary>
/// エディタ領域の1タブ（4.3節・9.2節）。<see cref="Kind"/>が<see cref="EditorTabKind.Document"/>の
/// 場合は<see cref="Graft.Editor.DocumentSession"/>を1件保持し、タブ見出し表示
/// （ファイル名・未保存マーカー・プレビュー表示）とカーソル位置を公開する。
/// <see cref="EditorTabKind.Diff"/>の場合は<see cref="Graft.Editor.DocumentSession"/>を持たず、
/// 代わりに<see cref="Diff"/>（<see cref="DiffViewModel"/>）を表示する（仕様書4.8・9.2）。
/// </summary>
public sealed class EditorTabViewModel : ObservableObject
{
    private readonly Func<EditorTabViewModel, Task> _closeRequested;
    private readonly Graft.Editor.DocumentSession? _session;
    private bool _isPreview;
    private int _caretLine = 1;
    private int _caretColumn = 1;
    private bool _isModified;
    private bool _indentUseTabs;
    private int _indentWidth = 4;
    private bool _hasExternalConflict;

    /// <summary>通常のドキュメントタブ（4.3節）。</summary>
    public EditorTabViewModel(Graft.Editor.DocumentSession session, Func<EditorTabViewModel, Task> closeRequested)
    {
        Kind = EditorTabKind.Document;
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _closeRequested = closeRequested ?? throw new ArgumentNullException(nameof(closeRequested));
        _isModified = session.IsModified;
        session.ModifiedChanged += OnSessionModifiedChanged;

        CloseCommand = new AsyncRelayCommand(() => _closeRequested(this));
        ReloadDiscardingChangesCommand = new AsyncRelayCommand(ReloadDiscardingChangesAsync);
        DismissExternalConflictCommand = new RelayCommand(() => HasExternalConflict = false);
    }

    /// <summary>
    /// 差分タブ（4.8・9.2節）。ブロック一覧で選択したブロックの差分をエディタ領域のタブとして
    /// 表示する。編集不可・保存確認の対象外のため<see cref="Graft.Editor.DocumentSession"/>は持たない。
    /// </summary>
    public EditorTabViewModel(DiffViewModel diff, Func<EditorTabViewModel, Task> closeRequested)
    {
        Kind = EditorTabKind.Diff;
        Diff = diff ?? throw new ArgumentNullException(nameof(diff));
        _closeRequested = closeRequested ?? throw new ArgumentNullException(nameof(closeRequested));
        Diff.PropertyChanged += OnDiffPropertyChanged;

        CloseCommand = new AsyncRelayCommand(() => _closeRequested(this));
        ReloadDiscardingChangesCommand = new RelayCommand(() => { });
        DismissExternalConflictCommand = new RelayCommand(() => HasExternalConflict = false);
    }

    /// <summary>
    /// 修正1: 履歴差分タブ（履歴のリビジョン選択に連動する複数ファイルぶんの差分表示）。
    /// 通常の差分タブ（<see cref="Diff"/>）とは異なり<see cref="HistoryDiffViewModel"/>を持つ。
    /// </summary>
    public EditorTabViewModel(HistoryDiffViewModel historyDiff, Func<EditorTabViewModel, Task> closeRequested)
    {
        Kind = EditorTabKind.HistoryDiff;
        HistoryDiff = historyDiff ?? throw new ArgumentNullException(nameof(historyDiff));
        _closeRequested = closeRequested ?? throw new ArgumentNullException(nameof(closeRequested));
        HistoryDiff.PropertyChanged += OnHistoryDiffPropertyChanged;

        CloseCommand = new AsyncRelayCommand(() => _closeRequested(this));
        ReloadDiscardingChangesCommand = new RelayCommand(() => { });
        DismissExternalConflictCommand = new RelayCommand(() => HasExternalConflict = false);
    }

    /// <summary>タブ種別。<see cref="Views.EditorPane"/>がこれに応じて表示を切り替える。</summary>
    public EditorTabKind Kind { get; }

    /// <summary>通常のドキュメントタブかどうか。</summary>
    public bool IsDocument => Kind == EditorTabKind.Document;

    /// <summary>差分タブかどうか。</summary>
    public bool IsDiffTab => Kind == EditorTabKind.Diff;

    /// <summary>修正1: 履歴差分タブかどうか。</summary>
    public bool IsHistoryDiffTab => Kind == EditorTabKind.HistoryDiff;

    /// <summary>
    /// 不具合3対応の常時表示closeButtonを差分系タブ全般（通常のdiffタブ・履歴差分タブ）で
    /// 共通に使うための判定（EditorPane.axamlのDiffCloseButton参照）。
    /// </summary>
    public bool IsAnyDiffTab => Kind is EditorTabKind.Diff or EditorTabKind.HistoryDiff;

    /// <summary>
    /// このタブが編集しているファイルのセッション。<see cref="Kind"/>が<see cref="EditorTabKind.Diff"/>
    /// のタブでは利用できない（呼び出し側は事前に<see cref="IsDocument"/>で判定すること）。
    /// </summary>
    public Graft.Editor.DocumentSession Session
        => _session ?? throw new InvalidOperationException("差分タブにはSessionがありません。");

    /// <summary>差分タブの表示内容。<see cref="Kind"/>が<see cref="EditorTabKind.Document"/>のタブでは null。</summary>
    public DiffViewModel? Diff { get; }

    /// <summary>
    /// 修正1: 履歴差分タブの表示内容。<see cref="Kind"/>が<see cref="EditorTabKind.HistoryDiff"/>の
    /// タブでのみ非null。
    /// </summary>
    public HistoryDiffViewModel? HistoryDiff { get; }

    /// <summary>
    /// タブ見出しに表示するファイル名（Documentタブ）・「差分: パス」（Diffタブ）・
    /// 「差分: r3」（履歴差分タブ。修正1: ファイル名だけだとどのリビジョンか分からないため
    /// リビジョンラベルを出す）。
    /// </summary>
    public string Title => Kind switch
    {
        EditorTabKind.Document => Session.FileName,
        EditorTabKind.Diff => Diff is { } d ? BuildDiffTitle(d) : "差分",
        EditorTabKind.HistoryDiff => HistoryDiff is { RevisionLabel.Length: > 0 } h ? $"差分: {h.RevisionLabel}" : "差分",
        _ => "差分",
    };

    /// <summary>ツールチップ・読み上げ（AutomationProperties.Name）に表示するプロジェクト相対パス。</summary>
    public string ToolTipText => Kind == EditorTabKind.Document
        ? Session.RelativePath
        : Title;

    /// <summary>未保存の変更があるかどうか（タブの●マーカー）。差分タブでは常にfalse。</summary>
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

    /// <summary>
    /// タブ非表示中の垂直スクロール位置（ピクセル）。水平位置と合わせ、View側
    /// （<see cref="Graft.Views.EditorPane"/>）がタブ切替のたびに退避・復元する。
    /// UIバインディング対象ではないため通知プロパティにはしない。
    /// </summary>
    public double ScrollOffsetY { get; set; }

    /// <summary>タブ非表示中の水平スクロール位置（ピクセル）。</summary>
    public double ScrollOffsetX { get; set; }

    /// <summary>選択範囲の開始オフセット（文字単位）。</summary>
    public int SelectionStart { get; set; }

    /// <summary>選択範囲の長さ（文字単位）。0は選択なし。</summary>
    public int SelectionLength { get; set; }

    /// <summary>
    /// 一度でもこのタブが非表示側へ回り、スクロール位置が退避されたかどうか。falseのままなら
    /// （＝開いてから一度も他タブへ切り替えていない）、<see cref="ScrollOffsetX"/>/
    /// <see cref="ScrollOffsetY"/>は「まだ記録されていない」ことを意味するため、View側は
    /// 正確な位置復元を行わずCaretLineへのおおまかなスクロールのみで済ませる
    /// （4.8のdiffジャンプ等でCaretLineだけが指定された初回表示を正しく機能させるため）。
    /// </summary>
    public bool HasViewState { get; set; }

    /// <summary>
    /// 4.6: ディスク上の変更と未保存編集が競合している（E702）かどうか。trueの間、
    /// エディタ側に非モーダルの通知バーを表示する。外部からの通知は
    /// <see cref="Graft.ViewModels.EditorPaneViewModel.NotifyExternalChangeAsync"/>を経由する。
    /// 差分タブでは発生しない。
    /// </summary>
    public bool HasExternalConflict { get => _hasExternalConflict; set => SetProperty(ref _hasExternalConflict, value); }

    /// <summary>タブを閉じる（未保存なら保存確認を挟む。差分タブでは確認なしで閉じる）。</summary>
    public ICommand CloseCommand { get; }

    /// <summary>通知バーの「再読込」。未保存の編集を破棄してディスクの内容へ戻す。差分タブでは何もしない。</summary>
    public ICommand ReloadDiscardingChangesCommand { get; }

    /// <summary>通知バーの「無視」。バーを閉じ、現在の編集内容をそのまま保持する。</summary>
    public ICommand DismissExternalConflictCommand { get; }

    /// <summary>タブが一覧から取り除かれる際に呼び出し側から呼ぶ。イベント購読を解除する。</summary>
    public void DetachEvents()
    {
        if (Kind == EditorTabKind.Document)
        {
            Session.ModifiedChanged -= OnSessionModifiedChanged;
        }
        else if (Kind == EditorTabKind.Diff && Diff is not null)
        {
            Diff.PropertyChanged -= OnDiffPropertyChanged;
        }
        else if (Kind == EditorTabKind.HistoryDiff && HistoryDiff is not null)
        {
            HistoryDiff.PropertyChanged -= OnHistoryDiffPropertyChanged;
        }
    }

    /// <summary>
    /// エクスプローラでのリネームに追従してSessionのパスを更新した後、呼び出し側が呼ぶ。
    /// <see cref="Title"/>/<see cref="ToolTipText"/>はSessionからの計算プロパティのため、
    /// 明示的に変更通知を発火しないとタブ見出しの表示が古いままになる。
    /// </summary>
    public void NotifyPathChanged()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(ToolTipText));
    }

    private async Task ReloadDiscardingChangesAsync()
    {
        await Session.ReloadAsync().ConfigureAwait(true);
        HasExternalConflict = false;
    }

    private void OnSessionModifiedChanged(object? sender, EventArgs e) => IsModified = Session.IsModified;

    /// <summary>
    /// 差分タブの表示対象が別ブロック・別ファイルへ切り替わった際（<see cref="DiffViewModel.Load"/>の
    /// 再実行）に、タブ見出し・ツールチップを追従させる（9.2: 既存タブの再利用）。
    /// </summary>
    private void OnDiffPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DiffViewModel.FilePath)) return;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(ToolTipText));
    }

    private static string BuildDiffTitle(DiffViewModel diff)
        => diff.FilePath is { } path ? $"差分: {path}" : "差分";

    /// <summary>
    /// 修正1: 履歴のリビジョン選択が変わる（<see cref="HistoryDiffViewModel.Load"/>の再実行）たびに、
    /// 同じタブを使い回しながらタブ見出し（「差分: r3」）を追従させる（OnDiffPropertyChangedと同じ考え方）。
    /// </summary>
    private void OnHistoryDiffPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(HistoryDiffViewModel.RevisionLabel)) return;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(ToolTipText));
    }
}
