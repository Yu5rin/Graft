namespace Graft.Core;

/// <summary>
/// 1変更単位を適用した結果。BlockPlan の元になる。
/// SR形式は設定の適用モード（allOrNothing / 部分適用）がブロックをまたいだ一括性を担うため、
/// ここではペア単位を最小粒度とする（1ペア=1件）。FULL / APPEND / PREPEND はブロック単位のまま。
/// </summary>
public sealed record ChangeUnitResult
{
    /// <summary>元のブロック。SR形式では複数ペアが同じ SourceBlock を共有しうる。</summary>
    public required PatchBlock SourceBlock { get; init; }

    /// <summary>このユニットに対応する SEARCH/REPLACE ペア。SR形式以外では null。</summary>
    public SearchReplacePair? SourcePair { get; init; }

    /// <summary>変更説明。</summary>
    public string? Description { get; init; }

    /// <summary>マッチ段階。</summary>
    public MatchStage Stage { get; init; }

    /// <summary>適用可能かどうか。</summary>
    public bool CanApply { get; init; }

    /// <summary>要確認かどうか（段階5でマッチした場合。OCCURRENCE=ALLで複数箇所にマッチした場合は
    /// いずれかが段階5であれば true）。</summary>
    public bool NeedsConfirmation { get; init; }

    /// <summary>検出された問題。</summary>
    public IReadOnlyList<GraftIssue> Issues { get; init; } = Array.Empty<GraftIssue>();

    /// <summary>このブロックの適用が始まる直前のファイル全文（"\n"区切り）。</summary>
    public string? BeforeText { get; init; }

    /// <summary>このブロックの適用が終わった直後のファイル全文（"\n"区切り）。</summary>
    public string? AfterText { get; init; }
}

/// <summary>
/// 最終行配列の1行。<see cref="OriginalIndex"/> が非nullの場合、元ファイルの該当行（0始まり）を
/// そのまま引き継いだ未変更行であることを表す。null の場合はブロックの適用によって新たに
/// 生成された行（置換後・追記・先頭挿入・FULL全文）であることを表す。
/// 書き込み時（<see cref="ApplyEngine"/>）に、未変更行の元の改行コードを維持するために使う
/// （仕様書6.4「混在は可能な限り維持」）。
/// </summary>
public readonly record struct ResolvedLine(string Text, int? OriginalIndex);

/// <summary>1ファイル分の解決結果。</summary>
public sealed record FileResolution
{
    /// <summary>ブロックごとの結果。入力の <c>fileBlocks</c> と同じ順序（FULL系が先）。</summary>
    public required IReadOnlyList<ChangeUnitResult> Units { get; init; }

    /// <summary>選択されたブロックすべてを適用し終えた最終行配列。</summary>
    public required IReadOnlyList<ResolvedLine> FinalLines { get; init; }
}

/// <summary>
/// 仕様書6.6（ブロックの適用順序）・5.3（同一ファイル内の適用順序）に従い、
/// 1ファイルに対する複数ブロックの適用後テキストを計算する。ファイルI/Oは行わない。
/// </summary>
public static class BlockResolver
{
    /// <summary>
    /// 1ファイルに関連するブロック（FULL / SR / APPEND / PREPEND）を解決する。
    /// DELETE / RENAME / MKDIR はテキスト変換を伴わないためここでは扱わない。
    /// </summary>
    /// <param name="includedPairs">
    /// 対象とする SEARCH/REPLACE ペアの絞り込み（参照同一性で判定）。null の場合はブロックが持つ
    /// 全ペアを対象にする（ドライラン時など）。本適用の書き込み時は、ユーザーがペア単位で選択を
    /// 外した場合にそのペアを除外して再解決できるよう、選択済みペアの集合を渡す
    /// （同一ブロック内の他ペアの選択状態に関わらず、ペア単位の選択がそのまま書き込みに反映される）。
    /// </param>
    public static FileResolution ResolveFile(
        IReadOnlyList<string> originalLines, IReadOnlyList<PatchBlock> fileBlocks, MatchEngine matcher,
        IReadOnlySet<SearchReplacePair>? includedPairs = null)
    {
        var units = new List<ChangeUnitResult>();
        var baseLines = originalLines.Select((l, i) => new ResolvedLine(l, i)).ToList();

        foreach (var full in fileBlocks.OfType<FullContentBlock>())
        {
            // FULL は全文を新規コンテンツで置き換えるため、以降の行は元ファイル由来ではなくなる
            // （OriginalIndex = null）。
            var newLines = TextNormalizer.SplitLines(full.Content).Select(l => new ResolvedLine(l, null)).ToList();
            units.Add(new ChangeUnitResult
            {
                SourceBlock = full,
                Description = full.Description,
                Stage = MatchStage.None,
                CanApply = true,
                BeforeText = JoinText(baseLines),
                AfterText = JoinText(newLines),
            });
            baseLines = newLines;
        }

        var others = fileBlocks.Where(b => b is not FullContentBlock).ToList();
        if (others.Count == 0)
        {
            return new FileResolution { Units = units, FinalLines = baseLines };
        }

        var finalLines = ResolveTextBlocks(baseLines, others, matcher, units, includedPairs);
        return new FileResolution { Units = units, FinalLines = finalLines };
    }

