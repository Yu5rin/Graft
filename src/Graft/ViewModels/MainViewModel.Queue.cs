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
        var addResult = PatchQueue.Add(patch);
        Queue.Refresh();

        var duplicateCount = addResult.Issues.Count(i => i.Code == ErrorCode.E007);
        var confirmMessage = BuildTruncatedPatchConfirmMessage(addResult.Value.Count, duplicateCount);
        var confirmed = await _dialogs.ConfirmAsync("続きを依頼するプロンプトをコピーしますか？", confirmMessage).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        var continuation = RecoveryPrompt.BuildContinuation(patch.TailLines);
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
            ? "パッチが途中で切れていました。解析できたブロックは無かったため、続きを依頼するプロンプトをクリップボードへコピーしますか？"
            : $"パッチが途中で切れていたため、解析できた{addedCount}件をキューへ追加しました。続きを依頼するプロンプトをクリップボードへコピーしますか？";
        if (duplicateCount > 0)
        {
            message += $" 同一ファイルへの重複ブロックが{duplicateCount}件あります（キュー画面で確認してください）。";
        }
        message += "\n\n今クリップボードにある内容（貼り付けた元のパッチなど）は上書きされて失われます。";
        return message;
    }

    /// <summary>4.10: 現在解析中のパッチを手動でキューへ追加する。</summary>
    private async Task AddCurrentPatchToQueueAsync()
    {
        if (_currentPatch is null) return;

        var result = PatchQueue.Add(_currentPatch);
        Queue.Refresh();

        var duplicateCount = result.Issues.Count(i => i.Code == ErrorCode.E007);
        var message = duplicateCount > 0
            ? $"{result.Value.Count}件をキューへ追加しました（うち{duplicateCount}件は同一ファイルへの重複ブロックです）。"
            : $"{result.Value.Count}件をキューへ追加しました。";
        await _dialogs.ShowMessageAsync("キューへ追加しました", message).ConfigureAwait(true);

        DiscardCurrentPatch();
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

    /// <summary>11章: 適用に失敗したブロックについて、現在のコードを添えた再依頼文をコピーする。</summary>
    private async Task CopyRecoveryPromptAsync()
    {
        var failedPlans = Blocks.Where(b => !b.Plan.CanApply).Select(b => b.Plan).ToList();
        if (failedPlans.Count == 0) return;

        var projectRoot = ProjectPane.SelectedItem?.Project.Root;
        if (projectRoot is null) return;

        var prompt = RecoveryPrompt.Build(failedPlans, path => ReadCurrentTextForRecovery(projectRoot, path));
        TrySetClipboardText(prompt);
        await _dialogs.ShowMessageAsync("再依頼プロンプトをコピーしました",
            $"{failedPlans.Count}件の失敗ブロックについて、現在のコードを含む再依頼文をクリップボードへコピーしました。")
            .ConfigureAwait(true);
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
