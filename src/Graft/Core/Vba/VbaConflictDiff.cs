namespace Graft.Core.Vba;

/// <summary>
/// 衝突ダイアログ（仕様書_VBA連携 7.5）に出す差分の組み立て。既存の <c>DiffViewModel</c> は <see cref="BlockPlan"/> を
/// 受け取るので、<c>HistoryPaneViewModel.BuildDiffPlansAsync</c> にならって、2つの本文から <see cref="BlockPlan"/> を作る。
/// <para>
/// 向きは「Excel 側（変更前）→ Graft 側＝ミラー（変更後）」。「Graft 側で上書き」を選んだときに Excel の本文が
/// どう変わるか、がそのまま差分として読める。適用の対象ではないので <see cref="BlockPlan.CanApply"/> は false。
/// </para>
/// </summary>
public static class VbaConflictDiff
{
    /// <summary>衝突1件分の表示用ブロック。</summary>
    public static BlockPlan BuildPlan(VbaConflictItem item, int contextLines)
    {
        ArgumentNullException.ThrowIfNull(item);

        // 本文の比較は改行コードの違いを拾わない形にそろえてから行う（Excel 側は CRLF、ミラーのファイルは
        // 既存の形のまま LF のこともある。そろえないと全行が変更に見える）。
        var excel = VbaTextNormalizer.ToCrLf(item.ExcelText);
        var mirror = VbaTextNormalizer.ToCrLf(item.MirrorText);
        var diff = DiffBuilder.Build(item.RelativePath, excel, mirror, contextLines);
        return new BlockPlan
        {
            Block = new FullContentBlock { Path = item.RelativePath, Content = mirror },
            Path = item.RelativePath,
            Operation = EntryOperation.Modify,
            Stage = MatchStage.None,
            CanApply = false,
            NeedsConfirmation = false,
            IsSelected = false,
            BeforeText = excel,
            AfterText = mirror,
            Diff = diff,
            Description = "左: Excel 側 / 右: Graft 側（ミラー）",
            Added = diff.Added,
            Removed = diff.Removed,
        };
    }
}
