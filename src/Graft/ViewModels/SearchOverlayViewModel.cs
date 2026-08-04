using System.Text.RegularExpressions;
using System.Windows.Input;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using Graft.Features;

namespace Graft.ViewModels;

/// <summary>
/// エディタ内検索・置換オーバーレイ（4.4節・Ctrl+F/Ctrl+H）のViewModel。AvalonEdit標準の
/// <c>SearchPanel</c>は使わず、正規表現・大文字小文字・単語単位の3トグルと現在位置表示
/// （「3 / 12」）を自前で持つ。マッチ位置は<see cref="Matches"/>として公開し、
/// <c>Views/SearchOverlay.xaml.cs</c>側の<see cref="IBackgroundRenderer"/>実装が
/// これを参照してヒットを強調表示する。
/// </summary>
public sealed class SearchOverlayViewModel : ObservableObject
{
    // 100万文字級の文書でも1回のRegex.Matches走査で完結させつつ、キー入力のたびに
    // 全文走査しないようデバウンスする（18章: 10万行のファイルでも遅延なく編集できること）。
    private const int DebounceMs = 150;
    private const int MaxMatches = 20000;

    private readonly DispatcherTimer _debounceTimer;
    private readonly List<Match> _matches = new();

    private TextEditor? _editor;
    private int _currentIndex = -1;
    private string _query = string.Empty;
    private string _replaceText = string.Empty;
    private bool _useRegex;
    private bool _caseSensitive;
    private bool _wholeWord;
    private bool _isOpen;
    private bool _isReplaceMode;
    private string _statusText = string.Empty;
    private string? _patternError;
    private bool _pendingMoveToNearestCaret;

