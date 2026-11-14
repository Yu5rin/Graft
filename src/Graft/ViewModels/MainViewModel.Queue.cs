using System.IO;
using System.Windows.Input;
using Graft.Core;
using Graft.Features;

namespace Graft.ViewModels;

/// <summary>
/// <see cref="MainViewModel"/> の分割ファイル（1ファイル400行上限のため）。
/// 仕様書4.10「分割パッチの受け取り」（切断検出→パッチキューへの追加→確認のうえ継続依頼の
/// コピー→キューの結合適用）と、11章「失敗時リカバリ支援」（失敗ブロックの再依頼文コピー）を担う。
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>4.10 パッチキュー本体。終了時の保存・起動時の復元は起動処理担当が呼び出す。</summary>
    public PatchQueue PatchQueue { get; }

    /// <summary>キュー一覧表示用のViewModel（QueueWindowのDataContext）。</summary>
    public QueueViewModel Queue { get; }

    /// <summary>現在解析中のパッチをキューへ追加する（4.10、手動）。</summary>
    public ICommand AddCurrentPatchToQueueCommand { get; }

    /// <summary>キュー管理ウィンドウを開く。</summary>
    public ICommand OpenQueueCommand { get; }

    /// <summary>11章: 失敗ブロックの再依頼プロンプトをコピーする。</summary>
    public ICommand CopyRecoveryPromptCommand { get; }

    /// <summary>View側でキュー管理ウィンドウを開くタイミングの通知。</summary>
    public event EventHandler? RequestOpenQueue;

    /// <summary>
    /// 4.10: パッチが途中で切れていた場合の処理。解析できたブロックはキューへ追加したうえで、
    /// 続きを依頼するプロンプトをクリップボードへコピーしてよいか確認する。
    ///
    /// 以前は確認なしに上書きコピーしていたが、貼り付けた元のパッチ（実機で3MBのパッチが
    /// 消失したことを確認済み）がクリップボードから失われてしまう事故があったため、
    /// 「事後報告」ではなく「事前の確認」に変更した。キャンセルした場合はクリップボードへ
    /// 一切触れないため、元の内容がそのまま保たれる。
    /// </summary>
    private async Task HandleTruncatedPatchAsync(Patch patch)
    {
        var addResult = PatchQueue.Add(patch, ProjectPane.SelectedItem?.Project.Id);
        Queue.Refresh();

        var duplicateCount = addResult.Issues.Count(i => i.Code == ErrorCode.E007);
        var confirmMessage = BuildTruncatedPatchConfirmMessage(addResult.Value.Count, duplicateCount);
        var confirmed = await _dialogs.ConfirmAsync("続きの依頼文をコピーしますか？", confirmMessage).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        // 受け取った形式（Graft独自／標準SR／unified diff）に合わせて続きを頼む。
        // 常に「同じGraft形式で」と頼んでいた頃は、標準SR形式の出力の続きまでGraft形式で返ってきた。
        var continuation = RecoveryPrompt.BuildContinuation(patch.TailLines, patch.Format);
        TrySetClipboardText(continuation);
    }

    /// <summary>
    /// 4.10: パッチが途中で切れていた場合の確認ダイアログの文言を組み立てる。解析できた
    /// ブロックが0件のときは「解析できた0件をキューへ追加し」が不自然（実際には何も
    /// 追加されていない）ため、「解析できたブロックは無かった」旨の文言に分岐する。
    /// 1件以上のときは従来どおり件数を示す。いずれの場合も、コピーすると現在クリップボードに
    /// ある内容（貼り付けた元のパッチ）が失われる旨を明示する。
    /// </summary>
    public static string BuildTruncatedPatchConfirmMessage(int addedCount, int duplicateCount)
    {
        var message = addedCount == 0
            ? "パッチが途中で切れていました。解析できたブロックは無かったため、続きの依頼文をクリップボードへコピーしますか？"
            : $"パッチが途中で切れていたため、解析できた{addedCount}件をキューへ追加しました。続きの依頼文をクリップボードへコピーしますか？";
        if (duplicateCount > 0)
        {
            message += $" 同一ファイルへの重複ブロックが{duplicateCount}件あります（キュー画面で確認してください）。";
        }
        message += "\n\n今クリップボードにある内容（貼り付けた元のパッチなど）は上書きされて失われます。";
        return message;
    }

    /// <summary>4.10: 現在解析中のパッチを手動でキューへ追加する。</summary>
    private Task AddCurrentPatchToQueueAsync()
    {
        if (_currentPatch is null) return Task.CompletedTask;

        var result = PatchQueue.Add(_currentPatch, ProjectPane.SelectedItem?.Project.Id);
        Queue.Refresh();

        var duplicateCount = result.Issues.Count(i => i.Code == ErrorCode.E007);
        var message = duplicateCount > 0
            ? $"{result.Value.Count}件をキューへ追加しました（うち{duplicateCount}件は同一ファイルへの重複ブロックです）。"
            : $"{result.Value.Count}件をキューへ追加しました。";
        // 成功の知らせだけなので、OKを押すまで閉じないダイアログではなく、ステータスバーの
        // 数秒で消える通知にする（StatusNoticeViewModel.cs）。
        DiscardCurrentPatch();
        Notice.Show(message);
        return Task.CompletedTask;
    }

    /// <summary>4.10: キュー全体を1つのパッチに結合し、通常のドライラン・適用フローに乗せる。</summary>
    private async Task MergeQueueAndLoadAsync()
    {
        var merged = PatchQueue.Merge();
        if (!merged.IsSuccess)
        {
            await _dialogs.ShowMessageAsync("キューが空です", "結合するブロックがありません。").ConfigureAwait(true);
            return;
        }

        // 別のプロジェクトを選んでいるときに積んだ項目が混ざっていたら、確認する（C12）。
        // 結合はいま選んでいるプロジェクトに対して解析・適用されるため、気づかずに進めると
        // 別のプロジェクトのファイルへ当てはめようとして、まず失敗するか、同名のファイルがあれば
        // 意図しない場所を書き換える。
        var currentProjectId = ProjectPane.SelectedItem?.Project.Id;
        var otherProjects = PatchQueue.Items
            .Where(i => i.ProjectId is not null && i.ProjectId != currentProjectId)
            .Select(i => i.ProjectId!)
            .Distinct()
            .ToList();
        if (otherProjects.Count > 0)
        {
            var names = otherProjects
                .Select(id => ProjectPane.Items.FirstOrDefault(p => p.Project.Id == id)?.Project.DisplayName ?? "（削除されたプロジェクト）");
            var currentName = ProjectPane.SelectedItem?.Project.DisplayName ?? "（未選択）";
            var proceed = await _dialogs.ConfirmAsync(
                "別のプロジェクトで積んだ変更が含まれています",
                $"キューには、「{string.Join("」「", names)}」で積んだ変更が含まれています。" +
                $"現在選んでいるのは「{currentName}」で、結合した変更はこのプロジェクトに対して解析・適用されます。" +
                Environment.NewLine + Environment.NewLine + "このまま結合して解析しますか？").ConfigureAwait(true);
            if (!proceed) return;
        }

        _currentPatch = merged.Value;
        _dryRunFromQueue = true;
        await RunDryRunAsync().ConfigureAwait(true);
    }

    /// <summary>キュー結合適用が成功した直後に、不要になったキューを空にする（ApplyAsyncから呼ぶ）。</summary>
    private void FinalizeApplyFromQueueIfNeeded()
    {
        if (!_dryRunFromQueue) return;
        PatchQueue.Clear();
        Queue.Refresh();
        _dryRunFromQueue = false;
    }

    /// <summary>
    /// 11章: 適用に失敗した<b>すべての</b>ブロックについて、失敗したSEARCH部と現在のコードを添えた
    /// 修正依頼文をコピーする（「修正を依頼」ボタン）。
    /// </summary>
    private async Task CopyRecoveryPromptAsync()
    {
        var failedPlans = Blocks.Where(b => !b.Plan.CanApply).Select(b => b.Plan).ToList();
        await CopyRecoveryPromptForPlansAsync(failedPlans).ConfigureAwait(true);
    }

    /// <summary>
    /// 右クリックメニュー「修正依頼プロンプトをコピー」用。<b>指定した1ブロックだけ</b>の修正依頼文を
    /// コピーする。メニューのヘルプ文は「このブロックの」と謳っているのに、以前は引数を持たない
    /// <see cref="CopyRecoveryPromptCommand"/> をそのまま使っていたため、どの行で押しても
    /// 失敗ブロックすべてがコピーされていた。失敗していないブロックが渡されたら何もしない
    /// （メニュー側も無効化しているが、キー操作などの別経路からの呼び出しに備えた二重の防御）。
    /// </summary>
    public Task CopyRecoveryPromptForBlockAsync(BlockItemViewModel block)
    {
        ArgumentNullException.ThrowIfNull(block);
        return block.Plan.CanApply
            ? Task.CompletedTask
            : CopyRecoveryPromptForPlansAsync(new[] { block.Plan });
    }

    private Task CopyRecoveryPromptForPlansAsync(IReadOnlyList<BlockPlan> failedPlans)
    {
        if (failedPlans.Count == 0) return Task.CompletedTask;

        var projectRoot = ProjectPane.SelectedItem?.Project.Root;
        if (projectRoot is null) return Task.CompletedTask;

        var prompt = RecoveryPrompt.Build(failedPlans, path => ReadCurrentTextForRecovery(projectRoot, path));
        TrySetClipboardText(prompt);
        var message = failedPlans.Count == 1
            ? $"「{failedPlans[0].Path}」の失敗ブロックについて、失敗したSEARCH部と現在のコードを含む修正依頼文をクリップボードへコピーしました。"
            : $"{failedPlans.Count}件の失敗ブロックについて、失敗したSEARCH部と現在のコードを含む修正依頼文をクリップボードへコピーしました。";
        // コピーの成功はステータスバーの通知で足りる（失敗の余地がある処理ではないため、
        // 判断を求めるダイアログにはしない）。
        Notice.Show(message);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 11章のリカバリ文生成に使う現在のファイル内容を読む。<see cref="RecoveryPrompt.Build"/> の
    /// 引数が同期デリゲート（Func&lt;string, string?&gt;）のため、ここのみ同期読み取りとする
    /// （表示用の小さな抜粋取得のみで、書き込みは行わない）。
    /// </summary>
    private static string? ReadCurrentTextForRecovery(string projectRoot, string relativePath)
    {
        try
        {
            var full = Path.Combine(projectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(full) ? File.ReadAllText(full) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // IClipboardAccess.SetTextは失敗しても例外を投げない契約のため、ここでの保護は不要。
    private void TrySetClipboardText(string text) => _ui.Clipboard.SetText(text);
}
