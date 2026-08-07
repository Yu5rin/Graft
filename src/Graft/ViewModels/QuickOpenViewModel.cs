using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using Graft.Features;
using Graft.Infra;

namespace Graft.ViewModels;

/// <summary>クイックオープンの候補1件。表示は「ファイル名（強調）＋相対パス（薄字）」（仕様書）。</summary>
public sealed class QuickOpenResultItem
{
    public QuickOpenResultItem(string projectRoot, string relativePath)
    {
        RelativePath = relativePath;
        FullPath = Path.Combine(projectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        FileName = Path.GetFileName(relativePath);
    }

    /// <summary>プロジェクトルートからの相対パス（区切りは "/"）。</summary>
    public string RelativePath { get; }
    /// <summary>絶対パス。ファイルを開く際に使う。</summary>
    public string FullPath { get; }
    /// <summary>ファイル名のみ（強調表示対象）。</summary>
    public string FileName { get; }

    /// <summary>色だけに依存しない読み上げ用テキスト（9.4節）。</summary>
    public string AutomationName => $"{FileName}、{RelativePath}";
}

/// <summary>
/// クイックオープン（Ctrl+P、ファイル名あいまい検索）のViewModel。<see cref="QuickOpenFileEnumerator"/>
/// でプロジェクト配下のファイル一覧を非同期に取得し、<see cref="FuzzyMatcher"/>（WPF非依存の
/// 純粋ロジック）でクエリに応じて絞り込む。ファイルを開く実処理はエディタへの直接依存を避けるため
/// <see cref="FileOpenRequested"/>イベントとして外へ出す（配線はShellViewModelが行う、
/// SearchViewModel.JumpRequestedと同じ考え方）。
/// </summary>
public sealed class QuickOpenViewModel : ObservableObject
{
    // VS Code同等に「表示は最大10行程度・それ以上はスクロール」とするため、ソート後の
    // 表示件数には緩めの上限を設ける（暴走防止。実際の可視行数はView側のMaxHeightで絞る）。
    private const int MaxResults = 50;

    private readonly QuickOpenFileEnumerator _enumerator = new();

    private Project? _project;
    private Settings _settings = new();
    private CancellationTokenSource? _cts;
    private IReadOnlyList<string> _allFiles = Array.Empty<string>();

    private bool _isOpen;
    private bool _isLoading;
    private string _query = string.Empty;
    private QuickOpenResultItem? _selectedResult;

    public QuickOpenViewModel()
    {
        CloseCommand = new RelayCommand(Close);
    }

    /// <summary>絞り込み結果（スコア順、最大<see cref="MaxResults"/>件）。</summary>
    public ObservableCollection<QuickOpenResultItem> Results { get; } = new();

    /// <summary>オーバーレイが開いているかどうか。</summary>
    public bool IsOpen { get => _isOpen; private set => SetProperty(ref _isOpen, value); }

    /// <summary>ファイル一覧の取得中かどうか（開いた直後の非同期列挙中に立つ）。</summary>
    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }

    /// <summary>検索ボックスの入力文字列。</summary>
    public string Query
    {
        get => _query;
        set
        {
            if (SetProperty(ref _query, value)) UpdateResults();
        }
    }

    /// <summary>候補一覧での選択中の項目。</summary>
    public QuickOpenResultItem? SelectedResult { get => _selectedResult; set => SetProperty(ref _selectedResult, value); }

    /// <summary>プロジェクトが選択されているかどうか（Ctrl+Pを無視するかの判定に使う）。</summary>
    public bool HasProject => _project is not null;

    public ICommand CloseCommand { get; }

    /// <summary>開いた直後、検索ボックスへフォーカスするようView側へ知らせる。</summary>
    public event EventHandler? Opened;

    /// <summary>Enterまたはマウスクリックで確定。開くファイルの絶対パスを渡す。</summary>
    public event EventHandler<string>? FileOpenRequested;

    /// <summary>プロジェクト・設定の切り替え。オーバーレイが開いていれば閉じる。</summary>
    public void SetContext(Project? project, Settings settings)
    {
        _cts?.Cancel();
        _project = project;
        _settings = settings ?? new Settings();
        if (IsOpen) Close();
    }

    /// <summary>
    /// Ctrl+P。既に開いていれば閉じ（トグル）、閉じていればプロジェクト選択時のみ開く
    /// （仕様: プロジェクト未選択時のCtrl+Pは無視）。
    /// </summary>
    public async Task ToggleAsync()
    {
        if (IsOpen)
        {
            Close();
            return;
        }
        if (_project is null) return;

        await OpenAsync().ConfigureAwait(true);
    }

    /// <summary>Esc、または再度のCtrl+P。</summary>
    public void Close()
    {
        _cts?.Cancel();
        IsOpen = false;
        IsLoading = false;
        _query = string.Empty;
        OnPropertyChanged(nameof(Query));
        Results.Clear();
        SelectedResult = null;
        _allFiles = Array.Empty<string>();
    }

    /// <summary>上下キーでの選択移動。候補が無ければ何もしない。</summary>
    public void MoveSelection(int direction)
    {
        if (Results.Count == 0) return;

        var currentIndex = SelectedResult is null ? -1 : Results.IndexOf(SelectedResult);
        var nextIndex = ((currentIndex + direction) % Results.Count + Results.Count) % Results.Count;
        SelectedResult = Results[nextIndex];
    }

    /// <summary>Enter、またはマウスクリック。選択中の候補でファイルを開く。</summary>
    public void ConfirmSelection()
    {
        if (SelectedResult is not { } item) return;

        FileOpenRequested?.Invoke(this, item.FullPath);
        Close();
    }

    private async Task OpenAsync()
    {
        IsOpen = true;
        _query = string.Empty;
        OnPropertyChanged(nameof(Query));
        Results.Clear();
        SelectedResult = null;
        Opened?.Invoke(this, EventArgs.Empty);

        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;

        IsLoading = true;
        try
        {
            _allFiles = await _enumerator.EnumerateAsync(_project!, _settings, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return; // 開いている間にプロジェクトが切り替わった等。UpdateResultsは呼ばない。
        }
        finally
        {
            if (!cts.IsCancellationRequested) IsLoading = false;
        }

        UpdateResults();
    }

    /// <summary>
    /// 仕様: 「空入力時は何も出さない」を採用する（もう一方の選択肢である全件表示は、
    /// プロジェクトの規模によっては初期表示が重くなるため見送った）。
    /// </summary>
    private void UpdateResults()
    {
        Results.Clear();
        if (_query.Length == 0 || _project is null)
        {
            SelectedResult = null;
            return;
        }

        var ordered = _allFiles
            .Select(rel => (RelativePath: rel, Match: FuzzyMatcher.TryMatch(_query, rel)))
            .Where(x => x.Match.IsMatch)
            .OrderBy(x => x.Match.Tier)
            .ThenBy(x => x.Match.RelativePathLength)
            .ThenBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(MaxResults);

        foreach (var (relativePath, _) in ordered)
        {
            Results.Add(new QuickOpenResultItem(_project.Root, relativePath));
        }

        SelectedResult = Results.Count > 0 ? Results[0] : null;
    }
}
