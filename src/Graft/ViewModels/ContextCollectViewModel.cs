using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Graft.Core;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;

namespace Graft.ViewModels;

/// <summary>
/// 仕様書10章のコンテキスト収集UIを担う。収集モードの選択、ファイルの3状態選択（内容も出す／
/// 構成だけ／出さない）、除外規則の確認、出力前の概算トークン数表示（10.4）と上限超過時の警告、
/// クリップボードへのコピー・ファイルへの保存を行う。
/// </summary>
public sealed class ContextCollectViewModel : ObservableObject, IDisposable
{
    // 10.3出力形式のファイル見出し「# 相対パス  (ハッシュ)」を検出する正規表現（前提・ツリー見出しは末尾の(ハッシュ)が無く誤検出しない）。
    private static readonly Regex FileHeaderPattern = new(@"^# (?<path>.+?)  \((?<hash>[0-9a-fA-F]+)\)$", RegexOptions.Compiled);

    /// <summary>3状態の記録・復元に使う保存済み状態の反映を、チェック連打1回にまとめる間隔。</summary>
    private const int PersistDebounceMs = 300;

    private readonly ContextCollector _collector;
    private readonly RevisionStore _revisionStore;
    private readonly ProjectStore _projectStore;
    private readonly IUiServices _ui;
    private readonly IDialogService _dialogs;
    private readonly IUiTimer _persistTimer;
    private Project _project; private readonly Settings _settings;

    private ContextMode _selectedMode = ContextMode.TreeAndSelected;
    private RevisionOption? _selectedRevision;
    private ContextFileNodeViewModel? _selectedFile;
    private int _estimatedTokens;
    private bool _exceedsWarnThreshold;
    private bool _isScanning;
    private bool _isEmpty;
    private GraftIssue? _errorIssue;
    private string? _statusMessage;
    private string _newExcludePattern = string.Empty;

    /// <summary>直近のScanAsync結果。トークン数の概算（SizeBytes基準）に使う。</summary>
    private IReadOnlyList<ContextFileNode> _lastScan = Array.Empty<ContextFileNode>();

    /// <summary>相対パス（ディレクトリは"" =ルート）→直下の子ノード一覧。フォルダの一括切替・集計計算に使う。</summary>
    private Dictionary<string, List<ContextFileNodeViewModel>> _childrenByPath = new(StringComparer.Ordinal);

