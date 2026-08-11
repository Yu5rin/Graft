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
/// （Ctrl+Zと同じ経路。並行実装を作らない）。そのため確認ダイアログ（「復元の確認」）も
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
    private string _applyUndoNoticeText = string.Empty;

    /// <summary>
    /// 適用直後、数秒間だけステータスバーに「元に戻す」通知を出すかどうか。消えた後も
    /// <see cref="UndoCommand"/>自体は（取り消せる直前のリビジョンがある限り）実行できる。
    /// </summary>
    public bool HasApplyUndoNotice { get => _hasApplyUndoNotice; private set => SetProperty(ref _hasApplyUndoNotice, value); }

    /// <summary>適用直後の通知文言（例:「r12 として適用しました — 元に戻す」）。</summary>
    public string ApplyUndoNoticeText { get => _applyUndoNoticeText; private set => SetProperty(ref _applyUndoNoticeText, value); }

    /// <summary>
    /// ApplyCoreAsyncの適用成功直後から呼ぶ。仕様どおり、適用が失敗した場合や
    /// （呼び出し元でガードすることにより）リビジョンが記録されなかった場合は呼ばれない。
    /// </summary>
    private void ShowApplyUndoNotice(int revision)
    {
        ApplyUndoNoticeText = $"r{revision} として適用しました — 元に戻す";
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
