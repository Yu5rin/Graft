using System.Windows.Input;
using Graft.Core;

namespace Graft.ViewModels;

/// <summary>
/// <see cref="ShellViewModel"/> の分割ファイル（1ファイル400行上限のため）。
/// B: 接ぎ木パネル（<c>GraftPanel.axaml</c>）のブロック一覧・右クリックメニュー用コマンドを担う。
/// 「対象ファイルを開く」は既存の<see cref="OpenBlockInEditorCommand"/>をそのまま再利用するため
/// ここには含めない（コンストラクタ参照）。「修正依頼プロンプトをコピー」は
/// <see cref="CopyBlockRecoveryPromptCommand"/>（その行のブロックだけをコピーする）を使う。
/// 以前は「修正を依頼」ボタンと同じ<see cref="MainViewModel.CopyRecoveryPromptCommand"/>を
/// 流用していたが、あれは引数を取らず失敗ブロックすべてをコピーするため、メニューの
/// 「このブロックの」という説明と食い違っていた。ボタン側は従来どおり全失敗ブロックを対象にする。
/// </summary>
public sealed partial class ShellViewModel
{
    /// <summary>B: ブロック右クリックメニュー「このブロックの差分をコピー」。unified diff形式で出力する。</summary>
    public ICommand CopyBlockDiffCommand { get; private set; } = null!;

    /// <summary>
    /// B: ブロック右クリックメニュー「修正依頼プロンプトをコピー」。パラメータのブロック<b>1件だけ</b>の
    /// 修正依頼文をコピーする。失敗していないブロックでは実行できない。
    /// </summary>
    public ICommand CopyBlockRecoveryPromptCommand { get; private set; } = null!;

    /// <summary>B: ブロック右クリックメニュー「チェックを付ける／外す」。</summary>
    public ICommand ToggleBlockCheckCommand { get; private set; } = null!;

    private void InitializeGraftPanelContextMenuCommands()
    {
        CopyBlockDiffCommand = new RelayCommand<BlockItemViewModel>(
            block =>
            {
                if (block is null) return;
                var text = UnifiedDiffFormatter.Format(block.Plan.Path, block.Plan.BeforeText, block.Plan.AfterText);
                // IClipboardAccess.SetTextは失敗しても例外を投げない契約のため、ここでの保護は不要。
                _ui.Clipboard.SetText(text);
            },
            block => block is not null && (block.Plan.BeforeText is not null || block.Plan.AfterText is not null));

        // MainViewModel側は非同期（完了ダイアログを出す）だが、メニューのコマンドは同期のRelayCommand<T>。
        // AsyncRelayCommandと同じ作法（SafeHandler.RunAsync）で包み、想定外の例外でアプリが落ちないようにする。
        CopyBlockRecoveryPromptCommand = new RelayCommand<BlockItemViewModel>(
            block =>
            {
                if (block is null) return;
                _ = SafeHandler.RunAsync("修正依頼プロンプトのコピー", () => Graft.CopyRecoveryPromptForBlockAsync(block));
            },
            block => block is { IsError: true });

        ToggleBlockCheckCommand = new RelayCommand<BlockItemViewModel>(
            block => block?.Toggle(),
            block => block is { CanToggle: true });
    }
}
