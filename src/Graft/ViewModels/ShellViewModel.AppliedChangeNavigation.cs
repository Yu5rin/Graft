using System.IO;
using System.Windows.Input;

namespace Graft.ViewModels;

/// <summary>
/// <see cref="ShellViewModel"/> の分割ファイル。適用したあと、変更したファイルへ辿る導線を担う。
///
/// 【修正前の問題】 全件成功した適用の直後は接ぎ木パネルのブロック一覧が空になる。適用前なら
/// ブロックごとに「対象ファイルを開く」があるが、適用後は開く入口が無く、履歴の差分タブ
/// （HistoryDiffView）にもファイルを開くコマンドが無かった。
/// 1. 履歴差分タブの各ファイル見出しに「ファイルを開く」を足す（<see cref="OnHistoryDiffOpenFileRequested"/>）。
/// 2. 適用直後のステータスバー通知に「変更を見る」を足す（<see cref="ShowAppliedRevisionCommand"/>）。
/// </summary>
public sealed partial class ShellViewModel
{
    /// <summary>
    /// 適用直後のステータスバー通知の「変更を見る」。たった今適用したリビジョンを履歴で選び、
    /// 履歴差分タブ（そのリビジョンが変更した全ファイルの差分。各ファイルを開ける）を表示する。
    /// 通知が出ていないとき（適用直後の数秒を過ぎたとき）は押せない。
    /// </summary>
    public ICommand ShowAppliedRevisionCommand { get; private set; } = null!;

    private void InitializeAppliedChangeNavigation()
    {
        ShowAppliedRevisionCommand = new RelayCommand(
            ShowAppliedRevision, () => Graft.HasApplyUndoNotice && Graft.LastAppliedRevision is not null);
    }

    private void ShowAppliedRevision()
    {
        if (Graft.LastAppliedRevision is not { } revision) return;

        // 履歴ペインでそのリビジョンを選ぶ。選択の変化は履歴差分タブの表示につながる
        // （MainViewModel.OnRevisionSelected → HistoryDiffChanged → OnHistoryDiffChanged）。
        // 絞り込み条件で一覧から隠れている場合は、TrySelectRevisionが絞り込みを解除する。
        Graft.History.TrySelectRevision(revision);

        // 履歴ビューを開いて一覧へフォーカスする。OnShowFileHistoryRequestedと同じ理由で、
        // すでに履歴ビューが表示中ならスキップする（再選択はサイドビューを折りたたむ
        // トグル動作に巻き込まれるため）。
        if (!IsHistoryActive) Graft.ShowHistoryCommand.Execute(null);
    }

    /// <summary>
    /// 履歴差分タブの「ファイルを開く」。接ぎ木パネルのブロックの「対象ファイルを開く」と同じ
    /// <see cref="OpenProjectFileAsync"/>の経路で開く。
    /// <para>
    /// 開くのは<b>いまのディスク上の内容</b>で、そのリビジョン時点の内容ではない（差分タブが
    /// 示すのは当時の変更）。後のリビジョンで削除・移動されたファイルは存在しないため、
    /// 何も起きずに終わらないよう、押した時点で存在を確認して理由を伝える。
    /// （削除操作そのもののエントリには、ボタン自体を出さない。HistoryDiffFileViewModel.CanOpenFile参照）
    /// </para>
    /// </summary>
    private async void OnHistoryDiffOpenFileRequested(object? sender, string relativePath)
        => await SafeHandler.RunAsync("履歴の変更ファイルを開く", async () =>
        {
            var root = Graft.ProjectPane.SelectedItem?.Project.Root;
            if (root is null) return;

            var fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
            {
                await _dialogs.ShowMessageAsync(
                    "ファイルを開けません",
                    $"{relativePath} は現在のプロジェクトにありません。このリビジョンより後に削除・移動された可能性があります。")
                    .ConfigureAwait(true);
                return;
            }

            await OpenProjectFileAsync(relativePath).ConfigureAwait(true);
        }).ConfigureAwait(true);
}
