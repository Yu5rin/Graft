using System.Collections.ObjectModel;
using Graft.Core;
using Graft.Infra;
using Graft.Platform;

namespace Graft.ViewModels;

/// <summary>
/// diff表示・シンタックスハイライト・インライン編集を統括するViewModel（仕様書5章・8.3・8.6・8.7・8.13・8.14）。
/// 1回の <see cref="Load"/> はブロック1件分の差分を表示する。他担当（ブロック一覧側）は
/// このクラスの <see cref="Load"/> / <see cref="Clear"/> / <see cref="IsIncluded"/> を通じて連携する。
/// 4.8のdiffジャンプ（<see cref="JumpRequested"/> / <see cref="RequestJump"/>）は
/// 1ファイル400行上限のため <c>DiffViewModel.Jump.cs</c> に分割する。並列／統合表示の
/// 切り替え（<see cref="IsSideBySide"/>）と行組み立ては同じ理由で <c>DiffViewModel.SideBySide.cs</c>
/// に分割する。
/// </summary>
public sealed partial class DiffViewModel : ObservableObject
{
    // 8.13: 省略行を1クリックでどれだけ展開するか（一括全展開は IsFullyExpanded を使う）。
    private const int ExpandBatchSize = 50;
    private const double MinCodeFontSize = 8;
    private const double MaxCodeFontSize = 32;

    // 課題1: 構築時に固定するとMainViewModelのDiff構築（設定読み込み前）で渡した既定値の
    // ままになってしまうため（従来はWordWrap/ShowWhitespaceだけ個別に上書きしていた）、
    // readonlyにせずUpdateSettings経由で差し替え可能にしておく。
    private Settings _settings;
    private readonly IUiServices _ui;

    private BlockPlan? _plan;
    private SyntaxLexer? _beforeLexer;
    private SyntaxLexer? _afterLexer;
    private IReadOnlyList<DiffLine> _flatFolded = Array.Empty<DiffLine>();
    private IReadOnlyList<DiffLine>? _flatFull;
    private readonly Dictionary<int, int> _expansions = new();

    private bool _isIncluded;
    private bool _isSideBySide = true;
    private bool _wordWrap;
    private bool _showWhitespace;
    private bool _isFullyExpanded;
    private bool _syntaxHighlightDisabled;
    private double _codeFontSize = 13;