    public ContextCollectViewModel(
        AppPaths appPaths, ProjectStore projectStore, Project project, Settings settings, IUiServices ui, IDialogService dialogs)
    {
        ArgumentNullException.ThrowIfNull(appPaths);
        _projectStore = projectStore ?? throw new ArgumentNullException(nameof(projectStore));
        _project = project ?? throw new ArgumentNullException(nameof(project));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _collector = new ContextCollector(appPaths);
        _revisionStore = new RevisionStore(appPaths);
        _persistTimer = ui.CreateTimer(TimeSpan.FromMilliseconds(PersistDebounceMs), OnPersistTick);

        Modes = new ObservableCollection<ModeOption>
        {
            new("ツリーのみ", ContextMode.TreeOnly), new("選択ファイル", ContextMode.SelectedFiles),
            new("ツリー＋選択", ContextMode.TreeAndSelected), new("差分のみ", ContextMode.ChangedSince),
        };
        Revisions = new ObservableCollection<RevisionOption>();
        Files = new ObservableCollection<ContextFileNodeViewModel>();
        ExtraExcludes = new ObservableCollection<string>(project.Overrides.Excludes);
        PreviewLines = new ObservableCollection<PreviewLine>();

        // ContextCollectWindow.axamlのプレビュー表示・空状態プレースホルダは、IsVisible="{Binding
        // PreviewLines, Converter=...HasItems}"（および IsEmptyCollection）という、コレクション
        // 「への参照」を対象にした値バインディングである。SettingsViewModel.ValidationIssuesと
        // 同じ理由で、UpdatePreviewLines内のClear()/Add()はコレクションの中身だけを書き換え、
        // プロパティの参照自体は変えない（INotifyCollectionChangedで通知するのみ）ため、この
        // ままではプレビュー欄が最初の（空の）評価のまま固まり、「プレビュー」を押しても出力
        // 内容が一切表示されない（実機で確認済みの不具合）。CollectionChangedのたびに
        // PreviewLines自体のPropertyChangedを代わりに発火させ、バインディングを強制的に
        // 再評価させる。
        PreviewLines.CollectionChanged += (_, _) => OnPropertyChanged(nameof(PreviewLines));

        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(), context: "コンテキスト対象の再走査");
        PreviewCommand = new AsyncRelayCommand(PreviewAsync, () => !_isScanning, context: "コンテキストのプレビュー");
        CopyCommand = new AsyncRelayCommand(CopyAsync, () => !_isScanning, context: "コンテキストのコピー");
        SaveToFileCommand = new AsyncRelayCommand(SaveToFileAsync, () => !_isScanning, context: "コンテキストのファイル保存");
        CycleStateCommand = new RelayCommand<ContextFileNodeViewModel>(node => { if (node is not null) CycleState(node); });
        CycleSelectedStateCommand = new RelayCommand(
            () => { if (_selectedFile is not null) CycleState(_selectedFile); },
            () => _selectedFile is { IsExcluded: false } && ShowFileTree);
        AddExcludeCommand = new AsyncRelayCommand(
            AddExcludeAsync, () => !string.IsNullOrWhiteSpace(_newExcludePattern), context: "除外パターンの追加");
        RemoveExcludeCommand = new RelayCommand<string>(pattern => _ = RemoveExcludeAsync(pattern));
    }

    public Project Project => _project;

    public ObservableCollection<ModeOption> Modes { get; }
    public ObservableCollection<RevisionOption> Revisions { get; }
    public ObservableCollection<ContextFileNodeViewModel> Files { get; }

    /// <summary>8.6: 出力プレビューの行（シンタックストークン付き）。プレビュー・コピー実行時に更新する。</summary>
    public ObservableCollection<PreviewLine> PreviewLines { get; }

    /// <summary>10.2: 既定除外・.gitignore に加え、プロジェクト単位で追加した除外パターン。</summary>
    public ObservableCollection<string> ExtraExcludes { get; }

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand PreviewCommand { get; }
    public AsyncRelayCommand CopyCommand { get; }
    /// <summary>「ファイルへ保存」。名前を付けて保存ダイアログを表示し、Markdown形式で書き出す。</summary>
    public AsyncRelayCommand SaveToFileCommand { get; }
    /// <summary>行のアイコンをクリックしたときの3状態切替（ファイルは自身、フォルダは配下一括）。</summary>
    public RelayCommand<ContextFileNodeViewModel> CycleStateCommand { get; }
    /// <summary>Spaceキーで選択中の行の3状態を切り替える（旧ToggleSelectedCommand相当）。</summary>
    public RelayCommand CycleSelectedStateCommand { get; }
    public AsyncRelayCommand AddExcludeCommand { get; }
    public RelayCommand<string> RemoveExcludeCommand { get; }

    public ContextMode SelectedMode
    {
        get => _selectedMode;
        set => SetProperty(ref _selectedMode, value, OnModeChanged);
    }

    public RevisionOption? SelectedRevision
    {
        get => _selectedRevision;
        set => SetProperty(ref _selectedRevision, value);
    }

    public ContextFileNodeViewModel? SelectedFile
    {
        get => _selectedFile;
        set => SetProperty(ref _selectedFile, value, () => CycleSelectedStateCommand.RaiseCanExecuteChanged());
    }

    public bool ShowFileTree => _selectedMode is ContextMode.SelectedFiles or ContextMode.TreeAndSelected;

    public bool ShowRevisionPicker => _selectedMode == ContextMode.ChangedSince;

    public string NewExcludePattern
    {
        get => _newExcludePattern;
        set => SetProperty(ref _newExcludePattern, value, () => AddExcludeCommand.RaiseCanExecuteChanged());
    }

    /// <summary>10.4: 出力前の概算トークン数。</summary>
    public int EstimatedTokens
    {
        get => _estimatedTokens;
        private set => SetProperty(ref _estimatedTokens, value);
    }

    /// <summary>10.4: 上限超過フラグ。超過時はファイル選択の見直しを促す警告を表示する。</summary>
    public bool ExceedsWarnThreshold
    {
        get => _exceedsWarnThreshold;
        private set => SetProperty(ref _exceedsWarnThreshold, value);
    }

    public int TokenWarnThreshold => _settings.Context.TokenWarnThreshold;

    /// <summary>8.8: 読み込み中インジケータ。</summary>
    public bool IsScanning
    {
        get => _isScanning;
        private set => SetProperty(ref _isScanning, value);
    }

    /// <summary>8.8: 空状態。走査対象ファイルが1件もない場合。</summary>
    public bool IsEmpty
    {
        get => _isEmpty;
        private set => SetProperty(ref _isEmpty, value);
    }

    /// <summary>8.8: エラー状態（コード＋対処方法）。</summary>
    public GraftIssue? ErrorIssue
    {
        get => _errorIssue;
        private set => SetProperty(ref _errorIssue, value);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>初期表示時に一度呼び出し、ファイルツリーを走査する。</summary>
    public Task InitializeAsync(CancellationToken ct = default) => RefreshAsync(ct);

    /// <summary>デバウンス用タイマーを止める。プロジェクト切替でこのインスタンスを捨てる際に呼ぶ。</summary>
    public void Dispose() => _persistTimer.Dispose();

    private void OnModeChanged()
    {
        OnPropertyChanged(nameof(ShowFileTree));
        OnPropertyChanged(nameof(ShowRevisionPicker));
        CycleSelectedStateCommand.RaiseCanExecuteChanged();
        if (_selectedMode == ContextMode.ChangedSince && Revisions.Count == 0)
        {
            _ = LoadRevisionsAsync();
        }
    }

    private async Task LoadRevisionsAsync()
    {
        var result = await _revisionStore.ListAsync(_project.Id).ConfigureAwait(true);
        Revisions.Clear();
        if (!result.IsSuccess) return;
        foreach (var summary in result.Value)
        {
            Revisions.Add(new RevisionOption(summary.Manifest.Revision, summary.Manifest.Summary ?? "(要約なし)"));
        }
        SelectedRevision = Revisions.FirstOrDefault();
    }

    private async Task RefreshAsync(CancellationToken ct = default)
    {
        IsScanning = true;
        ErrorIssue = null;
        try
        {
            var scan = await _collector.ScanAsync(_project, _settings, ct).ConfigureAwait(true);
            if (!scan.IsSuccess)
            {
                ErrorIssue = scan.Errors.FirstOrDefault();
                Files.Clear();
                _lastScan = Array.Empty<ContextFileNode>();
                IsEmpty = false;
                return;
            }

            _lastScan = scan.Value;
            Files.Clear();
            foreach (var node in scan.Value)
            {
                // 課題3: 既定は全部「内容も出す」（ContextFileNodeViewModelのコンストラクタで設定）。
                Files.Add(new ContextFileNodeViewModel(node));
            }
            IsEmpty = Files.Count == 0;

            RebuildChildrenMap();
            await ApplyPersistedStatesAsync().ConfigureAwait(true);
            RecomputeDirectoryStates();
            UpdateApproxTokenEstimate();
            WarnIfDefaultSelectionIsLarge();
        }
        finally
        {
            IsScanning = false;
        }
    }

    private async Task PreviewAsync()
    {
        var result = await CollectAsync().ConfigureAwait(true);
        if (result is null) return;
        StatusMessage = ExceedsWarnThreshold
            ? $"推定トークン数 {EstimatedTokens} 件。上限（{TokenWarnThreshold} 件）を超えています。ファイル選択を見直してください。"
            : $"推定トークン数 {EstimatedTokens} 件。";
    }

    private async Task CopyAsync()
    {
        var result = await CollectAsync().ConfigureAwait(true);
        if (result is null) return;

        if (ExceedsWarnThreshold)
        {
            StatusMessage = $"推定トークン数 {EstimatedTokens} 件が上限（{TokenWarnThreshold} 件）を超えています。ファイル選択を見直してください。";
            return;
        }

        _ui.Clipboard.SetText(result.Text);
        StatusMessage = "クリップボードにコピーしました。";
    }

    /// <summary>
    /// 課題1: 名前を付けて保存ダイアログを表示し、Markdown形式のテキストとして書き出す。
    /// コピーと異なり、トークン数が上限を超えていても保存自体は行う（保存は「ファイルへ出力する
    /// だけ」でAIへ即渡すコピーとは性質が違い、大きい構成をいったんファイル化して後で選別する
    /// 使い方もあり得るため）。ただし超過している旨はステータスに残し、見直しを促す。
    /// </summary>
    private async Task SaveToFileAsync()
    {
        var result = await CollectAsync().ConfigureAwait(true);
        if (result is null) return;

        var suggested = BuildDefaultFileName(_project.DisplayName, DateTimeOffset.Now);
        var path = await _dialogs.SaveFileAsync("コンテキストをファイルへ保存", suggested, new[] { ".md" }).ConfigureAwait(true);
        if (path is null) return; // キャンセル

        var bytes = Encoding.UTF8.GetBytes(result.Text);
        var write = await SafeFileWriter.ReplaceAsync(path, bytes).ConfigureAwait(true);
        if (!write.IsSuccess)
        {
            var issue = write.Errors.FirstOrDefault();
            var reason = issue is not null
                ? $"{issue.ToDisplayText()}（対処: {issue.Remedy}）"
                : "原因不明のエラーが発生しました。";
            StatusMessage = "ファイルへの保存に失敗しました。";
            await _dialogs.ShowMessageAsync("保存に失敗しました", reason).ConfigureAwait(true);
            return;
        }

        StatusMessage = ExceedsWarnThreshold
            ? $"推定トークン数 {EstimatedTokens} 件が上限（{TokenWarnThreshold} 件）を超えていますが保存しました。ファイル選択の見直しをお勧めします。保存先: {path}"
            : $"保存しました。保存先: {path}";
    }

    /// <summary>既定のファイル名を「プロジェクト名_yyyyMMdd_HHmm.md」の形式で組み立てる。</summary>
    private static string BuildDefaultFileName(string projectDisplayName, DateTimeOffset timestamp)
        => $"{SanitizeForFileName(projectDisplayName)}_{timestamp:yyyyMMdd_HHmm}.md";

    /// <summary>ファイル名として使えない文字を "_" に置き換える。全滅した場合は既定名で代替する。</summary>
    private static string SanitizeForFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var sanitized = new string(chars).Trim();
        return sanitized.Length == 0 ? "graft-context" : sanitized;
    }

    private async Task<ContextResult?> CollectAsync()
    {
        var selectedPaths = Files.Where(f => f is { IsDirectory: false, IsExcluded: false, State: ContextFileState.Full })
            .Select(f => f.RelativePath).ToArray();
        var hiddenPaths = Files.Where(f => f is { IsDirectory: false, IsExcluded: false, State: ContextFileState.Hidden })
            .Select(f => f.RelativePath).ToArray();

        var request = new ContextRequest
        {
            Project = _project,
            Settings = _settings,
            Mode = _selectedMode,
            SelectedPaths = selectedPaths,
            HiddenPaths = hiddenPaths,
            SinceRevision = _selectedRevision?.Revision,
        };

        var result = await _collector.CollectAsync(request).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            ErrorIssue = result.Errors.FirstOrDefault();
            return null;
        }

        ErrorIssue = null;
        EstimatedTokens = result.Value.EstimatedTokens;
        ExceedsWarnThreshold = result.Value.ExceedsWarnThreshold;
        UpdatePreviewLines(result.Value.Text);
        return result.Value;
    }

    /// <summary>
    /// 8.6: 実際の出力テキストをファイル見出しで区切り、区間ごとに拡張子別の<see cref="SyntaxLexer"/>で
    /// 走査する。syntax.enabled=false・言語ルール無し・差分のみモード（対象解決が煩雑なため）は
    /// プレーン表示へフォールバックする（コピー結果自体には一切影響しない）。
    /// </summary>
    private void UpdatePreviewLines(string text)
    {
        PreviewLines.Clear();
        var rawLines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (!_settings.Syntax.Enabled || _selectedMode == ContextMode.ChangedSince)
        {
            foreach (var line in rawLines) PreviewLines.Add(new PreviewLine(line, Array.Empty<SyntaxToken>()));
            return;
        }

        string? extension = null;
        var buffer = new List<string>();
        foreach (var line in rawLines)
        {
            var match = FileHeaderPattern.Match(line);
            if (match.Success)
            {
                FlushPreviewSection(extension, buffer);
                extension = Path.GetExtension(match.Groups["path"].Value);
                PreviewLines.Add(new PreviewLine(line, Array.Empty<SyntaxToken>()));
            }
            else
            {
                buffer.Add(line);
            }
        }
        FlushPreviewSection(extension, buffer);
    }

    /// <summary>直前のファイル見出しから現在行までの区間（1ファイル分の本文＋コードフェンス行）をトークン化して積む。</summary>
    private void FlushPreviewSection(string? extension, List<string> buffer)
    {
        if (buffer.Count == 0) return;

        var rule = extension is null ? null : SyntaxLexer.RuleForExtension(extension);
        if (rule is null)
        {
            foreach (var line in buffer) PreviewLines.Add(new PreviewLine(line, Array.Empty<SyntaxToken>()));
            buffer.Clear();
            return;
        }

        var lexer = new SyntaxLexer(rule);
        var scanned = lexer.Scan(buffer);
        for (var i = 0; i < buffer.Count; i++)
        {
            var tokens = scanned && !lexer.IsDisabled ? lexer.TokenizeLine(i, buffer[i]) : Array.Empty<SyntaxToken>();
            PreviewLines.Add(new PreviewLine(buffer[i], tokens));
        }
        buffer.Clear();
    }

    // ---- 課題2: ファイルごとの3状態選択・フォルダ単位の一括切替 ----

    /// <summary>
    /// 3状態のサイクル順は「内容も出す→構成だけ→出さない→(内容も出す)」。フォルダが中間状態
    /// （配下の状態が混在＝null）のときは、次の値へ進めるのではなく必ず「内容も出す」へ戻す。
    /// これは「中間状態はユーザーが選べる値ではなく、あくまで配下の状態から自動的に決まる
    /// 表示専用の状態」という要件のためで、混在フォルダをクリックするたびに一貫して
    /// 「配下をまとめて内容ありへ揃える」という分かりやすい1操作にする狙いがある。
    /// </summary>
    private static ContextFileState NextState(ContextFileState? current) => current switch
    {
        ContextFileState.Full => ContextFileState.StructureOnly,
        ContextFileState.StructureOnly => ContextFileState.Hidden,
        ContextFileState.Hidden => ContextFileState.Full,
        null => ContextFileState.Full,
        _ => ContextFileState.Full,
    };

    private void CycleState(ContextFileNodeViewModel node)
    {
        if (node.IsExcluded) return;

        var next = NextState(node.State);
        if (node.IsDirectory)
        {
            ApplyStateRecursive(node, next);
        }
        else
        {
            node.State = next;
        }

        RecomputeDirectoryStates();
        UpdateApproxTokenEstimate();
        _persistTimer.Restart();
    }

    /// <summary>
    /// フォルダの3状態を配下の非除外ファイル・非除外サブフォルダへ再帰的に適用する
    /// （要件: フォルダのチェックを操作すると配下が一括で切り替わる。除外済みファイルは対象外）。
    /// ツリーは実ファイルシステムから生成される真の木であり循環参照は理論上発生しないが、
    /// 想定外のデータ破損時に無限再帰へ陥らないよう深さの上限で打ち切る（要件: 再帰更新が
    /// 無限ループしないようガードを入れる）。
    /// </summary>
    private void ApplyStateRecursive(ContextFileNodeViewModel node, ContextFileState state, int depth = 0)
    {
        if (depth > 256) return;

        if (!node.IsDirectory)
        {
            if (!node.IsExcluded) node.State = state;
            return;
        }

        if (node.IsExcluded) return; // 除外フォルダは配下を走査していないため子を持たない
        if (!_childrenByPath.TryGetValue(node.RelativePath, out var children)) return;
        foreach (var child in children) ApplyStateRecursive(child, state, depth + 1);
    }

    /// <summary>
    /// 全ディレクトリの状態を、配下ファイル・配下サブディレクトリの集計から再計算する
    /// （要件: 子の変更が祖先フォルダへ即座に反映される）。IndentLevelの深い順（葉に近い順）に
    /// 処理することで、サブディレクトリの集計値を使って親ディレクトリを計算できる。
    /// </summary>
    private void RecomputeDirectoryStates()
    {
        foreach (var dir in Files.Where(f => f.IsDirectory && !f.IsExcluded).OrderByDescending(f => f.IndentLevel))
        {
            dir.State = AggregateChildState(dir);
        }
    }

    /// <summary>
    /// 配下の非除外ファイル・非除外サブディレクトリの状態から集計する。全部一致なら同じ値、
    /// 対象が1件も無ければ既定表示として<see cref="ContextFileState.Full"/>（空フォルダは
    /// 実害が無いため）、状態が混在するならnull（中間状態）を返す。
    /// </summary>
    private ContextFileState? AggregateChildState(ContextFileNodeViewModel dir)
    {
        if (!_childrenByPath.TryGetValue(dir.RelativePath, out var children)) return ContextFileState.Full;

        ContextFileState? aggregate = null;
        var hasTarget = false;
        foreach (var child in children)
        {
            if (child.IsExcluded) continue;
            var childState = child.State; // ファイルは3値、サブフォルダは直前に計算済みの集計値
            if (childState is null) return null; // 子が既に混在／除外扱いなら親も混在

            if (!hasTarget)
            {
                aggregate = childState;
                hasTarget = true;
            }
            else if (aggregate != childState)
            {
                return null;
            }
        }

        return hasTarget ? aggregate : ContextFileState.Full;
    }

    /// <summary>相対パス→直下の子ノード一覧を作り直す。RefreshAsyncでFilesを作り直すたびに呼ぶ。</summary>
    private void RebuildChildrenMap()
    {
        _childrenByPath = new Dictionary<string, List<ContextFileNodeViewModel>>(StringComparer.Ordinal);
        foreach (var node in Files)
        {
            var parent = ParentPathOf(node.RelativePath);
            if (!_childrenByPath.TryGetValue(parent, out var list))
            {
                list = new List<ContextFileNodeViewModel>();
                _childrenByPath[parent] = list;
            }
            list.Add(node);
        }
    }

    private static string ParentPathOf(string relativePath)
    {
        var idx = relativePath.LastIndexOf('/');
        return idx < 0 ? string.Empty : relativePath[..idx];
    }

    /// <summary>
    /// 実ファイルを読まず、走査時に取得済みのSizeBytesから概算トークン数を出す。フォルダの
    /// 一括切替など選択操作のたびに全ファイルを読み直すと大規模プロジェクトで重くなるための
    /// 近似（バイト数を文字数の近似として扱う。正確な値は「プレビュー」「コピー」「保存」実行時に
    /// 実際の文字数から再計算される）。課題3の「既定オンで巨大になりがち」に対する注意喚起
    /// （WarnIfDefaultSelectionIsLarge）にも使う。
    ///
    /// 課題（バグ2）: 以前はここが選択ファイルのバイト数だけしか数えておらず、実際の出力に
    /// 必ず含まれる構成ツリー分をまったく計算に入れていなかった（実機で確認済み。「プレビュー」を
    /// 押す前は7件、押した後は125件と約18倍ずれていた）。特に全ファイルを「構成だけ」に
    /// した場合はtotalBytesがほぼ0になる一方、実際の出力には構成ツリーの分だけ確実に文字数が
    /// 生じるため、ずれが最大になる。構成ツリーが出力に含まれるモード（TreeOnly・
    /// TreeAndSelected）のときは、EstimateTreeSectionCharsで見積もった概算文字数を合算する。
    /// </summary>
    private void UpdateApproxTokenEstimate()
    {
        var fullPaths = new HashSet<string>(
            Files.Where(f => f is { IsDirectory: false, IsExcluded: false, State: ContextFileState.Full }).Select(f => f.RelativePath),
            StringComparer.OrdinalIgnoreCase);

        var totalBytes = 0L;
        foreach (var node in _lastScan)
        {
            if (!node.IsDirectory && fullPaths.Contains(node.RelativePath)) totalBytes += node.SizeBytes;
        }

        var treeChars = IncludesTreeInEstimate(_selectedMode) ? EstimateTreeSectionChars() : 0;

        EstimatedTokens = TokenEstimator.EstimateLength(totalBytes, _settings.Context.TokenRatio)
            + TokenEstimator.EstimateLength(treeChars, _settings.Context.TokenRatio);
        ExceedsWarnThreshold = EstimatedTokens > TokenWarnThreshold;
    }

    /// <summary>ContextCollector.CollectAsync内のIncludesTreeと同じ判定（構成ツリーを出力に含むモードか）。</summary>
    private static bool IncludesTreeInEstimate(ContextMode mode) => mode is ContextMode.TreeOnly or ContextMode.TreeAndSelected;

    /// <summary>
    /// 実際に出力される構成ツリー部分（概要見出し＋フェンス＋ツリー本文）の概算文字数。
    /// <see cref="ContextCollector"/>のBuildTreeTextと同じ整形（インデント幅2・ディレクトリの
    /// "/"・除外ディレクトリの畳み込み・各種注記文言）を簡略化してなぞり、行ごとの概算文字数を
    /// 積み上げる。実ファイルを読まないため厳密な一致は狙わないが、実際の出力と桁が変わる
    /// ほどのずれは出さないことを目的とする。
    /// </summary>
    private int EstimateTreeSectionChars()
    {
        const int perLineOverhead = 3; // 改行・区切り分の概算
        const int overviewAndFenceOverhead = 220; // 概要見出し・生成日時等・```text/```フェンスの概算

        var chars = overviewAndFenceOverhead;
        foreach (var node in Files)
        {
            // 「出さない」（Hidden）は非除外ファイルに限りツリーから完全に除かれる
            // （BuildTreeTextのhiddenPaths判定と同じ。全滅したフォルダの畳み込みまでは追わない簡略化）。
            if (!node.IsExcluded && !node.IsDirectory && node.State == ContextFileState.Hidden) continue;

            chars += node.IndentLevel * 2 + node.DisplayName.Length + perLineOverhead;
            if (node.IsDirectory)
            {
                chars += 1; // "/"
                if (node.IsExcluded) chars += 20; // "  （N ファイル、内容は非出力）" 相当の概算
            }
            else if (node.IsExcluded)
            {
                chars += (node.ExcludeReason?.Length ?? 0) + 10; // "  (理由・内容は非出力)" 相当
            }
            else if (_selectedMode == ContextMode.TreeAndSelected && node.State != ContextFileState.Full)
            {
                chars += 14; // "  (構成のみ・内容は省略)" 相当
            }
        }
        return chars;
    }

    /// <summary>
    /// 課題3: 既定は全部「内容も出す」のため、大規模プロジェクトでは開いた直後から推定
    /// トークン数が上限を超えることがある。黙って超過させず、フォルダ単位で「構成だけ」へ
    /// 切り替えるよう一言添えて気付けるようにする。
    /// </summary>
    private void WarnIfDefaultSelectionIsLarge()
    {
        if (!ExceedsWarnThreshold) return;
        StatusMessage =
            $"既定ですべてのファイルの内容を含めています。推定トークン数 {EstimatedTokens} 件が上限（{TokenWarnThreshold} 件）を超えています。"
            + "lib/ など不要なフォルダを「構成だけ」または「出さない」に切り替えることをお勧めします。";
    }

    // ---- 追加要件: プロジェクトごとのチェック状態（3状態）の永続化 ----

    /// <summary>
    /// 保存済みの3状態（既定=内容も出す から外れたものだけの差分）を復元する。記録された
    /// パスが現存しない、または値を解釈できない場合は無視し、次回以降のために掃除する。
    /// </summary>
    private async Task ApplyPersistedStatesAsync()
    {
        var stored = _project.Overrides.ContextFileStates;
        if (stored.Count == 0) return;

        var nodesByPath = Files.Where(f => !f.IsDirectory && !f.IsExcluded)
            .ToDictionary(f => f.RelativePath, StringComparer.OrdinalIgnoreCase);

        var hasStale = false;
        foreach (var (path, rawState) in stored)
        {
            // 要件F: ロックファイルは既定がFullではなくStructureOnlyのため、"Full"という
            // 記録（＝ユーザーが手でオンへ戻した）も有効な非既定値としてそのまま適用する
            // （PersistFileStatesAsyncのDefaultStateFor参照）。
            if (nodesByPath.TryGetValue(path, out var node) && Enum.TryParse<ContextFileState>(rawState, out var state))
            {
                node.State = state;
            }
            else
            {
                hasStale = true;
            }
        }

        if (hasStale)
        {
            // 記録済みだが現存しない／解釈できないパスをここで一度だけ掃除する
            // （要件: 記録されたパスがもう存在しない場合は無視し、集合からも掃除する）。
            await PersistFileStatesAsync().ConfigureAwait(true);
        }
    }

    private void OnPersistTick()
    {
        _persistTimer.Stop();
        _ = PersistFileStatesAsync();
    }

    /// <summary>
    /// 既定の状態から外れているファイルの状態だけをプロジェクトへ保存する（差分方式）。
    /// 「既定」はファイルごとに<see cref="DefaultStateFor"/>で決まる（通常はFull、要件Fの
    /// ロックファイルだけはStructureOnyが既定）。ロックファイルをユーザーが手でFullへ
    /// 戻した場合、それはロックファイルにとっての非既定値のため、Fullであっても記録される。
    /// </summary>
    private async Task PersistFileStatesAsync()
    {
        var nonDefault = Files
            .Where(f => f is { IsDirectory: false, IsExcluded: false } && f.State is not null
                        && f.State.Value != DefaultStateFor(f.RelativePath))
            .ToDictionary(f => f.RelativePath, f => f.State!.Value.ToString(), StringComparer.OrdinalIgnoreCase);

        var loaded = await _projectStore.LoadAsync().ConfigureAwait(true);
        var projects = loaded.Value.ToList();
        var index = projects.FindIndex(p => p.Id == _project.Id);
        if (index < 0) return;

        _project = _project with { Overrides = _project.Overrides with { ContextFileStates = nonDefault } };
        projects[index] = _project;
        await _projectStore.SaveAsync(projects).ConfigureAwait(true);
    }

    /// <summary>
    /// 指定ファイルの既定の3状態選択。要件F: ロックファイル（<see cref="ContextCollector.IsLockFileForInitialUncheck"/>）
    /// は「構成だけ」が既定、それ以外は「内容も出す」が既定（要件B）。
    /// </summary>
    private static ContextFileState DefaultStateFor(string relativePath)
        => ContextCollector.IsLockFileForInitialUncheck(relativePath) ? ContextFileState.StructureOnly : ContextFileState.Full;

    private async Task AddExcludeAsync()
    {
        var pattern = _newExcludePattern.Trim();
        if (string.IsNullOrEmpty(pattern) || ExtraExcludes.Contains(pattern)) return;

        ExtraExcludes.Add(pattern);
        NewExcludePattern = string.Empty;
        await PersistOverridesAsync().ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
    }

    private async Task RemoveExcludeAsync(string? pattern)
    {
        if (pattern is null || !ExtraExcludes.Remove(pattern)) return;
        await PersistOverridesAsync().ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
    }

    private async Task PersistOverridesAsync()
    {
        var loaded = await _projectStore.LoadAsync().ConfigureAwait(true);
        var projects = loaded.Value.ToList();
        var index = projects.FindIndex(p => p.Id == _project.Id);
        if (index < 0) return;

        _project = _project with { Overrides = _project.Overrides with { Excludes = ExtraExcludes.ToArray() } };
        projects[index] = _project;
        await _projectStore.SaveAsync(projects).ConfigureAwait(true);
    }

    /// <summary>収集モードの選択肢1件。</summary>
    public sealed record ModeOption(string Label, ContextMode Mode);

    /// <summary>差分のみモード用のリビジョン選択肢1件。</summary>
    public sealed record RevisionOption(int Revision, string Summary)
    {
        public string DisplayText => $"r{Revision} — {Summary}";
    }

    /// <summary>8.6: 出力プレビューの1行。<see cref="Graft.Views.CodeLineControl"/> にそのまま束縛する。</summary>
    public sealed record PreviewLine(string Text, IReadOnlyList<SyntaxToken> Tokens);
}

