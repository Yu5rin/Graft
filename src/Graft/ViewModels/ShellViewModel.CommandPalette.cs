using System.Windows.Input;
using Graft.Features;

namespace Graft.ViewModels;

/// <summary>
/// <see cref="ShellViewModel"/> の分割ファイル（1ファイル400行上限のため）。
///
/// コマンドパレット（Ctrl+Shift+P、機能改善）を担う。既存のクイックオープン
/// （<see cref="QuickOpenViewModel"/>、Ctrl+P）と同じ操作感・実装作法に揃えるため、
/// このファイルもQuickOpen関連のShellViewModel側の配線（ToggleQuickOpenCommand等）と
/// 対になる構造にしている。
///
/// 一覧に載せる操作は、ツールバー・サイドバー・ショートカットで既に到達できる既存の
/// <see cref="ICommand"/>をそのまま集めたもの（新しいコマンドは作らない。仕様どおり）。
/// ショートカット表記は<see cref="ShortcutCatalog"/>から逆引きし、
/// <c>Graft.Views.ShortcutsWindow</c>の一覧と二重管理にならないようにする
/// （ShortcutCatalog.csのクラスコメント参照）。
/// </summary>
public sealed partial class ShellViewModel
{
    /// <summary>コマンドパレット本体。</summary>
    public CommandPaletteViewModel CommandPalette { get; private set; } = null!;

    /// <summary>Ctrl+Shift+P。コマンドパレットの開閉（トグル）。</summary>
    public ICommand ToggleCommandPaletteCommand { get; private set; } = null!;

    /// <summary>
    /// コンストラクタ終端から呼ぶ（Graft・Editor・他のICommandが一通り組み上がった後で
    /// ないと、対象コマンドを集められないため）。
    /// </summary>
    private void InitializeCommandPalette()
    {
        CommandPalette = new CommandPaletteViewModel(BuildPaletteCommands());
        ToggleCommandPaletteCommand = new RelayCommand(ToggleCommandPalette);
    }

    /// <summary>
    /// クイックオープンが開いていれば先に閉じてから開閉する（ToggleQuickOpenAsyncの
    /// 逆側の配線。2つのオーバーレイが同時に重なって表示されないようにするため）。
    /// </summary>
    private void ToggleCommandPalette()
    {
        if (QuickOpen.IsOpen) QuickOpen.Close();
        CommandPalette.Toggle();
    }

    /// <summary>
    /// パレットに載せる操作の一覧。ShellWindow.axamlのツールバー・SideBar.axaml・
    /// ShellWindow.Keyboard.csのショートカットで実際に到達できる操作から集める
    /// （プロジェクト番号切り替え（Ctrl+Alt+1〜9）やタブ個別のクローズ等、対象が
    /// 状況依存で単一の固定操作として表現しづらいものは対象外）。
    /// </summary>
    private List<PaletteCommandDescriptor> BuildPaletteCommands()
    {
        string? Gesture(string key) => ShortcutCatalog.GestureFor(key);

        return new List<PaletteCommandDescriptor>
        {
            new("プロジェクトのファイル一覧とコンテキスト収集を開く", Graft.OpenContextCollectCommand, null, Gesture("OpenContextCollect")),
            new("クリップボードのパッチを解析", Graft.PasteAndParseCommand, null, Gesture("PasteAndParse")),
            new("現在の解析結果をパッチキューへ追加", Graft.AddCurrentPatchToQueueCommand, null, Gesture("AddToQueue")),
            new("パッチキューを開く", Graft.OpenQueueCommand, null, Gesture("OpenQueue")),
            new("適用を実行", Graft.ApplyCommand, null, Gesture("Apply")),
            new("プレビューを再実行", Graft.PreviewCommand, null, Gesture("Preview")),
            new("プロンプトテンプレートを選んでコピー", Graft.OpenPromptDropdownCommand, null, Gesture("OpenPromptDropdown")),
            new("AIへの依頼文（プロンプトテンプレート）をコピー", Graft.CopyPromptCommand, null, Gesture("CopyPrompt")),
            new("失敗ブロックの再依頼プロンプトをコピー", Graft.CopyRecoveryPromptCommand, null, Gesture("CopyRecoveryPrompt")),
            new("履歴ビューを開く", Graft.ShowHistoryCommand, null, Gesture("ShowHistory")),
            new("直前に適用したリビジョンを元に戻す", Graft.UndoCommand, null, Gesture("Undo")),
            new("解析結果を破棄", Graft.DiscardCommand, null, Gesture("Discard")),
            new("設定を開く", Graft.OpenSettingsCommand, null, Gesture("OpenSettings")),
            new("キーボードショートカット一覧を開く", OpenShortcutsCommand, null, Gesture("OpenShortcuts")),
            new("取扱説明書を開く", OpenManualCommand, null, Gesture("OpenManual")),
            // 画面上のチュートリアル（コーチマーク）。専用のキー割り当ては無いため、Gestureは常にnull
            // （ShortcutCatalogに"StartTutorial"を登録していない。HasGesture=falseとしてバッジ非表示）。
            new("使い方を学ぶ（画面上のチュートリアル）", StartTutorialCommand, null, Gesture("StartTutorial")),
            new("接ぎ木パネルの開閉", ToggleGraftPanelCommand, null, Gesture("ToggleGraftPanel")),
            new("接ぎ木パネルの配置切り替え（下／右）", ToggleGraftPanelPlacementCommand, null, Gesture("ToggleGraftPanelPlacement")),
            new("ファイル名であいまい検索して開く（クイックオープン）", ToggleQuickOpenCommand, null, Gesture("QuickOpen")),
            new("プロジェクトビューに切り替え", SelectSideViewCommand, SideViewKind.Project, Gesture("SelectProject")),
            new("エクスプローラビューに切り替え", SelectSideViewCommand, SideViewKind.Explorer, Gesture("SelectExplorer")),
            new("検索ビューに切り替え", SelectSideViewCommand, SideViewKind.Search, Gesture("SelectSearch")),
            new("直前に閉じたタブを開き直す", Editor.ReopenLastClosedTabCommand, null, Gesture("ReopenLastClosedTab")),
        };
    }
}