    public DiffViewModel(Settings settings, IUiServices ui)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _wordWrap = settings.Diff.WordWrap;
        _showWhitespace = settings.Diff.ShowWhitespace;
        // 機能改善（差分の左右並列表示）: 初期値はSettings.Diff.SideBySideから読む。
        // ここではまだLines等が空のためRebuildRows・SideBySideChangeCommittedは不要（コンストラクタで
        // フィールドへ直接設定するだけに留め、IsSideBySideのsetterは通さない。setterを通すと
        // 構築時にもSideBySideChangeCommittedが飛んでしまい、設定の読み込みを「ユーザー操作」と
        // 誤認して保存経路に載せてしまう）。
        _isSideBySide = settings.Diff.SideBySide;
        // 機能改善: エディタ本文と差分表示は同じSettings.Editor.FontSizeを共有する
        // （UpdateSettingsのコメント参照）。
        _codeFontSize = Math.Clamp(settings.Editor.FontSize, MinCodeFontSize, MaxCodeFontSize);
    }

    /// <summary>要確認ブロックの適用可否（8.7）。ブロック一覧側が読み書きして反映する。</summary>
    public bool IsIncluded { get => _isIncluded; set => SetProperty(ref _isIncluded, value); }

    // IsSideBySide・SideBySideChangeCommitted・並列／統合表示の行組み立ては
    // 1ファイル400行上限のため DiffViewModel.SideBySide.cs に分割する。

    /// <summary>長い行を折り返すかどうか（8.13）。</summary>
    public bool WordWrap { get => _wordWrap; set => SetProperty(ref _wordWrap, value); }

    /// <summary>空白文字（タブ・行末空白）を可視化するかどうか（8.13）。</summary>
    public bool ShowWhitespace { get => _showWhitespace; set => SetProperty(ref _showWhitespace, value); }

    /// <summary>
    /// 課題1: 設定画面での変更（MainViewModel.UpdateSettings経由）・起動時の初回読み込みの
    /// 両方から呼ぶ。折り返し・空白表示・行番号表示は、いま画面に表示中のdiffの見た目
    /// そのものなので、再読み込みなしにその場で反映する（要件: 既に開いている画面への反映）。
    /// シンタックスハイライトの有効可否（PrepareSyntaxが使う）・マッチング設定
    /// （BuildInlineEditsが使う）は、いま表示中の内容を裏で作り直すことはせず、次に
    /// <see cref="Load"/>されるブロックから新しい値が効く（どちらも1ブロックぶんの表示を
    /// 組み立てる際に一度だけ参照する値のため、都度作り直すコストを払ってまで
    /// 表示中のものを即座に再構築する必要は無い）。
    ///
    /// 機能改善: <see cref="CodeFontSize"/>もエディタ本文と共有するSettings.Editor.FontSizeから
    /// その場で反映する。<see cref="FontSizeChangeCommitted"/>は発火しない（EditorPaneViewModel.
    /// UpdateSettingsと同じ理由。ここでの反映は既に確定済みの値を映すだけのため）。
    /// </summary>
    public void UpdateSettings(Settings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        WordWrap = settings.Diff.WordWrap;
        ShowWhitespace = settings.Diff.ShowWhitespace;
        ApplySideBySideFromSettings(settings.Diff.SideBySide);
        CodeFontSize = settings.Editor.FontSize;
        OnPropertyChanged(nameof(ShowLineNumbers));
    }

    /// <summary>すべての省略範囲を展開するトグル（8.13）。</summary>
    public bool IsFullyExpanded { get => _isFullyExpanded; set { if (SetProperty(ref _isFullyExpanded, value)) RebuildRows(); } }

    /// <summary>
    /// シンタックスハイライトが性能基準（10000行/100ms）を満たせず無効化されたかどうか（8.6）。
    /// ステータスバー表示は呼び出し側の責務。
    /// </summary>
    public bool SyntaxHighlightDisabled { get => _syntaxHighlightDisabled; private set => SetProperty(ref _syntaxHighlightDisabled, value); }

    /// <summary>行番号を表示するかどうか（設定 syntax.showLineNumbers）。</summary>
    public bool ShowLineNumbers => _settings.Syntax.ShowLineNumbers;

    /// <summary>コード表示のフォントサイズ（8.4）。Ctrl+マウスホイールでの変更をViewが書き込む。</summary>
    public double CodeFontSize
    {
        get => _codeFontSize;
        set => SetProperty(ref _codeFontSize, Math.Clamp(value, MinCodeFontSize, MaxCodeFontSize));
    }

    /// <summary>
    /// 機能改善: DiffView.axaml.csがCtrl+マウスホイールを検知したときに呼ぶ。
    /// EditorPaneViewModel.AdjustFontSizeと同じ考え方（値の即時反映＋確定通知の発火を分離）。
    /// </summary>
    public void AdjustCodeFontSize(double delta)
    {
        CodeFontSize += delta;
        FontSizeChangeCommitted?.Invoke(this, CodeFontSize);
    }

    /// <summary>
    /// 機能改善: 差分表示側でのCtrl+マウスホイールでの変更の確定通知。エディタ本文と設定を
    /// 共有するため、ShellViewModel経由でEditorPaneViewModel.FontSizeと同期させ、
    /// 設定への永続化（SettingsViewModelのデバウンス保存）へ乗せる
    /// （EditorPaneViewModel.FontSizeChangeCommittedのコメント参照）。
    /// </summary>
    public event EventHandler<double>? FontSizeChangeCommitted;

    /// <summary>段階5（類似度）でマッチした要確認ブロックかどうか（8.7）。</summary>
    public bool NeedsConfirmation => _plan?.NeedsConfirmation ?? false;

    /// <summary><see cref="IsIncluded"/> をユーザーが切り替えられるかどうか。</summary>
    public bool CanToggleInclusion => _plan?.NeedsConfirmation ?? false;

    /// <summary>マッチ失敗（適用不可）ブロックかどうか。</summary>
    public bool IsFailed => _plan is { CanApply: false };

    /// <summary>表示中のファイルパス。未読み込み時は null。</summary>
    public string? FilePath => _plan?.Path;

    /// <summary>変更説明。</summary>
    public string? Description => _plan?.Description;

    /// <summary>diff本体の表示行。ListBox等のItemsSourceに束縛する。</summary>
    public ObservableCollection<DiffLineViewModel> Lines { get; } = new();

    /// <summary>マッチ失敗ブロックのインライン編集対象（SEARCH/REPLACEペア単位）。</summary>
    public ObservableCollection<InlineEditViewModel> InlineEdits { get; } = new();

    /// <summary>インライン編集が1件以上あるかどうか。</summary>
    public bool HasInlineEdits => InlineEdits.Count > 0;

    /// <summary>ブロックの差分を読み込んで表示する。</summary>
    public void Load(BlockPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Clear();
        _plan = plan;

        PrepareSyntax(plan);
        _flatFolded = plan.Diff?.Hunks.SelectMany(h => h.Lines).ToArray() ?? Array.Empty<DiffLine>();

        IsIncluded = plan.IsSelected;
        // 5章: 段階3（相対インデント一致）はインデント差の確認が必要なため既定でON。
        if (plan.Stage == MatchStage.RelativeIndent)
        {
            ShowWhitespace = true;
        }

        BuildInlineEdits(plan);
        RebuildRows();
        NotifyPlanDependentProperties();
    }

    /// <summary>表示を空にする。</summary>
    public void Clear()
    {
        _plan = null;
        _beforeLexer = null;
        _afterLexer = null;
        _flatFolded = Array.Empty<DiffLine>();
        _flatFull = null;
        _expansions.Clear();
        Lines.Clear();

        foreach (var edit in InlineEdits) edit.Dispose();
        InlineEdits.Clear();

        SyntaxHighlightDisabled = false;
        IsIncluded = false;
        NotifyPlanDependentProperties();
    }

    private void NotifyPlanDependentProperties()
    {
        OnPropertyChanged(nameof(NeedsConfirmation));
        OnPropertyChanged(nameof(CanToggleInclusion));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(FilePath));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(HasInlineEdits));
    }

    // ------------------------------------------------------------------
    // 8.6 シンタックスハイライト（ファイル全体を一度スキャンし、行単位の描画時に参照する）
    // ------------------------------------------------------------------

    private void PrepareSyntax(BlockPlan plan)
    {
        var beforeLines = plan.BeforeText is null ? Array.Empty<string>() : TextNormalizer.SplitLines(plan.BeforeText);
        var afterLines = plan.AfterText is null ? Array.Empty<string>() : TextNormalizer.SplitLines(plan.AfterText);

        var rule = _settings.Syntax.Enabled ? SyntaxLexer.RuleForExtension(System.IO.Path.GetExtension(plan.Path)) : null;
        if (rule is null)
        {
            _beforeLexer = null;
            _afterLexer = null;
            SyntaxHighlightDisabled = false;
            return;
        }

        _beforeLexer = new SyntaxLexer(rule);
        _afterLexer = new SyntaxLexer(rule);
        var beforeOk = _beforeLexer.Scan(beforeLines);
        var afterOk = _afterLexer.Scan(afterLines);
        SyntaxHighlightDisabled = !beforeOk || !afterOk;
    }

    private IReadOnlyList<SyntaxToken> TokensFor(DiffLine line)
    {
        if (line.NewLine is int n && _afterLexer is not null) return _afterLexer.TokenizeLine(n - 1, line.Text);
        if (line.OldLine is int o && _beforeLexer is not null) return _beforeLexer.TokenizeLine(o - 1, line.Text);
        return Array.Empty<SyntaxToken>();
    }

    private DiffCellViewModel MakeCell(DiffLine line)
    {
        var tokens = line.Kind == DiffLineKind.Omitted ? Array.Empty<SyntaxToken>() : TokensFor(line);
        return new DiffCellViewModel(line.Kind, line.OldLine, line.NewLine, line.Text,
            line.InlineSpans, tokens, BuildAutomationName(line));
    }

    private static string BuildAutomationName(DiffLine line)
    {
        if (line.Kind == DiffLineKind.Omitted) return line.Text;
        var no = line.NewLine ?? line.OldLine;
        var prefix = no is int n ? $"{n}行目 " : string.Empty;
        return $"{prefix}{line.KindText}: {line.Text}";
    }

    // ------------------------------------------------------------------
    // 8.7 インライン編集（マッチ失敗ブロックのSEARCH部）
    // ------------------------------------------------------------------

    private void BuildInlineEdits(BlockPlan plan)
    {
        if (plan.CanApply || plan.Block is not SearchReplaceBlock srBlock) return;

        var options = new MatchOptions
        {
            SimilarityThreshold = _settings.Matching.SimilarityThreshold,
            AllowSimilarityMatch = _settings.Matching.AllowSimilarityMatch,
            RangeWarningLines = _settings.Matching.RangeWarningLines,
        };
        var fileText = plan.BeforeText ?? string.Empty;
        var engine = new MatchEngine(options);
        // SR形式は1ペア=1件のBlockPlanのため、対応ペアのみ対象にする（無ければ全ペア対象。ファイル単位の失敗など）。
        var targetPairs = plan.Pair is { } single ? new[] { single } : srBlock.Pairs;
        foreach (var pair in targetPairs)
        {
            var result = engine.Match(fileText, pair, srBlock.Occurrence);
            if (result.IsSuccess) continue;

            InlineEdits.Add(new InlineEditViewModel(
                plan.Path, pair, fileText, srBlock.Occurrence, options, _settings.Syntax.Enabled, _ui));
        }
    }

    // ------------------------------------------------------------------
    // 8.13 折りたたみ・段階的展開・全展開
    // ------------------------------------------------------------------

    // 省略行の展開先を求めるために、DiffBuilder.BuildFull（折りたたみなしの全文差分）を
    // 必要になった時点でのみ計算しキャッシュする。
    private IReadOnlyList<DiffLine> EnsureFullFlat()
    {
        if (_flatFull is not null) return _flatFull;
        if (_plan is null) return _flatFull = Array.Empty<DiffLine>();

        var full = DiffBuilder.BuildFull(_plan.Path, _plan.BeforeText, _plan.AfterText);
        return _flatFull = full.Hunks.SelectMany(h => h.Lines).ToArray();
    }

    // 省略行の直前・直後（必ず変更なし行、またはファイル端）の行番号を手がかりに、
    // 全展開版の対応区間を特定する。
    private List<DiffLine> SliceFullRange(int omittedIndex)
    {
        var full = EnsureFullFlat();
        var prev = omittedIndex > 0 ? _flatFolded[omittedIndex - 1] : null;
        var next = omittedIndex < _flatFolded.Count - 1 ? _flatFolded[omittedIndex + 1] : null;

        var start = prev is null ? 0 : FindAnchorIndex(full, prev) + 1;
        var end = next is null ? full.Count : FindAnchorIndex(full, next);
        if (start < 0 || end < 0 || start > end) return new List<DiffLine>();
        return full.Skip(start).Take(end - start).ToList();
    }

    private static int FindAnchorIndex(IReadOnlyList<DiffLine> full, DiffLine anchor)
    {
        for (var i = 0; i < full.Count; i++)
        {
            if (full[i].OldLine == anchor.OldLine && full[i].NewLine == anchor.NewLine) return i;
        }
        return -1;
    }

    private void ExpandOmitted(int omittedIndex)
    {
        var line = _flatFolded[omittedIndex];
        var current = _expansions.GetValueOrDefault(omittedIndex);
        _expansions[omittedIndex] = Math.Min(current + ExpandBatchSize, line.OmittedCount);
        RebuildRows();
    }

    // 折りたたみ済みの元配列に対し、これまでの展開状態（_expansions）を適用した実体行列を作る。
    // 各要素は「省略行として展開コマンドを持つべきか（OmittedKey>=0）」を併せて運ぶ。
    private readonly record struct FlatEntry(DiffLine Line, int OmittedKey);

    private List<FlatEntry> ApplyExpansions()
    {
        var result = new List<FlatEntry>(_flatFolded.Count);
        for (var i = 0; i < _flatFolded.Count; i++)
        {
            var line = _flatFolded[i];
            if (line.Kind != DiffLineKind.Omitted)
            {
                result.Add(new FlatEntry(line, -1));
                continue;
            }

            var revealed = _expansions.GetValueOrDefault(i);
            if (revealed <= 0)
            {
                result.Add(new FlatEntry(line, i));
                continue;
            }

            AppendExpanded(result, i, revealed);
        }
        return result;
    }

    private void AppendExpanded(List<FlatEntry> result, int omittedIndex, int revealed)
    {
        var slice = SliceFullRange(omittedIndex);
        var take = Math.Min(revealed, slice.Count);
        for (var k = 0; k < take; k++) result.Add(new FlatEntry(slice[k], -1));

        if (take < slice.Count)
        {
            var remaining = slice.Count - take;
            var marker = new DiffLine { Kind = DiffLineKind.Omitted, Text = $"…（{remaining}行省略）", OmittedCount = remaining };
            result.Add(new FlatEntry(marker, omittedIndex));
        }
    }

}