    private static string JoinText(IReadOnlyList<ResolvedLine> lines) => string.Join("\n", lines.Select(l => l.Text));

    // ------------------------------------------------------------------
    // SR / APPEND / PREPEND のまとめ解決
    // ------------------------------------------------------------------

    private static IReadOnlyList<ResolvedLine> ResolveTextBlocks(
        List<ResolvedLine> baseLines, IReadOnlyList<PatchBlock> others, MatchEngine matcher, List<ChangeUnitResult> units,
        IReadOnlySet<SearchReplacePair>? includedPairs)
    {
        var baseText = JoinText(baseLines);
        var resolved = others.SelectMany(b => ResolveBlockEdits(baseText, baseLines.Count, b, matcher, includedPairs)).ToList();

        foreach (var failed in resolved.Where(r => !r.Success))
        {
            units.Add(new ChangeUnitResult
            {
                SourceBlock = failed.Block,
                SourcePair = failed.Pair,
                Description = failed.Pair?.Description ?? failed.Block.Description,
                Stage = MatchStage.Failed,
                CanApply = false,
                Issues = failed.Issues,
                // Bug B: 失敗ユニットにも照合の基準テキストを残す。インライン編集パネル（DiffViewModel.
                // BuildInlineEdits）が「実際のファイル内容」としてこれを使い、SEARCH部の再判定に使う。
                BeforeText = baseText,
            });
        }

        var successful = resolved.Where(r => r.Success).ToList();
        var working = new List<ResolvedLine>(baseLines);
        ApplyBlockEditsInOrder(working, successful, units);
        return working;
    }

    private static IReadOnlyList<ResolvedBlock> ResolveBlockEdits(
        string baseText, int baseLineCount, PatchBlock block, MatchEngine matcher, IReadOnlySet<SearchReplacePair>? includedPairs)
        => block switch
        {
            SearchReplaceBlock sr => ResolveSearchReplacePairs(baseText, sr, matcher, includedPairs),
            AppendBlock append => new[] { MakeSimpleEdit(append, append.Content, startLine: baseLineCount) },
            PrependBlock prepend => new[] { MakeSimpleEdit(prepend, prepend.Content, startLine: 0) },
            _ => new[] { new ResolvedBlock { Block = block, Success = false,
                Issues = new[] { GraftIssue.Of(ErrorCode.E001, "テキスト変換の対象外のブロックです", path: block.Path) } } },
        };

    /// <summary>
    /// SR形式の各ペアを同一の基準テキストに対して独立に照合する。1ペアの失敗は他のペアに影響しない
    /// （成功ペアは適用可能なユニットに、失敗ペアはそのペア単独の失敗ユニットになる）。
    /// 全ペアが不可分に一括適用されるべきかどうかは BlockPlan 側ではなく設定の適用モード
    /// （allOrNothing / 部分適用）が担うため、ここでの強制連座はしない。
    /// <paramref name="includedPairs"/> が非nullの場合、そこに含まれないペアは処理自体をスキップする
    /// （書き込み時、ユーザーがペア単位で選択を外した場合の絞り込みに使う）。
    /// </summary>
    private static IReadOnlyList<ResolvedBlock> ResolveSearchReplacePairs(
        string baseText, SearchReplaceBlock block, MatchEngine matcher, IReadOnlySet<SearchReplacePair>? includedPairs)
    {
        var results = new List<ResolvedBlock>();

        foreach (var pair in block.Pairs)
        {
            if (includedPairs is not null && !includedPairs.Contains(pair)) continue;

            var matched = matcher.Match(baseText, pair, block.Occurrence);
            if (!matched.IsSuccess)
            {
                results.Add(new ResolvedBlock { Block = block, Pair = pair, Success = false, Issues = matched.Issues });
                continue;
            }

            var edits = new List<LineEdit>();
            var worstStage = MatchStage.Exact;
            var needsConfirmation = false;
            var subSeq = 0;
            foreach (var m in matched.Value)
            {
                edits.Add(new LineEdit
                {
                    StartLine = m.StartLine,
                    LineCount = m.LineCount,
                    NewLines = TextNormalizer.SplitLines(m.AppliedReplacement).Select(l => new ResolvedLine(l, null)).ToList(),
                    SubSeq = subSeq++,
                });
                if (m.Stage > worstStage) worstStage = m.Stage;
                needsConfirmation |= m.NeedsConfirmation;
            }

            results.Add(new ResolvedBlock
            {
                Block = block,
                Pair = pair,
                Success = true,
                Edits = edits,
                Stage = worstStage,
                NeedsConfirmation = needsConfirmation,
            });
        }

        return results;
    }

