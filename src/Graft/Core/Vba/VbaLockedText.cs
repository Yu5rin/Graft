namespace Graft.Core.Vba;

/// <summary>VBA プロジェクトのロック（E803）の窓を出す場面。</summary>
public enum VbaLockedScene
{
    /// <summary>適用の後の自動 Push が E803 で失敗した（その適用 rN を取り消せる）。</summary>
    AfterApply,

    /// <summary>解析の前の取込・適用の直前の再検査が E803 で失敗した（貼ったパッチを破棄できる）。</summary>
    BeforeApply,

    /// <summary>手動の「Excel へ反映」（取り消しは出さない。解除してもう一度反映できる）。</summary>
    Manual,

    /// <summary>取込・復元など、もう一度試す入口を窓の中に持たない操作（解除の案内だけ）。</summary>
    Info,
}

/// <summary>
/// VBA プロジェクトがパスワードでロックされているとき（E803）の窓の文面と選択肢（<c>VbaChoiceWindow</c> に渡す）。
/// <para>
/// 【専用の窓にする理由】 ロックは、利用者が VBE でパスワードを入れて解除すれば直る、「やり直せば通る」失敗である。
/// 一方で、ほかの失敗（Excel が応答しない等）と同じ窓では、「ファイルの変更は残す」が既定で、貼ったパッチや適用はそのまま残る。
/// 利用者の報告では、ロックに気付いた時点では「Graft 側もなかったことにしたい」ことが多かったので、場面ごとに既定を分けた:
/// 適用の後なら「その適用だけを取り消す」、適用の前なら「貼ったパッチを破棄する」。Excel に触れずに戻せるもの（ミラー・履歴）だけを戻し、
/// 以前から溜まっていた未反映は戻さない（既存の取り消しの考え方どおり）。
/// </para>
/// </summary>
public static class VbaLockedText
{
    /// <summary>窓の題名。</summary>
    public const string Title = "VBA プロジェクトがロックされています";

    /// <summary>「中止して適用を取り消す」の識別子。</summary>
    public const string UndoId = "undo";

    /// <summary>「中止してパッチを破棄する」の識別子。</summary>
    public const string DiscardId = "discard";

    /// <summary>「VBE を開いてロックを解除する」の識別子（窓を閉じない操作なので、結果としては返らない）。</summary>
    public const string VbeId = "vbe";

    /// <summary>「解除したのでもう一度…」の識別子。</summary>
    public const string RetryId = "retry";

    /// <summary>「ファイルの変更は残す」「閉じる」の識別子。</summary>
    public const string KeepId = "keep";

    /// <summary>「ファイルだけ適用する」の識別子。</summary>
    public const string FileOnlyId = "fileOnly";

    /// <summary>「VBE を開いてロックを解除する」のボタンの文言。</summary>
    public const string VbeLabel = "VBE を開いてロックを解除する";

    private const string HowToUnlock =
        "VBA プロジェクトにパスワードがかかっています。Excel の VBE（Alt+F11）で、「ツール → VBAProject のプロパティ → 保護」を開いて" +
        "ロックを解除してください。「" + VbeLabel + "」を押すと VBE を前面に出します（この窓は閉じません）。";

