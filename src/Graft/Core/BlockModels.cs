namespace Graft.Core;

/// <summary>
/// ブロックの種別。仕様書4章の各形式に対応する。
/// </summary>
public enum BlockKind
{
    /// <summary>SEARCH/REPLACE 形式。</summary>
    SearchReplace,
    /// <summary>ファイル全文形式。</summary>
    FullContent,
    /// <summary>ファイル削除。</summary>
    Delete,
    /// <summary>ファイル移動・改名。</summary>
    Rename,
    /// <summary>フォルダ作成。</summary>
    Mkdir,
    /// <summary>末尾への追記。</summary>
    Append,
    /// <summary>先頭への挿入。</summary>
    Prepend,
}

/// <summary>
/// マッチ段階。仕様書5章の表に対応する。
/// </summary>
public enum MatchStage
{
    /// <summary>未判定。</summary>
    None = 0,
    /// <summary>完全一致。</summary>
    Exact = 1,
    /// <summary>行末空白を無視して一致。</summary>
    TrailingWhitespace = 2,
    /// <summary>先頭インデント量を無視し相対インデントのみ比較。</summary>
    RelativeIndent = 3,
    /// <summary>空行を無視して一致。</summary>
    IgnoreBlankLines = 4,
    /// <summary>正規化編集距離による類似一致。要確認。</summary>
    Similarity = 5,
    /// <summary>該当なし。</summary>
    Failed = 6,
}

/// <summary>
/// 出現位置の指定。ヘッダの OCCURRENCE に対応する。
/// </summary>
public sealed record OccurrenceSpec
{
    /// <summary>既定。1箇所のみを許容する。</summary>
    public static readonly OccurrenceSpec Single = new() { Index = 1, All = false };

    /// <summary>何番目の出現を対象とするか（1始まり）。All が true の場合は無視する。</summary>
    public int Index { get; init; } = 1;

    /// <summary>すべての出現を対象とするかどうか。</summary>
    public bool All { get; init; }

    /// <summary>既定（OCCURRENCE 未指定）かどうか。</summary>
    public bool IsDefault => !All && Index == 1;
}

/// <summary>
/// パッチ全体のメタデータ。仕様書4.2に対応する。
/// </summary>
public sealed record PatchMeta
{
    /// <summary>変更内容の要約。リビジョンの見出しになる。</summary>
    public string? Summary { get; init; }

    /// <summary>変更の種別（feat / fix / refactor / docs / test / chore）。</summary>
    public string? Type { get; init; }