/// <summary>ファイルツリー1行分の3状態選択を保持する。</summary>
public sealed class ContextFileNodeViewModel : ObservableObject
{
    private ContextFileState? _state;

    public ContextFileNodeViewModel(ContextFileNode node)
    {
        RelativePath = node.RelativePath;
        IsDirectory = node.IsDirectory;
        IsExcluded = node.IsExcluded;
        ExcludeReason = node.ExcludeReason;
        IndentLevel = node.RelativePath.Count(c => c == '/');
        var nameStart = node.RelativePath.LastIndexOf('/') + 1;
        DisplayName = node.RelativePath[nameStart..];

        // 課題3: 既定は全部「内容も出す」。ディレクトリの初期値は、ContextCollectViewModelが
        // RefreshAsync完了時にRecomputeDirectoryStatesで配下ファイルの集計へ必ず上書きするため、
        // ここではFullのままにしておけば十分。除外ファイル・除外ディレクトリは選択の対象外
        // のため常にnull（「対象外」を表す）。
        // 要件F: ただしpackage-lock.json等のロックファイルだけは「構成だけ」を初期値にする
        // （中身は大半がAIにとって無価値なうえ数万行に及ぶこともあり、既定でトークンを
        // 浪費させないため）。チェックボックス自体は有効なままなのでユーザーが手でオンにできる。
        _state = IsExcluded ? null
            : IsDirectory ? ContextFileState.Full
            : ContextCollector.IsLockFileForInitialUncheck(RelativePath) ? ContextFileState.StructureOnly
            : ContextFileState.Full;
    }

