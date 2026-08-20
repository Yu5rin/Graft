using Graft.Infra;

namespace Graft.Core;

/// <summary>ブロック1件のドライラン結果。UI のブロック一覧に対応する。</summary>
public sealed record BlockPlan
{
    /// <summary>元のブロック。SR形式では複数の BlockPlan が同じ Block を共有しうる（ペア単位で1件）。</summary>
    public required PatchBlock Block { get; init; }

    /// <summary>SR形式で、このユニットに対応する個別のペア。SR形式以外・ファイル単位の失敗では null。</summary>
    public SearchReplacePair? Pair { get; init; }

    /// <summary>操作対象のプロジェクト相対パス。RENAME では移動先。</summary>
    public required string Path { get; init; }

    /// <summary>操作種別。<see cref="EntryOperation"/> の値。</summary>
    public required string Operation { get; init; }

    /// <summary>マッチ段階。SR形式以外では <see cref="MatchStage.None"/>。</summary>
    public MatchStage Stage { get; init; }

    /// <summary>適用可能かどうか。</summary>
    public bool CanApply { get; init; }

    /// <summary>要確認かどうか（マッチ段階5、または警告付き）。</summary>
    public bool NeedsConfirmation { get; init; }

    /// <summary>ユーザーが適用対象として選択しているかどうか。既定は CanApply と同値。</summary>
    public bool IsSelected { get; init; }

    /// <summary>検出された問題。</summary>
    public IReadOnlyList<GraftIssue> Issues { get; init; } = Array.Empty<GraftIssue>();

    /// <summary>適用前のファイル全文。新規作成では null。</summary>
    public string? BeforeText { get; init; }

    /// <summary>適用後のファイル全文。削除では null。</summary>
    public string? AfterText { get; init; }

    /// <summary>元ファイルのエンコーディング・改行の情報。新規作成では null。</summary>
    public TextShape? Shape { get; init; }

    /// <summary>差分。</summary>
    public DiffModel? Diff { get; init; }

    /// <summary>変更説明。ブロックまたはペアの # 以降から抽出したもの。</summary>
    public string? Description { get; init; }

    /// <summary>追加行数。</summary>
    public int Added { get; init; }

    /// <summary>削除行数。</summary>
    public int Removed { get; init; }
}

/// <summary>
/// 実機不具合対応（診断用）: ドライラン中に1ファイルへ行った存在確認・読み取りの記録。
/// UI表示には使わず、MainViewModel側でLoggerへ1行ずつ書き出す診断専用のデータ。
/// 「エディタは開けるのに接ぎ木だけ失敗する」という実機報告の原因を切り分けるため、
/// PathGuard.Inspectが実際に確認した絶対パス・存在有無・読み取れたサイズ/行数を残す。
/// </summary>
public sealed record DryRunFileProbe
{
    /// <summary>プロジェクト相対パス（パッチ内の表記）。</summary>
    public required string Path { get; init; }

    /// <summary>PathGuardが実際に存在確認・読み取りを行った絶対パス。</summary>
    public required string FullPath { get; init; }

    /// <summary>存在確認の結果。</summary>
    public required bool Exists { get; init; }

    /// <summary>FileCheck.SizeBytes（存在する場合のみ）。</summary>
    public long? SizeBytes { get; init; }

    /// <summary>読み取りに成功した場合の行数（TextNormalizer.SplitLines後）。読み取りを伴わない
    /// 操作（RENAME・DELETE）や読み取り失敗時はnull。</summary>
    public int? LineCount { get; init; }
}

/// <summary>ドライラン全体の結果。ファイルへは一切書き込まない。</summary>
public sealed record DryRunResult
{
    /// <summary>元のパッチ。</summary>
    public required Patch Patch { get; init; }

    /// <summary>ブロックごとの計画。仕様書6.6の適用順序に並んでいる。</summary>
    public required IReadOnlyList<BlockPlan> Plans { get; init; }

    /// <summary>パッチ本文の正規化ハッシュ。仕様書6.2。</summary>
    public required string PatchHash { get; init; }

    /// <summary>全体の統計。</summary>
    public RevisionStats Stats { get; init; } = new();

    /// <summary>診断用: ドライラン中に確認した対象ファイルごとの記録（MainViewModelがログへ出力する）。</summary>
    public IReadOnlyList<DryRunFileProbe> FileProbes { get; init; } = Array.Empty<DryRunFileProbe>();

    /// <summary>適用可能なブロック数。</summary>
    public int ApplicableCount => Plans.Count(p => p.CanApply);

    /// <summary>要確認のブロック数。</summary>
    public int ConfirmationCount => Plans.Count(p => p.NeedsConfirmation);

    /// <summary>失敗したブロック数。</summary>
    public int FailedCount => Plans.Count(p => !p.CanApply);
}

/// <summary>適用時の文脈。プロジェクトと設定を束ねる。</summary>
public sealed record ApplyContext
{
    /// <summary>プロジェクトID。</summary>
    public required string ProjectId { get; init; }

    /// <summary>プロジェクトルートの絶対パス。</summary>
    public required string ProjectRoot { get; init; }

    /// <summary>付与するリビジョン番号。</summary>
    public required int Revision { get; init; }

    /// <summary>全体設定（プロジェクトの overrides 反映後）。</summary>
    public required Settings Settings { get; init; }

    /// <summary>パス検証。</summary>
    public required PathGuard Guard { get; init; }

    /// <summary>適用済みパッチの再投入を許可するかどうか。仕様書6.2。</summary>
    public bool ForceReapply { get; init; }

    /// <summary>読み取り専用ファイルの属性解除を許可するかどうか。仕様書13。</summary>
    public bool AllowReadOnlyOverride { get; init; }
}
