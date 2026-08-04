namespace Graft.Core;

/// <summary>diff の行種別。仕様書8.13。</summary>
public enum DiffLineKind
{
    /// <summary>変更なし。</summary>
    Unchanged,
    /// <summary>追加行。</summary>
    Added,
    /// <summary>削除行。</summary>
    Removed,
    /// <summary>折りたたまれた省略部分を表す擬似行。</summary>
    Omitted,
}

/// <summary>行内の文字単位ハイライト範囲。</summary>
public readonly record struct InlineSpan(int Start, int Length);

/// <summary>diff の1行。</summary>
public sealed record DiffLine
{
    /// <summary>行種別。</summary>
    public DiffLineKind Kind { get; init; }
    /// <summary>変更前の行番号（1始まり）。追加行では null。</summary>
    public int? OldLine { get; init; }
    /// <summary>変更後の行番号（1始まり）。削除行では null。</summary>
    public int? NewLine { get; init; }
    /// <summary>行の内容。<see cref="DiffLineKind.Omitted"/> では「…（42行省略）」の文言。</summary>
    public required string Text { get; init; }
    /// <summary>省略行が表す実際の行数。それ以外は 0。</summary>
    public int OmittedCount { get; init; }
    /// <summary>行内の文字単位ハイライト範囲。仕様書8.3。</summary>
    public IReadOnlyList<InlineSpan> InlineSpans { get; init; } = Array.Empty<InlineSpan>();

    /// <summary>スクリーンリーダー向けの種別読み上げ文言。仕様書8.14。</summary>
    public string KindText => Kind switch
    {
        DiffLineKind.Added => "追加",
        DiffLineKind.Removed => "削除",
        DiffLineKind.Omitted => "省略",
        _ => "変更なし",
    };
}

/// <summary>連続した変更のかたまり。</summary>
public sealed record DiffHunk
{
    /// <summary>ハンクに含まれる行。</summary>
    public required IReadOnlyList<DiffLine> Lines { get; init; }
}

/// <summary>1ファイル分の差分。</summary>
public sealed record DiffModel
{
    /// <summary>プロジェクト相対パス。</summary>
    public required string Path { get; init; }
    /// <summary>ハンクの一覧。</summary>
    public IReadOnlyList<DiffHunk> Hunks { get; init; } = Array.Empty<DiffHunk>();
    /// <summary>追加行数。</summary>
    public int Added { get; init; }
    /// <summary>削除行数。</summary>
    public int Removed { get; init; }
}