    /// <summary>窓の本文。</summary>
    /// <param name="scene">場面。</param>
    /// <param name="lead">場面の最初の一文（何が済んでいて、何が済んでいないか）。</param>
    /// <param name="undoRevision">取り消す適用の番号（<see cref="VbaLockedScene.AfterApply"/> のときだけ）。</param>
    /// <param name="issueText">失敗の理由と対処（エラーの表示文）。</param>
    public static string Message(VbaLockedScene scene, string lead, int? undoRevision, string issueText)
    {
        var consequence = scene switch
        {
            VbaLockedScene.AfterApply when undoRevision is int r =>
                $"「中止してこの適用（r{r}）を取り消す」を選ぶと、この適用（r{r}）だけを取り消して元の状態に戻します" +
                "（以前から溜まっていた未反映は戻しません）。解除できたら「解除したのでもう一度反映する」で、そのまま Excel へ送れます。" +
                "「ファイルの変更は残す」を選ぶと、未反映の印を付けて残し、あとからプロジェクトの右クリック「Excel へ反映」で送れます。",
            VbaLockedScene.AfterApply =>
                "解除できたら「解除したのでもう一度反映する」で、そのまま Excel へ送れます。「ファイルの変更は残す」を選ぶと、" +
                "未反映の印を付けて残し、あとからプロジェクトの右クリック「Excel へ反映」で送れます。",
            VbaLockedScene.BeforeApply =>
                "「中止してパッチを破棄する」を選ぶと、貼ったパッチを破棄します（接ぎ木パネルが空になり、ファイルは変わりません）。" +
                "解除できたら「解除したのでもう一度試す」で、Excel 側の取り込みからやり直せます。" +
                "「ファイルだけ適用する」を選ぶと、Graft のファイルにだけ適用し、Excel には未反映のまま残します。",
            VbaLockedScene.Manual =>
                "解除できたら「解除したのでもう一度反映する」で、そのまま送れます。「閉じる」を選ぶと、未反映の印を付けたまま残ります。",
            _ => "解除できたら、もう一度同じ操作をしてください。",
        };

        var parts = new List<string>();
        if (lead.Length > 0) parts.Add(lead);
        parts.Add(HowToUnlock);
        parts.Add(consequence);
        if (issueText.Length > 0) parts.Add(issueText);
        return string.Join(Environment.NewLine + Environment.NewLine, parts);
    }

    /// <summary>窓のボタン。既定（Enter・Esc・×）は、場面ごとの「やめる側」。</summary>
    /// <param name="scene">場面。</param>
    /// <param name="undoRevision">取り消す適用の番号（<see cref="VbaLockedScene.AfterApply"/> のときだけ）。</param>
    /// <param name="openVbe">「VBE を開いてロックを解除する」が実行する処理（窓は閉じない）。</param>
    public static IReadOnlyList<VbaChoiceOption> Options(VbaLockedScene scene, int? undoRevision, Func<Task> openVbe)
    {
        ArgumentNullException.ThrowIfNull(openVbe);
        var vbe = new VbaChoiceOption(VbeId, VbeLabel, KeepOpenAction: openVbe);
        switch (scene)
        {
            case VbaLockedScene.AfterApply when undoRevision is int r:
                return new[]
                {
                    new VbaChoiceOption(UndoId, $"中止してこの適用（r{r}）を取り消す", IsDefault: true),
                    vbe,
                    new VbaChoiceOption(RetryId, "解除したのでもう一度反映する"),
                    new VbaChoiceOption(KeepId, "ファイルの変更は残す"),
                };
            case VbaLockedScene.AfterApply:
                return new[]
                {
                    new VbaChoiceOption(KeepId, "ファイルの変更は残す", IsDefault: true),
                    vbe,
                    new VbaChoiceOption(RetryId, "解除したのでもう一度反映する"),
                };
            case VbaLockedScene.BeforeApply:
                return new[]
                {
                    new VbaChoiceOption(DiscardId, "中止してパッチを破棄する", IsDefault: true),
                    vbe,
                    new VbaChoiceOption(RetryId, "解除したのでもう一度試す"),
                    new VbaChoiceOption(FileOnlyId, "ファイルだけ適用する"),
                };
            case VbaLockedScene.Manual:
                return new[]
                {
                    new VbaChoiceOption(KeepId, "閉じる", IsDefault: true),
                    vbe,
                    new VbaChoiceOption(RetryId, "解除したのでもう一度反映する"),
                };
            default:
                return new[]
                {
                    new VbaChoiceOption(KeepId, "閉じる", IsDefault: true),
                    vbe,
                };
        }
    }
}
