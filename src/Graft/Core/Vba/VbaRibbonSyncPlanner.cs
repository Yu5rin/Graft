namespace Graft.Core.Vba;

/// <summary>
/// リボンの 1 形式ぶんの同期基準（仕様書_ブックの情報と依頼の観点 16.4。保存先は段階 F2 で <c>vba-sync/&lt;Id&gt;.json</c> の
/// <c>ribbon</c> に足す。F1 では型と判定だけを用意する）。
/// <para>
/// 【基準を 2 本持つ理由】 モジュールの同期基準（<see cref="VbaModuleState"/> の <c>MirrorHash</c>・<c>ExcelHash</c>）と同じ形に
/// そろえるため。書き換えはミラーの本文をそのまま書くので通常は同じ値になるが、取込の直後に Excel が保存で部品を書き直した、
/// のような場面で、どちらの側が動いたかを区別できる。
/// </para>
/// </summary>
/// <param name="PartName">パッケージの中の実際の部品名（<c>customUI/customUI14.xml</c>。別名のこともある）。</param>
/// <param name="MirrorHash">最後に同期したときのミラーの本文のハッシュ（<see cref="VbaTextNormalizer.Hash"/>）。</param>
/// <param name="FileHash">最後に同期したときのブックの部品の本文のハッシュ。</param>
/// <param name="DeletePending">ミラーのファイルが消された（削除保留）。自動の取込では復活させない。</param>
/// <param name="NeedsReview">書き換えに失敗し、控えから戻せなかった（16.11）。衝突として扱い、利用者に選ばせる。</param>
/// <param name="SyncedAt">基準を作った日時。</param>
public sealed record VbaRibbonSyncRecord(
    string PartName, string MirrorHash, string FileHash, bool DeletePending = false, bool NeedsReview = false,
    DateTimeOffset SyncedAt = default);

/// <summary>ミラーのリボンのファイル 1 つ（読めたときは本文、読めなかったときは理由）。無いファイルは渡さない。</summary>
/// <param name="Text">本文（読めたとき）。</param>
/// <param name="Problem">読めなかった理由（読めたときは null）。</param>
public sealed record VbaRibbonMirrorFile(string? Text, string? Problem = null)
{
    /// <summary>読めたか。</summary>
    public bool IsReadable => Text is not null && Problem is null;
}

/// <summary>リボンの 1 形式について、同期で何をするか（16.4 の判定表）。</summary>
public enum VbaRibbonSyncAction
{
    /// <summary>何もしない（記録の直しだけが要るときは <see cref="VbaRibbonSyncItem.Baseline"/> に入る）。</summary>
    None,

    /// <summary>ミラー → ブックのファイル（「リボン未反映」。16.5 の書き換えの対象）。</summary>
    Push,

    /// <summary>ブックのファイル → ミラー（取込）。</summary>
    Pull,

    /// <summary>両側が変わっていて本文が違う、または要確認（<see cref="VbaRibbonSyncRecord.NeedsReview"/>）。</summary>
    Conflict,

    /// <summary>両側が変わっているが本文が同じ（または記録が無く両側が同じ）。基準だけを作り直す（I/O なし）。</summary>
    Rebaseline,

    /// <summary>削除保留。ミラーのファイルが無いが、ブックにはある。自動の取込では復活させず、ブックからも消さない。</summary>
    DeletePending,

    /// <summary>両側とも無くなったので、記録を捨てる。</summary>
    DiscardRecord,

    /// <summary>
    /// 読めない側がある（ミラーのファイルを読めない、部品が壊れている・DTD・大きすぎる）。<b>読めないものを「無い」と扱わない</b>
    /// （モジュールと同じ規則。「無い」と扱うと、削除保留や新規の追加と取り違えて、利用者の内容を上書きしうる）。警告にして計画から外す。
    /// </summary>
    Skip,
}

