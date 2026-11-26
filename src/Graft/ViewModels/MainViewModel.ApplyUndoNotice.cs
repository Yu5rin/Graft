namespace Graft.ViewModels;

/// <summary>
/// <see cref="MainViewModel"/> の分割ファイル（1ファイル400行上限のため）。
///
/// 製品としての使い勝手3件のうち機能2: 適用が成功した直後、ステータスバーに
/// 「rN として適用しました — 元に戻す」を数秒だけ表示する通知。
/// <see cref="ExplorerViewModel"/>の削除取り消し通知（<c>HasDeleteUndoNotice</c>・
/// <c>DeleteUndoNoticeText</c>・<see cref="Platform.IUiTimer"/>で数秒後に自動的に消す）と
/// まったく同じ作法に揃えている。
///
/// 「元に戻す」の実処理は<see cref="UndoCommand"/>（<c>UndoLastAsync</c>→
/// <c>History.UndoLatestAsync</c>→<c>HistoryPaneViewModel</c>内部の単発復元経路、
/// 最終的に<see cref="Core.RevisionRestorer.RestoreAsync"/>）をそのまま再利用する
/// （Ctrl+Zと同じ経路。並行実装を作らない）。そのため確認ダイアログ（「取り消しの確認」）も
/// Ctrl+Zと同じく挟まる。この通知はあくまで「直前に適用したばかりのリビジョン番号」と
/// 「元に戻す手段があること」を数秒間だけ知らせるものであり、クリックの結果自体は
/// 既存の復元フローに委ねる。
/// </summary>
public sealed partial class MainViewModel
{
    // 課題2の削除通知（ExplorerViewModel.DeleteNoticeDuration）と同じ長さに揃える。
    private static readonly TimeSpan ApplyUndoNoticeDuration = TimeSpan.FromSeconds(6);

    private readonly Platform.IUiTimer _applyUndoNoticeTimer;
    private bool _hasApplyUndoNotice;
    private string? _lastAppliedNoticeProjectId;
    private string _applyUndoNoticeText = string.Empty;

    /// <summary>
    /// 適用直後、数秒間だけステータスバーに「元に戻す」通知を出すかどうか。消えた後も
    /// <see cref="UndoCommand"/>自体は（取り消せる直前のリビジョンがある限り）実行できる。
    /// </summary>
    public bool HasApplyUndoNotice { get => _hasApplyUndoNotice; private set => SetProperty(ref _hasApplyUndoNotice, value); }

    /// <summary>
    /// 直近に適用したリビジョン番号。通知の「変更を見る」（ShellViewModel.ShowAppliedRevisionCommand、
    /// ShellViewModel.AppliedChangeNavigation.cs）が、どのリビジョンを履歴で選ぶかの手がかりに使う。
    /// 通知が消えた後も値は残る（コマンドの有効条件は<see cref="HasApplyUndoNotice"/>も見るため、
    /// 消えた後は押せない）。まだ一度も適用していなければnull。
    /// </summary>
    public int? LastAppliedRevision { get; private set; }

    /// <summary>適用直後の通知文言（例:「r12 として適用しました — 元に戻す」）。</summary>
    public string ApplyUndoNoticeText { get => _applyUndoNoticeText; private set => SetProperty(ref _applyUndoNoticeText, value, () => OnPropertyChanged(nameof(ApplyUndoNoticeMessage))); }

    /// <summary>
    /// 通知のうち押せない文言の部分（例:「r12 として適用しました」）。ステータスバーは、この文言を
    /// 通常の文字色で、続く「元に戻す」だけをリンクの見た目で並べる。全体を1つのボタンにすると
    /// 説明文までリンク色になり、押せる範囲が分かりにくかった。
    /// </summary>
    public string ApplyUndoNoticeMessage => _applyUndoNoticeText.EndsWith(" — 元に戻す", StringComparison.Ordinal)
        ? _applyUndoNoticeText[..^" — 元に戻す".Length]
        : _applyUndoNoticeText;

    /// <summary>
    /// ApplyCoreAsyncの適用成功直後から呼ぶ。仕様どおり、適用が失敗した場合や
    /// （呼び出し元でガードすることにより）リビジョンが記録されなかった場合は呼ばれない。
    /// </summary>
    /// <param name="revision">適用したリビジョン番号。</param>
    /// <param name="detail">
    /// 「rN として適用しました」に続けて添える括弧書き（Excel VBA 連携の「Excel へ反映済み」など。仕様書_VBA連携 7.3-4-7）。
    /// null なら従来どおり。
    /// </param>
    /// <param name="projectId">
    /// 適用したプロジェクトの Id。履歴だけを削除したとき（MainViewModel.ClearHistory.cs）、その適用についての通知なら
    /// 消す（消えたリビジョンへの「元に戻す」は押しても E405 になる）ために覚えておく。
    /// </param>
    private void ShowApplyUndoNotice(int revision, string? detail = null, string? projectId = null)
    {
        _lastAppliedNoticeProjectId = projectId;
        ApplyUndoNoticeText = detail is null
            ? $"r{revision} として適用しました — 元に戻す"
            : $"r{revision} として適用しました（{detail}） — 元に戻す";
        LastAppliedRevision = revision;
        HasApplyUndoNotice = true;
        _applyUndoNoticeTimer.Restart();
    }

    private void OnApplyUndoNoticeTimeout()
    {
        HasApplyUndoNotice = false;
        _applyUndoNoticeTimer.Stop();
    }

    /// <summary>
    /// 通知を即座に消す。「元に戻す」のクリック自体（<see cref="UndoCommand"/>）はCtrl+Zとも
    /// 共有しているため、どちらの経路で取り消しても、もう有効ではない通知を出しっぱなしに
    /// しないようUndoLastAsyncの冒頭から呼ぶ。
    /// </summary>
    private void DismissApplyUndoNotice()
    {
        if (!_hasApplyUndoNotice) return;
        _applyUndoNoticeTimer.Stop();
        HasApplyUndoNotice = false;
    }
}
