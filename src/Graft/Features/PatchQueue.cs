using System.IO;
using Graft.Core;
using Graft.Infra;

namespace Graft.Features;

/// <summary>
/// パッチキューに保持する1ブロック。仕様書4.10。
/// <see cref="Block"/> は <see cref="PatchBlock"/> の抽象レコード（多態）であり
/// System.Text.Json でそのまま多態シリアライズできないため、永続化では代わりに
/// <see cref="SourcePatchText"/>（元のパッチ本文全体）を保存し、復元時に
/// <see cref="PatchParser"/> で再解析して <see cref="Block"/> を組み立て直す。
/// </summary>
public sealed record QueuedBlock
{
    /// <summary>キュー内で一意のID。</summary>
    public required string Id { get; init; }

    /// <summary>解析済みのブロック。</summary>
    public required PatchBlock Block { get; init; }

    /// <summary>このブロックの元となったパッチ本文全体。</summary>
    public required string SourcePatchText { get; init; }

    /// <summary>キューへ追加された日時。</summary>
    public DateTimeOffset AddedAt { get; init; }

    /// <summary>元のパッチのメタデータ。</summary>
    public PatchMeta? Meta { get; init; }
}

/// <summary>
/// 4.10 分割パッチの受け取り。複数回に分けて受け取ったパッチを1つのリビジョンとして結合できる。
/// キューの内容はアプリ終了時に保持し、次回起動時に復元する（<c>queue.json</c>）。
/// </summary>
public sealed class PatchQueue
{
    private readonly string _queueFilePath;
    private readonly JsonFileStore _store = new();

    // Block と、その元パッチ内での出現順インデックス（永続化からの復元に使う）を対にして保持する。
    private readonly List<(QueuedBlock Block, int SourceIndex)> _slots = new();

    /// <param name="paths">
    /// queue.json の保存先を決める基準ディレクトリ。<see cref="AppPaths"/> には queue.json 専用の
    /// プロパティが無いため、ここでは <see cref="AppPaths.BaseDirectory"/> から直接組み立てる
    /// （AppPaths 自体は担当外のため変更していない。要確認事項として報告する）。
    /// </param>
    public PatchQueue(AppPaths paths)
    {
        _queueFilePath = paths.QueueFilePath;
    }

    /// <summary>キュー内のブロック一覧。追加順を保つ。</summary>
    public IReadOnlyList<QueuedBlock> Items => _slots.Select(s => s.Block).ToList();

    /// <summary>
    /// パッチ内の各ブロックをキューへ追加する。同一ファイルに対する重複ブロックを検出し、
    /// 警告として Issues に含める（追加自体は行う。ブロックを個別に取り除きたい場合は
    /// <see cref="Remove"/> を使う）。
    /// </summary>
    public GraftResult<IReadOnlyList<QueuedBlock>> Add(Patch patch)
    {
        var seenPaths = new HashSet<string>(
            _slots.Select(s => NormalizedPath(s.Block.Block)), StringComparer.OrdinalIgnoreCase);
        var added = new List<QueuedBlock>();
        var issues = new List<GraftIssue>();

        for (var i = 0; i < patch.Blocks.Count; i++)
        {
            var block = patch.Blocks[i];
            if (!seenPaths.Add(NormalizedPath(block)))
            {
                // 仕様書16章に「パッチキュー内の重複ブロック」専用のコードが無いため、
                // 意味的に近い E102（複数箇所にマッチ＝多重性の警告）を暫定的に用いる。
                // 要確認: 専用コード（例: E007）の追加を提案する。
                issues.Add(GraftIssue.Of(ErrorCode.E102,
                    detail: $"同一ファイル（{block.Path}）に対する重複ブロックがキュー内に既にあります。",
                    path: block.Path, severity: Severity.Warning));
            }

            var queued = new QueuedBlock
            {
                Id = Guid.NewGuid().ToString("N"),
                Block = block,
                SourcePatchText = patch.RawText,
                AddedAt = DateTimeOffset.Now,
                Meta = patch.Meta,
            };
            _slots.Add((queued, i));
            added.Add(queued);
        }

        return GraftResult<IReadOnlyList<QueuedBlock>>.Ok(added, issues);
    }

    /// <summary>指定IDのブロックをキューから削除する。存在しないIDは無視する。</summary>
    public void Remove(string id)
    {
        var index = _slots.FindIndex(s => s.Block.Id == id);
        if (index >= 0) _slots.RemoveAt(index);
    }

