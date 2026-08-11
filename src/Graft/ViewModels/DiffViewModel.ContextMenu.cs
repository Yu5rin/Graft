using System.Windows.Input;
using Graft.Core;

namespace Graft.ViewModels;

/// <summary>
/// C: 差分表示（DiffView.axaml）の右クリックメニュー用コマンドを担う分割ファイル。
/// DiffViewModel.cs本体は並行して左右並列表示の対応が進んでいるため、コンフリクトを避け
/// メニュー追加分をこのファイルへ独立させる（本体への変更はコンストラクタ末尾への初期化
/// 呼び出し1行のみ）。
/// </summary>
public sealed partial class DiffViewModel
{
    /// <summary>「変更前をコピー」。変更前が無い（新規作成）ブロックでは無効化する。</summary>
    public ICommand CopyBeforeCommand { get; private set; } = null!;

    /// <summary>「変更後をコピー」。変更後が無い（削除）ブロックでは無効化する。</summary>
    public ICommand CopyAfterCommand { get; private set; } = null!;

    /// <summary>
    /// 「この差分をunified diff形式でコピー」。AIへ「この変更のここを直して」と依頼するときに
    /// 貼れる形にする（<see cref="UnifiedDiffFormatter"/>は既存の取り込み側
    /// <see cref="UnifiedDiffAdapter"/>がそのまま解析できる形式で出力する）。
    /// </summary>
    public ICommand CopyUnifiedDiffCommand { get; private set; } = null!;

    private void InitializeContextMenuCommands()
    {
        // 新規作成ブロックのBeforeTextは「元ファイルが無い」ことを表すため空文字列
        // （BlockResolver.ResolveFileのJoinText(baseLines)がbaseLines=空リストのとき""を返す）
        // であって null ではない。null判定だけでは新規作成でも有効になってしまうため、
        // Operationで「そもそも変更前と呼べる内容が無い」かどうかを判定する。
        CopyBeforeCommand = new RelayCommand(
            () => { if (_plan?.BeforeText is { } text) _ui.Clipboard.SetText(text); },
            () => _plan is { } p && p.Operation != EntryOperation.Create && p.BeforeText is not null);
        CopyAfterCommand = new RelayCommand(
            () => { if (_plan?.AfterText is { } text) _ui.Clipboard.SetText(text); },
            () => _plan is { } p && p.Operation != EntryOperation.Delete && p.AfterText is not null);
        CopyUnifiedDiffCommand = new RelayCommand(
            () =>
            {
                if (_plan is not { } plan) return;
                var text = UnifiedDiffFormatter.Format(plan.Path, plan.BeforeText, plan.AfterText);
                _ui.Clipboard.SetText(text);
            },
            () => _plan is not null);
    }
}
