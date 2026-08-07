using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;

namespace Graft.ViewModels;

/// <summary>1件のヒット行。前後の文脈と一致箇所を分けて持ち、UI側で一致箇所だけを強調できるようにする。</summary>
public sealed class SearchHitViewModel
{
    public SearchHitViewModel(SearchHit hit)
    {
        FullPath = hit.FullPath;
        LineNumber = hit.LineNumber;
        var line = hit.LineText;
        var start = Math.Clamp(hit.ColumnStart, 0, line.Length);
        var end = Math.Clamp(start + hit.MatchLength, start, line.Length);
        Before = line[..start];
        Match = line[start..end];
        After = line[end..];
    }

    public string FullPath { get; }
    public int LineNumber { get; }
    public string Before { get; }
    public string Match { get; }
    public string After { get; }

    /// <summary>色だけに依存しない読み上げ用テキスト（9.4節）。</summary>
    public string AutomationName => $"{LineNumber}行目、{Before}{Match}{After}";
}

/// <summary>1ファイル分のヒットまとめ（ファイル→行ツリーの親ノード）。</summary>
public sealed class SearchFileGroupViewModel : ObservableObject
{
    private bool _isExpanded = true;

    public SearchFileGroupViewModel(string fullPath, string relativePath)
    {
        FullPath = fullPath;
        RelativePath = relativePath;
    }

    public string FullPath { get; }
    public string RelativePath { get; }
    public ObservableCollection<SearchHitViewModel> Hits { get; } = new();
    public bool IsExpanded { get => _isExpanded; set => SetProperty(ref _isExpanded, value); }
    public string FileName => Path.GetFileName(RelativePath);
    public string HeaderText => $"{RelativePath} ({Hits.Count})";
    public string AutomationName => $"{RelativePath}、{Hits.Count} 件";
}

/// <summary>
/// サイドビュー「検索」（4.4節・Ctrl+Shift+F）のViewModel。<see cref="CrossFileSearchEngine"/>
/// （WPF非依存）を呼び出し、結果をファイル→行のツリーとして<see cref="Groups"/>へ反映する。
/// ジャンプ要求はエディタへの直接依存を避けるため<see cref="JumpRequested"/>イベントとして外へ出す
/// （接続は統合担当が行う）。
///
/// 性能上の注意（hardening-perf）: エンジン自体は数百件のヒットを1秒未満で列挙できるほど
/// 高速だが、ヒット1件ごとに<see cref="ObservableCollection{T}"/>へAddすると、そのたびに
/// 結果ツリー（TreeView）側のレイアウト・描画が走り、実機（発行バイナリ）では
/// 869ファイルのヒットで検索完了まで6秒超かかっていた。そのため<see cref="RunSearchAsync"/>は
/// ヒットを一旦バッファへ溜め、一定間隔（<see cref="BatchFlushIntervalMs"/>）ごとにまとめて
/// <see cref="Groups"/>へ反映する。中止ボタン（<see cref="CancelCommand"/>）は従来どおり
/// <see cref="CancellationTokenSource"/>を介して効く（バッファリングは表示反映のみに影響し、
/// 検索そのものの中断可否には関与しない）。
/// </summary>
public sealed class SearchViewModel : ObservableObject
{
    /// <summary>結果反映のバッチ間隔（ミリ秒）。この間隔ごとにバッファをまとめて<see cref="Groups"/>へ反映する。</summary>
    private const int BatchFlushIntervalMs = 100;

    private readonly CrossFileSearchEngine _engine;
    private readonly IDialogService _dialogs;

    private Project? _project;
    private Settings _settings = new();
    private CancellationTokenSource? _cts;

    private string _query = string.Empty;
    private string _replaceText = string.Empty;
    private bool _useRegex;
    private bool _caseSensitive;
    private bool _wholeWord;
    private bool _isSearching;
    private string _statusText = string.Empty;
    private string? _truncatedMessage;

