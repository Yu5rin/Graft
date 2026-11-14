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

    /// <summary>実機不具合対応（修正4）: このユニットの代表インデント補正量。BlockPlan
    /// へそのまま引き継ぐ（<see cref="BlockPlan.IndentCorrectionChars"/>参照）。</summary>
    public int IndentCorrectionChars { get; init; }

    /// <summary>
    /// SR形式で、このユニットが一致した位置（照合の基準テキスト上の開始行。0始まり。昇順）。
    /// SR形式以外では空。
    ///
    /// 【なぜ持つか】 本適用（<see cref="ApplyEngine"/>）はファイルを読み直して再解決する。ドライランの後に
    /// 外部でファイルが書き換わっていると、同じSEARCHが別の場所に一致したり、あいまい一致の段階が
    /// 変わったりしうるが、以前はそれに気づかず、プレビューで確認した内容と違う場所へ書いていた。
    /// ドライラン時の位置と段階を覚えておき、再解決の結果と突き合わせるための値。基準テキストは
    /// 選択状態に依らず（ペアごとに独立に照合する）同じなので、一部のペアのチェックを外しても
    /// 位置は変わらない。
    /// </summary>
    public IReadOnlyList<int> MatchStartLines { get; init; } = Array.Empty<int>();
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
        var resolved = RejectOverlappingEdits(
            others.SelectMany(b => ResolveBlockEdits(baseText, baseLines.Count, b, matcher, includedPairs)).ToList());

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

    /// <summary>
    /// 照合に成功したペアどうしで、置き換える行の範囲が重なっているものを失敗に変える。
    /// <para>
    /// 【なぜ必要か】 各ペアは同じ「元のテキスト」に対して別々に照合され、適用は末尾側から行単位で
    /// 行う（<see cref="ApplyBlockEditsInOrder"/>）。範囲が重なると、先に適用した側が消した行を
    /// もう一方がさらに消す／行番号が前後にずれた位置へ差し込む、といった形で結果が壊れる。実測では、
    /// l1〜l6 の6行に「l2〜l4 を4行へ」「l4〜l5 を4行へ」を適用すると、エラーも警告も出ないまま
    /// 2つ目のペアの先頭行（Y4）が消えた内容が「成功」として書き込まれた。完全に同じペアが2つあると
    /// 置換後の内容が二重に入った。パッチキューで複数のパッチを結合した場合（PatchQueue.Merge）も
    /// 同じ経路を通るため、ここで一括して防ぐ。
    /// </para>
    /// <para>
    /// 先に現れた（ブロック順・ペア順で前の）ペアを生かし、後のペアを失敗にする。AI は一般に
    /// 上から順に書くため、前のペアを正とするほうが利用者の意図に近いと判断した。
    /// 同じ位置・同じ置換内容の完全な重複も、黙って1つにまとめず失敗にする。まとめて通すと
    /// 「2つ書いたのに1つしか効いていない」ことが画面に出ず、原因に気づけない。
    /// 重複は AI の出力の取り違えであることが多く、利用者に見える形で止めるほうが安全。
    /// </para>
    /// <para>
    /// 検査するのは行を置き換える編集（LineCount &gt; 0）だけ。APPEND は末尾、PREPEND は先頭へ
    /// 行を足すだけ（範囲を持たない）で、SR の範囲と境界で接しても内容は壊れない。1つのペアが
    /// OCCURRENCE=ALL で複数箇所に当たる場合の、そのペア内の編集どうしは対象にしない
    /// （別のペアとの重なりだけを見る。既存の仕様を変えないため）。
    /// </para>
    /// </summary>
    private static List<ResolvedBlock> RejectOverlappingEdits(List<ResolvedBlock> resolved)
    {
        var accepted = new List<ResolvedBlock>();
        var result = new List<ResolvedBlock>(resolved.Count);

        foreach (var block in resolved)
        {
            if (!block.Success)
            {
                result.Add(block);
                continue;
            }

            var conflict = block.Edits.Any(e => e.LineCount > 0)
                ? accepted.FirstOrDefault(a => ConflictsWith(a, block))
                : null;
            if (conflict is null)
            {
                result.Add(block);
                accepted.Add(block);
                continue;
            }

            var isDuplicate = IsSameEdits(conflict, block);
            var detail = isDuplicate
                ? "同じ位置を同じ内容に置き換えるペアが重複しています。二重に適用すると内容が壊れるため、後のペアを適用しません。重複しているペアを1つ取り除いてください。"
                : "このペアのSEARCH部は、先のペアが置き換える行と重なっています。両方を適用すると内容が壊れるため、後のペアを適用しません。"
                  + "重なっているペアを1つにまとめるか、SEARCH部が重ならないよう分けてください。";
            result.Add(new ResolvedBlock
            {
                Block = block.Block,
                Pair = block.Pair,
                Success = false,
                Issues = new[] { GraftIssue.Of(ErrorCode.E105, detail, path: block.Block.Path) },
            });
        }

        return result;
    }

    private static bool ConflictsWith(ResolvedBlock accepted, ResolvedBlock candidate)
        => accepted.Edits.Where(a => a.LineCount > 0).Any(a =>
            candidate.Edits.Where(c => c.LineCount > 0).Any(c =>
                a.StartLine < c.StartLine + c.LineCount && c.StartLine < a.StartLine + a.LineCount));

    private static bool IsSameEdits(ResolvedBlock a, ResolvedBlock b)
        => a.Edits.Count == b.Edits.Count
           && a.Edits.Zip(b.Edits).All(t =>
               t.First.StartLine == t.Second.StartLine
               && t.First.LineCount == t.Second.LineCount
               && t.First.NewLines.Select(l => l.Text).SequenceEqual(t.Second.NewLines.Select(l => l.Text)));

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
            // OCCURRENCE=ALLで1ペアが複数箇所にマッチする場合、箇所ごとに補正量が異なりうる。
            // 「補正あり」を利用者に見落とされないよう、絶対値が最大の補正量を代表として残す
            // （0文字補正の箇所に埋もれて「補正あり」の箇所が隠れてしまうことを避ける）。
            var indentCorrectionChars = 0;
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
                if (Math.Abs(m.IndentCorrectionChars) > Math.Abs(indentCorrectionChars))
                {
                    indentCorrectionChars = m.IndentCorrectionChars;
                }
            }

            results.Add(new ResolvedBlock
            {
                Block = block,
                Pair = pair,
                Success = true,
                Edits = edits,
                Stage = worstStage,
                NeedsConfirmation = needsConfirmation,
                IndentCorrectionChars = indentCorrectionChars,
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
                    IndentCorrectionChars = owner.IndentCorrectionChars,
                    MatchStartLines = owner.Pair is null
                        ? Array.Empty<int>()
                        : owner.Edits.Select(e => e.StartLine).OrderBy(l => l).ToList(),
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
        public int IndentCorrectionChars { get; init; }
    }
}
