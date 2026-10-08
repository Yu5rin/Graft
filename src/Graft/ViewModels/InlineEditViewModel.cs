using System.Windows.Input;
using Graft.Core;
using Graft.Platform;

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
///
/// 【書き換えたSEARCHを適用へ反映する経路（<see cref="AdoptCommand"/>）】
/// 以前は再判定で「一致した」と分かっても、その結果を適用へ渡す経路が無く
/// （<see cref="BuildEditedPair"/> の呼び出し元が1つも無かった）、利用者は一致まで確認できても
/// 結局AIに依頼し直すしかなかった。<see cref="AdoptCommand"/> は、書き換えたSEARCHが
/// <b>確定的に</b>一致しているときだけ押せる操作で、押すと呼び出し元（<c>MainViewModel</c>）が
/// メモリ上の現在のパッチの該当ペアを差し替えてドライランをやり直す。このクラス自身は
/// 差し替えもドライランも行わない（<see cref="Func{T, TResult}"/> で渡されたハンドラへ編集後のペアを
/// 渡すだけ）。
/// </summary>
public sealed class InlineEditViewModel : ObservableObject, IDisposable
{
    private const int DebounceMs = 200;

    private readonly SearchReplacePair _originalPair;
    private readonly string _fileText;
    private readonly OccurrenceSpec _occurrence;
    private readonly MatchEngine _matchEngine;
    private readonly IUiTimer _debounceTimer;
    private readonly Func<SearchReplacePair, Task>? _adoptHandler;
    private string _searchText;
    private string _resultSummary = string.Empty;
    private bool _isMatchSuccessful;
    private MatchStage _resultStage = MatchStage.None;

    // 入力してから再判定が走るまでの200msの間は、画面の「一致」表示が古い入力に対するものである。
    // この間に「適用に含める」を押せてしまうと、直前に消した文字を含む古い判定で差し替えかねない。
    private bool _isMatchPending;

    /// <param name="adoptHandler">
    /// 「この修正で適用に含める」が押されたときに、<b>編集後のペア</b>を受け取って差し替えと
    /// ドライランのやり直しを行う処理。null のとき（適用前プレビューなど、差し替えの意味が無い
    /// 場所）は <see cref="CanAdopt"/> が常に false になり、操作自体が出ない。
    /// </param>
    public InlineEditViewModel(string filePath, SearchReplacePair originalPair, string fileText,
        OccurrenceSpec occurrence, MatchOptions matchOptions, bool syntaxEnabled, IUiServices ui,
        Func<SearchReplacePair, Task>? adoptHandler = null)
    {
        ArgumentNullException.ThrowIfNull(ui);
        _adoptHandler = adoptHandler;
        AdoptCommand = new AsyncRelayCommand(AdoptAsync, () => CanAdopt, context: "SEARCH部の修正の取り込み");
        FilePath = filePath;
        _originalPair = originalPair;
        _fileText = fileText;
        _occurrence = occurrence;
        _matchEngine = new MatchEngine(matchOptions);
        _searchText = originalPair.SearchText;

        FileLines = BuildFileLines(filePath, fileText, syntaxEnabled);

        _debounceTimer = ui.CreateTimer(TimeSpan.FromMilliseconds(DebounceMs), OnDebounceTick);

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
            _isMatchPending = true;
            NotifyCanAdoptChanged();
            _debounceTimer.Restart();
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

    /// <summary>
    /// 編集後のペアを返す。元のパッチ本文は変更せず、新しいインスタンスを都度作る。REPLACE部・説明・
    /// 行番号などSEARCH部以外はすべて元のまま引き継ぎ、書き換えられたことが分かる印
    /// （<see cref="SearchReplacePair.IsSearchEdited"/>）だけを立てる。編集していなければ元のペアを
    /// そのまま返す（印を立てない。履歴に「書き換えた」と嘘を残さないため）。
    /// </summary>
    public SearchReplacePair BuildEditedPair()
        => HasEdits ? _originalPair with { SearchText = _searchText, IsSearchEdited = true } : _originalPair;

    /// <summary>
    /// 「この修正で適用に含める」を押せるかどうか。次のすべてを満たすときだけ true。
    /// <list type="bullet">
    /// <item>差し替え先のハンドラがある。</item>
    /// <item>SEARCH部が元から変わっている（変わっていなければ差し替える意味が無い。しかも元のSEARCHは
    /// 失敗しているので、ここが一致することもあり得ない）。</item>
    /// <item>現在のSEARCH部が一致している（<see cref="IsMatchSuccessful"/>）。複数箇所に一致して
    /// OCCURRENCE未指定の場合は <see cref="MatchEngine"/> が E102 で失敗を返すため、ここには
    /// 含まれない（「一意に一致」はエンジンの判定に任せ、独自の数え直しをしない）。OCCURRENCE=ALL を
    /// 明示したブロックが複数箇所に一致するのは書き手の意図どおりなので押せる。</item>
    /// <item>入力後の再判定が済んでいる（<c>_isMatchPending</c> でない）。</item>
    /// </list>
    /// 類似度による一致（<see cref="MatchStage.Similarity"/>）も押せるが、差し替え後のドライランで
    /// 通常の「要確認」扱いになり、利用者の確認なしには適用されない。
    /// </summary>
    public bool CanAdopt => _adoptHandler is not null && HasEdits && IsMatchSuccessful && !_isMatchPending;

    /// <summary>差し替えの受け皿がある画面かどうか。false の画面では「適用に含める」ボタン自体を出さない。</summary>
    public bool HasAdoptHandler => _adoptHandler is not null;

    /// <summary>「この修正で適用に含める」。実体は <see cref="AdoptAsync"/>。</summary>
    public ICommand AdoptCommand { get; }

    /// <summary>
    /// 書き換えたSEARCHを適用へ反映する。押された瞬間の入力で必ず再判定し直してから、まだ一致して
    /// いるときだけハンドラへ編集後のペアを渡す（画面の判定が古い・直前に別の経路で状態が変わった、
    /// といった取りこぼしを防ぐ二重の確認）。ハンドラ側もさらに、実際のドライラン
    /// （安全検査すべてを含むE217など）でこのペアを改めて検証する。
    /// </summary>
    public async Task AdoptAsync()
    {
        if (_adoptHandler is null) return;

        _debounceTimer.Stop();
        RunMatch();
        if (!CanAdopt) return;

        await _adoptHandler(BuildEditedPair()).ConfigureAwait(true);
    }

    public void Dispose() => _debounceTimer.Dispose();

    private void OnDebounceTick()
    {
        _debounceTimer.Stop();
        RunMatch();
    }

    private void RunMatch()
    {
        _isMatchPending = false;
        RunMatchCore();
        NotifyCanAdoptChanged();
    }

    private void NotifyCanAdoptChanged()
    {
        OnPropertyChanged(nameof(CanAdopt));
        // ボタンの有効/無効は CommandRequery（ポインタ・キー操作のたびの再評価）に任せていると、
        // 「入力 → 200ms後に一致」の時点では次の操作が来るまで古い状態のままになる。
        ((AsyncRelayCommand)AdoptCommand).RaiseCanExecuteChanged();
    }

    private void RunMatchCore()
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