    public SearchViewModel(CrossFileSearchEngine engine, IDialogService dialogs)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));

        SearchCommand = new AsyncRelayCommand(RunSearchAsync, () => _project is not null && !string.IsNullOrEmpty(Query));
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsSearching);
        ReplaceAllCommand = new AsyncRelayCommand(ReplaceAllAsync, () => Groups.Count > 0 && !IsSearching);
        JumpCommand = new RelayCommand<SearchHitViewModel>(hit => { if (hit is not null) RequestJump(hit); });
        ToggleRegexCommand = new RelayCommand(() => UseRegex = !UseRegex);
        ToggleCaseCommand = new RelayCommand(() => CaseSensitive = !CaseSensitive);
        ToggleWholeWordCommand = new RelayCommand(() => WholeWord = !WholeWord);
    }

    public ObservableCollection<SearchFileGroupViewModel> Groups { get; } = new();

    public string Query { get => _query; set => SetProperty(ref _query, value); }
    public string ReplaceText { get => _replaceText; set => SetProperty(ref _replaceText, value); }
    public bool UseRegex { get => _useRegex; set => SetProperty(ref _useRegex, value); }
    public bool CaseSensitive { get => _caseSensitive; set => SetProperty(ref _caseSensitive, value); }
    public bool WholeWord { get => _wholeWord; set => SetProperty(ref _wholeWord, value); }
    public bool IsSearching { get => _isSearching; private set => SetProperty(ref _isSearching, value); }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    /// <summary>18章: 上限到達で打ち切った場合に必ず表示する注記。打ち切りが無ければnull。</summary>
    public string? TruncatedMessage { get => _truncatedMessage; private set => SetProperty(ref _truncatedMessage, value); }

    public ICommand SearchCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ReplaceAllCommand { get; }
    public ICommand JumpCommand { get; }
    public ICommand ToggleRegexCommand { get; }
    public ICommand ToggleCaseCommand { get; }
    public ICommand ToggleWholeWordCommand { get; }

    /// <summary>結果の行がクリック（またはEnter）された。<c>(FullPath, Line)</c>を渡す。
    /// エディタへの実際のジャンプは統合担当がこのイベントを購読して行う。</summary>
    public event EventHandler<(string FullPath, int Line)>? JumpRequested;

    /// <summary>プロジェクト・設定の切り替え。呼び出しのたびに結果をクリアする。</summary>
    public void SetContext(Project? project, Settings settings)
    {
        _cts?.Cancel();
        _project = project;
        _settings = settings ?? new Settings();
        Groups.Clear();
        StatusText = string.Empty;
        TruncatedMessage = null;
    }

    private void RequestJump(SearchHitViewModel hit) => JumpRequested?.Invoke(this, (hit.FullPath, hit.LineNumber));

    private async Task RunSearchAsync()
    {
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;

        Groups.Clear();
        TruncatedMessage = null;
        if (_project is null || string.IsNullOrEmpty(Query)) { StatusText = string.Empty; return; }

        IsSearching = true;
        StatusText = "検索中...";
        var state = new SearchRunState();
        var byPath = new Dictionary<string, SearchFileGroupViewModel>(StringComparer.Ordinal);
        var pending = new List<SearchHit>();
        var sinceFlush = Stopwatch.StartNew();
        try
        {
            var options = BuildOptions();
            await foreach (var hit in _engine.SearchAsync(_project, _settings, options, state, cts.Token).ConfigureAwait(true))
            {
                pending.Add(hit);
                // 1件ごとにツリーへ反映すると、そのたびにレイアウト・描画が走り体感速度を
                // 大きく損なうため、一定間隔でまとめて反映する（クラスコメント参照）。
                if (sinceFlush.ElapsedMilliseconds >= BatchFlushIntervalMs)
                {
                    FlushPending(pending, byPath);
                    sinceFlush.Restart();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // ユーザーによる中止、または新しい検索での置き換え。エラーとしては扱わない。
        }
        finally
        {
            // 中止時も含め、未反映のヒットを最後に一括反映してから完了扱いにする。
            FlushPending(pending, byPath);
            IsSearching = false;
            UpdateStatus(state);
        }
    }

    /// <summary>バッファ済みのヒットをまとめて結果ツリーへ反映する。</summary>
    private void FlushPending(List<SearchHit> pending, Dictionary<string, SearchFileGroupViewModel> byPath)
    {
        if (pending.Count == 0) return;

        foreach (var hit in pending)
        {
            if (!byPath.TryGetValue(hit.FullPath, out var group))
            {
                group = new SearchFileGroupViewModel(hit.FullPath, hit.RelativePath);
                byPath[hit.FullPath] = group;
                Groups.Add(group);
            }
            group.Hits.Add(new SearchHitViewModel(hit));
        }
        pending.Clear();
    }

    private async Task ReplaceAllAsync()
    {
        if (_project is null || Groups.Count == 0) return;
        var totalHits = Groups.Sum(g => g.Hits.Count);
        var message = $"{Groups.Count} ファイルの {totalHits} 件を「{ReplaceText}」に置換します。よろしいですか?";
        var confirmed = await _dialogs.ConfirmAsync("すべて置換", message).ConfigureAwait(true);
        if (!confirmed) return;

        var targets = Groups.Select(g => g.FullPath).ToList();
        var outcome = await _engine.ReplaceInFilesAsync(targets, _project.Root, BuildOptions(), ReplaceText).ConfigureAwait(true);

        StatusText = $"{outcome.ReplacedCount} 件を {outcome.FilesChanged} ファイルに置換しました。";
        TruncatedMessage = outcome.Failures.Count == 0
            ? null
            : $"{outcome.Failures.Count} 件のファイルで置換に失敗しました。";

        await RunSearchAsync().ConfigureAwait(true); // 置換後の状態で結果を最新化する
    }

    private CrossFileSearchOptions BuildOptions() => new()
    {
        Query = Query,
        UseRegex = UseRegex,
        CaseSensitive = CaseSensitive,
        WholeWord = WholeWord,
    };

    private void UpdateStatus(SearchRunState state)
    {
        if (state.PatternError is not null)
        {
            StatusText = state.PatternError;
            return;
        }

        var totalHits = Groups.Sum(g => g.Hits.Count);
        StatusText = totalHits == 0 ? "一致なし" : $"{totalHits} 件（{Groups.Count} ファイル）";

        var notes = new List<string>();
        if (state.TruncatedByTotalLimit) notes.Add("全体のヒット上限に達したため、検索を途中で打ち切りました。");
        if (state.FilesTruncatedByPerFileLimit.Count > 0)
        {
            notes.Add($"{state.FilesTruncatedByPerFileLimit.Count} 件のファイルで1ファイルあたりの上限に達しました。");
        }
        TruncatedMessage = notes.Count == 0 ? null : string.Join(" ", notes);
    }
}
