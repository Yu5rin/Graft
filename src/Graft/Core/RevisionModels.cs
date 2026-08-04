namespace Graft.Core;

/// <summary>リビジョンの状態。仕様書6.3の中断復帰に使う。</summary>
public static class RevisionStatus
{
    /// <summary>処理中。この状態で残っていれば中断されている。</summary>
    public const string InProgress = "in_progress";
    /// <summary>正常完了。</summary>
    public const string Success = "success";
    /// <summary>失敗しロールバック済み。</summary>
    public const string RolledBack = "rolled_back";
}

/// <summary>ファイルに対する操作の種別。manifest の operation に対応する。</summary>
public static class EntryOperation
{
    /// <summary>既存ファイルの変更。</summary>
    public const string Modify = "modify";
    /// <summary>新規作成。</summary>
    public const string Create = "create";
    /// <summary>削除。</summary>
    public const string Delete = "delete";
    /// <summary>移動・改名。</summary>
    public const string Rename = "rename";
    /// <summary>フォルダ作成。</summary>
    public const string Mkdir = "mkdir";
}

/// <summary>リビジョンの統計。仕様書7.1・12章。</summary>
public sealed record RevisionStats
{
    /// <summary>変更ファイル数。</summary>
    public int Files { get; init; }
    /// <summary>追加行数。</summary>
    public int Added { get; init; }
    /// <summary>削除行数。</summary>
    public int Removed { get; init; }
    /// <summary>パッチの推定トークン数。</summary>
    public int EstimatedTokens { get; init; }
    /// <summary>全文出力に比べて削減できた推定トークン数。</summary>
    public int EstimatedSavedTokens { get; init; }
}

/// <summary>manifest の entries 要素。</summary>
public sealed record RevisionEntry
{
    /// <summary>プロジェクト相対パス。</summary>
    public required string Path { get; init; }
    /// <summary>操作種別。<see cref="EntryOperation"/> の値。</summary>
    public required string Operation { get; init; }
    /// <summary>変更説明。SEARCH マーカーの # 以降から抽出したもの。</summary>
    public string? Desc { get; init; }
    /// <summary>マッチ段階。SR形式以外では 0。</summary>
    public int MatchStage { get; init; }
    /// <summary>変更前の内容ハッシュ。</summary>
    public string? HashBefore { get; init; }
    /// <summary>変更後の内容ハッシュ。仕様書7.3の復元前照合に使う。</summary>
    public string? HashAfter { get; init; }
    /// <summary>RENAME の移動元。それ以外は null。</summary>
    public string? RenamedFrom { get; init; }
}

/// <summary>適用後フックの実行結果。仕様書6.5・7.1。</summary>
public sealed record HookResult
{
    /// <summary>フック名。</summary>
    public required string Name { get; init; }
    /// <summary>終了コード。タイムアウト時は -1。</summary>
    public int ExitCode { get; init; }
    /// <summary>所要ミリ秒。</summary>
    public long DurationMs { get; init; }
    /// <summary>タイムアウトしたかどうか。</summary>
    public bool TimedOut { get; init; }
    /// <summary>標準出力と標準エラーを結合したもの。manifest には保存しない。</summary>
    public string? Output { get; init; }
}

/// <summary>
/// リビジョンの manifest.json。仕様書7.1。
/// </summary>
public sealed record RevisionManifest
{
    /// <summary>リビジョン番号。</summary>
    public int Revision { get; init; }
    /// <summary>プロジェクトID。</summary>
    public required string ProjectId { get; init; }
    /// <summary>変更の要約。</summary>
    public string? Summary { get; init; }
    /// <summary>変更の種別。</summary>
    public string? Type { get; init; }
    /// <summary>適用日時。</summary>
    public DateTimeOffset AppliedAt { get; init; }
    /// <summary>パッチ本文の正規化ハッシュ。仕様書6.2の二重適用検知に使う。</summary>
    public string? PatchHash { get; init; }
    /// <summary>状態。<see cref="RevisionStatus"/> の値。</summary>
    public string Status { get; init; } = RevisionStatus.InProgress;
    /// <summary>統計。</summary>
    public RevisionStats Stats { get; init; } = new();
    /// <summary>ファイルごとの記録。</summary>
    public IReadOnlyList<RevisionEntry> Entries { get; init; } = Array.Empty<RevisionEntry>();
    /// <summary>フックの実行結果。</summary>
    public IReadOnlyList<HookResult> Hooks { get; init; } = Array.Empty<HookResult>();
}

/// <summary>履歴一覧の1行。バックアップ実体の有無を含む。</summary>
public sealed record RevisionSummary
{
    /// <summary>manifest の内容。</summary>
    public required RevisionManifest Manifest { get; init; }
    /// <summary>バックアップフォルダの絶対パス。</summary>
    public required string FolderPath { get; init; }
    /// <summary>実体が存在し復元可能かどうか。仕様書13.1。</summary>
    public bool IsRestorable { get; init; }
    /// <summary>バックアップの合計サイズ（バイト）。仕様書7.4の世代管理に使う。</summary>
    public long SizeBytes { get; init; }
}