    private static ResolvedBlock MakeSimpleEdit(PatchBlock block, string content, int startLine)
    {
        var newLines = TextNormalizer.SplitLines(content).Select(l => new ResolvedLine(l, null)).ToList();
        var edit = new LineEdit { StartLine = startLine, LineCount = 0, NewLines = newLines };
        return new ResolvedBlock { Block = block, Success = true, Edits = new[] { edit } };
    }

    /// <summary>
    /// 仕様書5.3のとおり、全ブロックの全編集をマッチ位置の降順（末尾側から）に適用する。
    /// 同一開始位置は文書内で後方のブロックを先に適用することで、複数APPEND/PREPENDの
    /// 相対順序が文書順のまま結果に反映されるようにする。
    /// </summary>
    private static void ApplyBlockEditsInOrder(List<ResolvedLine> working, List<ResolvedBlock> blocks, List<ChangeUnitResult> units)
    {
        var flattened = new List<(ResolvedBlock Owner, LineEdit Edit, int BlockSeq)>();
        for (var i = 0; i < blocks.Count; i++)
        {
            foreach (var edit in blocks[i].Edits) flattened.Add((blocks[i], edit, i));
        }

        var ordered = flattened
            .OrderByDescending(t => t.Edit.StartLine)
            .ThenByDescending(t => t.BlockSeq)
            .ThenByDescending(t => t.Edit.SubSeq)
            .ToList();

        var lastIndex = new Dictionary<ResolvedBlock, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < ordered.Count; i++) lastIndex[ordered[i].Owner] = i;

        var beforeByOwner = new Dictionary<ResolvedBlock, string>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < ordered.Count; i++)
        {
            var (owner, edit, _) = ordered[i];
            if (!beforeByOwner.ContainsKey(owner)) beforeByOwner[owner] = JoinText(working);

            working.RemoveRange(edit.StartLine, edit.LineCount);
            working.InsertRange(edit.StartLine, edit.NewLines);

            if (lastIndex[owner] == i)
            {
                units.Add(new ChangeUnitResult
                {
                    SourceBlock = owner.Block,
                    SourcePair = owner.Pair,
                    Description = owner.Pair?.Description ?? owner.Block.Description,
                    Stage = owner.Stage,
                    CanApply = true,
                    NeedsConfirmation = owner.NeedsConfirmation,
                    BeforeText = beforeByOwner[owner],
                    AfterText = JoinText(working),
                });
            }
        }
    }

    // ------------------------------------------------------------------
    // 内部表現
    // ------------------------------------------------------------------

    private sealed record LineEdit
    {
        public required int StartLine { get; init; }
        public required int LineCount { get; init; }
        public required IReadOnlyList<ResolvedLine> NewLines { get; init; }
        public int SubSeq { get; init; }
    }

    private sealed record ResolvedBlock
    {
        public required PatchBlock Block { get; init; }

        /// <summary>SR形式で、このユニットが対応する個別のペア。SR形式以外では null。</summary>
        public SearchReplacePair? Pair { get; init; }

        public bool Success { get; init; }
        public IReadOnlyList<LineEdit> Edits { get; init; } = Array.Empty<LineEdit>();
        public MatchStage Stage { get; init; }
        public bool NeedsConfirmation { get; init; }
        public IReadOnlyList<GraftIssue> Issues { get; init; } = Array.Empty<GraftIssue>();
    }
}