/// <summary>リボンの 1 形式ぶんの同期計画。</summary>
/// <param name="Format">形式。</param>
/// <param name="MirrorPath">ミラーの正規の相対パス（<c>CustomUI/customUI14.xml</c>）。</param>
/// <param name="PartName">
/// 書く（または読んだ）部品名。ブックに部品があればその名前（同期状態の値と違えば、ブックの現在の値を優先する。16.7）、
/// 無ければ記録の名前、それも無ければ既定の名前。
/// </param>
/// <param name="Action">何をするか。</param>
/// <param name="MirrorHash">ミラーの本文のハッシュ（読めたとき）。</param>
/// <param name="FileHash">ブックの部品の本文のハッシュ（読めたとき）。</param>
/// <param name="Baseline">
/// I/O なしで保存してよい新しい基準（<see cref="VbaRibbonSyncAction.Rebaseline"/>・<see cref="VbaRibbonSyncAction.DeletePending"/> の
/// 記録の更新、部品名・削除保留の直しだけの <see cref="VbaRibbonSyncAction.None"/>）。Push・Pull の基準は、実行が成功してから
/// 呼び出し側が作る（失敗したのに基準だけ進むと、次の比較で変更を見落とすため）。直すものが無ければ null。
/// </param>
/// <param name="Warning">利用者に知らせる警告（読めない・ブックから部品が無くなった）。無ければ null。</param>
/// <param name="IsNewPart">ブックにこの形式の部品が無く、書き換えで新しく足すことになるか（確認ダイアログの文面を変える。16.9）。</param>
/// <param name="ConflictReason">衝突のときの理由（衝突以外では意味を持たない）。</param>
public sealed record VbaRibbonSyncItem(
    VbaRibbonFormat Format,
    string MirrorPath,
    string PartName,
    VbaRibbonSyncAction Action,
    string? MirrorHash = null,
    string? FileHash = null,
    VbaRibbonSyncRecord? Baseline = null,
    string? Warning = null,
    bool IsNewPart = false,
    VbaConflictReason ConflictReason = VbaConflictReason.BothChanged);

/// <summary>リボンの同期計画（形式ごとに最大 1 件）。</summary>
/// <param name="Items">形式ごとの計画。両側とも無く記録も無い形式は含まない。</param>
/// <param name="SkippedReason">
/// ブック全体を読めず、リボンの同期を丸ごと見送ったときの理由（警告にする）。.xls（<see cref="VbaRibbonReadStatus.LegacyFormat"/>）は
/// 対象外として黙って何もしないので、このときも null。
/// </param>
public sealed record VbaRibbonSyncPlan(IReadOnlyList<VbaRibbonSyncItem> Items, string? SkippedReason = null)
{
    /// <summary>指定の動作の計画。</summary>
    public IEnumerable<VbaRibbonSyncItem> Where(VbaRibbonSyncAction action) => Items.Where(i => i.Action == action);

    /// <summary>ブックへの書き換えが要る（「リボン未反映」の）ものがあるか。バッジに使う。</summary>
    public bool HasPush => Items.Any(i => i.Action == VbaRibbonSyncAction.Push);

    /// <summary>衝突があるか。</summary>
    public bool HasConflicts => Items.Any(i => i.Action == VbaRibbonSyncAction.Conflict);
}

