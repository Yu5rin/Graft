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
    // A: 結果行の右クリックメニュー「パスをコピー」用。SearchViewModelはIUiServicesを
    // コンストラクタで受け取っていなかった（既存呼び出し箇所が複数あり、破壊的変更を避けたい）
    // ため、テストから差し替え可能な任意引数として追加する。既定値は他の箇所（LogViewerWindow・
    // AvaloniaDialogService）と同じ共有クリップボード（Linuxでは自前のX11実装）。
    private readonly IClipboardAccess _clipboard;

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

    /// <summary>直近の検索で得られた進行状態。<see cref="ReplaceAllAsync"/>が「全体上限で
    /// 打ち切られた検索結果か」を判定するために保持する（課題1）。<see cref="Groups"/>だけでは
    /// 打ち切りの有無が分からないため、この状態を別途覚えておく必要がある。</summary>
    private SearchRunState? _lastRunState;

    public SearchViewModel(CrossFileSearchEngine engine, IDialogService dialogs, IClipboardAccess? clipboard = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _clipboard = clipboard ?? AvaloniaUiServices.SharedClipboard;

        SearchCommand = new AsyncRelayCommand(
            RunSearchAsync, () => _project is not null && !string.IsNullOrEmpty(Query), context: "検索の実行");
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsSearching);
        ReplaceAllCommand = new AsyncRelayCommand(ReplaceAllAsync, () => Groups.Count > 0 && !IsSearching, context: "すべて置換");
        JumpCommand = new RelayCommand<SearchHitViewModel>(hit => { if (hit is not null) RequestJump(hit); });
        ToggleRegexCommand = new RelayCommand(() => UseRegex = !UseRegex);
        ToggleCaseCommand = new RelayCommand(() => CaseSensitive = !CaseSensitive);
        ToggleWholeWordCommand = new RelayCommand(() => WholeWord = !WholeWord);
        // A: 検索結果の右クリックメニュー。「開く」は既存のJumpCommandをそのまま再利用する。
        CopyPathCommand = new RelayCommand<SearchHitViewModel>(hit =>
        {
            // IClipboardAccess.SetTextは失敗しても例外を投げない契約のため、ここでの保護は不要。
            if (hit is not null) _clipboard.SetText(hit.FullPath);
        });
        RevealCommand = new RelayCommand<SearchHitViewModel>(hit =>
        {
            if (hit is not null) PlatformServices.Current.FileManager.Reveal(hit.FullPath);
        });
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

    /// <summary>A: 結果行の右クリックメニュー「パスをコピー」。</summary>
    public ICommand CopyPathCommand { get; }

    /// <summary>A: 結果行の右クリックメニュー「ファイルマネージャで表示」。</summary>
    public ICommand RevealCommand { get; }

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
        _lastRunState = null;
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
            _lastRunState = state; // ReplaceAllAsyncが打ち切りの有無を判定できるよう保持する
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

    /// <summary>
    /// 課題1（重要）: <see cref="Groups"/>は画面に表示されている検索結果でしかなく、
    /// 全体のヒット上限（<see cref="CrossFileSearchOptions.MaxTotalHits"/>、既定5000件）で
    /// 打ち切られている場合、上限に達した時点より後のファイルは一切走査されていないため
    /// <see cref="Groups"/>に含まれない。以前の実装はこれをそのまま置換対象にしていたため、
    /// 「すべて置換」を実行してもコードベースが中途半端に置換された状態になっていた
    /// （利用者は名前と確認ダイアログの文言から全件置換されると理解するが、実際には
    /// 打ち切り分が未置換のまま残る）。
    ///
    /// 対処方針（案A採用）: 全体上限で打ち切られていた場合に限り、置換の直前だけ上限を
    /// 実質無制限（<see cref="int.MaxValue"/>）にしてプロジェクト全体をもう一度検索し直し、
    /// 本当にすべての一致ファイルを対象にする。案B（打ち切り時は実行不可にする）は安全だが
    /// 「すべて置換」の目的を達成できず、利用者は結局手動で検索語を絞り込んでから
    /// 何度もやり直す必要がある。全体を数え直す方が多少時間がかかっても利用者の意図
    /// （本当にすべて置換したい）に沿うと判断した。ただし巨大なプロジェクトでは
    /// 数え直しに時間がかかりうるため、検索中と同様にIsSearchingを立てて中止ボタンで
    /// 打ち切れるようにし、暴走を防いでいる。
    ///
    /// 打ち切られていない場合でも、1ファイルあたりの上限（<see cref="CrossFileSearchOptions.MaxHitsPerFile"/>）
    /// に達したファイルがあると、画面上の件数（<see cref="SearchFileGroupViewModel.Hits"/>の数）は
    /// そのファイル内の実際の一致件数より少なく表示される。<see cref="CrossFileSearchEngine.ReplaceInFilesAsync"/>
    /// はファイル単位で正規表現の全置換を行う（ファイルは漏れない）ため置換自体は正しく行われるが、
    /// 確認ダイアログの件数表示が実態とずれるため、その旨を文言に明示する。
    /// </summary>
    private async Task ReplaceAllAsync()
    {
        if (_project is null || Groups.Count == 0) return;

        IsSearching = true; // 数え直し・置換の間は新たな検索を止め、中止ボタンを有効にする
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;

        try
        {
            var truncated = _lastRunState?.TruncatedByTotalLimit ?? false;
            var perFileTruncatedCount = _lastRunState?.FilesTruncatedByPerFileLimit.Count ?? 0;

            List<string> targets;
            string message;

            if (truncated)
            {
                StatusText = "全体の上限を外して置換対象を数え直しています...(プロジェクトの規模によっては時間がかかります)";
                var fullOptions = BuildOptions() with { MaxTotalHits = int.MaxValue, MaxHitsPerFile = int.MaxValue };
                var recountState = new SearchRunState();
                var allTargets = new HashSet<string>(StringComparer.Ordinal);
                var recountedHits = 0;
                await foreach (var hit in _engine.SearchAsync(_project, _settings, fullOptions, recountState, cts.Token).ConfigureAwait(true))
                {
                    allTargets.Add(hit.FullPath);
                    recountedHits++;
                }

                targets = allTargets.ToList();
                StatusText = $"数え直しが完了しました（{targets.Count} ファイルの {recountedHits} 件が一致）。";
                message = $"検索結果の画面には上限のため {Groups.Count} ファイルまでしか表示されていませんが、" +
                    $"上限を外して数え直したところ実際には {targets.Count} ファイルの {recountedHits} 件が一致しています。" +
                    $"漏れなくすべて「{ReplaceText}」に置換します。よろしいですか？";
            }
            else
            {
                targets = Groups.Select(g => g.FullPath).ToList();
                var totalHits = Groups.Sum(g => g.Hits.Count);
                message = perFileTruncatedCount == 0
                    ? $"{Groups.Count} ファイルの {totalHits} 件を「{ReplaceText}」に置換します。よろしいですか？"
                    : $"{Groups.Count} ファイルの {totalHits} 件以上を「{ReplaceText}」に置換します" +
                        $"（{perFileTruncatedCount} 件のファイルで1ファイルあたりの表示上限に達しているため、" +
                        $"実際の置換件数は表示より多くなります。対象ファイル自体に漏れはありません）。よろしいですか？";
            }

            var confirmed = await _dialogs.ConfirmAsync("すべて置換", message).ConfigureAwait(true);
            if (!confirmed) return;

            var outcome = await _engine.ReplaceInFilesAsync(targets, _project.Root, BuildOptions(), ReplaceText, cts.Token).ConfigureAwait(true);

            StatusText = $"{outcome.ReplacedCount} 件を {outcome.FilesChanged} ファイルに置換しました。";
            TruncatedMessage = outcome.Failures.Count == 0
                ? null
                : $"{outcome.Failures.Count} 件のファイルで置換に失敗しました。";
        }
        catch (OperationCanceledException)
        {
            // 数え直し中、または置換の途中で中止された。置換済みのファイルが一部残っている
            // 可能性があるため、途中経過ではなく「中止した」ことをそのまま伝える
            // （検索結果は下のRunSearchAsyncで最新化され、実際に何が残っているか確認できる）。
            StatusText = "置換を中止しました。中止までに処理したファイルだけが置換されています。";
        }
        finally
        {
            IsSearching = false;
        }

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

        // 上限値（MaxTotalHits・MaxHitsPerFile）は設定画面から変更できないため、打ち切った事実
        // だけを伝えても利用者には打つ手がない。次に取れる行動（検索語を絞り込む）を必ず添える。
        // なお全体上限で打ち切られていても「すべて置換」は打ち切り分も含めて全件を対象にする
        // （ReplaceAllAsync参照）ため、その旨も伝えて利用者の不安を減らす。
        var notes = new List<string>();
        if (state.TruncatedByTotalLimit)
        {
            notes.Add("全体のヒット上限に達したため、検索結果の表示を途中で打ち切りました。" +
                "検索語を絞り込むと全件を確認できます（「すべて置換」は上限を数え直して打ち切り分も含めすべて置換します）。");
        }
        if (state.FilesTruncatedByPerFileLimit.Count > 0)
        {
            notes.Add($"{state.FilesTruncatedByPerFileLimit.Count} 件のファイルで1ファイルあたりの表示上限に達しました。" +
                "検索語を絞り込むと1ファイル内の全件を確認できます（「すべて置換」はファイル内の一致をすべて置換します）。");
        }
        TruncatedMessage = notes.Count == 0 ? null : string.Join(" ", notes);
    }
}