    /// <summary>base で指定されたファイル内容ハッシュ。キーは相対パス。</summary>
    public IReadOnlyDictionary<string, string> BaseHashes { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// SEARCH / REPLACE の1ペア。
/// </summary>
public sealed record SearchReplacePair
{
    /// <summary>検索対象のテキスト。アンカー省略記法では開始・終了アンカーを "..." 行で挟んだ形。</summary>
    public required string SearchText { get; init; }

    /// <summary>置換後のテキスト。空文字は削除操作を意味する。</summary>
    public required string ReplaceText { get; init; }

    /// <summary>SEARCH マーカー行の # 以降から抽出した変更説明。</summary>
    public string? Description { get; init; }

    /// <summary>アンカー省略記法（SEARCH-RANGE）かどうか。</summary>
    public bool IsRange { get; init; }

    /// <summary>パッチ本文中の SEARCH マーカー行の行番号（1始まり）。</summary>
    public int SourceLine { get; init; }
}

/// <summary>
/// パッチ内の1ファイル・1操作に対応する単位。
/// </summary>
public abstract record PatchBlock
{
    /// <summary>操作対象のプロジェクト相対パス（区切りは "/" に正規化済み）。</summary>
    public required string Path { get; init; }

    /// <summary>ブロック種別。</summary>
    public abstract BlockKind Kind { get; }

    /// <summary>パッチ本文中のヘッダ行の行番号（1始まり）。</summary>
    public int HeaderLine { get; init; }

    /// <summary>ヘッダ行の # 以降から抽出した説明。</summary>
    public string? Description { get; init; }

    /// <summary>FENCE 指定。終了マーカーを ">>>> END:&lt;値&gt;" に変更する。</summary>
    public string? Fence { get; init; }

    /// <summary>OCCURRENCE 指定。</summary>
    public OccurrenceSpec Occurrence { get; init; } = OccurrenceSpec.Single;

    /// <summary>適用順序。仕様書6.6の並び順。</summary>
    public int ApplyOrder => Kind switch
    {
        BlockKind.Mkdir => 1,
        BlockKind.Rename => 2,
        BlockKind.FullContent => 3,
        BlockKind.SearchReplace or BlockKind.Append or BlockKind.Prepend => 4,
        BlockKind.Delete => 5,
        _ => 4,
    };
}

/// <summary>SEARCH/REPLACE 形式のブロック。1ヘッダに複数ペアを連結できる。</summary>
public sealed record SearchReplaceBlock : PatchBlock
{
    /// <inheritdoc />
    public override BlockKind Kind => BlockKind.SearchReplace;

    /// <summary>連結された SEARCH/REPLACE ペア。</summary>
    public required IReadOnlyList<SearchReplacePair> Pairs { get; init; }
}

/// <summary>ファイル全文形式のブロック。</summary>
public sealed record FullContentBlock : PatchBlock
{
    /// <inheritdoc />
    public override BlockKind Kind => BlockKind.FullContent;

    /// <summary>ファイル全文。</summary>
    public required string Content { get; init; }
}

/// <summary>ファイル削除のブロック。</summary>
public sealed record DeleteBlock : PatchBlock
{
    /// <inheritdoc />
    public override BlockKind Kind => BlockKind.Delete;
}

/// <summary>ファイル移動・改名のブロック。Path は移動元を保持する。</summary>
public sealed record RenameBlock : PatchBlock
{
    /// <inheritdoc />
    public override BlockKind Kind => BlockKind.Rename;

    /// <summary>移動元の相対パス。</summary>
    public string FromPath => Path;

    /// <summary>移動先の相対パス。</summary>
    public required string ToPath { get; init; }
}

/// <summary>フォルダ作成のブロック。</summary>
public sealed record MkdirBlock : PatchBlock
{
    /// <inheritdoc />
    public override BlockKind Kind => BlockKind.Mkdir;
}

/// <summary>末尾への追記ブロック。</summary>
public sealed record AppendBlock : PatchBlock
{
    /// <inheritdoc />
    public override BlockKind Kind => BlockKind.Append;

    /// <summary>追記する内容。</summary>
    public required string Content { get; init; }
}

/// <summary>先頭への挿入ブロック。</summary>
public sealed record PrependBlock : PatchBlock
{
    /// <inheritdoc />
    public override BlockKind Kind => BlockKind.Prepend;

    /// <summary>先頭に挿入する内容。</summary>
    public required string Content { get; init; }
}

/// <summary>
/// AIが出力し、クリップボード経由で渡されたテキスト全体の解析結果。
/// </summary>
public sealed record Patch
{
    /// <summary>パッチメタデータ。</summary>
    public PatchMeta Meta { get; init; } = new();

    /// <summary>解析されたブロック。パッチ本文の出現順を保つ。</summary>
    public required IReadOnlyList<PatchBlock> Blocks { get; init; }

    /// <summary>元のパッチテキスト。</summary>
    public required string RawText { get; init; }

    /// <summary>出力が途中で切れていると判定されたかどうか。</summary>
    public bool IsTruncated { get; init; }

    /// <summary>切断時、最後に受け取った末尾の行（継続依頼プロンプトに使う）。</summary>
    public IReadOnlyList<string> TailLines { get; init; } = Array.Empty<string>();

    /// <summary>仕様書6.6の適用順序に並べ替えたブロック列を返す。</summary>
    public IEnumerable<PatchBlock> InApplyOrder()
        => Blocks.Select((b, i) => (b, i))
                 .OrderBy(t => t.b.ApplyOrder)
                 .ThenBy(t => t.i)
                 .Select(t => t.b);
}