    /// <summary>キューを空にする。</summary>
    public void Clear() => _slots.Clear();

    /// <summary>キュー全体を、各ブロックの出現順を保った1つの Patch として結合する。</summary>
    public GraftResult<Patch> Merge()
    {
        if (_slots.Count == 0)
        {
            return GraftResult<Patch>.Fail(ErrorCode.E001, detail: "パッチキューが空です。");
        }

        var blocks = _slots.Select(s => s.Block.Block).ToList();
        var rawText = string.Join("\n\n", _slots.Select(s => s.Block.SourcePatchText).Distinct());
        var patch = new Patch
        {
            Meta = MergeMeta(),
            Blocks = blocks,
            RawText = rawText,
        };
        return GraftResult<Patch>.Ok(patch);
    }

    /// <summary>終了時に呼び出し、キューの内容を queue.json へ保存する。</summary>
    public async Task<GraftResult<bool>> SaveAsync(CancellationToken ct = default)
    {
        try
        {
            var dto = new QueueFileDto { Entries = _slots.Select(ToEntryDto).ToArray() };
            await _store.WriteAsync(_queueFilePath, dto, ct: ct).ConfigureAwait(false);
            return GraftResult<bool>.Ok(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return GraftResult<bool>.Fail(ErrorCode.E402, detail: ex.Message, path: _queueFilePath);
        }
    }

    /// <summary>起動時に呼び出し、queue.json からキューの内容を復元する。</summary>
    public async Task<GraftResult<bool>> LoadAsync(CancellationToken ct = default)
    {
        var read = await _store.ReadWithRecoveryAsync(_queueFilePath, () => new QueueFileDto(), ct: ct)
            .ConfigureAwait(false);
        if (!read.IsSuccess)
        {
            return GraftResult<bool>.Fail(read.Issues);
        }

        _slots.Clear();
        var issues = new List<GraftIssue>(read.Issues);
        var parser = new PatchParser();
        foreach (var entry in read.Value.Entries)
        {
            RestoreEntry(entry, parser, issues);
        }

        return GraftResult<bool>.Ok(true, issues);
    }

    private void RestoreEntry(QueueEntryDto entry, PatchParser parser, List<GraftIssue> issues)
    {
        var parsed = parser.Parse(entry.SourcePatchText);
        if (!parsed.IsSuccess || entry.BlockIndex < 0 || entry.BlockIndex >= parsed.Value.Blocks.Count)
        {
            issues.Add(GraftIssue.Of(ErrorCode.E002,
                detail: $"パッチキューの項目を復元できませんでした（id={entry.Id}）。",
                severity: Severity.Warning));
            return;
        }

        var patch = parsed.Value;
        var queued = new QueuedBlock
        {
            Id = entry.Id,
            Block = patch.Blocks[entry.BlockIndex],
            SourcePatchText = entry.SourcePatchText,
            AddedAt = entry.AddedAt,
            Meta = patch.Meta,
        };
        _slots.Add((queued, entry.BlockIndex));
    }

    private PatchMeta MergeMeta()
    {
        var first = _slots.Select(s => s.Block.Meta).FirstOrDefault(m => m is not null) ?? new PatchMeta();
        if (!string.IsNullOrEmpty(first.Summary)) return first;

        var laterSummary = _slots
            .Select(s => s.Block.Meta)
            .FirstOrDefault(m => m is not null && !string.IsNullOrEmpty(m.Summary));

        return laterSummary is not null ? first with { Summary = laterSummary.Summary } : first;
    }

    private static QueueEntryDto ToEntryDto((QueuedBlock Block, int SourceIndex) slot) => new()
    {
        Id = slot.Block.Id,
        SourcePatchText = slot.Block.SourcePatchText,
        BlockIndex = slot.SourceIndex,
        AddedAt = slot.Block.AddedAt,
    };

    private static string NormalizedPath(PatchBlock block) => block.Path.Replace('\\', '/').ToLowerInvariant();

    private sealed record QueueFileDto
    {
        public IReadOnlyList<QueueEntryDto> Entries { get; init; } = Array.Empty<QueueEntryDto>();
    }

    private sealed record QueueEntryDto
    {
        public required string Id { get; init; }
        public required string SourcePatchText { get; init; }
        public required int BlockIndex { get; init; }
        public DateTimeOffset AddedAt { get; init; }
    }
}
