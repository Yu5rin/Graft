namespace Graft.ViewModels;

/// <summary>
/// <see cref="MainViewModel"/> の分割ファイル。6.2「二重適用検知」のうち、E302
/// （このパッチはrNで適用済み）を<b>プレビューの時点で</b>利用者に見せる部分を担う。
///
/// 【修正前の問題】 E302は<see cref="Core.DryRunPlanner"/>が結果レベルのissuesにだけ載せ、
/// <see cref="RunDryRunAsync"/>は成功時にそれを読まなかった。利用者は適用済みと知らされないまま
/// 「要約を入力」と「適用の確認」（または適用前プレビュー）の窓を通り抜け、
/// <see cref="Core.ApplyEngine.ApplyAsync"/>の適用時の再判定で初めて止められていた。
/// しかも止められた時点でリビジョン番号が1つ消費される（ConsumeRevisionNumberAsyncは失敗時も
/// 消費する）。UIから強制再適用（ForceReapply）する経路も無い。
///
/// 【表示方法の判断】 全ブロックを失敗（CanApply=false）扱いにする案は採らない。それをすると
/// 「修正を依頼」（<see cref="CopyRecoveryPromptCommand"/>）が押せるようになるが、適用済みのパッチを
/// AIに直してもらう意味は無い。ブロック一覧は通常どおり見せたまま、接ぎ木パネルの上部に
/// 1つだけバナーを出し（GraftPanel.axaml）、ステータスバーの要約にも理由を足し、
/// 「適用」だけを押せなくする。ApplyEngine側の適用時の再判定は安全網としてそのまま残す。
/// </summary>
public sealed partial class MainViewModel
{
    private int? _alreadyAppliedRevision;

    /// <summary>
    /// 現在プレビュー中のパッチが適用済みのリビジョン番号。適用済みでない（または強制再適用で
    /// 止まらない）ときはnull。<see cref="ApplyCommand"/>はこれがnullでないあいだ押せない。
    /// </summary>
    public int? AlreadyAppliedRevision => _alreadyAppliedRevision;

    /// <summary>接ぎ木パネル上部の「適用済み」バナーを出すかどうか。</summary>
    public bool HasAlreadyAppliedNotice => _alreadyAppliedRevision is not null;

    /// <summary>バナーの文言。適用済みでないときは空文字。</summary>
    public string AlreadyAppliedNoticeText => _alreadyAppliedRevision is { } revision
        ? $"このパッチは r{revision} で適用済みです。同じ内容をもう一度適用することはできません。"
        : string.Empty;

    /// <summary>
    /// 適用済みリビジョンを更新し、画面に出す要素（バナー・要約・適用ボタンの可否）を再評価させる。
    /// ドライランの結果を取り込む箇所（<see cref="RunDryRunAsync"/>）と、解析結果を捨てる箇所
    /// （<see cref="DiscardCurrentPatch"/>、一部適用後の<see cref="KeepOnlyFailedBlocks"/>）から呼ぶ。
    /// </summary>
    private void SetAlreadyAppliedRevision(int? revision)
    {
        _alreadyAppliedRevision = revision;
        OnPropertyChanged(nameof(AlreadyAppliedRevision));
        OnPropertyChanged(nameof(HasAlreadyAppliedNotice));
        OnPropertyChanged(nameof(AlreadyAppliedNoticeText));
        OnPropertyChanged(nameof(StatusSummaryText));
        CommandRequery.Invalidate();
    }
}
