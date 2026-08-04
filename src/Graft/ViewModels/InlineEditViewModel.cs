using System.Windows.Threading;
using Graft.Core;

namespace Graft.ViewModels;

/// <summary>
/// インライン編集パネル右ペインの1行（実ファイル内容の表示専用、編集不可）。
/// </summary>
public sealed class FileLineViewModel
{
    public FileLineViewModel(int lineNumber, string text, IReadOnlyList<SyntaxToken> tokens)
    {
        LineNumberText = lineNumber.ToString();
        Text = text;
        Tokens = tokens;
        AutomationName = $"{lineNumber}行目: {text}";
    }

    public string LineNumberText { get; }
    public string Text { get; }
    public IReadOnlyList<SyntaxToken> Tokens { get; }
    public string AutomationName { get; }
}

/// <summary>
/// 仕様書8.7: マッチ失敗ブロックのSEARCH部インライン編集。1つの SEARCH/REPLACE ペアに対応する。
/// 右ペインに実ファイル内容、左に編集可能なSEARCH部を並べ、入力から200ms後に
/// <see cref="MatchEngine"/> で再判定する。編集内容はそのリビジョンにのみ適用する想定であり、
/// このクラス自身は元のパッチ本文（<see cref="PatchBlock"/>）を一切変更しない
/// （<see cref="BuildEditedPair"/> は新しい <see cref="SearchReplacePair"/> を都度生成して返す）。
/// </summary>
public sealed class InlineEditViewModel : ObservableObject, IDisposable
{
    private const int DebounceMs = 200;

    private readonly SearchReplacePair _originalPair;
    private readonly string _fileText;
    private readonly OccurrenceSpec _occurrence;
    private readonly MatchEngine _matchEngine;
    private readonly DispatcherTimer _debounceTimer;
    private string _searchText;
    private string _resultSummary = string.Empty;
    private bool _isMatchSuccessful;
    private MatchStage _resultStage = MatchStage.None;

