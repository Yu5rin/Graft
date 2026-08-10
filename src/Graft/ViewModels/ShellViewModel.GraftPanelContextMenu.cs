using System.Windows.Input;
using Graft.Core;

namespace Graft.ViewModels;

/// <summary>
/// <see cref="ShellViewModel"/> の分割ファイル（1ファイル400行上限のため）。
/// B: 接ぎ木パネル（<c>GraftPanel.axaml</c>）のブロック一覧・右クリックメニュー用コマンドを担う。
/// 「対象ファイルを開く」は既存の<see cref="OpenBlockInEditorCommand"/>をそのまま再利用するため
/// ここには含めない（コンストラクタ参照）。「修正依頼プロンプトをコピー」も既存の
/// <see cref="MainViewModel.CopyRecoveryPromptCommand"/>を再利用し、GraftPanel.axaml側で
/// 行（ブロック）ごとのIsError（このブロックが失敗かどうか）をIsEnabledへ直接束縛することで
/// 「失敗したブロックのときだけ有効」を満たす（コマンド自体のCanExecuteは「一覧内に1件でも
/// 失敗ブロックがあるか」のままで変えない。他画面での挙動に影響を与えないため）。
/// </summary>
public sealed partial class ShellViewModel
{
    /// <summary>B: ブロック右クリックメニュー「このブロックの差分をコピー」。unified diff形式で出力する。</summary>
    public ICommand CopyBlockDiffCommand { get; private set; } = null!;

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

        ToggleBlockCheckCommand = new RelayCommand<BlockItemViewModel>(
            block => block?.Toggle(),
            block => block is { CanToggle: true });
    }
}
