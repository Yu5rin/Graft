namespace Graft.Core;

/// <summary>
/// 1ブロック（PatchBlock）を適用した結果。BlockPlan の元になる。
/// 粒度はブロック単位（SR形式のペアが複数あっても1ブロック=1件）とする。
/// これは <see cref="ApplyModels.BlockPlan.Block"/> がブロック単位の参照しか持たず、
/// ペア単位で区別する手段がないことに合わせた設計判断である。
/// </summary>
public sealed record ChangeUnitResult
{
    /// <summary>元のブロック。</summary>
    public required PatchBlock SourceBlock { get; init; }

    /// <summary>変更説明。</summary>
    public string? Description { get; init; }

    /// <summary>マッチ段階。複数ペアがある場合は最も注意を要する段階（数値が大きいほう）。</summary>
    public MatchStage Stage { get; init; }

    /// <summary>適用可能かどうか。1件でもペアが失敗すればブロック全体を不可とする。</summary>
    public bool CanApply { get; init; }

    /// <summary>要確認かどうか（いずれかのペアが段階5でマッチした場合）。</summary>
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
    public static FileResolution ResolveFile(
        IReadOnlyList<string> originalLines, IReadOnlyList<PatchBlock> fileBlocks, MatchEngine matcher)
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

        var finalLines = ResolveTextBlocks(baseLines, others, matcher, units);
        return new FileResolution { Units = units, FinalLines = finalLines };
    }

    private static string JoinText(IReadOnlyList<ResolvedLine> lines) => string.Join("\n", lines.Select(l => l.Text));

    // ------------------------------------------------------------------
    // SR / APPEND / PREPEND のまとめ解決
    // ------------------------------------------------------------------

    private static IReadOnlyList<ResolvedLine> ResolveTextBlocks(
        List<ResolvedLine> baseLines, IReadOnlyList<PatchBlock> others, MatchEngine matcher, List<ChangeUnitResult> units)
    {
        var baseText = JoinText(baseLines);
        var resolved = others.Select(b => ResolveBlockEdits(baseText, baseLines.Count, b, matcher)).ToList();

        foreach (var failed in resolved.Where(r => !r.Success))
        {
            units.Add(new ChangeUnitResult
            {
                SourceBlock = failed.Block,
                Description = failed.Block.Description,
                Stage = MatchStage.Failed,
                CanApply = false,
                Issues = failed.Issues,
            });
        }

        var successful = resolved.Where(r => r.Success).ToList();
        var working = new List<ResolvedLine>(baseLines);
        ApplyBlockEditsInOrder(working, successful, units);
        return working;
    }

    private static ResolvedBlock ResolveBlockEdits(string baseText, int baseLineCount, PatchBlock block, MatchEngine matcher)
        => block switch
        {
            SearchReplaceBlock sr => ResolveSearchReplaceBlock(baseText, sr, matcher),
            AppendBlock append => MakeSimpleEdit(append, append.Content, startLine: baseLineCount),
            PrependBlock prepend => MakeSimpleEdit(prepend, prepend.Content, startLine: 0),
            _ => new ResolvedBlock { Block = block, Success = false,
                Issues = new[] { GraftIssue.Of(ErrorCode.E001, "テキスト変換の対象外のブロックです", path: block.Path) } },
        };

    /// <summary>
    /// SR形式の全ペアを同一の基準テキストに対して照合する。1件でも失敗すればブロック全体を
    /// 失敗として扱う（BlockPlan がブロック単位でしかペアを区別できないため）。
    /// </summary>
    private static ResolvedBlock ResolveSearchReplaceBlock(string baseText, SearchReplaceBlock block, MatchEngine matcher)
    {
        var edits = new List<LineEdit>();
        var issues = new List<GraftIssue>();
        var worstStage = MatchStage.Exact;
        var needsConfirmation = false;
        var subSeq = 0;

        foreach (var pair in block.Pairs)
        {
            var matched = matcher.Match(baseText, pair, block.Occurrence);
            if (!matched.IsSuccess)
            {
                issues.AddRange(matched.Issues);
                continue;
            }

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
        }

        if (issues.Count > 0)
        {
            return new ResolvedBlock { Block = block, Success = false, Issues = issues };
        }

        return new ResolvedBlock
        {
            Block = block,
            Success = true,
            Edits = edits,
            Stage = worstStage,
            NeedsConfirmation = needsConfirmation,
        };
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
                    Description = owner.Block.Description,
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
        public bool Success { get; init; }
        public IReadOnlyList<LineEdit> Edits { get; init; } = Array.Empty<LineEdit>();
        public MatchStage Stage { get; init; }
        public bool NeedsConfirmation { get; init; }
        public IReadOnlyList<GraftIssue> Issues { get; init; } = Array.Empty<GraftIssue>();
    }
}