    public string RelativePath { get; }
    public bool IsDirectory { get; }
    public bool IsExcluded { get; }
    public string? ExcludeReason { get; }
    public int IndentLevel { get; }
    public string DisplayName { get; }

    /// <summary>
    /// 3状態選択。ファイルは常に具体的な値（Full/StructureOnly/Hidden）を持ち、ディレクトリは
    /// 配下の非除外ファイル・非除外サブディレクトリの集計結果を持つ（全部一致なら同じ値、
    /// 混在するならnull=中間状態。<see cref="ContextCollectViewModel"/>のRecomputeDirectoryStates
    /// が計算する）。除外ファイル・除外ディレクトリは選択の対象外のため常にnull。
    /// </summary>
    public ContextFileState? State
    {
        get => _state;
        set
        {
            if (!SetProperty(ref _state, value)) return;
            OnPropertyChanged(nameof(StateLabel));
            OnPropertyChanged(nameof(AutomationLabel));
            OnPropertyChanged(nameof(IsFull));
            OnPropertyChanged(nameof(IsStructureOnly));
            OnPropertyChanged(nameof(IsHidden));
            OnPropertyChanged(nameof(IsMixed));
        }
    }

    /// <summary>状態を表す短い日本語ラベル。ツールチップ・読み上げに使う。</summary>
    public string StateLabel => State switch
    {
        ContextFileState.Full => "内容も出す",
        ContextFileState.StructureOnly => "構成だけ",
        ContextFileState.Hidden => "出さない",
        null => IsDirectory ? "混在（配下の状態が一致していません）" : "対象外",
        _ => "",
    };

    // XAML側は「Classes文字列プロパティへの直接バインド」ができないため、状態ごとの
    // bool発火に分解している（ExplorerView.axamlのlocal|IconGlyph.treeIcon.excludedと同じ考え方。
    // Window.StylesでClasses.xxxごとにアイコンのData/Strokeを切り替える）。
    public bool IsFull => State == ContextFileState.Full;
    public bool IsStructureOnly => State == ContextFileState.StructureOnly;
    public bool IsHidden => State == ContextFileState.Hidden;
    /// <summary>フォルダの配下状態が混在している「中間状態」。ファイルでは常にfalse。</summary>
    public bool IsMixed => State is null && !IsExcluded;

    /// <summary>8.14: スクリーンリーダー向けの読み上げ文言。種別・除外理由・現在の状態を含める。</summary>
    public string AutomationLabel => IsExcluded
        ? $"{(IsDirectory ? "フォルダ" : "ファイル")} {DisplayName}（除外: {ExcludeReason}）"
        : $"{(IsDirectory ? "フォルダ" : "ファイル")} {DisplayName}（{StateLabel}）";
}