    public SearchOverlayViewModel()
    {
        _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DebounceMs) };
        _debounceTimer.Tick += (_, _) => { _debounceTimer.Stop(); RecomputeNow(_pendingMoveToNearestCaret); };

        FindNextCommand = new RelayCommand(() => MoveTo(1), () => _matches.Count > 0);
        FindPreviousCommand = new RelayCommand(() => MoveTo(-1), () => _matches.Count > 0);
        ReplaceCommand = new RelayCommand(ReplaceCurrent, () => IsReplaceMode && _matches.Count > 0);
        ReplaceAllCommand = new RelayCommand(ReplaceAll, () => IsReplaceMode && _matches.Count > 0);
        CloseCommand = new RelayCommand(Close);
        ToggleReplaceModeCommand = new RelayCommand(() => IsReplaceMode = !IsReplaceMode);
        ToggleRegexCommand = new RelayCommand(() => UseRegex = !UseRegex);
        ToggleCaseCommand = new RelayCommand(() => CaseSensitive = !CaseSensitive);
        ToggleWholeWordCommand = new RelayCommand(() => WholeWord = !WholeWord);
    }

    /// <summary>マッチ集合・現在位置が変わるたびに発火する（ハイライト再描画のトリガー）。</summary>
    public event EventHandler? MatchesChanged;

    public string Query { get => _query; set { if (SetProperty(ref _query, value)) ScheduleRecompute(true); } }
    public string ReplaceText { get => _replaceText; set => SetProperty(ref _replaceText, value); }
    public bool UseRegex { get => _useRegex; set { if (SetProperty(ref _useRegex, value)) ScheduleRecompute(true); } }
    public bool CaseSensitive { get => _caseSensitive; set { if (SetProperty(ref _caseSensitive, value)) ScheduleRecompute(true); } }
    public bool WholeWord { get => _wholeWord; set { if (SetProperty(ref _wholeWord, value)) ScheduleRecompute(true); } }
    public bool IsOpen { get => _isOpen; private set => SetProperty(ref _isOpen, value); }
    public bool IsReplaceMode { get => _isReplaceMode; private set => SetProperty(ref _isReplaceMode, value); }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public bool HasError => _patternError is not null;

    public ICommand FindNextCommand { get; }
    public ICommand FindPreviousCommand { get; }
    public ICommand ReplaceCommand { get; }
    public ICommand ReplaceAllCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand ToggleReplaceModeCommand { get; }
    public ICommand ToggleRegexCommand { get; }
    public ICommand ToggleCaseCommand { get; }
    public ICommand ToggleWholeWordCommand { get; }

    /// <summary>現在のヒット一覧（ハイライト描画・置換のいずれからも参照する）。</summary>
    public IReadOnlyList<Match> Matches => _matches;

    /// <summary><see cref="Matches"/>内での現在位置。ヒットが無ければ -1。</summary>
    public int CurrentIndex => _currentIndex;

    /// <summary>対象のエディタへ接続する。タブ切替のたびに呼び直す。</summary>
    public void Attach(TextEditor editor)
    {
        _editor = editor;
        if (IsOpen) RecomputeNow(false);
    }

    /// <summary>Ctrl+F。既存の選択文字列があればクエリへ引き継ぐ。</summary>
    public void OpenFind(string? seedText) => Open(replaceMode: false, seedText);

    /// <summary>Ctrl+H。</summary>
    public void OpenReplace(string? seedText) => Open(replaceMode: true, seedText);

    /// <summary>Esc、または閉じるボタン。</summary>
    public void Close()
    {
        IsOpen = false;
        _matches.Clear();
        _currentIndex = -1;
        _debounceTimer.Stop();
        MatchesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>検索ボックスでのEnter/Shift+Enter用。デバウンス中でも即座に確定してから移動する。</summary>
    public void CommitAndFindNext() { FlushPending(); MoveTo(1); }
    public void CommitAndFindPrevious() { FlushPending(); MoveTo(-1); }

    private void Open(bool replaceMode, string? seedText)
    {
        IsReplaceMode = replaceMode;
        if (!string.IsNullOrEmpty(seedText)) _query = seedText;
        IsOpen = true;
        OnPropertyChanged(nameof(Query));
        RecomputeNow(true);
    }

    private void ScheduleRecompute(bool moveToNearestCaret)
    {
        if (!IsOpen) return;
        _pendingMoveToNearestCaret = moveToNearestCaret;
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private void FlushPending()
    {
        if (!_debounceTimer.IsEnabled) return;
        _debounceTimer.Stop();
        RecomputeNow(_pendingMoveToNearestCaret);
    }

    private void RecomputeNow(bool moveToNearestCaret)
    {
        _matches.Clear();
        var (regex, error) = SearchPatternBuilder.TryBuild(_query, _useRegex, _caseSensitive, _wholeWord);
        _patternError = error;

        if (regex is not null && _editor is not null && !string.IsNullOrEmpty(_query))
        {
            CollectMatches(regex, _editor.Document.Text);
        }

        _currentIndex = _matches.Count > 0 ? 0 : -1;
        if (moveToNearestCaret) SelectNearestToCaret();
        else if (_matches.Count > 0) SelectCurrent();

        UpdateStatus();
        OnPropertyChanged(nameof(HasError));
        MatchesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CollectMatches(Regex regex, string text)
    {
        foreach (Match m in regex.Matches(text))
        {
            if (m.Length == 0) continue; // 空一致は強調・移動の対象にしない
            _matches.Add(m);
            if (_matches.Count >= MaxMatches) break;
        }
    }

    private void MoveTo(int direction)
    {
        if (_matches.Count == 0) return;
        _currentIndex = ((_currentIndex + direction) % _matches.Count + _matches.Count) % _matches.Count;
        SelectCurrent();
        UpdateStatus();
        MatchesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SelectCurrent()
    {
        if (_editor is null || _currentIndex < 0) return;
        var match = _matches[_currentIndex];
        _editor.Select(match.Index, match.Length);
        _editor.ScrollToLine(_editor.Document.GetLineByOffset(match.Index).LineNumber);
    }

    private void SelectNearestToCaret()
    {
        if (_editor is null || _matches.Count == 0) { _currentIndex = -1; return; }
        var caret = _editor.CaretOffset;
        var index = _matches.FindIndex(m => m.Index >= caret);
        _currentIndex = index >= 0 ? index : 0;
        SelectCurrent();
    }

    private void ReplaceCurrent()
    {
        if (_editor is null || _currentIndex < 0 || _currentIndex >= _matches.Count) return;
        var match = _matches[_currentIndex];
        var replacement = SafeResult(match);
        if (replacement is null) { UpdateStatus(); OnPropertyChanged(nameof(HasError)); return; }

        _editor.Document.Replace(match.Index, match.Length, replacement);
        var caretAfter = match.Index + replacement.Length;
        RecomputeNow(false);
        SelectNearestTo(caretAfter);
    }

    private void ReplaceAll()
    {
        if (_editor is null || _matches.Count == 0) return;
        var doc = _editor.Document;
        doc.UndoStack.StartUndoGroup();
        try
        {
            for (var i = _matches.Count - 1; i >= 0; i--) // 末尾から処理しオフセットのズレを避ける
            {
                var replacement = SafeResult(_matches[i]);
                if (replacement is null) continue;
                doc.Replace(_matches[i].Index, _matches[i].Length, replacement);
            }
        }
        finally
        {
            doc.UndoStack.EndUndoGroup();
        }
        RecomputeNow(false);
    }

    private string? SafeResult(Match match)
    {
        try
        {
            return _useRegex ? match.Result(_replaceText) : _replaceText;
        }
        catch (ArgumentException ex)
        {
            _patternError = $"置換文字列が不正です: {ex.Message}";
            return null;
        }
    }

    private void SelectNearestTo(int offset)
    {
        if (_matches.Count == 0) { _currentIndex = -1; UpdateStatus(); return; }
        var index = _matches.FindIndex(m => m.Index >= offset);
        _currentIndex = index >= 0 ? index : 0;
        SelectCurrent();
        UpdateStatus();
        MatchesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateStatus()
    {
        if (_patternError is not null) { StatusText = _patternError; return; }
        if (string.IsNullOrEmpty(_query)) { StatusText = string.Empty; return; }
        StatusText = _matches.Count == 0 ? "一致なし" : $"{_currentIndex + 1} / {_matches.Count}";
    }
}