/// <summary>
/// ミラーのリボンのファイルと、ブックのファイルの customUI の部品の三方比較（仕様書_ブックの情報と依頼の観点 16.4）。
/// 入力も出力も純粋なレコードで、ファイルにも COM にも触れない（判定表の全行を Linux の CI で確かめるため）。
/// <para>
/// 【相手は Excel ではなくブックのファイル】 Excel のオブジェクトモデルに customUI を読み書きする手段は無いので（付録A-24）、
/// 比べる相手は <see cref="VbaRibbonPackageReader.Read"/> が読んだディスク上のファイル。<c>M</c>＝ミラーの本文、
/// <c>F</c>＝ブックの部品、<c>Bm</c>・<c>Bf</c>＝基準。ハッシュは <see cref="VbaTextNormalizer.Hash"/>（改行の違いと末尾の改行だけの
/// 違いを「変更」にしない）。VBE のような整形は無いので、ゆるい形のハッシュは使わない。
/// </para>
/// <para>
/// 【別のブックの記録】 同期状態の <c>workbookFullName</c> がいまのブックと違うときは、モジュールと同じく「記録なし」として扱う。
/// その判定と E812 の警告は呼び出し側（段階 F2 の同期の実行）が行い、ここへは <paramref name="records"/> を渡さない。
/// </para>
/// </summary>
public static class VbaRibbonSyncPlanner
{
    /// <summary>同期計画を作る。</summary>
    /// <param name="workbook">ブックの読み取り結果（<see cref="VbaRibbonPackageReader.Read"/>）。</param>
    /// <param name="mirror">ミラーにある形式ごとのファイル（無い形式は入れない）。</param>
    /// <param name="records">形式ごとの同期基準（無ければ null か、その形式を入れない）。</param>
    /// <param name="now">新しい基準に記録する日時（純ロジックに保つため、呼び出し側から渡す）。</param>
    public static VbaRibbonSyncPlan Plan(
        VbaRibbonReadResult workbook,
        IReadOnlyDictionary<VbaRibbonFormat, VbaRibbonMirrorFile> mirror,
        IReadOnlyDictionary<VbaRibbonFormat, VbaRibbonSyncRecord>? records,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(workbook);
        ArgumentNullException.ThrowIfNull(mirror);

        switch (workbook.Status)
        {
            case VbaRibbonReadStatus.LegacyFormat:
                // .xls は customUI を持てない形式。異常ではないので、警告にもせず何もしない（15.7 の「対象が無い」と同じ扱い）。
                return new VbaRibbonSyncPlan(Array.Empty<VbaRibbonSyncItem>());
            case VbaRibbonReadStatus.FileNotFound:
            case VbaRibbonReadStatus.Locked:
            case VbaRibbonReadStatus.Corrupt:
            case VbaRibbonReadStatus.Encrypted:
                // ブック全体が読めないときに「部品が無い」と扱うと、記録のあるリボンを「ブックから無くなった」と取り違えたり、
                // ミラーにだけあるものを新規の追加として書きに行ったりする。丸ごと見送り、モジュールの同期は止めない（16.4）。
                return new VbaRibbonSyncPlan(
                    Array.Empty<VbaRibbonSyncItem>(),
                    $"ブックのファイルを読めないため、リボンの同期を見送りました（{workbook.Reason ?? workbook.Status.ToString()}）");
        }

        var items = new List<VbaRibbonSyncItem>();
        foreach (var format in Enum.GetValues<VbaRibbonFormat>())
        {
            mirror.TryGetValue(format, out var mirrorFile);
            VbaRibbonSyncRecord? record = null;
            records?.TryGetValue(format, out record);
            var part = workbook.Parts.FirstOrDefault(p => VbaRibbonMirrorLayout.FormatOf(p) == format);
            var item = PlanOne(format, mirrorFile, part, record, now);
            if (item is not null) items.Add(item);
        }

        return new VbaRibbonSyncPlan(items);
    }