    public InlineEditViewModel(string filePath, SearchReplacePair originalPair, string fileText,
        OccurrenceSpec occurrence, MatchOptions matchOptions, bool syntaxEnabled)
    {
        FilePath = filePath;
        _originalPair = originalPair;
        _fileText = fileText;
        _occurrence = occurrence;
        _matchEngine = new MatchEngine(matchOptions);
        _searchText = originalPair.SearchText;

        FileLines = BuildFileLines(filePath, fileText, syntaxEnabled);

        _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DebounceMs) };
        _debounceTimer.Tick += OnDebounceTick;

        RunMatch();
    }

    /// <summary>対象ファイルのプロジェクト相対パス。</summary>
    public string FilePath { get; }

    /// <summary>SEARCH マーカー行の # 以降から抽出した変更説明。</summary>
    public string Description => _originalPair.Description ?? string.Empty;

    /// <summary>パッチ本文中の SEARCH マーカー行の行番号（1始まり）。</summary>
    public int SourceLine => _originalPair.SourceLine;

    /// <summary>REPLACE部（編集対象外、表示専用）。</summary>
    public string ReplaceText => _originalPair.ReplaceText;

    /// <summary>編集前のSEARCH部（差分表示・破棄用）。</summary>
    public string OriginalSearchText => _originalPair.SearchText;

    /// <summary>右ペインに表示する実ファイル内容（行単位、シンタックス付き）。</summary>
    public IReadOnlyList<FileLineViewModel> FileLines { get; }

    /// <summary>編集中のSEARCH部。変更のたびに200ms後の再判定をスケジュールする。</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value)) return;
            OnPropertyChanged(nameof(HasEdits));
            _debounceTimer.Stop();
            _debounceTimer.Start();
        }
    }

    /// <summary>元のSEARCH部から編集されているかどうか。</summary>
    public bool HasEdits => !string.Equals(_searchText, _originalPair.SearchText, StringComparison.Ordinal);

    /// <summary>現在のSEARCH部でマッチに成功しているかどうか。</summary>
    public bool IsMatchSuccessful { get => _isMatchSuccessful; private set => SetProperty(ref _isMatchSuccessful, value); }

    /// <summary>再判定結果の説明文（成功時はどの段階で一致したか、失敗時は理由）。</summary>
    public string ResultSummary { get => _resultSummary; private set => SetProperty(ref _resultSummary, value); }

    /// <summary>再判定結果のマッチ段階。未成功時は <see cref="MatchStage.Failed"/> または <see cref="MatchStage.None"/>。</summary>
    public MatchStage ResultStage { get => _resultStage; private set => SetProperty(ref _resultStage, value); }

    /// <summary>編集後のペアを返す。元のパッチ本文は変更せず、このリビジョンへの適用時にのみ使う。</summary>
    public SearchReplacePair BuildEditedPair() => _originalPair with { SearchText = _searchText };

    public void Dispose()
    {
        _debounceTimer.Stop();
        _debounceTimer.Tick -= OnDebounceTick;
    }

    private void OnDebounceTick(object? sender, EventArgs e)
    {
        _debounceTimer.Stop();
        RunMatch();
    }

    private void RunMatch()
    {
        if (string.IsNullOrEmpty(_searchText))
        {
            IsMatchSuccessful = false;
            ResultStage = MatchStage.None;
            ResultSummary = "SEARCH部が空です";
            return;
        }

        var candidate = _originalPair with { SearchText = _searchText };
        var result = _matchEngine.Match(_fileText, candidate, _occurrence);
        if (result.IsSuccess)
        {
            ApplySuccess(result.Value[0].Stage, result.Value.Count);
        }
        else
        {
            ApplyFailure(result);
        }
    }

    private void ApplySuccess(MatchStage stage, int matchCount)
    {
        IsMatchSuccessful = true;
        ResultStage = stage;
        var suffix = matchCount > 1 ? $"（{matchCount}箇所）" : string.Empty;
        ResultSummary = DescribeStage(stage) + suffix;
    }

    private void ApplyFailure(GraftResult<IReadOnlyList<MatchResult>> result)
    {
        IsMatchSuccessful = false;
        ResultStage = MatchStage.Failed;
        ResultSummary = result.Issues.Count > 0 ? result.Issues[0].ToDisplayText() : "一致しませんでした";
    }

    private static string DescribeStage(MatchStage stage) => stage switch
    {
        MatchStage.Exact => "完全一致で成功しました",
        MatchStage.TrailingWhitespace => "行末空白を無視して一致しました",
        MatchStage.RelativeIndent => "インデント差を吸収して一致しました",
        MatchStage.IgnoreBlankLines => "空行を無視して一致しました",
        MatchStage.Similarity => "類似度による一致です。内容を確認してください",
        _ => "一致しませんでした",
    };

    // 右ペイン（実ファイル内容）は独立してシンタックススキャンする。ファイル全体を対象に
    // するDiffViewModel側のスキャンとは責務が分かれており（このパネルはブロック単体の
    // インライン編集専用のため）、多少のスキャン重複は許容する。
    private static IReadOnlyList<FileLineViewModel> BuildFileLines(string filePath, string fileText, bool syntaxEnabled)
    {
        var lines = TextNormalizer.SplitLines(fileText);
        var rule = syntaxEnabled ? SyntaxLexer.RuleForExtension(System.IO.Path.GetExtension(filePath)) : null;
        SyntaxLexer? lexer = null;
        if (rule is not null)
        {
            lexer = new SyntaxLexer(rule);
            lexer.Scan(lines);
        }

        var result = new List<FileLineViewModel>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            var tokens = lexer?.TokenizeLine(i, lines[i]) ?? Array.Empty<SyntaxToken>();
            result.Add(new FileLineViewModel(i + 1, lines[i], tokens));
        }
        return result;
    }
}