    private static VbaRibbonSyncItem? PlanOne(
        VbaRibbonFormat format, VbaRibbonMirrorFile? mirrorFile, VbaRibbonRawPart? part, VbaRibbonSyncRecord? record, DateTimeOffset now)
    {
        var mirrorPath = VbaRibbonMirrorLayout.ToRelativePath(format);
        var partName = part?.PartName ?? record?.PartName ?? VbaRibbonMirrorLayout.DefaultPartName(format);

        // 読めない側があれば、その形式を計画から外す（「無い」と扱わない）。
        if (mirrorFile is not null && !mirrorFile.IsReadable)
        {
            return new VbaRibbonSyncItem(format, mirrorPath, partName, VbaRibbonSyncAction.Skip,
                Warning: $"{mirrorPath} を読めないため、リボンの同期を見送りました（{mirrorFile.Problem ?? "理由は不明"}）");
        }

        if (part is not null && !part.IsReadable)
        {
            return new VbaRibbonSyncItem(format, mirrorPath, partName, VbaRibbonSyncAction.Skip,
                Warning: $"ブックの {part.PartName} を読めないため、リボンの同期を見送りました（{part.Problem ?? "理由は不明"}）");
        }

        var m = mirrorFile?.Text is { } mirrorText ? VbaTextNormalizer.Hash(mirrorText) : null;
        var f = part?.Xml is { } fileXml ? VbaTextNormalizer.Hash(fileXml) : null;

        if (record is null)
        {
            return (m, f) switch
            {
                (null, null) => null,
                // 記録なし・ミラーにだけある: customUI の無いブックへの新規の追加（AI が CustomUI/customUI14.xml を作った）。
                (not null, null) => new VbaRibbonSyncItem(format, mirrorPath, partName, VbaRibbonSyncAction.Push, m, null, IsNewPart: true),
                // 記録なし・ブックにだけある: 既存のプロジェクトの最初の取込もこれ（移行処理が要らない理由。16.3）。
                (null, not null) => new VbaRibbonSyncItem(format, mirrorPath, partName, VbaRibbonSyncAction.Pull, null, f),
                // 記録なし・両方ある: 同じなら基準を作るだけ。違えばどちらが新しいか分からないので、利用者に選ばせる。
                _ when m == f => new VbaRibbonSyncItem(format, mirrorPath, partName, VbaRibbonSyncAction.Rebaseline, m, f,
                    Baseline: new VbaRibbonSyncRecord(partName, m!, f!, SyncedAt: now)),
                _ => new VbaRibbonSyncItem(format, mirrorPath, partName, VbaRibbonSyncAction.Conflict, m, f),
            };
        }

        if (m is null && f is null)
        {
            return new VbaRibbonSyncItem(format, mirrorPath, partName, VbaRibbonSyncAction.DiscardRecord);
        }

        if (record.NeedsReview)
        {
            // 書き換えに失敗して戻せなかった（16.11）。どちらが正しいか Graft には分からないので、自動ではどちらへも動かさない。
            return new VbaRibbonSyncItem(format, mirrorPath, partName, VbaRibbonSyncAction.Conflict, m, f,
                IsNewPart: f is null, ConflictReason: VbaConflictReason.NeedsReview);
        }

        if (m is null)
        {
            // 記録あり・ミラーのファイルが無い・ブックにはある: 削除保留。利用者が消したファイルを、次の取込で黙って復活させない。
            // ブックからも消さない（部品の削除は初版では扱わない。16.16-7）。印が既に立っていれば記録は直さない。
            return new VbaRibbonSyncItem(format, mirrorPath, partName, VbaRibbonSyncAction.DeletePending, null, f,
                Baseline: record.DeletePending && record.PartName == partName
                    ? null
                    : record with { PartName = partName, DeletePending = true });
        }

        var mirrorChanged = m != record.MirrorHash;
        if (f is null)
        {
            // 記録あり・ミラーにある・ブックに無い: Excel や別のツールで部品が消された。ミラーが変わっていれば反映（足し直す）、
            // 変わっていなければ何もしない（勝手に足し直すと、利用者が意図して消したリボンを戻してしまう）。どちらでも知らせる。
            const string missing = "ブックからリボンの部品が無くなっています";
            return mirrorChanged
                ? new VbaRibbonSyncItem(format, mirrorPath, partName, VbaRibbonSyncAction.Push, m, null, Warning: missing, IsNewPart: true)
                : new VbaRibbonSyncItem(format, mirrorPath, partName, VbaRibbonSyncAction.None, m, null, Warning: missing);
        }

        var fileChanged = f != record.FileHash;
        // 部品名がブックの現在の値と違う、削除保留の印が残っている（ミラーにファイルが戻った）ときは、記録だけを直す。
        var fixedRecord = record.PartName != partName || record.DeletePending
            ? record with { PartName = partName, DeletePending = false }
            : null;

        if (!mirrorChanged && !fileChanged)
            return new VbaRibbonSyncItem(format, mirrorPath, partName, VbaRibbonSyncAction.None, m, f, Baseline: fixedRecord);
        if (mirrorChanged && !fileChanged)
            return new VbaRibbonSyncItem(format, mirrorPath, partName, VbaRibbonSyncAction.Push, m, f, Baseline: fixedRecord);
        if (!mirrorChanged)
            return new VbaRibbonSyncItem(format, mirrorPath, partName, VbaRibbonSyncAction.Pull, m, f, Baseline: fixedRecord);
        return m == f
            ? new VbaRibbonSyncItem(format, mirrorPath, partName, VbaRibbonSyncAction.Rebaseline, m, f,
                Baseline: new VbaRibbonSyncRecord(partName, m, f, SyncedAt: now))
            : new VbaRibbonSyncItem(format, mirrorPath, partName, VbaRibbonSyncAction.Conflict, m, f);
    }
}
